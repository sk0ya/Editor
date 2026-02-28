using System.Text.Json;
using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>Full LSP client implementation using JSON-RPC 2.0 over stdio.</summary>
public sealed class LspClient : ILspClient
{
    private readonly LspProcess _process;

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;
    public bool IsRunning => _process.IsRunning;

    public LspClient(string executable, IEnumerable<string> args, string? workingDir = null)
    {
        _process = new LspProcess(executable, args, workingDir);
        _process.NotificationReceived += OnNotification;
    }

    public async Task InitializeAsync(string rootUri)
    {
        await _process.SendRequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    synchronization = new { openClose = true, change = 1 },
                    publishDiagnostics = new { relatedInformation = false },
                    completion = new { completionItem = new { snippetSupport = false } },
                    hover = new { contentFormat = new[] { "plaintext", "markdown" } },
                    definition = new { }
                }
            },
            workspaceFolders = (object?)null
        });
        _process.SendNotification("initialized", new { });
    }

    public Task OpenDocumentAsync(string uri, string languageId, string text)
    {
        _process.SendNotification("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId, version = 1, text }
        });
        return Task.CompletedTask;
    }

    public Task ChangeDocumentAsync(string uri, int version, string text)
    {
        _process.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } }
        });
        return Task.CompletedTask;
    }

    public Task CloseDocumentAsync(string uri)
    {
        _process.SendNotification("textDocument/didClose", new { textDocument = new { uri } });
        return Task.CompletedTask;
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

    public async Task<string?> GetDefinitionUriAsync(
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

            if (result.Value.ValueKind == JsonValueKind.Array)
            {
                var first = result.Value.EnumerateArray().FirstOrDefault();
                return first.TryGetProperty("uri", out var u) ? u.GetString() : null;
            }
            if (result.Value.ValueKind == JsonValueKind.Object)
                return result.Value.TryGetProperty("uri", out var u) ? u.GetString() : null;

            return null;
        }
        catch { return null; }
    }

    private void OnNotification(string method, JsonElement @params)
    {
        if (method != "textDocument/publishDiagnostics") return;
        try
        {
            var uri = @params.GetProperty("uri").GetString() ?? "";
            var diags = new List<LspDiagnostic>();
            foreach (var d in @params.GetProperty("diagnostics").EnumerateArray())
            {
                var range = ParseRange(d.GetProperty("range"));
                var message = d.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var severity = d.TryGetProperty("severity", out var s)
                    ? (DiagnosticSeverity)s.GetInt32()
                    : DiagnosticSeverity.Error;
                var source = d.TryGetProperty("source", out var src) ? src.GetString() : null;
                diags.Add(new LspDiagnostic(range, message, severity, source));
            }
            DiagnosticsChanged?.Invoke(this, new DiagnosticsChangedEventArgs(uri, diags));
        }
        catch { }
    }

    private static LspRange ParseRange(JsonElement el)
    {
        var s = el.GetProperty("start");
        var e = el.GetProperty("end");
        return new LspRange(
            new LspPosition(s.GetProperty("line").GetInt32(), s.GetProperty("character").GetInt32()),
            new LspPosition(e.GetProperty("line").GetInt32(), e.GetProperty("character").GetInt32()));
    }

    private static IReadOnlyList<LspCompletionItem> ParseCompletionResult(JsonElement result)
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
            list.Add(new LspCompletionItem(label, kind, detail, insertText ?? label, filterText));
            if (list.Count >= 500) break;
        }
        return list;
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

    public void Dispose() => _process.Dispose();
}
