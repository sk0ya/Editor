using System.Text.Json;
using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>Full LSP client implementation using JSON-RPC 2.0 over stdio.</summary>
public sealed class LspClient : ILspClient
{
    private readonly LspProcess _process;

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;
    public bool IsRunning => _process.IsRunning;
    public bool SupportsFoldingRange { get; private set; }
    public bool SupportsWorkspaceSymbol { get; private set; }
    public bool SupportsInlayHint { get; private set; }
    public bool SupportsSemanticTokens { get; private set; }
    public SemanticTokensLegend? SemanticTokensLegend { get; private set; }

    public LspClient(string executable, IEnumerable<string> args, string? workingDir = null)
    {
        _process = new LspProcess(executable, args, workingDir);
        _process.NotificationReceived += OnNotification;
    }

    public async Task InitializeAsync(string rootUri)
    {
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
                    rename = new { },
                    references = new { },
                    foldingRange = new { },
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
                    symbol = new { }
                }
            },
            workspaceFolders = (object?)null
        });
        _process.SendNotification("initialized", new { });

        // サーバーの capabilities を解析して対応機能を確定する
        if (result.HasValue &&
            result.Value.ValueKind == JsonValueKind.Object &&
            result.Value.TryGetProperty("capabilities", out var caps))
        {
            if (caps.TryGetProperty("foldingRangeProvider", out var frp))
                SupportsFoldingRange = frp.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            if (caps.TryGetProperty("workspaceSymbolProvider", out var wsp))
                SupportsWorkspaceSymbol = wsp.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            if (caps.TryGetProperty("inlayHintProvider", out var ihp))
                SupportsInlayHint = ihp.ValueKind is JsonValueKind.True or JsonValueKind.Object;
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

    private static string[] ParseStringArray(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.GetString() is string s) list.Add(s);
        return [.. list];
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
                var uri = uriEl.GetString() ?? "";
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
                list.Add(new LspLocation(uriEl.GetString() ?? "", ParseRange(rangeEl)));
            }
            return list;
        }
        catch { return []; }
    }

    public async Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(
        string uri, LspRange range, CancellationToken ct = default)
    {
        try
        {
            var result = await _process.SendRequestAsync("textDocument/codeAction", new
            {
                textDocument = new { uri },
                range = new
                {
                    start = new { line = range.Start.Line, character = range.Start.Character },
                    end   = new { line = range.End.Line,   character = range.End.Character }
                },
                context = new { diagnostics = Array.Empty<object>() }
            }, ct);

            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return [];

            var list = new List<LspCodeAction>();
            foreach (var item in result.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("title", out var titleEl)) continue;
                var title = titleEl.GetString() ?? "";
                var kind = item.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
                LspWorkspaceEdit? edit = null;
                if (item.TryGetProperty("edit", out var editEl))
                    edit = ParseWorkspaceEdit(editEl);
                list.Add(new LspCodeAction(title, kind, edit));
            }
            return list;
        }
        catch { return []; }
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
                (name, kind, itemUri, range, sel) => new CallHierarchyItem(name, kind, itemUri, range, sel));
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
                item = SerializeHierarchyItem(item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange)
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            var list = new List<CallHierarchyIncomingCall>();
            foreach (var el in result.Value.EnumerateArray())
            {
                if (!el.TryGetProperty("from", out var fromEl)) continue;
                var from = ParseHierarchyItem<CallHierarchyItem>(fromEl,
                    (name, kind, u, r, s) => new CallHierarchyItem(name, kind, u, r, s));
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
                item = SerializeHierarchyItem(item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange)
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            var list = new List<CallHierarchyOutgoingCall>();
            foreach (var el in result.Value.EnumerateArray())
            {
                if (!el.TryGetProperty("to", out var toEl)) continue;
                var to = ParseHierarchyItem<CallHierarchyItem>(toEl,
                    (name, kind, u, r, s) => new CallHierarchyItem(name, kind, u, r, s));
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
                (name, kind, itemUri, range, sel) => new TypeHierarchyItem(name, kind, itemUri, range, sel));
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
                item = SerializeHierarchyItem(item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange)
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
                item = SerializeHierarchyItem(item.Name, item.Kind, item.Uri, item.Range, item.SelectionRange)
            }, ct);
            if (result is null || result.Value.ValueKind != JsonValueKind.Array) return null;
            return ParseTypeHierarchyItems(result.Value);
        }
        catch { return null; }
    }

    private static T? ParseHierarchyItem<T>(JsonElement el,
        Func<string, int, string, LspRange, LspRange, T> factory) where T : class
    {
        if (!el.TryGetProperty("name", out var nameEl)) return null;
        var name = nameEl.GetString() ?? "";
        var kind = el.TryGetProperty("kind", out var kindEl) ? kindEl.GetInt32() : 0;
        var itemUri = el.TryGetProperty("uri", out var uriEl) ? uriEl.GetString() ?? "" : "";
        var range = el.TryGetProperty("range", out var rangeEl)
            ? ParseRange(rangeEl)
            : new LspRange(new LspPosition(0, 0), new LspPosition(0, 0));
        var sel = el.TryGetProperty("selectionRange", out var selEl)
            ? ParseRange(selEl)
            : range;
        return factory(name, kind, itemUri, range, sel);
    }

    private static TypeHierarchyItem[] ParseTypeHierarchyItems(JsonElement array)
    {
        var list = new List<TypeHierarchyItem>();
        foreach (var el in array.EnumerateArray())
        {
            var item = ParseHierarchyItem<TypeHierarchyItem>(el,
                (name, kind, u, r, s) => new TypeHierarchyItem(name, kind, u, r, s));
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

    private static object SerializeHierarchyItem(string name, int kind, string uri, LspRange range, LspRange selRange) =>
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
            }
        };

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
            list.Add(new LspCompletionItem(label, kind, detail, insertText ?? label, filterText, documentation, textFormat));
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

    public void Dispose() => _process.Dispose();
}
