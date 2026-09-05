using Editor.Controls.Lsp;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

/// <summary>
/// 補完まわりの経路を観測するためのテスト用 <see cref="IEditorLspView"/>。
/// 要求の記録と、ポップアップ表示状態の手動セットだけを持つ。
/// </summary>
internal sealed class FakeEditorLspView : IEditorLspView
{
    public List<(int Line, int Character)> CompletionRequests { get; } = [];

    /// <summary>補完要求の戻り値。""=成功（ポップアップ表示）／非空=メッセージ／null=破棄。</summary>
    public string? CompletionResult { get; set; } = "";

    /// <summary>true の間は要求を保留し、<see cref="ResolveCompletion"/> で明示的に応答させる。</summary>
    public bool DeferCompletionRequests { get; set; }

    private readonly List<TaskCompletionSource<string?>> _pendingCompletions = [];

    /// <summary>保留中の補完要求の数。</summary>
    public int PendingCompletionCount => _pendingCompletions.Count;

    /// <summary>保留中の index 番目（要求順）の補完要求に応答を返す。</summary>
    public void ResolveCompletion(int index, string? result) =>
        _pendingCompletions[index].TrySetResult(result);

    public List<int> SelectionMoves { get; } = [];
    public int HideCompletionCalls { get; private set; }
    public LspCompletionItem? SelectedItem { get; set; }
    public bool CompletionVisible { get; set; }

    public ILspDocument? Document => null;
    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => [];
    public IReadOnlyList<LspCompletionItem> CompletionItems =>
        SelectedItem is null ? [] : [SelectedItem];
    public int CompletionSelection => SelectedItem is null ? -1 : 0;
    public int CompletionScrollOffset => 0;
    public LspSignatureHelp? CurrentSignatureHelp => null;
    public IReadOnlyList<LspCodeAction> CurrentCodeActions => [];
    public IReadOnlyList<LspDocumentLink> CurrentDocumentLinks => [];
    public IReadOnlyList<LspCodeLens> CurrentCodeLenses => [];
    public int CodeActionsSelection => 0;
    public int CodeActionsScrollOffset => 0;
    public bool CodeActionsVisible => false;
    public bool IsConnected => true;
    public bool IsDocumentReady => true;
    public bool ServerSupportsFoldingRange => false;
    public bool ServerSupportsRangeFormatting => false;
    public string? CurrentUri => "file:///fake.cs";

    public event Action<string>? StatusMessage { add { } remove { } }
    public event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged { add { } remove { } }
    public event Action? StateChanged { add { } remove { } }
    public event Action<string>? BreadcrumbChanged { add { } remove { } }
    public event Action<IReadOnlyList<LspFoldingRange>>? FoldingRangesChanged { add { } remove { } }
    public event Action<IReadOnlyList<InlayHint>>? InlayHintsChanged { add { } remove { } }
    public event Action<SemanticToken[]>? SemanticTokensChanged { add { } remove { } }
    public event Action<IReadOnlyList<DocumentHighlight>?>? DocumentHighlightsChanged { add { } remove { } }
    public event Action<IReadOnlyList<LspDocumentLink>>? DocumentLinksChanged { add { } remove { } }
    public event Action<IReadOnlyList<LspCodeLens>>? CodeLensesChanged { add { } remove { } }

    public void OnFileOpened(string? filePath, string text) { }
    public void OnTextChanged(string text) { }

    public Task<string?> RequestCompletionAsync(int line, int character)
    {
        CompletionRequests.Add((line, character));
        if (!DeferCompletionRequests)
            return Task.FromResult(CompletionResult);

        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCompletions.Add(pending);
        return pending.Task;
    }

    /// <summary>hover の応答（Markdown）。null なら「説明なし」。</summary>
    public string? HoverResult { get; set; }
    public List<(int Line, int Character)> HoverRequests { get; } = [];

    public Task<string?> RequestHoverAsync(int line, int character)
    {
        HoverRequests.Add((line, character));
        return Task.FromResult(HoverResult);
    }

    public Task<(string FilePath, int Line, int Column)?> RequestDefinitionAsync(int line, int character) =>
        Task.FromResult<(string FilePath, int Line, int Column)?>(null);

    public void MoveCompletionSelection(int delta) => SelectionMoves.Add(delta);

    public LspCompletionItem? GetSelectedCompletion() => CompletionVisible ? SelectedItem : null;

    public void FilterCompletion(string prefix) { }

    public void HideCompletion()
    {
        HideCompletionCalls++;
        CompletionVisible = false;
    }

    public Task RequestSignatureHelpAsync(int line, int character) => Task.CompletedTask;
    public void HideSignatureHelp() { }

    public Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName) =>
        Task.FromResult<LspWorkspaceEdit?>(null);

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character) =>
        Task.FromResult<IReadOnlyList<LspLocation>>([]);

    /// <summary>codeAction の応答。</summary>
    public List<LspCodeAction> CodeActions { get; } = [];
    /// <summary>要求された (範囲, kind) の記録。ホバーが診断の範囲で quickfix だけを聞いているかを見る。</summary>
    public List<(LspRange Range, IReadOnlyList<string>? Only)> CodeActionRequests { get; } = [];

    public Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character) =>
        RequestCodeActionsAsync(
            new LspRange(new LspPosition(line, character), new LspPosition(line, character)), null);

    public Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(
        LspRange range, IReadOnlyList<string>? only)
    {
        CodeActionRequests.Add((range, only));
        return Task.FromResult<IReadOnlyList<LspCodeAction>>(CodeActions.ToArray());
    }

    public void ShowCodeActions(IReadOnlyList<LspCodeAction> actions) { }
    public void HideCodeActions() { }
    public void MoveCodeActionsSelection(int delta) { }

    public Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize = 4, bool insertSpaces = true) =>
        Task.FromResult<IReadOnlyList<LspTextEdit>>([]);

    public Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize = 4, bool insertSpaces = true) =>
        Task.FromResult<IReadOnlyList<LspTextEdit>>([]);

    public IReadOnlyList<DocumentSymbol> GetDocumentSymbols() => [];

    public Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync() =>
        Task.FromResult<IReadOnlyList<DocumentSymbol>>([]);

    public string? GetBreadcrumb(int line, int col) => null;
    public IReadOnlyList<BreadcrumbSegment> GetBreadcrumbSegments(int line, int col) => [];
    public void UpdateBreadcrumb(int line, int col) { }
    public void ClearBreadcrumb() { }
    public Task RequestDocumentHighlightAsync(string uri, int line, int character) => Task.CompletedTask;
    public void ClearDocumentHighlights() { }

    public Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character) =>
        Task.FromResult<LspSelectionRange?>(null);

    public void RequestFoldingRanges() { }
    public void SetInlayHintsEnabled(bool enabled) { }
    public void RequestInlayHints(int startLine, int endLine) { }
    public void SetSemanticTokensEnabled(bool enabled) { }
    public void RequestSemanticTokens() { }
    public void Dispose() { }
}
