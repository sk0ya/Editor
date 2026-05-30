using System.Text.Json;

namespace Editor.Core.Lsp;

public record LspPosition(int Line, int Character);
public record LspRange(LspPosition Start, LspPosition End);

public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

public record LspDiagnostic(
    LspRange Range,
    string Message,
    DiagnosticSeverity Severity,
    string? Source = null);

public record LspWorkspaceDiagnosticDocument(
    string Uri,
    int? Version,
    IReadOnlyList<LspDiagnostic> Diagnostics);

public record LspWorkspaceDiagnosticSummary(
    int DocumentCount,
    int DiagnosticCount,
    int ErrorCount,
    int WarningCount,
    int InformationCount,
    int HintCount);

public record LspWorkspaceDiagnosticResult(
    IReadOnlyList<LspWorkspaceDiagnosticDocument> Documents,
    LspWorkspaceDiagnosticSummary Summary);

public static class LspWorkspaceDiagnosticAggregator
{
    public static LspWorkspaceDiagnosticResult CreateResult(IEnumerable<LspWorkspaceDiagnosticDocument> documents)
    {
        var ordered = documents
            .Where(d => !string.IsNullOrWhiteSpace(d.Uri))
            .GroupBy(d => d.Uri, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(d => d.Uri, StringComparer.OrdinalIgnoreCase)
            .Select(d => d with
            {
                Diagnostics = d.Diagnostics
                    .OrderBy(x => x.Range.Start.Line)
                    .ThenBy(x => x.Range.Start.Character)
                    .ToArray()
            })
            .ToArray();

        var summary = new LspWorkspaceDiagnosticSummary(
            ordered.Length,
            ordered.Sum(d => d.Diagnostics.Count),
            CountSeverity(ordered, DiagnosticSeverity.Error),
            CountSeverity(ordered, DiagnosticSeverity.Warning),
            CountSeverity(ordered, DiagnosticSeverity.Information),
            CountSeverity(ordered, DiagnosticSeverity.Hint));

        return new LspWorkspaceDiagnosticResult(ordered, summary);
    }

    private static int CountSeverity(
        IReadOnlyList<LspWorkspaceDiagnosticDocument> documents,
        DiagnosticSeverity severity) =>
        documents.Sum(d => d.Diagnostics.Count(x => x.Severity == severity));
}

public static class LspCapabilityParser
{
    public static bool SupportsWorkspaceDiagnostics(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("diagnosticProvider", out var diagnosticProvider))
            return false;

        return diagnosticProvider.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Object when diagnosticProvider.TryGetProperty("workspaceDiagnostics", out var workspaceDiagnostics) =>
                workspaceDiagnostics.ValueKind == JsonValueKind.True,
            _ => false
        };
    }
}

public static class LspWorkspaceDiagnosticParser
{
    public static LspWorkspaceDiagnosticResult Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("items", out var itemsEl) ||
            itemsEl.ValueKind != JsonValueKind.Array)
            return LspWorkspaceDiagnosticAggregator.CreateResult([]);

        var documents = new List<LspWorkspaceDiagnosticDocument>();
        foreach (var item in itemsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("uri", out var uriEl))
                continue;

            var uri = uriEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(uri))
                continue;

            if (item.TryGetProperty("kind", out var kindEl) &&
                kindEl.ValueKind == JsonValueKind.String &&
                string.Equals(kindEl.GetString(), "unchanged", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!item.TryGetProperty("items", out var diagnosticsEl) ||
                diagnosticsEl.ValueKind != JsonValueKind.Array)
                continue;

            int? version = item.TryGetProperty("version", out var versionEl) &&
                versionEl.ValueKind == JsonValueKind.Number
                    ? versionEl.GetInt32()
                    : null;

            var diagnostics = new List<LspDiagnostic>();
            foreach (var diagnosticEl in diagnosticsEl.EnumerateArray())
            {
                if (TryParseDiagnostic(diagnosticEl, out var diagnostic))
                    diagnostics.Add(diagnostic);
            }

            documents.Add(new LspWorkspaceDiagnosticDocument(uri, version, diagnostics));
        }

        return LspWorkspaceDiagnosticAggregator.CreateResult(documents);
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
            var message = el.TryGetProperty("message", out var messageEl) ? messageEl.GetString() ?? "" : "";
            var severity = el.TryGetProperty("severity", out var severityEl) &&
                severityEl.ValueKind == JsonValueKind.Number
                    ? (DiagnosticSeverity)severityEl.GetInt32()
                    : DiagnosticSeverity.Error;
            var source = el.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : null;
            diagnostic = new LspDiagnostic(range, message, severity, source);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LspRange ParseRange(JsonElement el)
    {
        var start = el.GetProperty("start");
        var end = el.GetProperty("end");
        return new LspRange(
            new LspPosition(start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32()),
            new LspPosition(end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32()));
    }
}

