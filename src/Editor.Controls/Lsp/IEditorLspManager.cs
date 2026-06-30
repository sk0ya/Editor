using System.Threading;
using System.Windows.Threading;
using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

public interface IEditorLspManager : IDisposable
{
    IReadOnlyList<LspDiagnostic> CurrentDiagnostics { get; }
    IReadOnlyList<LspCompletionItem> CompletionItems { get; }
    int CompletionSelection { get; }
    int CompletionScrollOffset { get; }
    bool CompletionVisible { get; }
    LspSignatureHelp? CurrentSignatureHelp { get; }
    IReadOnlyList<LspCodeAction> CurrentCodeActions { get; }
    int CodeActionsSelection { get; }
    int CodeActionsScrollOffset { get; }
    bool CodeActionsVisible { get; }
    bool IsConnected { get; }
    bool IsDocumentReady { get; }
    bool ServerSupportsFoldingRange { get; }
    bool ServerSupportsWorkspaceDiagnostics { get; }
    string? CurrentUri { get; }

    event Action<string>? StatusMessage;
    event Action? StateChanged;
    event Action<string>? BreadcrumbChanged;
    event Action<IReadOnlyList<LspFoldingRange>>? FoldingRangesChanged;
    event Action<IReadOnlyList<InlayHint>>? InlayHintsChanged;
    event Action<SemanticToken[]>? SemanticTokensChanged;
    event Action<IReadOnlyList<DocumentHighlight>?>? DocumentHighlightsChanged;

    void OnFileOpened(string? filePath, string text);
    void OnTextChanged(string text);
    Task<string> RequestCompletionAsync(int line, int character);
    Task<string?> RequestHoverAsync(int line, int character);
    Task<(string FilePath, int Line, int Column)?> RequestDefinitionAsync(int line, int character);
    void MoveCompletionSelection(int delta);
    LspCompletionItem? GetSelectedCompletion();
    void FilterCompletion(string prefix);
    void HideCompletion();
    Task RequestSignatureHelpAsync(int line, int character);
    void HideSignatureHelp();
    Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName);
    Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character);
    Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character);
    void ShowCodeActions(IReadOnlyList<LspCodeAction> actions);
    void HideCodeActions();
    void MoveCodeActionsSelection(int delta);
    Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize = 4, bool insertSpaces = true);
    IReadOnlyList<DocumentSymbol> GetDocumentSymbols();
    Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync();
    string? GetBreadcrumb(int line, int col);
    IReadOnlyList<BreadcrumbSegment> GetBreadcrumbSegments(int line, int col);
    void UpdateBreadcrumb(int line, int col);
    void ClearBreadcrumb();
    Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, bool isClass, CancellationToken ct = default);
    Task<CallHierarchyItem?> PrepareCallHierarchyAsync(int line, int character);
    Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item);
    Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item);
    Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(int line, int character);
    Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item);
    Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item);
    Task RequestDocumentHighlightAsync(string uri, int line, int character);
    void ClearDocumentHighlights();
    Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character);
    void RequestFoldingRanges();
    void SetInlayHintsEnabled(bool enabled);
    void RequestInlayHints(int startLine, int endLine);
    void SetSemanticTokensEnabled(bool enabled);
    void RequestSemanticTokens();
    Task<LspWorkspaceDiagnosticResult?> RequestWorkspaceDiagnosticsAsync(CancellationToken ct = default);
}
