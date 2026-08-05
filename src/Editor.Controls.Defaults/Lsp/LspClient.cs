using System.IO;
using System.Text.Json;
using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>Full LSP client implementation using JSON-RPC 2.0 over stdio.</summary>
public sealed class LspClient : ILspClient
{
    private readonly LspProcess _process;
    private readonly object _documentGate = new();
    private readonly Dictionary<string, string> _documentTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _diagnosticGate = new();
    private readonly Dictionary<string, string> _diagnosticResultIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _diagnosticIdentifier;
    private int _textDocumentSyncKind = 1;

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    /// <summary>Raised once when the underlying process/connection dies for any reason other than
    /// <see cref="Dispose"/> — see <see cref="LspProcess.Exited"/>.</summary>
    public event Action? Exited;

    public bool IsRunning => _process.IsRunning;

    /// <summary>
    /// 要求ごとの応答待ちタイムアウト（<see cref="LspProcess.RequestTimeout"/> への委譲）。
    /// テストが実時間を待たずに検証するための内部シームで、製品コードからは変更しない。
    /// </summary>
    internal TimeSpan RequestTimeout
    {
        get => _process.RequestTimeout;
        set => _process.RequestTimeout = value;
    }

    public bool SupportsFoldingRange { get; private set; }
    public bool SupportsWorkspaceSymbol { get; private set; }
    public bool SupportsRangeFormatting { get; private set; }
    public bool SupportsInlayHint { get; private set; }
    public bool SupportsSemanticTokens { get; private set; }
    public bool SupportsSelectionRange { get; private set; }
    public bool SupportsDocumentDiagnostics { get; private set; }
    public bool SupportsWorkspaceDiagnostics { get; private set; }
    public IReadOnlyList<string> CompletionTriggerCharacters { get; private set; } = ["."];
    public SemanticTokensLegend? SemanticTokensLegend { get; private set; }
    public IReadOnlyList<string> CodeActionKinds { get; private set; } = [];
    public bool SupportsCodeActionResolve { get; private set; }
    public IReadOnlyList<string> ExecuteCommandNames { get; private set; } = [];

    public event EventHandler<LspApplyEditEventArgs>? ApplyEditRequested;

    public LspClient(string executable, IEnumerable<string> args, string? workingDir = null)
    {
        _process = new LspProcess(executable, args, workingDir);
        _process.NotificationReceived += OnNotification;
        _process.Exited += () => Exited?.Invoke();
        _process.ServerRequestHandler = OnServerRequest;
    }

    /// <summary>サーバー起点の要求のうち、ホストが実際に処理できるものを引き受ける。
    /// 関知しないものは null を返して <see cref="LspProcess.CreateServerRequestResult"/> の最小応答へ渡す。</summary>
    private object? OnServerRequest(string method, JsonElement @params)
    {
        if (!string.Equals(method, "workspace/applyEdit", StringComparison.Ordinal)) return null;
        if (ApplyEditRequested is not { } handler) return null;

        try
        {
            if (@params.ValueKind != JsonValueKind.Object ||
                !@params.TryGetProperty("edit", out var editEl) ||
                ParseWorkspaceEdit(editEl) is not { } edit)
                return new { applied = false, failureReason = "編集内容を解釈できませんでした。" };

            var label = @params.TryGetProperty("label", out var labelEl) ? labelEl.GetString() : null;
            var args = new LspApplyEditEventArgs(edit, label);
            handler(this, args);
            return new
            {
                applied = args.Applied,
                failureReason = args.Applied
                    ? null
                    : args.FailureReason ?? "ホストが編集を適用しませんでした。"
            };
        }
        catch (Exception ex)
        {
            return new { applied = false, failureReason = ex.Message };
        }
    }

    public async Task InitializeAsync(string rootUri)
        => await InitializeAsync(rootUri, workspaceFolderPaths: null);

