using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>The view used when no host injected an <see cref="ILspWorkspace"/> — LSP is simply off.</summary>
internal sealed class NullLspView : IEditorLspView
{
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private CancellationTokenSource? _signatureHelpCts;
    private LspSignatureHelp? _signatureHelp;
    private CancellationTokenSource? _inlayHintsCts;
    private IReadOnlyList<InlayHint> _inlayHints = [];
    private bool _inlayHintsEnabled;
    private IReadOnlyList<LspCompletionItem> _rawCompletionItems = [];
    private IReadOnlyList<LspCompletionItem> _completionItems = [];
    private int _completionSelection = -1;
    private bool _semanticTokensEnabled;

    public NullLspView(System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
    }

    public ILspDocument? Document => null;
    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => [];
    public IReadOnlyList<LspCompletionItem> CompletionItems => _completionItems;
    public int CompletionSelection => _completionSelection;
    public int CompletionScrollOffset => 0;
    public bool CompletionVisible => _completionItems.Count > 0;
    public LspSignatureHelp? CurrentSignatureHelp => _signatureHelp;
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspCompletionItem>>>? HostCompletionProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific semantic tokens.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<SemanticToken>>>? HostSemanticTokensProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific signature help.</summary>
    public Func<int, int, CancellationToken, Task<LspSignatureHelp?>>? HostSignatureHelpProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific semantic rename.</summary>
    public Func<int, int, string, CancellationToken, Task<LspWorkspaceEdit?>>? HostRenameProvider { get; set; }
    public Func<int, int, CancellationToken, Task<LspRange?>>? HostPrepareRenameProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific definition lookup.</summary>
    public Func<int, int, CancellationToken, Task<(string Uri, int Line, int Column)?>>? HostDefinitionProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific reference lookup.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostReferencesProvider { get; set; }
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostImplementationProvider { get; set; }
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostTypeDefinitionProvider { get; set; }
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostDeclarationProvider { get; set; }
    public Func<int, int, CancellationToken, Task<string?>>? HostHoverProvider { get; set; }
    public Func<int, int, CancellationToken, Task<IReadOnlyList<DocumentHighlight>>>? HostDocumentHighlightProvider { get; set; }
    public Func<int, int, CancellationToken, Task<IReadOnlyList<InlayHint>>>? HostInlayHintProvider { get; set; }
    public IReadOnlyList<LspCodeAction> CurrentCodeActions => [];
    public IReadOnlyList<LspDocumentLink> CurrentDocumentLinks => [];
    public IReadOnlyList<LspCodeLens> CurrentCodeLenses => [];
    public int CodeActionsSelection => 0;
    public int CodeActionsScrollOffset => 0;
    public bool CodeActionsVisible => false;
    public bool IsConnected => false;
    public bool IsDocumentReady => false;
    public bool HasHostRenameProvider => HostRenameProvider is not null;
    public bool HasHostPrepareRenameProvider => HostPrepareRenameProvider is not null;
    public bool HasHostDefinitionProvider => HostDefinitionProvider is not null;
    public bool HasHostReferencesProvider => HostReferencesProvider is not null;
    public bool HasHostImplementationProvider => HostImplementationProvider is not null;
    public bool HasHostTypeDefinitionProvider => HostTypeDefinitionProvider is not null;
    public bool HasHostDeclarationProvider => HostDeclarationProvider is not null;
    public bool HasHostHoverProvider => HostHoverProvider is not null;
    public bool HasHostDocumentHighlightProvider => HostDocumentHighlightProvider is not null;
    public bool HasHostCompletionProvider => HostCompletionProvider is not null;
    public bool ServerSupportsFoldingRange => false;
    public bool ServerSupportsRangeFormatting => false;
    public string? CurrentUri => null;

