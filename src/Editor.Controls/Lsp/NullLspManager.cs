using System.Threading;
using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

internal sealed class NullLspManager : IEditorLspManager
{
    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => [];
    public IReadOnlyList<LspCompletionItem> CompletionItems => [];
    public int CompletionSelection => -1;
    public int CompletionScrollOffset => 0;
    public bool CompletionVisible => false;
    public LspSignatureHelp? CurrentSignatureHelp => null;
    public IReadOnlyList<LspCodeAction> CurrentCodeActions => [];
    public int CodeActionsSelection => 0;
    public int CodeActionsScrollOffset => 0;
    public bool CodeActionsVisible => false;
    public bool IsConnected => false;
    public bool IsDocumentReady => false;
    public bool ServerSupportsFoldingRange => false;
    public bool ServerSupportsRangeFormatting => false;
    public bool ServerSupportsWorkspaceDiagnostics => false;
    public string? CurrentUri => null;

    public event Action<string>? StatusMessage { add { } remove { } }
    public event Action? StateChanged { add { } remove { } }
    public event Action<string>? BreadcrumbChanged { add { } remove { } }
    public event Action<IReadOnlyList<LspFoldingRange>>? FoldingRangesChanged { add { } remove { } }
    public event Action<IReadOnlyList<InlayHint>>? InlayHintsChanged { add { } remove { } }
    public event Action<SemanticToken[]>? SemanticTokensChanged { add { } remove { } }
    public event Action<IReadOnlyList<DocumentHighlight>?>? DocumentHighlightsChanged { add { } remove { } }

    public void OnFileOpened(string? filePath, string text)
    {
    }

    public void OnTextChanged(string text)
    {
    }

    public Task<string?> RequestCompletionAsync(int line, int character) =>
        Task.FromResult<string?>("LSP integration is not configured");

    public Task<string?> RequestHoverAsync(int line, int character) =>
        Task.FromResult<string?>(null);

    public Task<(string FilePath, int Line, int Column)?> RequestDefinitionAsync(int line, int character) =>
        Task.FromResult<(string FilePath, int Line, int Column)?>(null);

    public void MoveCompletionSelection(int delta)
    {
    }

    public LspCompletionItem? GetSelectedCompletion() => null;

    public void FilterCompletion(string prefix)
    {
    }

    public void HideCompletion()
    {
    }

    public Task RequestSignatureHelpAsync(int line, int character) => Task.CompletedTask;

    public void HideSignatureHelp()
    {
    }

    public Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName) =>
        Task.FromResult<LspWorkspaceEdit?>(null);

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character) =>
        Task.FromResult<IReadOnlyList<LspLocation>>([]);

    public Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character) =>
        Task.FromResult<IReadOnlyList<LspCodeAction>>([]);

    public void ShowCodeActions(IReadOnlyList<LspCodeAction> actions)
    {
    }

    public void HideCodeActions()
    {
    }

    public void MoveCodeActionsSelection(int delta)
    {
    }

    public Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize = 4, bool insertSpaces = true) =>
        Task.FromResult<IReadOnlyList<LspTextEdit>>([]);

    public Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize = 4, bool insertSpaces = true) =>
        Task.FromResult<IReadOnlyList<LspTextEdit>>([]);

    public IReadOnlyList<DocumentSymbol> GetDocumentSymbols() => [];

    public Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync() =>
        Task.FromResult<IReadOnlyList<DocumentSymbol>>([]);

    public string? GetBreadcrumb(int line, int col) => null;

    public IReadOnlyList<BreadcrumbSegment> GetBreadcrumbSegments(int line, int col) => [];

    public void UpdateBreadcrumb(int line, int col)
    {
    }

    public void ClearBreadcrumb()
    {
    }

    public Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, bool isClass, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LspSymbolInformation>>([]);

    public Task<CallHierarchyItem?> PrepareCallHierarchyAsync(int line, int character) =>
        Task.FromResult<CallHierarchyItem?>(null);

    public Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item) =>
        Task.FromResult<CallHierarchyIncomingCall[]?>(null);

    public Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item) =>
        Task.FromResult<CallHierarchyOutgoingCall[]?>(null);

    public Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(int line, int character) =>
        Task.FromResult<TypeHierarchyItem?>(null);

    public Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item) =>
        Task.FromResult<TypeHierarchyItem[]?>(null);

    public Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item) =>
        Task.FromResult<TypeHierarchyItem[]?>(null);

    public Task RequestDocumentHighlightAsync(string uri, int line, int character) => Task.CompletedTask;

    public void ClearDocumentHighlights()
    {
    }

    public Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character) =>
        Task.FromResult<LspSelectionRange?>(null);

    public void RequestFoldingRanges()
    {
    }

    public void SetInlayHintsEnabled(bool enabled)
    {
    }

    public void RequestInlayHints(int startLine, int endLine)
    {
    }

    public void SetSemanticTokensEnabled(bool enabled)
    {
    }

    public void RequestSemanticTokens()
    {
    }

    public Task<LspWorkspaceDiagnosticResult?> RequestWorkspaceDiagnosticsAsync(CancellationToken ct = default) =>
        Task.FromResult<LspWorkspaceDiagnosticResult?>(null);

    public void Dispose()
    {
    }
}