    /// <summary>
    /// LSP サーバーを初期化する。ホストが実際のワークスペースフォルダーを所有する場合は
    /// <paramref name="workspaceFolderPaths"/> に全件を渡す。未指定時だけ単一ルートへフォールバックする。
    /// </summary>
    public async Task InitializeAsync(
        string rootUri, IReadOnlyList<string>? workspaceFolderPaths)
    {
        // サーバーが後から workspace/workspaceFolders を要求してきたときに同じ一覧を返せるよう、
        // initialize を送る前に LspProcess 側へ渡しておく。
        var workspaceFolders = CreateWorkspaceFolders(rootUri, workspaceFolderPaths);
        _process.WorkspaceFolders = workspaceFolders;

        var result = await _process.SendRequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    synchronization = new { openClose = true, change = 1 },
                    publishDiagnostics = new { relatedInformation = false },
                    completion = new { completionItem = new { snippetSupport = true } },
                    hover = new { contentFormat = new[] { "plaintext", "markdown" } },
                    definition = new { },
                    signatureHelp = new { signatureInformation = new { documentationFormat = new[] { "plaintext" } } },
                    formatting = new { },
                    rangeFormatting = new { },
                    rename = new { },
                    references = new { },
                    // codeActionLiteralSupport を宣言しないサーバーは旧仕様の Command[] しか返さない
                    // （kind が無いのでリファクタリングと quick fix を区別できない）。
                    // resolveSupport / dataSupport は Roslyn 系が必須——一覧では data だけを返し、
                    // edit は codeAction/resolve で初めて作るため、宣言しないと候補が空か edit なしになる。
                    codeAction = new
                    {
                        dynamicRegistration = false,
                        isPreferredSupport = true,
                        disabledSupport = true,
                        dataSupport = true,
                        resolveSupport = new { properties = new[] { "edit" } },
                        codeActionLiteralSupport = new
                        {
                            codeActionKind = new
                            {
                                valueSet = new[]
                                {
                                    "", "quickfix", "refactor", "refactor.extract", "refactor.inline",
                                    "refactor.rewrite", "refactor.move", "source", "source.organizeImports",
                                    "source.fixAll"
                                }
                            }
                        }
                    },
                    foldingRange = new { },
                    selectionRange = new { },
                    diagnostic = new { dynamicRegistration = false, relatedDocumentSupport = false },
                    documentHighlight = new { },
                    documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                    inlayHint = new { },
                    callHierarchy = new { },
                    typeHierarchy = new { },
                    semanticTokens = new
                    {
                        requests = new { full = true },
                        tokenTypes = new[]
                        {
                            "namespace", "type", "class", "enum", "interface", "struct",
                            "typeParameter", "parameter", "variable", "property", "enumMember",
                            "event", "function", "method", "macro", "keyword", "modifier",
                            "comment", "string", "number", "regexp", "operator", "decorator"
                        },
                        tokenModifiers = new[]
                        {
                            "declaration", "definition", "readonly", "static", "deprecated",
                            "abstract", "async", "modification", "documentation", "defaultLibrary"
                        },
                        formats = new[] { "relative" }
                    }
                },
                workspace = new
                {
                    symbol = new { },
                    diagnostics = new { refreshSupport = false },
                    // コマンド型リファクタリング（tsserver 系の「関数へ抽出」等）は
                    // executeCommand → サーバー起点の applyEdit で編集が返る。applyEdit を
                    // 宣言しないと、そもそもコマンドを出さないサーバーがある。
                    applyEdit = true,
                    executeCommand = new { dynamicRegistration = false },
                    workspaceEdit = new
                    {
                        documentChanges = true,
                        resourceOperations = new[] { "create", "rename", "delete" },
                        failureHandling = "abort"
                    },
                    // これを宣言しないと InitializeParams.workspaceFolders は無視される
                    // (Roslyn / rust-analyzer はフォルダを取り込む前にこの capability を見る)。
                    workspaceFolders = true
                }
            },
            workspaceFolders
        });
        _process.SendNotification("initialized", new { });

        // サーバーの capabilities を解析して対応機能を確定する
        if (result.HasValue &&
            result.Value.ValueKind == JsonValueKind.Object &&
            result.Value.TryGetProperty("capabilities", out var caps))
        {
            _textDocumentSyncKind = ParseTextDocumentSyncKind(caps);
            if (caps.TryGetProperty("foldingRangeProvider", out var frp))
                SupportsFoldingRange = frp.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            if (caps.TryGetProperty("workspaceSymbolProvider", out var wsp))
                SupportsWorkspaceSymbol = wsp.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            if (caps.TryGetProperty("documentRangeFormattingProvider", out var drf))
                SupportsRangeFormatting = drf.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            if (caps.TryGetProperty("inlayHintProvider", out var ihp))
                SupportsInlayHint = ihp.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            if (caps.TryGetProperty("selectionRangeProvider", out var srp))
                SupportsSelectionRange = srp.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            SupportsDocumentDiagnostics = LspCapabilityParser.SupportsDocumentDiagnostics(caps);
            _diagnosticIdentifier = LspCapabilityParser.DocumentDiagnosticIdentifier(caps);
            SupportsWorkspaceDiagnostics = LspCapabilityParser.SupportsWorkspaceDiagnostics(caps);
            CompletionTriggerCharacters = ParseCompletionTriggerCharacters(caps);
            (CodeActionKinds, SupportsCodeActionResolve) = ParseCodeActionProvider(caps);
            ExecuteCommandNames = ParseExecuteCommandNames(caps);
            if (caps.TryGetProperty("semanticTokensProvider", out var stp) &&
                stp.ValueKind == JsonValueKind.Object &&
                stp.TryGetProperty("legend", out var legend))
            {
                var types = ParseStringArray(legend, "tokenTypes");
                var mods  = ParseStringArray(legend, "tokenModifiers");
                if (types.Length > 0)
                {
                    SemanticTokensLegend = new SemanticTokensLegend(types, mods);
                    SupportsSemanticTokens = true;
                }
            }
        }
    }

    /// <summary>
    /// <c>--autoLoadProjects</c> を使う Roslyn 等は <c>rootUri</c> だけでなく
    /// <c>workspaceFolders</c> を見てプロジェクトを自動ロードする。
    /// ホスト提供の実フォルダー一覧を優先し、単体利用など一覧が無い場合だけ
    /// 初期化対象のルートを1件のワークスペースとして通知する。
    /// </summary>
    internal static LspWorkspaceFolder[] CreateWorkspaceFolders(
        string rootUri, IReadOnlyList<string>? workspaceFolderPaths = null)
    {
        var paths = workspaceFolderPaths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths is { Length: > 0 })
            return paths.Select(path => CreateWorkspaceFolder(new Uri(path).AbsoluteUri)).ToArray();

        return [CreateWorkspaceFolder(rootUri)];
    }

    private static LspWorkspaceFolder CreateWorkspaceFolder(string uriText)
    {
        var name = uriText;
        if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var path = uri.LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name))
                name = path;
        }
        return new LspWorkspaceFolder(uriText, name);
    }

    private static string[] ParseStringArray(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.GetString() is string s) list.Add(s);
        return [.. list];
    }

    /// <summary><c>codeActionProvider</c> から対応 kind と resolve 対応を読む。
    /// <c>true</c>（真偽値）で返すサーバーは kind を申告しないので空一覧＝「絞り込まず全件取る」。</summary>
    internal static (IReadOnlyList<string> Kinds, bool SupportsResolve) ParseCodeActionProvider(
        JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("codeActionProvider", out var provider))
            return ([], false);
        if (provider.ValueKind != JsonValueKind.Object)
            return ([], false);

        var kinds = ParseStringArray(provider, "codeActionKinds");
        bool resolve = provider.TryGetProperty("resolveProvider", out var rp) &&
                       rp.ValueKind == JsonValueKind.True;
        return (kinds, resolve);
    }

    internal static IReadOnlyList<string> ParseExecuteCommandNames(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("executeCommandProvider", out var provider) ||
            provider.ValueKind != JsonValueKind.Object)
            return [];
        return ParseStringArray(provider, "commands");
    }

    internal static IReadOnlyList<string> ParseCompletionTriggerCharacters(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("completionProvider", out var provider) ||
            provider.ValueKind != JsonValueKind.Object ||
            !provider.TryGetProperty("triggerCharacters", out var triggers) ||
            triggers.ValueKind != JsonValueKind.Array)
            return ["."];
        var result = triggers.EnumerateArray().Select(x => x.GetString()).OfType<string>()
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return result.Count > 0 ? result : ["."];
    }

    public Task OpenDocumentAsync(string uri, string languageId, string text)
    {
        lock (_documentGate) _documentTexts[uri] = text;
        _process.SendNotification("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId, version = 1, text }
        });
        return Task.CompletedTask;
    }

    public Task ChangeDocumentAsync(string uri, int version, string text)
    {
        string previousText;
        lock (_documentGate)
        {
            previousText = _documentTexts.GetValueOrDefault(uri, "");
            _documentTexts[uri] = text;
        }
        _process.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { CreateContentChange(_textDocumentSyncKind, previousText, text) }
        });
        return Task.CompletedTask;
    }

    public Task CloseDocumentAsync(string uri)
    {
        lock (_documentGate) _documentTexts.Remove(uri);
        lock (_diagnosticGate) _diagnosticResultIds.Remove(uri);
        _process.SendNotification("textDocument/didClose", new { textDocument = new { uri } });
        return Task.CompletedTask;
    }

    internal static int ParseTextDocumentSyncKind(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("textDocumentSync", out var sync))
            return 1;
        if (sync.ValueKind == JsonValueKind.Number && sync.TryGetInt32(out var numeric))
            return numeric;
        if (sync.ValueKind == JsonValueKind.Object &&
            sync.TryGetProperty("change", out var change) &&
            change.TryGetInt32(out var nested))
            return nested;
        return 1;
    }

    internal static object CreateContentChange(int syncKind, string previousText, string text)
    {
        if (syncKind != 2)
            return new { text };

        var normalized = previousText.Replace("\r\n", "\n").Replace('\r', '\n');
        var lastNewline = normalized.LastIndexOf('\n');
        var endLine = lastNewline < 0 ? 0 : normalized.Count(c => c == '\n');
        var endCharacter = lastNewline < 0 ? normalized.Length : normalized.Length - lastNewline - 1;
        return new
        {
            range = new
            {
                start = new { line = 0, character = 0 },
                end = new { line = endLine, character = endCharacter }
            },
            rangeLength = previousText.Length,
            text
        };
    }

    public async Task<IReadOnlyList<LspCompletionItem>> GetCompletionAsync(
        string uri, LspPosition position, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/completion", new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character }
            }, ct);

            return result is null ? [] : ParseCompletionResult(result.Value);
        }
        catch { return []; }
    }

    public async Task<LspHover?> GetHoverAsync(
        string uri, LspPosition position, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/hover", new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character }
            }, ct);

            return result is null ? null : ParseHoverResult(result.Value);
        }
        catch { return null; }
    }

    public async Task<(string Uri, int Line, int Column)?> GetDefinitionAsync(
        string uri, LspPosition position, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/definition", new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character }
            }, ct);

            if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;

            JsonElement loc;
            if (result.Value.ValueKind == JsonValueKind.Array)
            {
                loc = result.Value.EnumerateArray().FirstOrDefault();
                if (loc.ValueKind == JsonValueKind.Undefined) return null;
            }
            else if (result.Value.ValueKind == JsonValueKind.Object)
                loc = result.Value;
            else
                return null;

            // LocationLink: targetUri + targetSelectionRange
            if (loc.TryGetProperty("targetUri", out var tu) && tu.GetString() is string targetUri)
            {
                int line = 0, col = 0;
                if (loc.TryGetProperty("targetSelectionRange", out var tsr) &&
                    tsr.TryGetProperty("start", out var start))
                {
                    line = start.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    col  = start.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
                }
                return (LspUri.Normalize(targetUri), line, col);
            }

            // Location: uri + range
            if (loc.TryGetProperty("uri", out var u) && u.GetString() is string locUri)
            {
                int line = 0, col = 0;
                if (loc.TryGetProperty("range", out var range) &&
                    range.TryGetProperty("start", out var start))
                {
                    line = start.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                    col  = start.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
                }
                return (LspUri.Normalize(locUri), line, col);
            }

            return null;
        }
        catch { return null; }
    }

    public async Task<LspSignatureHelp?> GetSignatureHelpAsync(
        string uri, LspPosition position, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/signatureHelp", new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character }
            }, ct);

            return result is null || result.Value.ValueKind == JsonValueKind.Null
                ? null
                : ParseSignatureHelpResult(result.Value);
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<LspTextEdit>> GetFormattingEditsAsync(
        string uri, int tabSize, bool insertSpaces, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/formatting", new
            {
                textDocument = new { uri },
                options = new { tabSize, insertSpaces }
            }, ct);

            return ParseTextEdits(result);
        }
        catch { return []; }
    }

    public async Task<IReadOnlyList<LspTextEdit>> GetRangeFormattingEditsAsync(
        string uri, LspRange range, int tabSize, bool insertSpaces, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/rangeFormatting", new
            {
                textDocument = new { uri },
                range = new
                {
                    start = new { line = range.Start.Line, character = range.Start.Character },
                    end   = new { line = range.End.Line,   character = range.End.Character }
                },
                options = new { tabSize, insertSpaces }
            }, ct);

            return ParseTextEdits(result);
        }
        catch { return []; }
    }

    private static IReadOnlyList<LspTextEdit> ParseTextEdits(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];
        var list = new List<LspTextEdit>();
        foreach (var item in result.Value.EnumerateArray())
        {
            if (!item.TryGetProperty("range", out var rangeEl) ||
                !item.TryGetProperty("newText", out var textEl)) continue;
            list.Add(new LspTextEdit(ParseRange(rangeEl), textEl.GetString() ?? ""));
        }
        return list;
    }

    public async Task<LspWorkspaceEdit?> GetRenameAsync(
        string uri, LspPosition position, string newName, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/rename", new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character },
                newName
            }, ct);

            if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
            return ParseWorkspaceEdit(result.Value);
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(
        string query, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("workspace/symbol", new { query }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];
            var list = new List<LspSymbolInformation>();
            foreach (var item in result.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString() ?? "";
                var kind = item.TryGetProperty("kind", out var kindEl)
                    ? (SymbolKind)kindEl.GetInt32()
                    : SymbolKind.Variable;
                string? container = item.TryGetProperty("containerName", out var cn) ? cn.GetString() : null;

                if (!item.TryGetProperty("location", out var locEl)) continue;
                if (!locEl.TryGetProperty("uri", out var uriEl)) continue;
                var uri = LspUri.Normalize(uriEl.GetString() ?? "");
                var range = locEl.TryGetProperty("range", out var rangeEl)
                    ? ParseRange(rangeEl)
                    : new LspRange(new LspPosition(0, 0), new LspPosition(0, 0));

                list.Add(new LspSymbolInformation(name, kind, new LspLocation(uri, range), container));
                if (list.Count >= 200) break;
            }
            return list;
        }
        catch { return []; }
    }

    public async Task<LspWorkspaceDiagnosticResult?> GetWorkspaceDiagnosticsAsync(
        CancellationToken ct = default)
    {
        if (!SupportsWorkspaceDiagnostics) return null;

        try
        {
            var result = await _process.SendRequestAsync("workspace/diagnostic", new
            {
                previousResultIds = Array.Empty<object>()
            }, ct);

            if (result is null || result.Value.ValueKind == JsonValueKind.Null)
                return LspWorkspaceDiagnosticAggregator.CreateResult([]);

            return LspWorkspaceDiagnosticParser.Parse(result.Value);
        }
        catch { return null; }
    }

    public async Task<LspDocumentDiagnosticReport?> GetDocumentDiagnosticsAsync(
        string uri, CancellationToken ct = default)
    {
        if (!SupportsDocumentDiagnostics) return null;

        try
        {
            string? previousResultId;
            lock (_diagnosticGate) _diagnosticResultIds.TryGetValue(uri, out previousResultId);

            // identifier / previousResultId は任意。null を送ると嫌がるサーバーがあるので、
            // 値があるときだけ載せる（identifier はサーバーが宣言したときのみ返す義務がある）。
            var @params = new Dictionary<string, object>
            {
                ["textDocument"] = new { uri }
            };
            if (_diagnosticIdentifier is not null) @params["identifier"] = _diagnosticIdentifier;
            if (previousResultId is not null) @params["previousResultId"] = previousResultId;

            var result = await _process.SendRequestAsync("textDocument/diagnostic", @params, ct);

            // エラー応答も result == null として返ってくる (LspProcess.HandleMessage)。
            // textDocument/diagnostic では ServerCancelled(-32802) / ContentModified(-32801) が
            // 日常的に返るので、「診断ゼロ件」と取り違えて既存の波線を消さないよう null を返す。
            if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;

            var report = LspDocumentDiagnosticParser.Parse(result.Value);
            if (report is not null) RememberDiagnosticResultId(uri, report.ResultId);
            return report;
        }
        catch { return null; }
    }

    private void RememberDiagnosticResultId(string uri, string? resultId)
    {
        lock (_diagnosticGate)
        {
            if (resultId is null) _diagnosticResultIds.Remove(uri);
            else _diagnosticResultIds[uri] = resultId;
        }
    }

    public async Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        string uri, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/documentSymbol", new
            {
                textDocument = new { uri }
            }, ct);

            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];
            return ParseDocumentSymbols(result.Value);
        }
        catch { return []; }
    }

    private static IReadOnlyList<DocumentSymbol> ParseDocumentSymbols(JsonElement array)
    {
        var list = new List<DocumentSymbol>();
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameEl)) continue;
            var name = nameEl.GetString() ?? "";
            var kind = item.TryGetProperty("kind", out var kindEl)
                ? (SymbolKind)kindEl.GetInt32() : SymbolKind.Variable;

            // Hierarchical DocumentSymbol has "selectionRange"; flat SymbolInformation has "location"
            if (item.TryGetProperty("selectionRange", out var selRangeEl))
            {
                var range = item.TryGetProperty("range", out var rangeEl)
                    ? ParseRange(rangeEl)
                    : new LspRange(new LspPosition(0, 0), new LspPosition(0, 0));
                var selRange = ParseRange(selRangeEl);
                DocumentSymbol[]? children = null;
                if (item.TryGetProperty("children", out var childrenEl) &&
                    childrenEl.ValueKind == JsonValueKind.Array)
                    children = ParseDocumentSymbols(childrenEl).ToArray();
                list.Add(new DocumentSymbol(name, kind, range, selRange, children));
            }
            else if (item.TryGetProperty("location", out var locEl))
            {
                // SymbolInformation format — flatten into DocumentSymbol with no children
                var range = locEl.TryGetProperty("range", out var rangeEl)
                    ? ParseRange(rangeEl)
                    : new LspRange(new LspPosition(0, 0), new LspPosition(0, 0));
                list.Add(new DocumentSymbol(name, kind, range, range, null));
            }
            if (list.Count >= 500) break;
        }
        return list;
    }

    public async Task<IReadOnlyList<LspFoldingRange>> GetFoldingRangesAsync(
        string uri, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/foldingRange", new
            {
                textDocument = new { uri }
            }, ct);

            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];
            var list = new List<LspFoldingRange>();
            foreach (var item in result.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("startLine", out var startEl) ||
                    !item.TryGetProperty("endLine", out var endEl)) continue;
                string? kind = item.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
                list.Add(new LspFoldingRange(startEl.GetInt32(), endEl.GetInt32(), kind));
            }
            return list;
        }
        catch { return []; }
    }

    public async Task<IReadOnlyList<LspLocation>> GetReferencesAsync(
        string uri, LspPosition position, bool includeDeclaration = true, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/references", new
            {
                textDocument = new { uri },
                position = new { line = position.Line, character = position.Character },
                context = new { includeDeclaration }
            }, ct);

            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

            var list = new List<LspLocation>();
            foreach (var item in result.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("uri", out var uriEl) ||
                    !item.TryGetProperty("range", out var rangeEl)) continue;
                list.Add(new LspLocation(LspUri.Normalize(uriEl.GetString() ?? ""), ParseRange(rangeEl)));
            }
            return list;
        }
        catch { return []; }
    }

    public Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(
        string uri, LspRange range, CancellationToken ct = default)
        => GetCodeActionsAsync(uri, range, only: null, diagnostics: null, ct);

    public async Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(
        string uri, LspRange range, IReadOnlyList<string>? only,
        IReadOnlyList<LspDiagnostic>? diagnostics, CancellationToken ct = default)
    {
        try
        {
            object context = only is { Count: > 0 }
                ? new { diagnostics = SerializeDiagnostics(diagnostics), only = only.ToArray() }
                : new { diagnostics = SerializeDiagnostics(diagnostics) };

            var result = await _process.SendRequestAsync("textDocument/codeAction", new
            {
                textDocument = new { uri },
                range = new
                {
                    start = new { line = range.Start.Line, character = range.Start.Character },
                    end   = new { line = range.End.Line,   character = range.End.Character }
                },
                context
            }, ct);

            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

            var list = new List<LspCodeAction>();
            foreach (var item in result.Value.EnumerateArray())
                if (ParseCodeAction(item) is { } action)
                    list.Add(action);
            return list;
        }
        catch { return []; }
    }

    /// <summary>code action 1件を解釈する。旧仕様の <c>Command</c>（title + command が直下にある形）と
    /// <c>CodeAction</c> リテラルの両方を受ける。</summary>
    internal static LspCodeAction? ParseCodeAction(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("title", out var titleEl)) return null;

        var title = titleEl.GetString() ?? "";
        var kind = item.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;

        LspWorkspaceEdit? edit = null;
        if (item.TryGetProperty("edit", out var editEl))
            edit = ParseWorkspaceEdit(editEl);

        // CodeAction リテラルは command がオブジェクト、旧仕様の Command は command が文字列で
        // title/arguments と同じ階層に並ぶ。前者は入れ子を、後者は item 自身を読む。
        LspCodeActionCommand? command = item.TryGetProperty("command", out var cmdEl)
            ? cmdEl.ValueKind == JsonValueKind.Object ? ParseCommand(cmdEl) : ParseCommand(item)
            : null;

        bool preferred = item.TryGetProperty("isPreferred", out var pref) &&
                         pref.ValueKind == JsonValueKind.True;
        string? disabled = item.TryGetProperty("disabled", out var dis) &&
                           dis.ValueKind == JsonValueKind.Object &&
                           dis.TryGetProperty("reason", out var reason)
            ? reason.GetString()
            : null;

        return new LspCodeAction(title, kind, edit, command, item.GetRawText(), preferred, disabled);
    }

    private static LspCodeActionCommand? ParseCommand(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("command", out var cmdEl) ||
            cmdEl.ValueKind != JsonValueKind.String ||
            cmdEl.GetString() is not { Length: > 0 } name) return null;

        string? title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
        List<string>? args = null;
        if (el.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            args = [];
            foreach (var a in argsEl.EnumerateArray()) args.Add(a.GetRawText());
        }
        return new LspCodeActionCommand(name, title, args);
    }

    private static object[] SerializeDiagnostics(IReadOnlyList<LspDiagnostic>? diagnostics)
    {
        if (diagnostics is not { Count: > 0 }) return [];
        return [.. diagnostics.Select(d => (object)new
        {
            range = new
            {
                start = new { line = d.Range.Start.Line, character = d.Range.Start.Character },
                end   = new { line = d.Range.End.Line,   character = d.Range.End.Character }
            },
            severity = (int)d.Severity,
            message = d.Message,
            source = d.Source,
            code = d.Code
        })];
    }

    /// <summary>未解決の code action を <c>codeAction/resolve</c> で確定させる。
    /// サーバーが返した元 JSON をそのまま送り返す必要がある（<c>data</c> はサーバー固有）。</summary>
    public async Task<LspCodeAction?> ResolveCodeActionAsync(
        LspCodeAction action, CancellationToken ct = default)
    {
        if (action.RawJson is not { Length: > 0 } raw) return null;
        try
        {
            using var payload = JsonDocument.Parse(raw);
            var result = await _process.SendRequestAsync("codeAction/resolve", payload.RootElement, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Object) return null;
            return ParseCodeAction(result.Value);
        }
        catch { return null; }
    }

    public async Task<bool> ExecuteCommandAsync(
        LspCodeActionCommand command, CancellationToken ct = default)
    {
        // 引数はサーバーが返した JSON をそのまま戻す。再構成すると数値型や null が落ちる。
        var documents = new List<JsonDocument>();
        try
        {
            foreach (var json in command.ArgumentsJson ?? [])
                documents.Add(JsonDocument.Parse(json));

            await _process.SendRequestAsync("workspace/executeCommand", new
            {
                command = command.Command,
                arguments = documents.Select(d => d.RootElement).ToArray()
            }, ct);
            return true;
        }
        catch { return false; }
        finally { foreach (var d in documents) d.Dispose(); }
    }

    public async Task<IReadOnlyList<InlayHint>> GetInlayHintsAsync(
        string uri, LspRange range, CancellationToken ct = default)
    {
        if (!SupportsInlayHint) return [];
        try
        {
            var result = await _process.SendRequestAsync("textDocument/inlayHint", new
            {
                textDocument = new { uri },
                range = new
                {
                    start = new { line = range.Start.Line, character = range.Start.Character },
                    end   = new { line = range.End.Line,   character = range.End.Character }
                }
            }, ct);

            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];
            var list = new List<InlayHint>();
            foreach (var item in result.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("position", out var posEl)) continue;
                int line = posEl.TryGetProperty("line", out var lEl) ? lEl.GetInt32() : 0;
                int ch   = posEl.TryGetProperty("character", out var cEl) ? cEl.GetInt32() : 0;

                // label can be a string or array of InlayHintLabelPart
                string label = "";
                if (item.TryGetProperty("label", out var labelEl))
                {
                    if (labelEl.ValueKind == JsonValueKind.String)
                        label = labelEl.GetString() ?? "";
                    else if (labelEl.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var part in labelEl.EnumerateArray())
                        {
                            if (part.TryGetProperty("value", out var valEl))
                                sb.Append(valEl.GetString());
                        }
                        label = sb.ToString();
                    }
                }

                if (string.IsNullOrEmpty(label)) continue;

                var kind = item.TryGetProperty("kind", out var kindEl)
                    ? (InlayHintKind)kindEl.GetInt32()
                    : InlayHintKind.Type;

                list.Add(new InlayHint(new LspPosition(line, ch), label, kind));
            }
            return list;
        }
        catch { return []; }
    }

    public async Task<SemanticToken[]?> GetSemanticTokensAsync(string uri, CancellationToken ct = default)
    {
        if (!SupportsSemanticTokens || SemanticTokensLegend is null) return null;
        try
        {
            var result = await _process.SendRequestAsync("textDocument/semanticTokens/full", new
            {
                textDocument = new { uri }
            }, ct);

            if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
            if (!result.Value.TryGetProperty("data", out var dataEl) ||
                dataEl.ValueKind != JsonValueKind.Array) return null;

            var data = new List<int>(dataEl.GetArrayLength());
            foreach (var n in dataEl.EnumerateArray())
                data.Add(n.GetInt32());

            return DecodeSemanticTokens(data, SemanticTokensLegend);
        }
        catch { return null; }
    }

    private static SemanticToken[] DecodeSemanticTokens(List<int> data, SemanticTokensLegend legend)
    {
        // Each token is encoded as 5 ints: [deltaLine, deltaStartChar, length, tokenTypeIndex, tokenModifiersBitmask]
        var tokens = new List<SemanticToken>(data.Count / 5);
        int line = 0, startChar = 0;
        for (int i = 0; i + 4 < data.Count; i += 5)
        {
            int deltaLine      = data[i];
            int deltaStartChar = data[i + 1];
            int length         = data[i + 2];
            int typeIdx        = data[i + 3];
            int modsBitmask    = data[i + 4];

            line = line + deltaLine;
            startChar = deltaLine == 0 ? startChar + deltaStartChar : deltaStartChar;

            string tokenType = typeIdx >= 0 && typeIdx < legend.TokenTypes.Length
                ? legend.TokenTypes[typeIdx] : "";
            if (string.IsNullOrEmpty(tokenType)) continue;

            var mods = new List<string>();
            for (int bit = 0; bit < legend.TokenModifiers.Length; bit++)
                if ((modsBitmask & (1 << bit)) != 0)
                    mods.Add(legend.TokenModifiers[bit]);

            tokens.Add(new SemanticToken(line, startChar, length, tokenType, [.. mods]));
        }
        return [.. tokens];
    }

    public async Task<CallHierarchyItem?> PrepareCallHierarchyAsync(
        string uri, LspPosition pos, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/prepareCallHierarchy", new
            {
                textDocument = new { uri },
                position = new { line = pos.Line, character = pos.Character }
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            var first = result.Value.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined) return null;
            return ParseHierarchyItem<CallHierarchyItem>(first,
                (name, kind, itemUri, range, sel, data) =>
                    new CallHierarchyItem(name, kind, itemUri, range, sel, data));
        }
        catch { return null; }
    }

    public async Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(
        CallHierarchyItem item, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("callHierarchy/incomingCalls", new
            {
                item = SerializeHierarchyItem(
                    item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange, item.Data)
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            var list = new List<CallHierarchyIncomingCall>();
            foreach (var el in result.Value.EnumerateArray())
            {
                if (!el.TryGetProperty("from", out var fromEl)) continue;
                var from = ParseHierarchyItem<CallHierarchyItem>(fromEl,
                    (name, kind, u, r, s, data) => new CallHierarchyItem(name, kind, u, r, s, data));
                if (from is null) continue;
                var ranges = ParseFromRanges(el);
                list.Add(new CallHierarchyIncomingCall(from, ranges));
            }
            return [.. list];
        }
        catch { return null; }
    }

    public async Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(
        CallHierarchyItem item, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("callHierarchy/outgoingCalls", new
            {
                item = SerializeHierarchyItem(
                    item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange, item.Data)
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            var list = new List<CallHierarchyOutgoingCall>();
            foreach (var el in result.Value.EnumerateArray())
            {
                if (!el.TryGetProperty("to", out var toEl)) continue;
                var to = ParseHierarchyItem<CallHierarchyItem>(toEl,
                    (name, kind, u, r, s, data) => new CallHierarchyItem(name, kind, u, r, s, data));
                if (to is null) continue;
                var ranges = ParseFromRanges(el);
                list.Add(new CallHierarchyOutgoingCall(to, ranges));
            }
            return [.. list];
        }
        catch { return null; }
    }

    public async Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(
        string uri, LspPosition pos, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/prepareTypeHierarchy", new
            {
                textDocument = new { uri },
                position = new { line = pos.Line, character = pos.Character }
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            var first = result.Value.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Undefined) return null;
            return ParseHierarchyItem<TypeHierarchyItem>(first,
                (name, kind, itemUri, range, sel, data) =>
                    new TypeHierarchyItem(name, kind, itemUri, range, sel, data));
        }
        catch { return null; }
    }

    public async Task<TypeHierarchyItem[]?> GetSupertypesAsync(
        TypeHierarchyItem item, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("typeHierarchy/supertypes", new
            {
                item = SerializeHierarchyItem(
                    item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange, item.Data)
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            return ParseTypeHierarchyItems(result.Value);
        }
        catch { return null; }
    }

    public async Task<TypeHierarchyItem[]?> GetSubtypesAsync(
        TypeHierarchyItem item, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("typeHierarchy/subtypes", new
            {
                item = SerializeHierarchyItem(
                    item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange, item.Data)
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            return ParseTypeHierarchyItems(result.Value);
        }
        catch { return null; }
    }

    private static T? ParseHierarchyItem<T>(JsonElement el,
        Func<string, int, string, LspRange, LspRange, JsonElement?, T> factory) where T : class
    {
        if (!el.TryGetProperty("name", out var nameEl)) return null;
        var name = nameEl.GetString() ?? "";
        var kind = el.TryGetProperty("kind", out var kindEl) ? kindEl.GetInt32() : 0;
        var itemUri = el.TryGetProperty("uri", out var uriEl) ? LspUri.Normalize(uriEl.GetString() ?? "") : "";
        var range = el.TryGetProperty("range", out var rangeEl)
            ? ParseRange(rangeEl)
            : new LspRange(new LspPosition(0, 0), new LspPosition(0, 0));
        var sel = el.TryGetProperty("selectionRange", out var selEl)
            ? ParseRange(selEl)
            : range;
        var data = el.TryGetProperty("data", out var dataEl) ? dataEl.Clone() : (JsonElement?)null;
        return factory(name, kind, itemUri, range, sel, data);
    }

    private static TypeHierarchyItem[] ParseTypeHierarchyItems(JsonElement array)
    {
        var list = new List<TypeHierarchyItem>();
        foreach (var el in array.EnumerateArray())
        {
            var item = ParseHierarchyItem<TypeHierarchyItem>(el,
                (name, kind, u, r, s, data) => new TypeHierarchyItem(name, kind, u, r, s, data));
            if (item is not null) list.Add(item);
        }
        return [.. list];
    }

    private static LspRange[] ParseFromRanges(JsonElement el)
    {
        if (!el.TryGetProperty("fromRanges", out var frEl) || frEl.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<LspRange>();
        foreach (var r in frEl.EnumerateArray())
            list.Add(ParseRange(r));
        return [.. list];
    }

    internal static object SerializeHierarchyItem(
        string name, int kind, string uri, LspRange range, LspRange selRange, JsonElement? data) =>
        new
        {
            name,
            kind,
            uri,
            range = new
            {
                start = new { line = range.Start.Line, character = range.Start.Character },
                end   = new { line = range.End.Line,   character = range.End.Character }
            },
            selectionRange = new
            {
                start = new { line = selRange.Start.Line, character = selRange.Start.Character },
                end   = new { line = selRange.End.Line,   character = selRange.End.Character }
            },
            data
        };

    private void OnNotification(string method, JsonElement @params)
    {
        if (method != "textDocument/publishDiagnostics") return;
        try
        {
            // サーバーが返す URI はドライブのコロンが %3A 符号化されていることがある（tsserver 等）。
            // ここで正規化しておかないと、ホスト側の「どの文書の診断か」照合が全て外れる。
            var uri = LspUri.Normalize(@params.GetProperty("uri").GetString() ?? "");
            var diags = new List<LspDiagnostic>();
            foreach (var d in @params.GetProperty("diagnostics").EnumerateArray())
            {
                if (TryParseDiagnostic(d, out var diagnostic))
                    diags.Add(diagnostic);
            }
            DiagnosticsChanged?.Invoke(this, new DiagnosticsChangedEventArgs(uri, diags));
        }
        catch { }
    }

    private static bool TryParseDiagnostic(JsonElement el, out LspDiagnostic diagnostic)
    {
        diagnostic = new LspDiagnostic(
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
            "",
            DiagnosticSeverity.Error);

        try
        {
            if (!el.TryGetProperty("range", out var rangeEl))
                return false;

            var range = ParseRange(rangeEl);
            var message = el.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var severity = el.TryGetProperty("severity", out var s) &&
                s.ValueKind == JsonValueKind.Number
                    ? (DiagnosticSeverity)s.GetInt32()
                    : DiagnosticSeverity.Error;
            var source = el.TryGetProperty("source", out var src) ? src.GetString() : null;
            string? code = null;
            if (el.TryGetProperty("code", out var codeElement))
                code = codeElement.ValueKind switch
                {
                    JsonValueKind.String => codeElement.GetString(),
                    JsonValueKind.Number => codeElement.GetRawText(),
                    _ => null,
                };
            diagnostic = new LspDiagnostic(range, message, severity, source, code);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static LspWorkspaceEdit? ParseWorkspaceEdit(JsonElement el)
    {
        // キーはサーバーが返した URI そのままではなく正規化した形にする（%3A 問題、§LspUri）。
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(LspUri.Comparer);
        var versions = new Dictionary<string, int?>(LspUri.Comparer);

        // "changes": { "file:///...": [ {range, newText}, ... ] }
        if (el.TryGetProperty("changes", out var changesEl) &&
            changesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in changesEl.EnumerateObject())
            {
                var edits = new List<LspTextEdit>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    foreach (var e in prop.Value.EnumerateArray())
                        if (e.TryGetProperty("range", out var r) && e.TryGetProperty("newText", out var t))
                            edits.Add(new LspTextEdit(ParseRange(r), t.GetString() ?? ""));
                changes[LspUri.Normalize(prop.Name)] = edits;
            }
        }
        // "documentChanges": [ { textDocument: {uri}, edits: [...] } | {kind: "create"|"rename"|"delete"} ]
        // ファイル操作は「クラスに抽出」「型をファイルへ移動」で本文の編集と一緒に来るので、
        // ここで落とすと新しいファイルが生まれず、そこへの編集だけが宙に浮く。
        // （LSP 上 changes と documentChanges は排他。両方あるサーバーは documentChanges を正とする
        //  仕様なので、changes を読めたときはこちらへ来ない。）
        var fileOperations = new List<LspFileOperation>();
        if (changes.Count == 0 &&
            el.TryGetProperty("documentChanges", out var dcEl) &&
            dcEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var dc in dcEl.EnumerateArray())
            {
                if (dc.ValueKind != JsonValueKind.Object) continue;

                if (dc.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String)
                {
                    if (ParseFileOperation(dc, kindEl.GetString()) is { } operation)
                        fileOperations.Add(operation);
                    continue;
                }

                if (!dc.TryGetProperty("textDocument", out var td) ||
                    !td.TryGetProperty("uri", out var uriEl)) continue;
                var fileUri = LspUri.Normalize(uriEl.GetString() ?? "");
                int? version = td.TryGetProperty("version", out var versionEl) &&
                               versionEl.ValueKind == JsonValueKind.Number
                    ? versionEl.GetInt32()
                    : null;
                var edits = new List<LspTextEdit>();
                if (dc.TryGetProperty("edits", out var editsEl) &&
                    editsEl.ValueKind == JsonValueKind.Array)
                    foreach (var e in editsEl.EnumerateArray())
                        if (e.TryGetProperty("range", out var r) && e.TryGetProperty("newText", out var t))
                            edits.Add(new LspTextEdit(ParseRange(r), t.GetString() ?? ""));
                // 同一 URI が複数回現れる（作成→追記）ことがあるので上書きせず連結する。
                changes[fileUri] = changes.TryGetValue(fileUri, out var existing)
                    ? [.. existing, .. edits]
                    : edits;
                versions[fileUri] = version;
            }
        }

        return changes.Count == 0 && fileOperations.Count == 0
            ? null
            : new LspWorkspaceEdit(changes, versions, fileOperations);
    }

    private static LspFileOperation? ParseFileOperation(JsonElement el, string? kind)
    {
        bool Option(string name) =>
            el.TryGetProperty("options", out var options) &&
            options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.True;

        string? Uri(string name) =>
            el.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
                ? LspUri.Normalize(text)
                : null;

        return kind switch
        {
            "create" when Uri("uri") is { } uri =>
                new LspFileOperation(LspFileOperationKind.Create, uri,
                    Overwrite: Option("overwrite"), IgnoreIfExists: Option("ignoreIfExists")),
            "rename" when Uri("oldUri") is { } oldUri && Uri("newUri") is { } newUri =>
                new LspFileOperation(LspFileOperationKind.Rename, oldUri, newUri,
                    Overwrite: Option("overwrite"), IgnoreIfExists: Option("ignoreIfExists")),
            "delete" when Uri("uri") is { } uri =>
                new LspFileOperation(LspFileOperationKind.Delete, uri,
                    Recursive: Option("recursive"), IgnoreIfNotExists: Option("ignoreIfNotExists")),
            _ => null,
        };
    }

    private static LspRange ParseRange(JsonElement el)
    {
        var s = el.GetProperty("start");
        var e = el.GetProperty("end");
        return new LspRange(
            new LspPosition(s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32()),
            new LspPosition(e.GetProperty("line").GetInt32(), e.GetProperty("character").GetInt32()));
    }

    internal static IReadOnlyList<LspCompletionItem> ParseCompletionResult(JsonElement result)
    {
        JsonElement items;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var listItems))
            items = listItems;
        else if (result.ValueKind == JsonValueKind.Array)
            items = result;
        else
            return [];

        var list = new List<LspCompletionItem>();
        foreach (var item in items.EnumerateArray())
        {
            var label = item.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            var kind = item.TryGetProperty("kind", out var k) ? (CompletionItemKind)k.GetInt32() : CompletionItemKind.Text;
            var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var insertText = item.TryGetProperty("insertText", out var ins) ? ins.GetString() : null;
            var filterText = item.TryGetProperty("filterText", out var ft) ? ft.GetString() : null;
            var sortText = item.TryGetProperty("sortText", out var st) ? st.GetString() : null;
            var preselect = item.TryGetProperty("preselect", out var ps) && ps.ValueKind == JsonValueKind.True;
            var deprecated = item.TryGetProperty("deprecated", out var dep) && dep.ValueKind == JsonValueKind.True;
            var textFormat = item.TryGetProperty("insertTextFormat", out var itf) && itf.GetInt32() == 2
                ? Editor.Core.Lsp.InsertTextFormat.Snippet
                : Editor.Core.Lsp.InsertTextFormat.PlainText;
            string? documentation = null;
            if (item.TryGetProperty("documentation", out var doc))
            {
                if (doc.ValueKind == JsonValueKind.String)
                    documentation = doc.GetString();
                else if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("value", out var docVal))
                    documentation = docVal.GetString();
            }
            LspTextEdit? textEdit = null;
            if (item.TryGetProperty("textEdit", out var te) && te.ValueKind == JsonValueKind.Object &&
                te.TryGetProperty("range", out var range) && te.TryGetProperty("newText", out var newText))
                textEdit = new LspTextEdit(ParseRange(range), newText.GetString() ?? "");
            IReadOnlyList<string>? commitCharacters = null;
            if (item.TryGetProperty("commitCharacters", out var cc) && cc.ValueKind == JsonValueKind.Array)
                commitCharacters = cc.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList();
            list.Add(new LspCompletionItem(label, kind, detail, insertText ?? label, filterText, documentation,
                textFormat, sortText, preselect, textEdit, commitCharacters, deprecated));
            if (list.Count >= 500) break;
        }
        return list;
    }

    private static LspSignatureHelp? ParseSignatureHelpResult(JsonElement result)
    {
        if (!result.TryGetProperty("signatures", out var sigsEl) ||
            sigsEl.ValueKind != JsonValueKind.Array) return null;

        int activeSig = result.TryGetProperty("activeSignature", out var asEl) ? asEl.GetInt32() : 0;
        int activeParam = result.TryGetProperty("activeParameter", out var apEl) ? apEl.GetInt32() : 0;

        var sigs = new List<LspSignatureInfo>();
        foreach (var sigEl in sigsEl.EnumerateArray())
        {
            var label = sigEl.TryGetProperty("label", out var lEl) ? lEl.GetString() ?? "" : "";
            var doc = sigEl.TryGetProperty("documentation", out var dEl)
                ? (dEl.ValueKind == JsonValueKind.String ? dEl.GetString()
                   : dEl.TryGetProperty("value", out var dv) ? dv.GetString() : null)
                : null;

            var parms = new List<LspParameterInfo>();
            if (sigEl.TryGetProperty("parameters", out var parmsEl) &&
                parmsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var pEl in parmsEl.EnumerateArray())
                {
                    string? pLabel = null;
                    if (pEl.TryGetProperty("label", out var plEl))
                        pLabel = plEl.ValueKind == JsonValueKind.String ? plEl.GetString() : null;
                    parms.Add(new LspParameterInfo(pLabel));
                }
            }
            sigs.Add(new LspSignatureInfo(label, doc, parms));
        }

        return sigs.Count == 0 ? null : new LspSignatureHelp(sigs, activeSig, activeParam);
    }

    private static LspHover? ParseHoverResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Null) return null;
        if (!result.TryGetProperty("contents", out var contents)) return null;

        if (contents.ValueKind == JsonValueKind.String)
            return new LspHover(contents.GetString() ?? "");

        if (contents.ValueKind == JsonValueKind.Object && contents.TryGetProperty("value", out var val))
            return new LspHover(val.GetString() ?? "");

        if (contents.ValueKind == JsonValueKind.Array)
        {
            var first = contents.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.String) return new LspHover(first.GetString() ?? "");
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out var v))
                return new LspHover(v.GetString() ?? "");
        }
        return null;
    }

    public async Task<IReadOnlyList<DocumentHighlight>?> RequestDocumentHighlightAsync(
        string uri, int line, int character, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/documentHighlight", new
            {
                textDocument = new { uri },
                position = new { line, character }
            }, ct);

            if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
            if (result.Value.ValueKind != JsonValueKind.Array) return null;

            var list = new List<DocumentHighlight>();
            foreach (var item in result.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("range", out var rangeEl)) continue;
                var range = ParseRange(rangeEl);
                var kind = item.TryGetProperty("kind", out var kindEl)
                    ? (DocumentHighlightKind)kindEl.GetInt32()
                    : DocumentHighlightKind.Text;
                list.Add(new DocumentHighlight(range, kind));
            }
            return list;
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<LspSelectionRange>?> RequestSelectionRangesAsync(
        string uri, IReadOnlyList<LspPosition> positions, CancellationToken ct = default)
    {
        if (!SupportsSelectionRange || positions.Count == 0) return null;
        try
        {
            var result = await _process.SendRequestAsync("textDocument/selectionRange", new
            {
                textDocument = new { uri },
                positions = positions.Select(p => new { line = p.Line, character = p.Character }).ToArray()
            }, ct);

            if (result is null || result.Value.ValueKind == JsonValueKind.Null) return null;
            if (result.Value.ValueKind != JsonValueKind.Array) return null;

            var list = new List<LspSelectionRange>();
            foreach (var item in result.Value.EnumerateArray())
            {
                var range = ParseSelectionRange(item);
                if (range is not null)
                    list.Add(range);
            }

            return list;
        }
        catch { return null; }
    }

    private static LspSelectionRange? ParseSelectionRange(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("range", out var rangeEl))
            return null;

        var parent = el.TryGetProperty("parent", out var parentEl)
            ? ParseSelectionRange(parentEl)
            : null;

        return new LspSelectionRange(ParseRange(rangeEl), parent);
    }

    public void Dispose() => _process.Dispose();
}