    public event Action<string>? StatusMessage { add { } remove { } }
    public event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged { add { } remove { } }
    public event Action? StateChanged;
    public event Action<string>? BreadcrumbChanged { add { } remove { } }
    public event Action<IReadOnlyList<LspFoldingRange>>? FoldingRangesChanged { add { } remove { } }
    public event Action<IReadOnlyList<InlayHint>>? InlayHintsChanged;
    private event Action<SemanticToken[]>? _semanticTokensChanged;
    public event Action<SemanticToken[]>? SemanticTokensChanged
    {
        add => _semanticTokensChanged += value;
        remove => _semanticTokensChanged -= value;
    }
    public event Action<IReadOnlyList<DocumentHighlight>?>? DocumentHighlightsChanged;
    public event Action<IReadOnlyList<LspDocumentLink>>? DocumentLinksChanged { add { } remove { } }
    public event Action<IReadOnlyList<LspCodeLens>>? CodeLensesChanged { add { } remove { } }

    public void OnFileOpened(string? filePath, string text)
    {
        HideCompletion();
        HideSignatureHelp();
        _inlayHintsCts?.Cancel();
        _inlayHintsCts?.Dispose();
        _inlayHintsCts = null;
        _inlayHints = [];
        InlayHintsChanged?.Invoke(_inlayHints);
        if (_semanticTokensEnabled)
            RequestSemanticTokens();
    }

    public void OnTextChanged(string text)
    {
        if (_inlayHintsEnabled) RequestInlayHints(0, int.MaxValue);
    }

