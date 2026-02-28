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

    public event Action<string, JsonElement>? NotificationReceived;
    public bool IsRunning => !_disposed && !_process.HasExited;

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

        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "LspStdout" };
        thread.Start();
    }

    public Task<JsonElement?> SendRequestAsync(string method, object? @params, CancellationToken ct = default)
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
            lock (_pending) _pending.Remove(id);
            tcs.SetException(ex);
            return tcs.Task;
        }

        if (ct.CanBeCanceled)
            ct.Register(() => { lock (_pending) _pending.Remove(id); tcs.TrySetCanceled(ct); });

        return tcs.Task;
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
    private static void Log(string msg)
    {
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
                if (hasId && idProp.ValueKind == JsonValueKind.Number)
                {
                    var reqId = idProp.GetInt32();
                    Log($"responding to server request id={reqId} method={methodProp.GetString()}");
                    SendResponse(reqId);
                }

                NotificationReceived?.Invoke(methodProp.GetString() ?? "", @params);
            }
        }
        catch { }
    }

    /// <summary>Send a successful null response to a server-initiated request.</summary>
    private void SendResponse(int id)
    {
        try { WriteMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = (object?)null })); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _process.Kill(); } catch { }
        _process.Dispose();
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }
}