public enum CompletionItemKind
{
    Text = 1, Method = 2, Function = 3, Constructor = 4, Field = 5,
    Variable = 6, Class = 7, Interface = 8, Module = 9, Property = 10,
    Unit = 11, Value = 12, Enum = 13, Keyword = 14, Snippet = 15,
    Color = 16, File = 17, Reference = 18
}

/// <summary>LSP insertTextFormat: 1 = PlainText, 2 = Snippet</summary>
public enum InsertTextFormat { PlainText = 1, Snippet = 2 }

public record LspCompletionItem(
    string Label,
    CompletionItemKind Kind = CompletionItemKind.Text,
    string? Detail = null,
    string? InsertText = null,
    string? FilterText = null,
    string? Documentation = null,
    InsertTextFormat TextFormat = InsertTextFormat.PlainText);

public record LspHover(string Value);

// Signature Help
public record LspParameterInfo(string? Label);
public record LspSignatureInfo(string Label, string? Documentation, IReadOnlyList<LspParameterInfo> Parameters);
public record LspSignatureHelp(IReadOnlyList<LspSignatureInfo> Signatures, int ActiveSignature, int ActiveParameter);

// Text edits (for formatting / rename)
public record LspTextEdit(LspRange Range, string NewText);

// Location (for find references)
public record LspLocation(string Uri, LspRange Range);

// Workspace edit (for rename — maps file URI → list of edits)
public record LspWorkspaceEdit(IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Changes);

// Folding ranges
public record LspFoldingRange(int StartLine, int EndLine, string? Kind = null);

// Code actions
public record LspCodeAction(string Title, string? Kind, LspWorkspaceEdit? Edit);

// Inlay hints
public enum InlayHintKind { Type = 1, Parameter = 2 }
public record InlayHint(LspPosition Position, string Label, InlayHintKind Kind);

// Document symbols (hierarchical, returned by textDocument/documentSymbol)
public record DocumentSymbol(
    string Name,
    SymbolKind Kind,
    LspRange Range,
    LspRange SelectionRange,
    DocumentSymbol[]? Children);

// Call hierarchy
public record CallHierarchyItem(string Name, int Kind, string Uri, LspRange Range, LspRange SelectionRange);
public record CallHierarchyIncomingCall(CallHierarchyItem From, LspRange[] FromRanges);
public record CallHierarchyOutgoingCall(CallHierarchyItem To, LspRange[] FromRanges);

// Type hierarchy
public record TypeHierarchyItem(string Name, int Kind, string Uri, LspRange Range, LspRange SelectionRange);

// Semantic tokens
public record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);
public record SemanticToken(int Line, int StartChar, int Length, string TokenType, string[] Modifiers);

// Document highlight
public enum DocumentHighlightKind { Text = 1, Read = 2, Write = 3 }
public record DocumentHighlight(LspRange Range, DocumentHighlightKind Kind);

// Selection ranges (returned by textDocument/selectionRange)
public record LspSelectionRange(LspRange Range, LspSelectionRange? Parent = null);

// Workspace symbols
public enum SymbolKind
{
    File = 1, Module = 2, Namespace = 3, Package = 4,
    Class = 5, Method = 6, Property = 7, Field = 8,
    Constructor = 9, Enum = 10, Interface = 11, Function = 12,
    Variable = 13, Constant = 14, String = 15, Number = 16,
    Boolean = 17, Array = 18, Object = 19, Key = 20,
    Null = 21, EnumMember = 22, Struct = 23, Event = 24,
    Operator = 25, TypeParameter = 26
}

public record LspSymbolInformation(
    string Name,
    SymbolKind Kind,
    LspLocation Location,
    string? ContainerName = null);