    public async Task<string?> RequestCompletionAsync(int line, int character)
    {
        if (HostCompletionProvider is not { } provider)
            return "LSP integration is not configured";
        IReadOnlyList<LspCompletionItem> items;
        try
        {
            items = await provider(line, character, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return $"Completion failed: {ex.Message}";
        }

        _rawCompletionItems = items;
        _completionItems = CompletionRanker.Rank(items, "");
        _completionSelection = _completionItems.Count > 0 ? 0 : -1;
        StateChanged?.Invoke();
        return _completionItems.Count > 0 ? "" : "LSP: no completions at this position";
    }

    public Task<string?> RequestHoverAsync(int line, int character) =>
        HostHoverProvider is { } provider
            ? provider(line, character, CancellationToken.None)
            : Task.FromResult<string?>(null);

    public async Task<(string FilePath, int Line, int Column)?> RequestDefinitionAsync(int line, int character)
    {
        var result = await RequestDefinitionLocationAsync(line, character);
        return result is { } location && LspUri.TryToLocalPath(location.Uri) is { } path
            ? (path, location.Line, location.Column)
            : null;
    }

    public Task<(string Uri, int Line, int Column)?> RequestDefinitionLocationAsync(int line, int character)
        => HostDefinitionProvider is { } provider
            ? provider(line, character, CancellationToken.None)
            : Task.FromResult<(string Uri, int Line, int Column)?>(null);

    public void MoveCompletionSelection(int delta)
    {
        if (_completionItems.Count == 0) return;
        _completionSelection = (_completionSelection + delta + _completionItems.Count) % _completionItems.Count;
        StateChanged?.Invoke();
    }

    public LspCompletionItem? GetSelectedCompletion()
        => _completionSelection >= 0 && _completionSelection < _completionItems.Count
            ? _completionItems[_completionSelection]
            : null;

    public void FilterCompletion(string prefix)
    {
        if (_rawCompletionItems.Count == 0) return;
        _completionItems = CompletionRanker.Rank(_rawCompletionItems, prefix);
        _completionSelection = _completionItems.Count > 0 ? 0 : -1;
        StateChanged?.Invoke();
    }

    public void HideCompletion()
    {
        if (_completionItems.Count == 0 && _rawCompletionItems.Count == 0) return;
        _rawCompletionItems = [];
        _completionItems = [];
        _completionSelection = -1;
        StateChanged?.Invoke();
    }

    public async Task RequestSignatureHelpAsync(int line, int character)
    {
        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _signatureHelpCts = new CancellationTokenSource();
        var ct = _signatureHelpCts.Token;
        LspSignatureHelp? help = null;
        if (HostSignatureHelpProvider is { } provider)
        {
            try { help = await provider(line, character, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { help = null; }
        }

        if (ct.IsCancellationRequested) return;
        _signatureHelp = help?.Signatures.Count > 0 ? help : null;
        StateChanged?.Invoke();
    }

    public void HideSignatureHelp()
    {
        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _signatureHelpCts = null;
        if (_signatureHelp is null) return;
        _signatureHelp = null;
        StateChanged?.Invoke();
    }

    public Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName)
        => HostRenameProvider is { } provider
            ? provider(line, character, newName, CancellationToken.None)
            : Task.FromResult<LspWorkspaceEdit?>(null);

    public Task<LspRange?> PrepareRenameAsync(int line, int character, CancellationToken ct = default)
        => HostPrepareRenameProvider is { } provider
            ? provider(line, character, ct)
            : Task.FromResult<LspRange?>(null);

    public Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character)
        => HostReferencesProvider is { } provider
            ? provider(line, character, CancellationToken.None)
            : Task.FromResult<IReadOnlyList<LspLocation>>([]);

    public Task<IReadOnlyList<LspLocation>> RequestImplementationAsync(
        int line, int character, CancellationToken ct = default)
        => HostImplementationProvider is { } provider
            ? provider(line, character, ct)
            : Task.FromResult<IReadOnlyList<LspLocation>>([]);

    public Task<IReadOnlyList<LspLocation>> RequestTypeDefinitionAsync(
        int line, int character, CancellationToken ct = default)
        => HostTypeDefinitionProvider is { } provider
            ? provider(line, character, ct)
            : Task.FromResult<IReadOnlyList<LspLocation>>([]);

    public Task<IReadOnlyList<LspLocation>> RequestDeclarationAsync(
        int line, int character, CancellationToken ct = default)
        => HostDeclarationProvider is { } provider
            ? provider(line, character, ct)
            : Task.FromResult<IReadOnlyList<LspLocation>>([]);

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

    public async Task RequestDocumentHighlightAsync(string uri, int line, int character)
    {
        if (HostDocumentHighlightProvider is not { } provider) return;
        try
        {
            var highlights = await provider(line, character, CancellationToken.None);
            if (_dispatcher is { } dispatcher)
                await dispatcher.InvokeAsync(() => DocumentHighlightsChanged?.Invoke(highlights));
            else
                DocumentHighlightsChanged?.Invoke(highlights);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

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
        _inlayHintsEnabled = enabled;
        if (enabled)
            RequestInlayHints(0, int.MaxValue);
        else
        {
            _inlayHintsCts?.Cancel();
            _inlayHintsCts?.Dispose();
            _inlayHintsCts = null;
            _inlayHints = [];
            InlayHintsChanged?.Invoke(_inlayHints);
        }
    }

    public void RequestInlayHints(int startLine, int endLine)
    {
        if (!_inlayHintsEnabled || HostInlayHintProvider is not { } provider) return;
        _inlayHintsCts?.Cancel();
        _inlayHintsCts?.Dispose();
        _inlayHintsCts = new CancellationTokenSource();
        _ = ApplyInlayHintsAsync(provider, startLine, endLine, _inlayHintsCts.Token);
    }

    private async Task ApplyInlayHintsAsync(
        Func<int, int, CancellationToken, Task<IReadOnlyList<InlayHint>>> provider,
        int startLine, int endLine, CancellationToken ct)
    {
        IReadOnlyList<InlayHint> hints;
        try { hints = await provider(startLine, endLine, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch { hints = []; }
        if (ct.IsCancellationRequested) return;
        _inlayHints = hints;
        InlayHintsChanged?.Invoke(_inlayHints);
    }

    public void SetSemanticTokensEnabled(bool enabled)
    {
        _semanticTokensEnabled = enabled;
        if (enabled && HostSemanticTokensProvider is { } provider)
            _ = RequestHostSemanticTokensAsync(provider);
        else if (!enabled)
            _semanticTokensChanged?.Invoke([]);
    }

    public void RequestSemanticTokens()
    {
        if (HostSemanticTokensProvider is { } provider)
            _ = RequestHostSemanticTokensAsync(provider);
    }

    private async Task RequestHostSemanticTokensAsync(
        Func<CancellationToken, Task<IReadOnlyList<SemanticToken>>> provider)
    {
        try { _semanticTokensChanged?.Invoke((await provider(CancellationToken.None)).ToArray()); }
        catch { }
    }

    public void Dispose()
    {
        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _inlayHintsCts?.Cancel();
        _inlayHintsCts?.Dispose();
    }
}
