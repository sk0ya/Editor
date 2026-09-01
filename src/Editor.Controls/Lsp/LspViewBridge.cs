using System.IO;
using System.Threading;
using System.Windows.Threading;
using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>
/// Bridges one editor control to the host's <see cref="ILspWorkspace"/>: holds a single
/// <see cref="ILspDocument"/> handle for the buffer being shown, owns all popup/breadcrumb/decoration
/// state, and marshals the workspace's background-thread events onto the control's dispatcher.
///
/// <para>It manages no processes. Server lifetime, pooling, document reference counting and
/// crash recovery all live behind <see cref="ILspWorkspace"/>, so opening the same solution in five
/// tabs is five bridges over one server.</para>
/// </summary>
public sealed class LspViewBridge : IEditorLspView
{
    private readonly Dispatcher _dispatcher;
    private readonly ILspWorkspace _workspace;

    private ILspDocument? _document;
    private string? _currentUri;
    private bool _documentReady;   // mirrored onto the dispatcher thread from the handle
    private bool _viewDisposed;
    private long _documentGeneration;

    // Set after the document opens so that the first publishDiagnostics triggers a fold range retry.
    // (Some servers are not ready to answer foldingRange immediately after didOpen.)
    private volatile string? _pendingFoldRangeUri;
    // Same idea for documentSymbol: csharp-ls silently drops the request sent before the
    // solution finishes loading, so the first file's symbols (and thus the breadcrumb) stay
    // empty until a publishDiagnostics-triggered retry. Cleared once symbols come back non-empty.
    private volatile string? _pendingSymbolUri;

    // State visible to the UI (always accessed on dispatcher thread)
    private IReadOnlyList<LspDiagnostic> _diagnostics = [];
    private IReadOnlyList<LspCompletionItem> _rawCompletionItems = [];  // full server response
    private IReadOnlyList<LspCompletionItem> _completionItems = [];     // filtered view
    private int _completionSelection = -1;
    private int _completionScrollOffset = 0;
    private bool _completionVisible;
    // Supersession guard for completion requests (same pattern as _highlightCts).
    // Two triggers can be in flight at once (the 300ms debounce and the immediate
    // '.' trigger); without this, whichever response arrives *last* wins, so a
    // stale keyword list can overwrite the newer member-access completion.
    // Cancelled on every new request and on explicit hide, which also stops the
    // superseded server round-trip instead of letting it run to be discarded.
    private CancellationTokenSource? _completionCts;

    private const int MaxVisibleCompletion = 10;

    private LspSignatureHelp? _signatureHelp;
    // Same last-writer-wins guard for signature help: '(' then ',' can have two
    // requests in flight, and typing ')' (HideSignatureHelp) must keep a late
    // response from re-showing the popup after the explicit close.
    private CancellationTokenSource? _signatureHelpCts;

    private IReadOnlyList<LspCodeAction> _codeActions = [];
    private int _codeActionsSelection = 0;
    private int _codeActionsScrollOffset = 0;
    private bool _codeActionsVisible;

    // Inlay hints
    private IReadOnlyList<InlayHint> _inlayHints = [];
    private bool _inlayHintsEnabled = false;
    private CancellationTokenSource? _inlayHintsCts;
    private System.Windows.Threading.DispatcherTimer? _inlayHintDebounce;
    private const int InlayHintDebounceMs = 250;

    // Document highlights
    private IReadOnlyList<DocumentHighlight>? _documentHighlights;
    private CancellationTokenSource? _highlightCts;

    // Document links
    private IReadOnlyList<LspDocumentLink> _documentLinks = [];
    private System.Threading.Timer? _documentLinkDebounce;
    private const int DocumentLinkDebounceMs = 500;

    // Code lenses
    private IReadOnlyList<LspCodeLens> _codeLenses = [];
    private System.Threading.Timer? _codeLensDebounce;
    private const int CodeLensDebounceMs = 700;

    // Semantic tokens
    private bool _semanticTokensEnabled = false;
    private string? _semanticTokenResultId;
    private readonly SemaphoreSlim _semanticTokenRequestGate = new(1, 1);
    private System.Threading.Timer? _semanticTokenDebounce;
    private const int SemanticTokenDebounceMs = 500;

    // Document symbols (for breadcrumb and :Symbols)
    private IReadOnlyList<DocumentSymbol> _documentSymbols = [];
    private System.Threading.Timer? _symbolDebounce;
    private string _lastBreadcrumb = "";
    private const int SymbolDebounceMs = 1000;

    public ILspDocument? Document => _document;

    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => _diagnostics;
    public IReadOnlyList<LspCompletionItem> CompletionItems => _completionItems;
    public int CompletionSelection => _completionSelection;
    public int CompletionScrollOffset => _completionScrollOffset;
    public bool CompletionVisible => _completionVisible;
    public LspSignatureHelp? CurrentSignatureHelp => _signatureHelp;
    /// <summary>Fallback for host-provided, language-specific completion.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspCompletionItem>>>? HostCompletionProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific semantic tokens.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<SemanticToken>>>? HostSemanticTokensProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific signature help.</summary>
    public Func<int, int, CancellationToken, Task<LspSignatureHelp?>>? HostSignatureHelpProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific semantic rename.</summary>
    public Func<int, int, string, CancellationToken, Task<LspWorkspaceEdit?>>? HostRenameProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific rename range validation.</summary>
    public Func<int, int, CancellationToken, Task<LspRange?>>? HostPrepareRenameProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific document highlights.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<DocumentHighlight>>>? HostDocumentHighlightProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific definition lookup.</summary>
    public Func<int, int, CancellationToken, Task<(string Uri, int Line, int Column)?>>? HostDefinitionProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific reference lookup.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostReferencesProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific implementation lookup.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostImplementationProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific type-definition lookup.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostTypeDefinitionProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific declaration lookup.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<LspLocation>>>? HostDeclarationProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific hover information.</summary>
    public Func<int, int, CancellationToken, Task<string?>>? HostHoverProvider { get; set; }
    /// <summary>Fallback for host-provided, language-specific inlay hints.</summary>
    public Func<int, int, CancellationToken, Task<IReadOnlyList<InlayHint>>>? HostInlayHintProvider { get; set; }
    public IReadOnlyList<LspCodeAction> CurrentCodeActions => _codeActions;
    public IReadOnlyList<LspDocumentLink> CurrentDocumentLinks => _documentLinks;
    public IReadOnlyList<LspCodeLens> CurrentCodeLenses => _codeLenses;
    public int CodeActionsSelection => _codeActionsSelection;
    public int CodeActionsScrollOffset => _codeActionsScrollOffset;
    public bool CodeActionsVisible => _codeActionsVisible;

