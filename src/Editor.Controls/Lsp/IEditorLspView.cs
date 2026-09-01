using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>
/// The editor control's <b>view</b> onto LSP: popup state (completion, signature help, code actions),
/// breadcrumb, folding/inlay/semantic-token plumbing, and dispatcher-thread marshalling of everything
/// the session pushes at it. One instance per <see cref="VimEditorControl"/>.
///
/// <para>It owns no processes and no protocol state — those belong to the host's
/// <see cref="ILspWorkspace"/>, from which this view takes a single <see cref="ILspDocument"/> handle
/// for the buffer it is showing. Workspace-scope queries (symbol search, workspace diagnostics,
/// call/type hierarchy) are deliberately absent here: ask the workspace, not a tab.</para>
///
/// <para><b>Threading:</b> unlike <see cref="ILspWorkspace"/>, every member and event of this interface
/// is dispatcher-thread only. The implementation is what marshals the workspace's background-thread
/// events onto the UI thread.</para>
/// </summary>
public interface IEditorLspView : IDisposable
{
    /// <summary>The document handle for the buffer currently shown, or null when the file has no server.</summary>
    ILspDocument? Document { get; }

    IReadOnlyList<LspDiagnostic> CurrentDiagnostics { get; }
    IReadOnlyList<LspCompletionItem> CompletionItems { get; }
    int CompletionSelection { get; }
    int CompletionScrollOffset { get; }
    bool CompletionVisible { get; }
    LspSignatureHelp? CurrentSignatureHelp { get; }
    IReadOnlyList<LspCodeAction> CurrentCodeActions { get; }
    IReadOnlyList<LspDocumentLink> CurrentDocumentLinks { get; }
    IReadOnlyList<LspCodeLens> CurrentCodeLenses { get; }
    int CodeActionsSelection { get; }
    int CodeActionsScrollOffset { get; }
    bool CodeActionsVisible { get; }
    bool IsConnected { get; }
    bool IsDocumentReady { get; }
    bool ServerSupportsFoldingRange { get; }
    bool ServerSupportsRangeFormatting { get; }
    bool ServerSupportsCompletionResolve => Document?.ServerSupportsCompletionResolve == true;
    bool ServerSupportsImplementation => Document?.ServerSupportsImplementation == true;
    bool ServerSupportsTypeDefinition => Document?.ServerSupportsTypeDefinition == true;
    bool ServerSupportsDeclaration => Document?.ServerSupportsDeclaration == true;
    bool ServerSupportsPrepareRename => Document?.ServerSupportsPrepareRename == true;
    bool ServerSupportsDocumentHighlight => Document?.ServerSupportsDocumentHighlight == true;
    IReadOnlyList<string> ServerCodeActionKinds => Document?.ServerCodeActionKinds ?? [];
    IReadOnlyList<string> CompletionTriggerCharacters => ["."];
    string? CurrentUri { get; }

    event Action<string>? StatusMessage;
    event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged;
    event Action? StateChanged;
    event Action<string>? BreadcrumbChanged;
    event Action<IReadOnlyList<LspFoldingRange>>? FoldingRangesChanged;
    event Action<IReadOnlyList<InlayHint>>? InlayHintsChanged;
    event Action<SemanticToken[]>? SemanticTokensChanged;
    event Action<IReadOnlyList<DocumentHighlight>?>? DocumentHighlightsChanged;
    event Action<IReadOnlyList<LspDocumentLink>>? DocumentLinksChanged;
    event Action<IReadOnlyList<LspCodeLens>>? CodeLensesChanged;

