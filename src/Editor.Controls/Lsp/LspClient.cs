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
                    definition = new { },
                    signatureHelp = new { signatureInformation = new { documentationFormat = new[] { "plaintext" } } },
                    formatting = new { },
                    rename = new { },
                    references = new { }
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
                return (targetUri, line, col);
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
                return (locUri, line, col);
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

            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];
            var list = new List<LspTextEdit>();
            foreach (var item in result.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("range", out var rangeEl) ||
                    !item.TryGetProperty("newText", out var textEl)) continue;
                var range = ParseRange(rangeEl);
                list.Add(new LspTextEdit(range, textEl.GetString() ?? ""));
            }
            return list;
        }
        catch { return []; }
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
                list.Add(new LspLocation(uriEl.GetString() ?? "", ParseRange(rangeEl)));
            }
            return list;
        }
        catch { return []; }
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

    private static LspWorkspaceEdit? ParseWorkspaceEdit(JsonElement el)
    {
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>();

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
                changes[prop.Name] = edits;
            }
        }
        // "documentChanges": [ { textDocument: {uri}, edits: [...] } ]
        else if (el.TryGetProperty("documentChanges", out var dcEl) &&
                 dcEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var dc in dcEl.EnumerateArray())
            {
                if (!dc.TryGetProperty("textDocument", out var td) ||
                    !td.TryGetProperty("uri", out var uriEl)) continue;
                var fileUri = uriEl.GetString() ?? "";
                var edits = new List<LspTextEdit>();
                if (dc.TryGetProperty("edits", out var editsEl) &&
                    editsEl.ValueKind == JsonValueKind.Array)
                    foreach (var e in editsEl.EnumerateArray())
                        if (e.TryGetProperty("range", out var r) && e.TryGetProperty("newText", out var t))
                            edits.Add(new LspTextEdit(ParseRange(r), t.GetString() ?? ""));
                changes[fileUri] = edits;
            }
        }

        return changes.Count == 0 ? null : new LspWorkspaceEdit(changes);
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

    public void Dispose() => _process.Dispose();
}
