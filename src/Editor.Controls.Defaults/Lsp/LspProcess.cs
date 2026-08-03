using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Editor.Controls.Lsp;

/// <summary>Manages a language server process and provides JSON-RPC 2.0 messaging over stdio.</summary>
internal sealed class LspProcess : IDisposable
{
    private readonly Process _process;
    private readonly object _writeLock = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private int _nextId;
    private bool _disposed;
    private int _exitRaised;

    public event Action<string, JsonElement>? NotificationReceived;

    /// <summary>Raised exactly once when the connection becomes unusable for any reason other than an
    /// intentional <see cref="Dispose"/> — the server crashed, its stdout closed, or the read loop hit an
    /// unrecoverable IO error. By the time this fires <see cref="IsRunning"/> is already false (the process
    /// has been torn down), so callers can rely on it to know a reconnect is actually needed.</summary>
    public event Action? Exited;

    public bool IsRunning => !_disposed && !_process.HasExited;

    /// <summary>
    /// initialize で通知したワークスペースフォルダ。サーバーが後から
    /// <c>workspace/workspaceFolders</c> を要求してきたときに同じ内容を返すために保持する
    /// (null を返すと「フォルダなし」の意味になり、initialize の内容と矛盾する)。
    /// </summary>
    public LspWorkspaceFolder[]? WorkspaceFolders { get; set; }

    /// <summary>
    /// 応答が返らない要求を諦めるまでの既定時間。
    ///
    /// 60 秒という値の根拠: 「速い正常系」ではなく「一番遅い正常系」に合わせる。実測で秒単位まで伸びるのは
    /// 大規模リポジトリでの <c>initialize</c>（Roslyn / rust-analyzer がプロジェクトを走査する）と
    /// <c>workspace/symbol</c> の初回、そして <c>workspace/diagnostic</c> で、いずれも 10 秒台までは
    /// 珍しくない。ここを 5〜10 秒にすると「本当は動いていた」応答を捨ててしまい、補完もシンボル検索も
    /// 間欠的に空を返す（=タイムアウトが新しいバグになる）。逆に上限が無いと、プロセスは生きているのに
    /// 応答だけ返さないサーバーで <c>InitializeAsync</c> が永久に固まり、ホスト側は
    /// 「言語サーバーへの接続待ちです」から一生進まない。60 秒は「正常系が引っかかることはまず無いが、
    /// 人間が固まったと判断するより前には必ず諦める」ための保守的な妥協点。
    /// </summary>
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <see cref="SendRequestAsync"/> のタイムアウト。既定は <see cref="DefaultRequestTimeout"/>。
    /// テストが実時間を待たずに検証するための内部シームで、製品コードからは変更しない。
    /// </summary>
    internal TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;

    /// <summary>応答待ちの要求数。タイムアウト時に確実に取り除けているか（リークしていないか）を
    /// テストから確認するための内部シーム。</summary>
    internal int PendingRequestCount { get { lock (_pending) return _pending.Count; } }

