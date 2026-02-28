namespace Editor.Core.Lsp;

public record LspPosition(int Line, int Character);
public record LspRange(LspPosition Start, LspPosition End);

public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

public record LspDiagnostic(
    LspRange Range,
    string Message,
    DiagnosticSeverity Severity,
    string? Source = null);

public enum CompletionItemKind
{
    Text = 1, Method = 2, Function = 3, Constructor = 4, Field = 5,
    Variable = 6, Class = 7, Interface = 8, Module = 9, Property = 10,
    Unit = 11, Value = 12, Enum = 13, Keyword = 14, Snippet = 15,
    Color = 16, File = 17, Reference = 18
}

public record LspCompletionItem(
    string Label,
    CompletionItemKind Kind = CompletionItemKind.Text,
    string? Detail = null,
    string? InsertText = null,
    string? FilterText = null);

public record LspHover(string Value);

// Signature Help
public record LspParameterInfo(string? Label);
public record LspSignatureInfo(string Label, string? Documentation, IReadOnlyList<LspParameterInfo> Parameters);
public record LspSignatureHelp(IReadOnlyList<LspSignatureInfo> Signatures, int ActiveSignature, int ActiveParameter);

// Text edits (for formatting)
public record LspTextEdit(LspRange Range, string NewText);