    /// <summary>True when the server is running for the current file.</summary>
    public bool IsConnected => _document?.IsConnected == true;

    /// <summary>True when initialization + didOpen completed for the current file.</summary>
    public bool IsDocumentReady => _documentReady;

    /// <summary>現在のサーバーが textDocument/foldingRange をサポートしているか。</summary>
    public bool ServerSupportsFoldingRange => _document?.ServerSupportsFoldingRange == true;

    /// <summary>現在のサーバーが textDocument/rangeFormatting をサポートしているか。</summary>
    public bool ServerSupportsRangeFormatting => _document?.ServerSupportsRangeFormatting == true;
    public bool ServerSupportsCompletionResolve => _document?.ServerSupportsCompletionResolve == true;
    public bool ServerSupportsImplementation => _document?.ServerSupportsImplementation == true;
    public bool ServerSupportsTypeDefinition => _document?.ServerSupportsTypeDefinition == true;
    public bool ServerSupportsDeclaration => _document?.ServerSupportsDeclaration == true;
    public bool ServerSupportsPrepareRename => _document?.ServerSupportsPrepareRename == true;
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
    public bool ServerSupportsDocumentHighlight => _document?.ServerSupportsDocumentHighlight == true;
    public IReadOnlyList<string> ServerCodeActionKinds => _document?.ServerCodeActionKinds ?? [];
    public IReadOnlyList<string> CompletionTriggerCharacters => _document?.CompletionTriggerCharacters ?? ["."];

    /// <summary>Fired on the dispatcher thread for status bar messages.</summary>
    public event Action<string>? StatusMessage;
    public event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged;

    /// <summary>Fired on the dispatcher thread whenever LSP state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Fired on the dispatcher thread when the breadcrumb path changes (cursor moved).</summary>
    public event Action<string>? BreadcrumbChanged;

    /// <summary>Fired on the dispatcher thread when LSP returns folding ranges for the current file.</summary>
    public event Action<IReadOnlyList<LspFoldingRange>>? FoldingRangesChanged;

    /// <summary>Fired on the dispatcher thread when inlay hints are refreshed.</summary>
    public event Action<IReadOnlyList<InlayHint>>? InlayHintsChanged;

    /// <summary>Fired on the dispatcher thread when semantic tokens are refreshed.</summary>
    public event Action<SemanticToken[]>? SemanticTokensChanged;

    /// <summary>Fired on the dispatcher thread when document highlights change.</summary>
    public event Action<IReadOnlyList<DocumentHighlight>?>? DocumentHighlightsChanged;

    /// <summary>Fired on the dispatcher thread when document links are refreshed.</summary>
    public event Action<IReadOnlyList<LspDocumentLink>>? DocumentLinksChanged;

    /// <summary>Fired on the dispatcher thread when code lenses are refreshed.</summary>
    public event Action<IReadOnlyList<LspCodeLens>>? CodeLensesChanged;

    public string? CurrentUri => _currentUri;

    public LspViewBridge(Dispatcher dispatcher, ILspWorkspace workspace)
    {
        _dispatcher = dispatcher;
        _workspace = workspace;
    }

    /// <summary>Call when a file is opened or the active buffer changes.</summary>
    public void OnFileOpened(string? filePath, string text)
    {
        HideCompletion();
        HideSignatureHelp();
        HideCodeActions();
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        _highlightCts = null;
        _documentHighlights = null;
        _documentLinks = [];
        DocumentLinksChanged?.Invoke(_documentLinks);
        _codeLenses = [];
        CodeLensesChanged?.Invoke(_codeLenses);
        _diagnostics = [];
        _inlayHints = [];
        _inlayHintsCts?.Cancel();
        _inlayHintsCts?.Dispose();
        _inlayHintsCts = null;
        _inlayHintDebounce?.Stop();
        InlayHintsChanged?.Invoke(_inlayHints);
        _documentSymbols = [];
        _semanticTokenResultId = null;
        if (_semanticTokensEnabled) SemanticTokensChanged?.Invoke([]);
        _lastBreadcrumb = "";
        _documentReady = false;
        _pendingFoldRangeUri = null;
        _pendingSymbolUri = null;
        _symbolDebounce?.Dispose();
        _symbolDebounce = null;
        _semanticTokenDebounce?.Dispose();
        _semanticTokenDebounce = null;
        _documentLinkDebounce?.Dispose();
        _documentLinkDebounce = null;
        _codeLensDebounce?.Dispose();
        _codeLensDebounce = null;

        DetachDocument();

        if (filePath == null)
        {
            _currentUri = null;
            StateChanged?.Invoke();
            return;
        }

        var document = _workspace.OpenDocument(filePath, text);
        if (document == null)
        {
            _currentUri = null;
            StateChanged?.Invoke();
            FoldingRangesChanged?.Invoke([]);   // LSP サーバーなし → シンタックスフォールドへフォールバック
            if (_semanticTokensEnabled)
                RequestSemanticTokens();
            return;
        }

        _document = document;
        _currentUri = document.Uri;
        if (_semanticTokensEnabled)
            RequestSemanticTokens();
        document.DiagnosticsChanged += OnDocumentDiagnostics;
        document.StateChanged += OnDocumentStateChanged;
        document.StatusMessage += OnDocumentStatusMessage;

        // The handle may already be ready (the URI was open in another view, or the server was
        // warm) — in that case no StateChanged is coming, so apply the ready path immediately.
        if (document.IsReady) OnDocumentReady(document);
        StateChanged?.Invoke();
    }