    public LspProcess(string executable, IEnumerable<string> args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start language server");
        // Guarantees the server dies even if this process is killed ungracefully (crash, forced-close, a
        // debugger session yanked away) — Dispose() below only covers a normal shutdown, and language-server
        // processes left behind by an ungraceful exit never get cleaned up on their own (observed as csharp-ls
        // instances accumulating across days, each competing for MSBuild/CPU and starving new sessions).
        JobObject.Assign(_process);

        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "LspStdout" };
        thread.Start();
    }

    /// <summary>
    /// 要求を送り、応答を待つ。応答が <see cref="RequestTimeout"/> 以内に返らなければ
    /// <see cref="TimeoutException"/> で完了させる（=永久待ちにしない）。呼び出し側の
    /// <paramref name="ct"/> はタイムアウトと合成され、従来どおりキャンセルとして観測できる。
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(
        string method, object? @params, CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pending) _pending[id] = tcs;

        try
        {
            WriteMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }));
        }
        catch (Exception ex)
        {
            RemovePending(id);
            // Dispose が先に TrySetCanceled 済みのことがある。SetException だと
            // InvalidOperationException になって本来の送信失敗が失われる。
            tcs.TrySetException(ex);
            return await tcs.Task.ConfigureAwait(false);
        }

        // キャンセルとタイムアウトを 1 本のトークンに合成する。応答が届けば下の await が返り、
        // using による破棄でタイマーも登録も解放されるので、待ち受けが残り続けることはない。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        using var registration = cts.Token.Register(() =>
        {
            // 先に _pending から外す。外せなかった＝ちょうど応答が入った、なので何もしない。
            if (!RemovePending(id)) return;

            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            // 「生きているのに黙っているサーバー」は放置すると最悪の見え方（初期化中のまま固まる）に
            // なるので痕跡を残す。ただしこの Log は SK0YA_EDITOR_IDE_DIAG=1 のときだけ書かれる
            // ——機能要求（補完・シンボル検索）は呼び出し側が空へ丸めるので、既定では
            // 「60秒待って空だった」ことは表に出ない。initialize だけは例外が伝播し、
            // ホスト側が「起動失敗」として表示できる（そこが一番効く経路なので現状はこれで足りる）。
            Log($"request timed out after {RequestTimeout.TotalSeconds:0.#}s: id={id} method={method}");
            tcs.TrySetException(new TimeoutException(
                $"LSP request '{method}' timed out after {RequestTimeout.TotalSeconds:0.#}s."));
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>応答待ちの一覧から取り除く。取り除けたら true（＝この呼び出しが要求の決着をつける権利を持つ）。</summary>
    private bool RemovePending(int id)
    {
        lock (_pending) return _pending.Remove(id);
    }

    public void SendNotification(string method, object? @params)
    {
        try { WriteMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params })); }
        catch { }
    }

    private void WriteMessage(string json)
    {
        Log($"send ({json.Length} bytes): {(json.Length > 300 ? json[..300] + "…" : json)}");
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeLock)
        {
            var stream = _process.StandardInput.BaseStream;
            stream.Write(header);
            stream.Write(body);
            stream.Flush();
        }
    }

    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "editor-lsp-debug.log");
    private static readonly bool _diagnosticLogEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SK0YA_EDITOR_IDE_DIAG"), "1", StringComparison.Ordinal);
    private static void Log(string msg)
    {
        if (!_diagnosticLogEnabled) return;
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [proc] {msg}\n"); } catch { }
    }

    private void ReadLoop()
    {
        var stream = _process.StandardOutput.BaseStream;
        while (!_disposed)
        {
            // IO errors break the loop; parse/dispatch errors must not.
            int len;
            byte[]? body;
            try
            {
                len = ReadContentLength(stream);
                if (len <= 0) break;   // EOF
                body = ReadExact(stream, len);
                if (body == null) break; // EOF
            }
            catch { break; }  // real IO error — stop reading

            try
            {
                var json = Encoding.UTF8.GetString(body);
                Log($"recv ({json.Length} bytes): {(json.Length > 300 ? json[..300] + "…" : json)}");
                HandleMessage(json);
            }
            catch { }  // bad JSON or dispatch error — skip this message
        }
        Log("ReadLoop exited");
        RaiseExitedAndTearDown();
    }

    /// <summary>Whatever ended the read loop, this connection can never receive another response or
    /// notification — tear the process down so <see cref="IsRunning"/> reflects that immediately (previously
    /// a dead-but-not-yet-exited process, or a broken pipe with the process still technically alive, left
    /// <see cref="IsRunning"/> true forever, so nothing ever noticed the server had stopped talking), then let
    /// the owner know so it can reconnect. A no-op if <see cref="Dispose"/> already claimed this exit (a
    /// deliberate shutdown is not a failure and must not trigger a reconnect).</summary>
    private void RaiseExitedAndTearDown()
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(); } catch { }
        Exited?.Invoke();
    }

    private static int ReadContentLength(Stream stream)
    {
        int contentLength = -1;
        while (true)
        {
            var line = ReadLine(stream);
            if (line == null) return -1;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out int len))
                contentLength = len;
            if (line.Length == 0) return contentLength;
        }
    }

    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>(128);
        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1) return null;
            if (b == '\r')
            {
                int next = stream.ReadByte();
                if (next == -1) return null;
                if (next == '\n') break;
                // bare \r — keep both bytes
                bytes.Add((byte)b);
                bytes.Add((byte)next);
            }
            else if (b == '\n') break;
            else bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static byte[]? ReadExact(Stream stream, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buf, read, count - read);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            bool hasId = root.TryGetProperty("id", out var idProp);
            bool hasResult = root.TryGetProperty("result", out var result);
            bool hasError = root.TryGetProperty("error", out _);
            bool hasMethod = root.TryGetProperty("method", out var methodProp);

            if (hasId && (hasResult || hasError))
            {
                // Response to one of our requests
                int id = idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : -1;
                TaskCompletionSource<JsonElement?>? tcs;
                lock (_pending) { _pending.TryGetValue(id, out tcs); _pending.Remove(id); }
                tcs?.TrySetResult(hasResult ? result : null);
            }
            else if (hasMethod)
            {
                root.TryGetProperty("params", out var @params);

                // Server-to-client REQUEST (has id + method, no result/error):
                // we MUST send a response or the server will block.
                // JSON-RPC 2.0 の id は number でも string でもよいので、受け取った値をそのまま返す。
                if (hasId && (idProp.ValueKind == JsonValueKind.Number || idProp.ValueKind == JsonValueKind.String))
                {
                    Log($"responding to server request id={idProp} method={methodProp.GetString()}");
                    SendResponse(idProp, CreateServerRequestResult(
                        methodProp.GetString() ?? "", @params, WorkspaceFolders));
                }

                NotificationReceived?.Invoke(methodProp.GetString() ?? "", @params);
            }
        }
        catch { }
    }

    /// <summary>
    /// サーバーからの要求に対する最小応答を作る。
    /// workspace/configuration は要求した item と同数の値を返す必要があり、null 応答では
    /// Roslyn/Razor 側が配列として扱えず初期化に失敗する。
    /// </summary>
    internal static object? CreateServerRequestResult(
        string method, JsonElement @params, LspWorkspaceFolder[]? workspaceFolders = null)
    {
        switch (method)
        {
            case "workspace/configuration":
                if (@params.ValueKind == JsonValueKind.Object
                    && @params.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array)
                    return new object?[items.GetArrayLength()];
                return null;

            // initialize で通知したものと同じ一覧を返す。null は「フォルダなし」を意味し、
            // ここで再同期するサーバーはルートを失ってプロジェクトの読み込みを止めてしまう。
            case "workspace/workspaceFolders":
                return workspaceFolders;

            // 結果型は applied (bool) が必須。null だとサーバー側でデシリアライズに失敗する。
            // 現状クライアント側で編集を適用する経路がないので、未適用として返す。
            case "workspace/applyEdit":
                return new { applied = false };

            default:
                return null;
        }
    }

    /// <summary>Send a successful response to a server-initiated request.</summary>
    /// <param name="id">受信した id をそのまま返す (number / string のいずれもあり得る)。</param>
    private void SendResponse(JsonElement id, object? result)
    {
        try { WriteMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result })); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _exitRaised, 1);  // this is deliberate — suppress the Exited/reconnect path
        try { _process.Kill(); } catch { }
        _process.Dispose();
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }
}