    void OnFileOpened(string? filePath, string text);
    void OnTextChanged(string text);
    /// <summary>
    /// Returns "" on success (popup state applied), a status message on failure,
    /// or null when the request was superseded and the caller must stay inert.
    /// </summary>
    Task<string?> RequestCompletionAsync(int line, int character);
    Task<string?> RequestHoverAsync(int line, int character);
    /// <summary>Request the raw definition URI, preserving external (non-file) sources for peek displays.</summary>
    Task<(string Uri, int Line, int Column)?> RequestDefinitionLocationAsync(int line, int character)
        => RequestDefinitionAsync(line, character);
    bool HasHostCompletionProvider => false;
    bool HasHostDefinitionProvider => false;
    bool HasHostReferencesProvider => false;
    bool HasHostImplementationProvider => false;
    bool HasHostTypeDefinitionProvider => false;
    bool HasHostDeclarationProvider => false;
    bool HasHostHoverProvider => false;
    bool HasHostDocumentHighlightProvider => false;
    Task<(string FilePath, int Line, int Column)?> RequestDefinitionAsync(int line, int character);
    Task<LspCompletionItem?> ResolveCompletionAsync(LspCompletionItem item, CancellationToken ct = default)
        => Task.FromResult<LspCompletionItem?>(null);
    Task<IReadOnlyList<LspLocation>> RequestImplementationAsync(int line, int character, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspLocation>>([]);
    Task<IReadOnlyList<LspLocation>> RequestTypeDefinitionAsync(int line, int character, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspLocation>>([]);
    Task<IReadOnlyList<LspLocation>> RequestDeclarationAsync(int line, int character, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspLocation>>([]);
    Task<LspRange?> PrepareRenameAsync(int line, int character, CancellationToken ct = default)
        => Task.FromResult<LspRange?>(null);
    bool HasHostRenameProvider => false;
    bool HasHostPrepareRenameProvider => false;
    Task<bool> ExecuteCompletionCommandAsync(LspCompletionCommand command, CancellationToken ct = default)
        => Task.FromResult(false);
    void MoveCompletionSelection(int delta);
    LspCompletionItem? GetSelectedCompletion();
    void FilterCompletion(string prefix);
    void HideCompletion();
    void RecordCompletionAccepted(LspCompletionItem item) { }
    Task RequestSignatureHelpAsync(int line, int character);
    void HideSignatureHelp();
    Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName);
    Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character);
    Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character);

    /// <summary>範囲指定の code action 要求。選択があるときはこちらを使う——
    /// 「メソッドの抽出」のような範囲対象のリファクタリングは1点では候補が出ない。
    /// 既定実装は開始位置だけの要求へ落ちる。</summary>
    Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(
        LspRange range, IReadOnlyList<string>? only)
        => RequestCodeActionsAsync(range.Start.Line, range.Start.Character);

    /// <summary>未解決 code action の <c>codeAction/resolve</c>。解決できなければ null。</summary>
    Task<LspCodeAction?> ResolveCodeActionAsync(LspCodeAction action)
        => Task.FromResult<LspCodeAction?>(null);
    Task<LspCodeLens?> ResolveCodeLensAsync(LspCodeLens lens)
        => Task.FromResult<LspCodeLens?>(null);

    /// <summary>コマンド型 code action の実行。編集はサーバー起点の applyEdit で返る。</summary>
    Task<bool> ExecuteCodeActionCommandAsync(LspCodeActionCommand command)
        => Task.FromResult(false);
    Task<bool> ExecuteCodeLensCommandAsync(LspCodeActionCommand command)
        => Task.FromResult(false);

    void ShowCodeActions(IReadOnlyList<LspCodeAction> actions);
    void HideCodeActions();
    void MoveCodeActionsSelection(int delta);
    Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize = 4, bool insertSpaces = true);

    /// <summary>Format only the text covered by <paramref name="range"/> instead of the whole document.</summary>
    Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize = 4, bool insertSpaces = true);
    IReadOnlyList<DocumentSymbol> GetDocumentSymbols();
    Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync();
    string? GetBreadcrumb(int line, int col);
    IReadOnlyList<BreadcrumbSegment> GetBreadcrumbSegments(int line, int col);
    void UpdateBreadcrumb(int line, int col);
    void ClearBreadcrumb();
    Task RequestDocumentHighlightAsync(string uri, int line, int character);
    void ClearDocumentHighlights();
    Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character);
    void RequestFoldingRanges();
    void SetInlayHintsEnabled(bool enabled);
    void RequestInlayHints(int startLine, int endLine);
    void SetSemanticTokensEnabled(bool enabled);
    void RequestSemanticTokens();
}