    private void DetachDocument()
    {
        if (_document is not { } doc) return;
        doc.DiagnosticsChanged -= OnDocumentDiagnostics;
        doc.StateChanged -= OnDocumentStateChanged;
        doc.StatusMessage -= OnDocumentStatusMessage;
        _document = null;
        doc.Dispose();   // drops this view's reference; didClose only when the last one goes
    }

    // ── Document handle events (background thread → dispatcher) ─────────────

    private void OnDocumentStatusMessage(string message)
        => _dispatcher.InvokeAsync(() => StatusMessage?.Invoke(message));

    private void OnDocumentStateChanged()
    {
        var doc = _document;
        if (doc == null) return;
        _dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_document, doc)) return;   // file switched while queued
            var wasReady = _documentReady;
            _documentReady = doc.IsReady;
            if (_documentReady && !wasReady) OnDocumentReady(doc);
            StateChanged?.Invoke();
        });
    }

    /// <summary>Dispatcher thread. The document just became usable — pull everything the view shows.</summary>
    private void OnDocumentReady(ILspDocument doc)
    {
        _documentReady = true;
        var uri = doc.Uri;
        _diagnostics = doc.CurrentDiagnostics;
        _ = RequestFoldingRangesInternalAsync(doc);
        _pendingFoldRangeUri = uri;
        _pendingSymbolUri = uri;
        _ = RefreshDocumentSymbolsUntilReadyAsync(doc);
        if (_inlayHintsEnabled)
            RequestInlayHints(0, int.MaxValue);
        if (_semanticTokensEnabled)
            _ = RequestSemanticTokensInternalAsync(doc);
        _ = RequestDocumentLinksInternalAsync(doc);
        _ = RequestCodeLensesInternalAsync(doc);
    }

    private void OnDocumentDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
    {
        var doc = _document;
        if (doc == null) return;
        var uri = doc.Uri;

        // publishDiagnostics means the server has finished analyzing the file — use this as
        // the signal to retry a fold range request if the initial one came back empty.
        if (uri == _pendingFoldRangeUri && doc.IsConnected)
        {
            _pendingFoldRangeUri = null;
            _ = RequestFoldingRangesInternalAsync(doc);
        }

        // Retry documentSymbol once the server has analyzed the file (its initial answer,
        // sent before the solution loaded, was silently dropped). Stays pending until the
        // result is non-empty so a too-early diagnostics push doesn't give up prematurely.
        if (uri == _pendingSymbolUri && doc.IsConnected)
            _ = RefreshDocumentSymbolsAsync(doc);

        _dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_document, doc)) return;
            _diagnostics = diagnostics;
            Log($"[LSP] diagnostics: {diagnostics.Count} items");
            DiagnosticsChanged?.Invoke(_diagnostics);
            StateChanged?.Invoke();
        });
    }

    /// <summary>Call whenever text content changes.</summary>
    public void OnTextChanged(string text)
    {
        Interlocked.Increment(ref _documentGeneration);
        if (_codeActionsVisible)
        {
            HideCodeActions();
            StatusMessage?.Invoke("Code Action: 文書が変更されたため候補を破棄しました。再度実行してください。");
        }
        var doc = _document;
        if (_inlayHintsEnabled) ScheduleInlayHintRefresh();
        if (doc == null)
        {
            if (_semanticTokensEnabled)
                ScheduleSemanticTokenRefresh();
            return;
        }
        doc.UpdateText(text);
        if (!_documentReady) return;
        ClearDocumentHighlights();
        ScheduleSymbolRefresh();
        if (_semanticTokensEnabled)
            ScheduleSemanticTokenRefresh();
        ScheduleDocumentLinkRefresh();
        // CodeLensの行位置は編集で直ちに古くなる。再取得のデバウンス中に旧行へ
        // クリック可能なラベルを残すと、別の宣言を実行してしまう可能性があるため先に消す。
        if (_codeLenses.Count > 0)
        {
            _codeLenses = [];
            CodeLensesChanged?.Invoke(_codeLenses);
        }
        ScheduleCodeLensRefresh();
    }

    private void ScheduleDocumentLinkRefresh()
    {
        _documentLinkDebounce?.Dispose();
        _documentLinkDebounce = new System.Threading.Timer(_ =>
        {
            var doc = _document;
            if (doc?.IsConnected == true && _documentReady)
                _ = RequestDocumentLinksInternalAsync(doc);
        }, null, DocumentLinkDebounceMs, Timeout.Infinite);
    }

    private async Task RequestDocumentLinksInternalAsync(ILspDocument doc)
    {
        try
        {
            var links = await doc.RequestDocumentLinksAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_document, doc)) return;
                _documentLinks = links;
                DocumentLinksChanged?.Invoke(_documentLinks);
            });
        }
        catch { }
    }

    private void ScheduleCodeLensRefresh()
    {
        _codeLensDebounce?.Dispose();
        _codeLensDebounce = new System.Threading.Timer(_ =>
        {
            var doc = _document;
            if (doc?.IsConnected == true && _documentReady)
                _ = RequestCodeLensesInternalAsync(doc);
        }, null, CodeLensDebounceMs, Timeout.Infinite);
    }

    private async Task RequestCodeLensesInternalAsync(ILspDocument doc)
    {
        try
        {
            var lenses = await doc.RequestCodeLensesAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_document, doc)) return;
                _codeLenses = lenses;
                CodeLensesChanged?.Invoke(_codeLenses);
            });
        }
        catch { }
    }

    private void ScheduleSemanticTokenRefresh()
    {
        _semanticTokenDebounce?.Dispose();
        _semanticTokenDebounce = new System.Threading.Timer(_ =>
        {
            var doc = _document;
            if (!_semanticTokensEnabled) return;
            if (doc?.IsConnected == true && _documentReady)
                _ = RequestSemanticTokensInternalAsync(doc);
            else if (HostSemanticTokensProvider is { } provider)
                _ = RequestHostSemanticTokensAsync(provider, doc);
        }, null, SemanticTokenDebounceMs, Timeout.Infinite);
    }

    private void ScheduleSymbolRefresh()
    {
        _symbolDebounce?.Dispose();
        _symbolDebounce = new System.Threading.Timer(_ =>
        {
            var doc = _document;
            if (doc?.IsConnected == true && _documentReady)
                _ = RefreshDocumentSymbolsAsync(doc);
        }, null, SymbolDebounceMs, Timeout.Infinite);
    }

    /// <summary>
    /// Fetches document symbols, retrying with a delay until they come back non-empty or the file
    /// changes. csharp-ls answers documentSymbol with null until its solution finishes loading
    /// (seconds after didOpen) and does not proactively re-send, so a one-shot request loses the
    /// breadcrumb for the first file opened. Bounded so genuinely symbol-less files stop quickly.
    /// </summary>
    private async Task RefreshDocumentSymbolsUntilReadyAsync(ILspDocument doc)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (_pendingSymbolUri != doc.Uri || !ReferenceEquals(_document, doc) || !doc.IsConnected) return;
            await RefreshDocumentSymbolsAsync(doc); // clears _pendingSymbolUri on non-empty
            if (_pendingSymbolUri != doc.Uri) return;           // got symbols (or file switched)
            await Task.Delay(1500);
        }
    }

    private async Task RefreshDocumentSymbolsAsync(ILspDocument doc)
    {
        try
        {
            var symbols = await doc.RequestDocumentSymbolsAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_document, doc)) return;
                _documentSymbols = symbols;
                // Stop retrying once the server actually returned symbols.
                if (symbols.Count > 0 && _pendingSymbolUri == doc.Uri) _pendingSymbolUri = null;
                // Notify so views depending on symbols (e.g. the breadcrumb bar) refresh
                // once they load, instead of waiting for the next cursor move.
                StateChanged?.Invoke();
            });
        }
        catch { }
    }

    /// <summary>
    /// Trigger LSP completion at the given position. Returns "" on success (popup state
    /// applied), a status message on failure, or null when the request was superseded —
    /// a null result means the caller owns nothing and must not act on popup state.
    /// </summary>
    public async Task<string?> RequestCompletionAsync(int line, int character)
    {
        var doc = _document;
        if (doc?.IsConnected != true && HostCompletionProvider is null)
            return "LSP: no language server for this file type";

        if (doc?.IsConnected == true && !_documentReady && HostCompletionProvider is null)
            return "LSP: indexing… try again in a moment";

        Log($"[LSP] completion request line={line} col={character}");

        // Cancel any in-flight request so it is both discarded and stops working.
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _completionCts = new CancellationTokenSource();
        var requestCts = _completionCts;
        var documentGeneration = Volatile.Read(ref _documentGeneration);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            requestCts.Token, timeoutCts.Token);
        var ct = linkedCts.Token;

        IReadOnlyList<LspCompletionItem> items;
        try
        {
            items = doc?.IsConnected == true && _documentReady
                ? await doc.RequestCompletionAsync(line, character, ct)
                : [];
            if (items.Count == 0 && HostCompletionProvider is { } provider)
                items = await provider(line, character, ct);
        }
        catch (OperationCanceledException)
        {
            if (!ReferenceEquals(_completionCts, requestCts))
                return null;
            if (!timeoutCts.IsCancellationRequested)
                return null;
            Log("[LSP] completion request timed out");
            return "LSP: completion request timed out";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_completionCts, requestCts)) return null;
            Log($"[LSP] completion request failed: {ex.Message}");
            return $"LSP: completion failed — {ex.Message}";
        }

        Log($"[LSP] completion got {items.Count} items");

        if (ct.IsCancellationRequested || !ReferenceEquals(doc, _document) ||
            documentGeneration != Volatile.Read(ref _documentGeneration))
        {
            Log($"[LSP] completion response discarded reason=" +
                (ct.IsCancellationRequested ? "superseded" : "document-modified"));
            return null;
        }

        bool applied = false;
        await _dispatcher.InvokeAsync(() =>
        {
            // Re-check on the dispatcher thread: a newer request or an explicit
            // HideCompletion may have happened while this apply was queued.
            if (ct.IsCancellationRequested || !ReferenceEquals(doc, _document) ||
                documentGeneration != Volatile.Read(ref _documentGeneration)) return;
            _rawCompletionItems = items;
            _completionItems = CompletionRanker.Rank(items, "");
            _completionSelection = FindInitialSelection(_completionItems);
            _completionScrollOffset = InitialScrollOffset(_completionSelection);
            _completionVisible = items.Count > 0;
            applied = true;
            StateChanged?.Invoke();
        });

        if (!applied) return null;
        return items.Count > 0 ? "" : "LSP: no completions at this position";
    }

    /// <summary>Request hover info at the given position.</summary>
    public async Task<string?> RequestHoverAsync(int line, int character)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true)
        {
            var hover = await doc.RequestHoverAsync(line, character);
            if (!string.IsNullOrWhiteSpace(hover?.Value)) return hover.Value;
        }
        if (HostHoverProvider is { } provider)
            return await provider(line, character, CancellationToken.None);
        return null;
    }

    /// <summary>Request go-to-definition. Returns (localFilePath, line, column) or null.</summary>
    public async Task<(string FilePath, int Line, int Column)?> RequestDefinitionAsync(int line, int character)
    {
        if (await RequestDefinitionLocationAsync(line, character) is not { } location)
            return null;
        var localPath = LspUri.TryToLocalPath(location.Uri);
        return localPath is null ? null : (localPath, location.Line, location.Column);
    }

    /// <summary>Request the definition while preserving an external URI for host peek surfaces.</summary>
    public async Task<(string Uri, int Line, int Column)?> RequestDefinitionLocationAsync(int line, int character)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true)
        {
            var result = await doc.RequestDefinitionAsync(line, character);
            if (result is not null) return result;
        }
        if (HostDefinitionProvider is { } provider)
            return await provider(line, character, CancellationToken.None);
        return null;
    }

    public async Task<LspCompletionItem?> ResolveCompletionAsync(
        LspCompletionItem item, CancellationToken ct = default)
    {
        var doc = _document;
        if (!_documentReady || doc?.IsConnected != true || !doc.ServerSupportsCompletionResolve)
            return null;
        return await doc.ResolveCompletionAsync(item, ct);
    }

    public async Task<IReadOnlyList<LspLocation>> RequestImplementationAsync(
        int line, int character, CancellationToken ct = default)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true && doc.ServerSupportsImplementation)
        {
            var result = await doc.RequestImplementationAsync(line, character, ct);
            if (result.Count > 0) return result;
        }
        if (HostImplementationProvider is { } provider)
            return await provider(line, character, ct);
        return [];
    }

    public async Task<IReadOnlyList<LspLocation>> RequestTypeDefinitionAsync(
        int line, int character, CancellationToken ct = default)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true && doc.ServerSupportsTypeDefinition)
        {
            var result = await doc.RequestTypeDefinitionAsync(line, character, ct);
            if (result.Count > 0) return result;
        }
        if (HostTypeDefinitionProvider is { } provider)
            return await provider(line, character, ct);
        return [];
    }

    public async Task<IReadOnlyList<LspLocation>> RequestDeclarationAsync(
        int line, int character, CancellationToken ct = default)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true && doc.ServerSupportsDeclaration)
        {
            var result = await doc.RequestDeclarationAsync(line, character, ct);
            if (result.Count > 0) return result;
        }
        if (HostDeclarationProvider is { } provider)
            return await provider(line, character, ct);
        return [];
    }

    public async Task<LspRange?> PrepareRenameAsync(
        int line, int character, CancellationToken ct = default)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true && doc.ServerSupportsPrepareRename)
        {
            var range = await doc.PrepareRenameAsync(line, character, ct);
            if (range is not null) return range;
        }
        return HostPrepareRenameProvider is { } provider
            ? await provider(line, character, ct)
            : null;
    }

    public Task<bool> ExecuteCompletionCommandAsync(
        LspCompletionCommand command, CancellationToken ct = default)
    {
        var doc = _document;
        if (!_documentReady || doc?.IsConnected != true) return Task.FromResult(false);
        return doc.ExecuteCommandAsync(new LspCodeActionCommand(
            command.Command, command.Title, command.ArgumentsJson), ct);
    }

    public void MoveCompletionSelection(int delta)
    {
        if (!_completionVisible || _completionItems.Count == 0) return;
        _completionSelection = (_completionSelection + delta + _completionItems.Count) % _completionItems.Count;
        // Adjust scroll offset to keep the selection visible
        if (_completionSelection < _completionScrollOffset)
            _completionScrollOffset = _completionSelection;
        else if (_completionSelection >= _completionScrollOffset + MaxVisibleCompletion)
            _completionScrollOffset = _completionSelection - MaxVisibleCompletion + 1;
        StateChanged?.Invoke();
    }

    public LspCompletionItem? GetSelectedCompletion() =>
        _completionVisible && _completionSelection >= 0 && _completionSelection < _completionItems.Count
            ? _completionItems[_completionSelection]
            : null;

    /// <summary>
    /// Filter the current completion list by prefix (case-insensitive prefix match).
    /// Hides the popup if no items match.
    /// </summary>
    public void FilterCompletion(string prefix)
    {
        if (_rawCompletionItems.Count == 0) return;

        IReadOnlyList<LspCompletionItem> filtered = CompletionRanker.Rank(_rawCompletionItems, prefix);

        if (filtered.Count == 0)
        {
            HideCompletion();
            return;
        }

        _completionItems = filtered;
        _completionVisible = true;
        _completionSelection = FindInitialSelection(filtered);
        _completionScrollOffset = InitialScrollOffset(_completionSelection);
        StateChanged?.Invoke();
    }

    internal static int FindInitialSelection(IReadOnlyList<LspCompletionItem> items)
    {
        if (items.Count == 0) return -1;
        var preselected = items.ToList().FindIndex(i => i.Preselect);
        return preselected >= 0 ? preselected : 0;
    }

    internal static int InitialScrollOffset(int selection) =>
        selection < MaxVisibleCompletion ? 0 : selection - MaxVisibleCompletion + 1;

    public void HideCompletion()
    {
        // Cancel any in-flight completion request so a late response
        // cannot re-open the popup after an explicit close.
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _completionCts = null;
        if (!_completionVisible && _rawCompletionItems.Count == 0) return;
        _completionVisible = false;
        _rawCompletionItems = [];
        _completionItems = [];
        _completionSelection = -1;
        _completionScrollOffset = 0;
        StateChanged?.Invoke();
    }

    public void RecordCompletionAccepted(LspCompletionItem item) =>
        Log($"[LSP] completion accepted label={item.Label} kind={item.Kind} textEdit={item.TextEdit is not null} snippet={item.TextFormat == InsertTextFormat.Snippet}");

    /// <summary>Request signature help at the given position.</summary>
    public async Task RequestSignatureHelpAsync(int line, int character)
    {
        // Cancel any in-flight request so it is both discarded and stops working.
        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _signatureHelpCts = new CancellationTokenSource();
        var ct = _signatureHelpCts.Token;
        var doc = _document;
        LspSignatureHelp? help = null;
        if (doc?.IsConnected == true && _documentReady)
        {
            try
            {
                help = await doc.RequestSignatureHelpAsync(line, character, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A language-specific host provider can still answer when the LSP
                // server does not implement signatureHelp or failed the request.
            }
        }

        if (help?.Signatures.Count is not > 0 && HostSignatureHelpProvider is { } provider)
        {
            try
            {
                help = await provider(line, character, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                help = null;
            }
        }

        await _dispatcher.InvokeAsync(() =>
        {
            // Re-check on the dispatcher thread: a newer request or an explicit
            // HideSignatureHelp may have happened while this apply was queued.
            if (ct.IsCancellationRequested) return;
            _signatureHelp = help?.Signatures.Count > 0 ? help : null;
            StateChanged?.Invoke();
        });
    }

    public void HideSignatureHelp()
    {
        // Cancel any in-flight request so a late response cannot re-show
        // the popup after an explicit close (e.g. typing ')').
        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _signatureHelpCts = null;
        if (_signatureHelp == null) return;
        _signatureHelp = null;
        StateChanged?.Invoke();
    }

    /// <summary>Request rename workspace edit.</summary>
    public async Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true)
        {
            var edit = await doc.RequestRenameAsync(line, character, newName);
            if (edit is { Changes.Count: > 0 }) return edit;
        }
        if (HostRenameProvider is { } provider)
            return await provider(line, character, newName, CancellationToken.None);
        return null;
    }

    /// <summary>Request all references at the given position.</summary>
    public async Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character)
    {
        var doc = _document;
        if (_documentReady && doc?.IsConnected == true)
        {
            var result = await doc.RequestReferencesAsync(line, character);
            if (result.Count > 0) return result;
        }
        if (HostReferencesProvider is { } provider)
            return await provider(line, character, CancellationToken.None);
        return [];
    }

    /// <summary>Request code actions at the given cursor line.</summary>
    public async Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character)
    {
        var doc = _document;
        if (!_documentReady || doc?.IsConnected != true) return [];
        var generation = Volatile.Read(ref _documentGeneration);
        var actions = await doc.RequestCodeActionsAsync(line, character);
        if (!ReferenceEquals(doc, _document) || generation != Volatile.Read(ref _documentGeneration))
        {
            StatusMessage?.Invoke("Code Action: 文書が変更されたため古い応答を破棄しました。再度実行してください。");
            return [];
        }
        return actions;
    }

    /// <summary>Request code actions covering a range (Extract Method 等は1点では出ない).</summary>
    public async Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(
        LspRange range, IReadOnlyList<string>? only)
    {
        var doc = _document;
        if (!_documentReady || doc?.IsConnected != true) return [];
        var generation = Volatile.Read(ref _documentGeneration);
        var actions = await doc.RequestCodeActionsAsync(range, only);
        if (!ReferenceEquals(doc, _document) || generation != Volatile.Read(ref _documentGeneration))
        {
            StatusMessage?.Invoke("Code Action: 文書が変更されたため古い応答を破棄しました。再度実行してください。");
            return [];
        }
        return actions;
    }

    public Task<LspCodeAction?> ResolveCodeActionAsync(LspCodeAction action)
    {
        var doc = _document;
        return _documentReady && doc?.IsConnected == true
            ? doc.ResolveCodeActionAsync(action)
            : Task.FromResult<LspCodeAction?>(null);
    }

    public Task<bool> ExecuteCodeActionCommandAsync(LspCodeActionCommand command)
    {
        var doc = _document;
        return _documentReady && doc?.IsConnected == true
            ? doc.ExecuteCommandAsync(command)
            : Task.FromResult(false);
    }

    public Task<LspCodeLens?> ResolveCodeLensAsync(LspCodeLens lens)
    {
        var doc = _document;
        return _documentReady && doc?.IsConnected == true
            ? doc.ResolveCodeLensAsync(lens)
            : Task.FromResult<LspCodeLens?>(null);
    }

    public Task<bool> ExecuteCodeLensCommandAsync(LspCodeActionCommand command)
    {
        var doc = _document;
        return _documentReady && doc?.IsConnected == true
            ? doc.ExecuteCommandAsync(command)
            : Task.FromResult(false);
    }

    /// <summary>Show code actions popup with the given items.</summary>
    public void ShowCodeActions(IReadOnlyList<LspCodeAction> actions)
    {
        _codeActions = actions;
        _codeActionsSelection = 0;
        _codeActionsScrollOffset = 0;
        _codeActionsVisible = true;
        StateChanged?.Invoke();
    }

    /// <summary>Hide code actions popup.</summary>
    public void HideCodeActions()
    {
        if (!_codeActionsVisible && _codeActions.Count == 0) return;
        _codeActionsVisible = false;
        _codeActions = [];
        _codeActionsScrollOffset = 0;
        StateChanged?.Invoke();
    }

    /// <summary>Move code actions selection by delta, adjusting scroll offset to keep selection visible.</summary>
    public void MoveCodeActionsSelection(int delta)
    {
        if (_codeActions.Count == 0) return;
        _codeActionsSelection = (_codeActionsSelection + delta + _codeActions.Count) % _codeActions.Count;
        if (_codeActionsSelection < _codeActionsScrollOffset)
            _codeActionsScrollOffset = _codeActionsSelection;
        else if (_codeActionsSelection >= _codeActionsScrollOffset + MaxVisibleCompletion)
            _codeActionsScrollOffset = _codeActionsSelection - MaxVisibleCompletion + 1;
        StateChanged?.Invoke();
    }

    /// <summary>Request formatting edits for the current document.</summary>
    public async Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize = 4, bool insertSpaces = true)
    {
        var doc = _document;
        if (doc?.IsConnected != true || !_documentReady) return [];
        return await doc.RequestFormattingAsync(tabSize, insertSpaces);
    }

    /// <summary>Request formatting edits for just <paramref name="range"/> of the current document.</summary>
    public async Task<IReadOnlyList<LspTextEdit>> RequestRangeFormattingAsync(LspRange range, int tabSize = 4, bool insertSpaces = true)
    {
        var doc = _document;
        if (doc?.IsConnected != true || !_documentReady) return [];
        if (!doc.ServerSupportsRangeFormatting) return [];
        return await doc.RequestRangeFormattingAsync(range, tabSize, insertSpaces);
    }

    /// <summary>Returns the current cached document symbols for the active file.</summary>
    public IReadOnlyList<DocumentSymbol> GetDocumentSymbols() => _documentSymbols;

    /// <summary>Fetches document symbols directly from the server (bypasses debounce), updates the cache, and returns the result.</summary>
    public async Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync()
    {
        var doc = _document;
        if (doc?.IsConnected != true || !_documentReady) return [];
        await RefreshDocumentSymbolsAsync(doc);
        return _documentSymbols;
    }

    /// <summary>
    /// Returns breadcrumb path string for the given 0-based line/column (e.g. "MyClass > MyMethod").
    /// Returns null if no symbols are loaded or no symbol contains the cursor.
    /// </summary>
    public string? GetBreadcrumb(int line, int col)
    {
        var segs = GetBreadcrumbSegments(line, col);
        return segs.Count > 0 ? string.Join(" > ", segs.Select(s => s.Name)) : null;
    }

    /// <summary>
    /// Returns the symbol path from the outermost enclosing symbol down to the innermost one
    /// containing the cursor (see <see cref="BreadcrumbBuilder"/>). Empty when no symbol
    /// contains the cursor.
    /// </summary>
    public IReadOnlyList<BreadcrumbSegment> GetBreadcrumbSegments(int line, int col)
        => BreadcrumbBuilder.GetSegments(_documentSymbols, line, col);

    /// <summary>
    /// Update breadcrumb for the current cursor position.
    /// Should be called when the cursor moves (in Normal mode).
    /// Fires BreadcrumbChanged if the path changed.
    /// </summary>
    public void UpdateBreadcrumb(int line, int col)
    {
        var path = GetBreadcrumb(line, col) ?? "";
        if (path == _lastBreadcrumb) return;
        _lastBreadcrumb = path;
        BreadcrumbChanged?.Invoke(path);
    }

    /// <summary>
    /// Clear the current breadcrumb (e.g. when the feature is disabled).
    /// Fires BreadcrumbChanged with an empty string if there was a previous breadcrumb.
    /// </summary>
    public void ClearBreadcrumb()
    {
        if (_lastBreadcrumb == "") return;
        _lastBreadcrumb = "";
        BreadcrumbChanged?.Invoke("");
    }

    /// <summary>Request document highlights at the given position with a 150ms debounce.</summary>
    public async Task RequestDocumentHighlightAsync(string uri, int line, int character)
    {
        // Cancel and dispose any in-flight highlight request
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        _highlightCts = new CancellationTokenSource();
        var ct = _highlightCts.Token;

        try
        {
            await Task.Delay(150, ct);
            var doc = _document;
            IReadOnlyList<DocumentHighlight>? highlights = null;
            if (_documentReady && doc?.IsConnected == true && doc.Uri == uri &&
                doc.ServerSupportsDocumentHighlight)
                highlights = await doc.RequestDocumentHighlightAsync(line, character, ct);

            if (highlights is not { } && HostDocumentHighlightProvider is { } provider)
                highlights = await provider(line, character, ct);
            if (highlights is not { }) return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested ||
                    _currentUri != uri ||
                    (doc is not null && !ReferenceEquals(_document, doc))) return;
                _documentHighlights = highlights;
                DocumentHighlightsChanged?.Invoke(_documentHighlights);
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>Clear document highlights immediately.</summary>
    public void ClearDocumentHighlights()
    {
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        _highlightCts = null;
        if (_documentHighlights is null or { Count: 0 }) return;
        _documentHighlights = [];
        DocumentHighlightsChanged?.Invoke([]);
    }

    /// <summary>Request the selection range tree at the given position.</summary>
    public async Task<LspSelectionRange?> RequestSelectionRangeAsync(int line, int character)
    {
        var doc = _document;
        if (!_documentReady || doc?.IsConnected != true) return null;
        if (!doc.ServerSupportsSelectionRange) return null;
        return await doc.RequestSelectionRangeAsync(line, character);
    }

    // ── Async helpers ──────────────────────────────────────────────────────

    private async Task RequestFoldingRangesInternalAsync(ILspDocument doc)
    {
        if (!doc.ServerSupportsFoldingRange)
        {
            Log($"[LSP] foldingRange: server does not support textDocument/foldingRange, skipping");
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_document, doc))
                    FoldingRangesChanged?.Invoke([]);   // VimEditorControl 側でフォールバックを適用する
            });
            return;
        }

        try
        {
            var ranges = await doc.RequestFoldingRangesAsync();
            Log($"[LSP] foldingRange: {ranges.Count} ranges");
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_document, doc))
                    FoldingRangesChanged?.Invoke(ranges);
            });
        }
        catch (Exception ex)
        {
            Log($"[LSP] foldingRange failed: {ex.Message}");
        }
    }

    /// <summary>Re-request folding ranges for the current document (e.g. after saving).</summary>
    public void RequestFoldingRanges()
    {
        var doc = _document;
        if (!_documentReady || doc?.IsConnected != true) return;
        _ = RequestFoldingRangesInternalAsync(doc);
    }

    /// <summary>Enable or disable inlay hints. When enabled, immediately fetches hints for the whole file.</summary>
    public void SetInlayHintsEnabled(bool enabled)
    {
        _inlayHintsEnabled = enabled;
        if (enabled)
            RequestInlayHints(0, int.MaxValue);
        else
            ClearInlayHints();
    }

    /// <summary>Request inlay hints for the given line range (0-based, inclusive).</summary>
    public void RequestInlayHints(int startLine, int endLine)
    {
        if (!_inlayHintsEnabled) return;
        _inlayHintsCts?.Cancel();
        _inlayHintsCts?.Dispose();
        _inlayHintsCts = new CancellationTokenSource();
        _ = RequestInlayHintsInternalAsync(
            _document, startLine, endLine, _inlayHintsCts.Token);
    }

    private void ClearInlayHints()
    {
        _inlayHintsCts?.Cancel();
        _inlayHintsCts?.Dispose();
        _inlayHintsCts = null;
        _inlayHintDebounce?.Stop();
        _inlayHints = [];
        InlayHintsChanged?.Invoke(_inlayHints);
    }

    private void ScheduleInlayHintRefresh()
    {
        _inlayHintDebounce ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(InlayHintDebounceMs),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                _inlayHintDebounce!.Stop();
                RequestInlayHints(0, int.MaxValue);
            },
            _dispatcher);
        _inlayHintDebounce.Stop();
        _inlayHintDebounce.Start();
    }

    private async Task RequestInlayHintsInternalAsync(
        ILspDocument? doc, int startLine, int endLine, CancellationToken ct)
    {
        IReadOnlyList<InlayHint> hints = [];
        try
        {
            if (doc?.IsConnected == true && _documentReady)
                hints = await doc.RequestInlayHintsAsync(startLine, endLine);
            if (hints.Count == 0 && HostInlayHintProvider is { } provider)
                hints = await provider(startLine, endLine, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            hints = [];
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return;
            if (doc is not null && !ReferenceEquals(_document, doc)) return;
            _inlayHints = hints;
            InlayHintsChanged?.Invoke(_inlayHints);
        });
    }

    /// <summary>Enable or disable semantic token highlighting. When enabled, immediately fetches tokens for the current file.</summary>
    public void SetSemanticTokensEnabled(bool enabled)
    {
        _semanticTokensEnabled = enabled;
        if (enabled)
            RequestSemanticTokens();
        else
        {
            _semanticTokenResultId = null;
            SemanticTokensChanged?.Invoke([]);
        }
    }

    /// <summary>Request semantic tokens for the current document.</summary>
    public void RequestSemanticTokens()
    {
        var doc = _document;
        if (!_semanticTokensEnabled) return;
        if (_documentReady && doc?.IsConnected == true)
        {
            _ = RequestSemanticTokensInternalAsync(doc);
            return;
        }
        if (HostSemanticTokensProvider is { } hostProvider)
            _ = RequestHostSemanticTokensAsync(hostProvider, doc);
    }

    private async Task RequestHostSemanticTokensAsync(
        Func<CancellationToken, Task<IReadOnlyList<SemanticToken>>> provider,
        ILspDocument? expectedDocument)
    {
        try
        {
            var tokens = await provider(CancellationToken.None);
            await _dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_document, expectedDocument))
                    SemanticTokensChanged?.Invoke(tokens.ToArray());
            });
        }
        catch { }
    }

    private async Task RequestSemanticTokensInternalAsync(ILspDocument doc)
    {
        try
        {
            await _semanticTokenRequestGate.WaitAsync();
            try
            {
                var result = await doc.RequestSemanticTokensResultAsync(_semanticTokenResultId);
                if ((result is null || result.Tokens.Length == 0) &&
                    HostSemanticTokensProvider is { } hostProvider)
                {
                    var hostTokens = await hostProvider(CancellationToken.None);
                    result = new SemanticTokensResult(null, hostTokens.ToArray());
                }
                await _dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_document, doc)) return;
                    if (result is null) return;
                    _semanticTokenResultId = result.ResultId;
                    SemanticTokensChanged?.Invoke(result.Tokens);
                });
            }
            finally
            {
                _semanticTokenRequestGate.Release();
            }
        }
        catch
        {
            if (HostSemanticTokensProvider is { } hostProvider)
                await RequestHostSemanticTokensAsync(hostProvider, doc);
        }
    }

    // ── Debug log ──────────────────────────────────────────────────────────

    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "editor-lsp-debug.log");
    private static readonly bool _diagnosticLogEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SK0YA_EDITOR_IDE_DIAG"), "1", StringComparison.Ordinal);

    private static void Log(string msg)
    {
        if (!_diagnosticLogEnabled) return;
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    public void Dispose()
    {
        if (_viewDisposed) return;
        _viewDisposed = true;
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _inlayHintsCts?.Cancel();
        _inlayHintsCts?.Dispose();
        _signatureHelpCts?.Cancel();
        _signatureHelpCts?.Dispose();
        _inlayHintDebounce?.Stop();
        _inlayHintDebounce = null;
        _symbolDebounce?.Dispose();
        _semanticTokenDebounce?.Dispose();
        _documentLinkDebounce?.Dispose();
        _codeLensDebounce?.Dispose();
        _semanticTokenRequestGate.Dispose();
        DetachDocument();
    }
}
