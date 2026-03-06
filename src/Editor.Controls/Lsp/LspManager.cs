using System.IO;
using System.Threading;
using System.Windows.Threading;
using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>
/// Manages LSP clients and bridges them with the editor.
/// One LspClient per language server executable (shared across files of the same language).
/// </summary>
public sealed class LspManager : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, LspClient> _clients = new();
    private readonly object _docLock = new();
    private readonly HashSet<string> _openDocuments = new();

    private string? _currentUri;
    private LspClient? _currentClient;
    private int _docVersion = 1;
    private bool _documentReady;   // didOpen sent and acknowledged
    // Set after didOpen so that the first publishDiagnostics triggers a fold range retry.
    // (Some servers are not ready to answer foldingRange immediately after didOpen.)
    private volatile string? _pendingFoldRangeUri;

    // State visible to the UI (always accessed on dispatcher thread)
    private IReadOnlyList<LspDiagnostic> _diagnostics = [];
    private IReadOnlyList<LspCompletionItem> _rawCompletionItems = [];  // full server response
    private IReadOnlyList<LspCompletionItem> _completionItems = [];     // filtered view
    private int _completionSelection = -1;
    private int _completionScrollOffset = 0;
    private bool _completionVisible;

    private const int MaxVisibleCompletion = 10;

    private LspSignatureHelp? _signatureHelp;

    private IReadOnlyList<LspCodeAction> _codeActions = [];
    private int _codeActionsSelection = 0;
    private int _codeActionsScrollOffset = 0;
    private bool _codeActionsVisible;

    // Inlay hints
    private IReadOnlyList<InlayHint> _inlayHints = [];
    private bool _inlayHintsEnabled = false;

    // Semantic tokens
    private bool _semanticTokensEnabled = false;
    private System.Threading.Timer? _semanticTokenDebounce;
    private const int SemanticTokenDebounceMs = 500;

    // Document symbols (for breadcrumb and :Symbols)
    private IReadOnlyList<DocumentSymbol> _documentSymbols = [];
    private System.Threading.Timer? _symbolDebounce;
    private string _lastBreadcrumb = "";
    private const int SymbolDebounceMs = 1000;

    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => _diagnostics;
    public IReadOnlyList<LspCompletionItem> CompletionItems => _completionItems;
    public int CompletionSelection => _completionSelection;
    public int CompletionScrollOffset => _completionScrollOffset;
    public bool CompletionVisible => _completionVisible;
    public LspSignatureHelp? CurrentSignatureHelp => _signatureHelp;
    public IReadOnlyList<LspCodeAction> CurrentCodeActions => _codeActions;
    public int CodeActionsSelection => _codeActionsSelection;
    public int CodeActionsScrollOffset => _codeActionsScrollOffset;
    public bool CodeActionsVisible => _codeActionsVisible;

    /// <summary>True when the server is running for the current file.</summary>
    public bool IsConnected => _currentClient?.IsRunning == true && _currentUri != null;

    /// <summary>True when initialization + didOpen completed for the current file.</summary>
    public bool IsDocumentReady => _documentReady;

    /// <summary>現在のサーバーが textDocument/foldingRange をサポートしているか。</summary>
    public bool ServerSupportsFoldingRange => _currentClient?.SupportsFoldingRange == true;

    /// <summary>Fired on the dispatcher thread for status bar messages.</summary>
    public event Action<string>? StatusMessage;

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

    public string? CurrentUri => _currentUri;

    public LspManager(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Call when a file is opened or the active buffer changes.</summary>
    public void OnFileOpened(string? filePath, string text)
    {
        HideCompletion();
        HideCodeActions();
        _diagnostics = [];
        _inlayHints = [];
        _documentSymbols = [];
        if (_semanticTokensEnabled) SemanticTokensChanged?.Invoke([]);
        _lastBreadcrumb = "";
        _documentReady = false;
        _pendingFoldRangeUri = null;
        _symbolDebounce?.Dispose();
        _symbolDebounce = null;
        _semanticTokenDebounce?.Dispose();
        _semanticTokenDebounce = null;

        if (filePath == null)
        {
            _currentUri = null;
            _currentClient = null;
            StateChanged?.Invoke();
            return;
        }

        var ext = Path.GetExtension(filePath);
        var def = LspServerConfig.GetForExtension(ext);
        if (def == null)
        {
            _currentUri = null;
            _currentClient = null;
            StateChanged?.Invoke();
            FoldingRangesChanged?.Invoke([]);   // LSP サーバーなし → シンタックスフォールドへフォールバック
            return;
        }

        var uri = PathToUri(filePath);

        // Close previous document when switching files on the same server
        if (_currentClient?.IsRunning == true && _currentUri != null && _currentUri != uri)
        {
            _ = CloseSafeAsync(_currentClient, _currentUri);
            lock (_docLock) _openDocuments.Remove(_currentUri);
        }

        _docVersion = 1;

        bool isNew = !_clients.TryGetValue(def.Executable, out var client);
        if (isNew)
        {
            try
            {
                client = new LspClient(def.Executable, def.Args, FindWorkspaceRoot(filePath));
                client.DiagnosticsChanged += OnDiagnosticsChanged;
                _clients[def.Executable] = client;
                Log($"[LSP] Process started: {def.Executable}");
            }
            catch (Exception ex)
            {
                Log($"[LSP] Failed to start {def.Executable}: {ex.Message}");
                _currentUri = null;
                _currentClient = null;
                FoldingRangesChanged?.Invoke([]);   // サーバー起動失敗 → シンタックスフォールドへフォールバック
                return;
            }
        }

        _currentUri = uri;
        _currentClient = client;

        bool alreadyOpen;
        lock (_docLock) alreadyOpen = _openDocuments.Contains(uri);

        if (isNew)
            _ = InitThenOpenAsync(client!, filePath, uri, def.LanguageId, text);
        else if (!alreadyOpen)
            _ = OpenSafeAsync(client!, uri, def.LanguageId, text);
        else
        {
            _documentReady = true; // already open from a previous visit
            _ = RequestFoldingRangesInternalAsync(client!, uri);
        }

        StateChanged?.Invoke();
    }

    /// <summary>Call whenever text content changes.</summary>
    public void OnTextChanged(string text)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return;
        _ = _currentClient.ChangeDocumentAsync(_currentUri, ++_docVersion, text);
        ScheduleSymbolRefresh();
        if (_semanticTokensEnabled)
            ScheduleSemanticTokenRefresh();
    }

    private void ScheduleSemanticTokenRefresh()
    {
        _semanticTokenDebounce?.Dispose();
        _semanticTokenDebounce = new System.Threading.Timer(_ =>
        {
            var client = _currentClient;
            var uri    = _currentUri;
            if (client?.IsRunning == true && uri != null && _documentReady && _semanticTokensEnabled)
                _ = RequestSemanticTokensInternalAsync(client, uri);
        }, null, SemanticTokenDebounceMs, Timeout.Infinite);
    }

    private void ScheduleSymbolRefresh()
    {
        _symbolDebounce?.Dispose();
        _symbolDebounce = new System.Threading.Timer(_ =>
        {
            var client = _currentClient;
            var uri = _currentUri;
            if (client?.IsRunning == true && uri != null && _documentReady)
                _ = RefreshDocumentSymbolsAsync(client, uri);
        }, null, SymbolDebounceMs, Timeout.Infinite);
    }

    private async Task RefreshDocumentSymbolsAsync(LspClient client, string uri)
    {
        try
        {
            var symbols = await client.GetDocumentSymbolsAsync(uri);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_currentUri != uri) return;
                _documentSymbols = symbols;
            });
        }
        catch { }
    }

    /// <summary>Trigger LSP completion at the given position. Returns a status message.</summary>
    public async Task<string> RequestCompletionAsync(int line, int character)
    {
        if (_currentClient?.IsRunning != true || _currentUri == null)
            return "LSP: no language server for this file type";

        if (!_documentReady)
            return "LSP: indexing… try again in a moment";

        Log($"[LSP] completion request line={line} col={character}");

        var items = await _currentClient.GetCompletionAsync(
            _currentUri, new LspPosition(line, character));

        Log($"[LSP] completion got {items.Count} items");

        await _dispatcher.InvokeAsync(() =>
        {
            _rawCompletionItems = items;
            _completionItems = items;
            _completionSelection = items.Count > 0 ? 0 : -1;
            _completionScrollOffset = 0;
            _completionVisible = items.Count > 0;
            StateChanged?.Invoke();
        });

        return items.Count > 0 ? "" : "LSP: no completions at this position";
    }

    /// <summary>Request hover info at the given position.</summary>
    public async Task<string?> RequestHoverAsync(int line, int character)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return null;
        var hover = await _currentClient.GetHoverAsync(_currentUri, new LspPosition(line, character));
        return hover?.Value;
    }

    /// <summary>Request go-to-definition. Returns (localFilePath, line, column) or null.</summary>
    public async Task<(string FilePath, int Line, int Column)?> RequestDefinitionAsync(int line, int character)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return null;
        var result = await _currentClient.GetDefinitionAsync(_currentUri, new LspPosition(line, character));
        if (result == null) return null;
        try
        {
            var parsed = new Uri(result.Value.Uri);
            if (!parsed.IsFile) return null;
            return (parsed.LocalPath, result.Value.Line, result.Value.Column);
        }
        catch { return null; }
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

        IReadOnlyList<LspCompletionItem> filtered = string.IsNullOrEmpty(prefix)
            ? _rawCompletionItems
            : (IReadOnlyList<LspCompletionItem>)_rawCompletionItems
                .Where(i => (i.FilterText ?? i.Label).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (filtered.Count == 0)
        {
            HideCompletion();
            return;
        }

        _completionItems = filtered;
        _completionVisible = true;
        _completionSelection = 0;
        _completionScrollOffset = 0;
        StateChanged?.Invoke();
    }

    public void HideCompletion()
    {
        if (!_completionVisible && _rawCompletionItems.Count == 0) return;
        _completionVisible = false;
        _rawCompletionItems = [];
        _completionItems = [];
        _completionSelection = -1;
        _completionScrollOffset = 0;
        StateChanged?.Invoke();
    }

    /// <summary>Request signature help at the given position.</summary>
    public async Task RequestSignatureHelpAsync(int line, int character)
    {
        if (_currentClient?.IsRunning != true || _currentUri == null || !_documentReady) return;
        var help = await _currentClient.GetSignatureHelpAsync(
            _currentUri, new LspPosition(line, character));
        await _dispatcher.InvokeAsync(() =>
        {
            _signatureHelp = help?.Signatures.Count > 0 ? help : null;
            StateChanged?.Invoke();
        });
    }

    public void HideSignatureHelp()
    {
        if (_signatureHelp == null) return;
        _signatureHelp = null;
        StateChanged?.Invoke();
    }

    /// <summary>Request rename workspace edit.</summary>
    public async Task<LspWorkspaceEdit?> RequestRenameAsync(int line, int character, string newName)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return null;
        return await _currentClient.GetRenameAsync(_currentUri, new LspPosition(line, character), newName);
    }

    /// <summary>Request all references at the given position.</summary>
    public async Task<IReadOnlyList<LspLocation>> RequestReferencesAsync(int line, int character)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return [];
        return await _currentClient.GetReferencesAsync(_currentUri, new LspPosition(line, character));
    }

    /// <summary>Request code actions at the given cursor line.</summary>
    public async Task<IReadOnlyList<LspCodeAction>> RequestCodeActionsAsync(int line, int character)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return [];
        var pos = new LspPosition(line, character);
        var range = new LspRange(pos, pos);
        return await _currentClient.GetCodeActionsAsync(_currentUri, range);
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
        if (_currentClient?.IsRunning != true || _currentUri == null || !_documentReady) return [];
        return await _currentClient.GetFormattingEditsAsync(_currentUri, tabSize, insertSpaces);
    }

    /// <summary>Returns the current cached document symbols for the active file.</summary>
    public IReadOnlyList<DocumentSymbol> GetDocumentSymbols() => _documentSymbols;

    /// <summary>Fetches document symbols directly from the server (bypasses debounce), updates the cache, and returns the result.</summary>
    public async Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsAsync()
    {
        var client = _currentClient;
        var uri = _currentUri;
        if (client?.IsRunning != true || uri == null || !_documentReady) return [];
        await RefreshDocumentSymbolsAsync(client, uri);
        return _documentSymbols;
    }

    /// <summary>
    /// Returns breadcrumb path string for the given 0-based line/column (e.g. "MyClass > MyMethod").
    /// Returns null if no symbols are loaded or no symbol contains the cursor.
    /// </summary>
    public string? GetBreadcrumb(int line, int col)
    {
        if (_documentSymbols.Count == 0) return null;
        var path = new List<string>();
        FindSymbolPath(_documentSymbols, line, col, path);
        return path.Count > 0 ? string.Join(" > ", path) : null;
    }

    private static bool FindSymbolPath(IReadOnlyList<DocumentSymbol> symbols, int line, int col, List<string> path)
    {
        foreach (var sym in symbols)
        {
            if (!ContainsPosition(sym.Range, line, col)) continue;
            path.Add(sym.Name);
            if (sym.Children != null && sym.Children.Length > 0)
                FindSymbolPath(sym.Children, line, col, path);
            return true;
        }
        return false;
    }

    private static bool ContainsPosition(LspRange range, int line, int col)
    {
        if (line < range.Start.Line || line > range.End.Line) return false;
        if (line == range.Start.Line && col < range.Start.Character) return false;
        if (line == range.End.Line && col > range.End.Character) return false;
        return true;
    }

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

    /// <summary>Search workspace symbols by query; when isClass=true, restricts to type-definition kinds.</summary>
    public async Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(
        string query, bool isClass, CancellationToken ct = default)
    {
        var client = _currentClient;
        if (client?.IsRunning != true) return [];
        var symbols = await client.GetWorkspaceSymbolsAsync(query, ct);
        return SymbolSearchFilter.FilterByKind(symbols, isClass);
    }

    /// <summary>Prepare call hierarchy item at the given position.</summary>
    public async Task<CallHierarchyItem?> PrepareCallHierarchyAsync(int line, int character)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return null;
        return await _currentClient.PrepareCallHierarchyAsync(_currentUri, new LspPosition(line, character));
    }

    /// <summary>Get incoming calls for a call hierarchy item.</summary>
    public async Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item)
    {
        if (_currentClient?.IsRunning != true) return null;
        return await _currentClient.GetIncomingCallsAsync(item);
    }

    /// <summary>Get outgoing calls for a call hierarchy item.</summary>
    public async Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item)
    {
        if (_currentClient?.IsRunning != true) return null;
        return await _currentClient.GetOutgoingCallsAsync(item);
    }

    /// <summary>Prepare type hierarchy item at the given position.</summary>
    public async Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(int line, int character)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return null;
        return await _currentClient.PrepareTypeHierarchyAsync(_currentUri, new LspPosition(line, character));
    }

    /// <summary>Get supertypes for a type hierarchy item.</summary>
    public async Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item)
    {
        if (_currentClient?.IsRunning != true) return null;
        return await _currentClient.GetSupertypesAsync(item);
    }

    /// <summary>Get subtypes for a type hierarchy item.</summary>
    public async Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item)
    {
        if (_currentClient?.IsRunning != true) return null;
        return await _currentClient.GetSubtypesAsync(item);
    }

    // ── Async helpers ──────────────────────────────────────────────────────

    private async Task InitThenOpenAsync(LspClient client, string filePath, string uri, string languageId, string text)
    {
        await _dispatcher.InvokeAsync(() => StatusMessage?.Invoke("LSP: initializing…"));
        try
        {
            var rootUri = PathToUri(FindWorkspaceRoot(filePath));
            Log($"[LSP] initialize rootUri={rootUri}");
            await client.InitializeAsync(rootUri);
            Log($"[LSP] initialize OK");
        }
        catch (Exception ex)
        {
            Log($"[LSP] initialize failed: {ex.Message}");
            await _dispatcher.InvokeAsync(() => StatusMessage?.Invoke($"LSP: init failed ({ex.Message})"));
            return;
        }

        await OpenSafeAsync(client, uri, languageId, text);
    }

    private async Task OpenSafeAsync(LspClient client, string uri, string languageId, string text)
    {
        try
        {
            Log($"[LSP] didOpen uri={uri}");
            await client.OpenDocumentAsync(uri, languageId, text);
            lock (_docLock) _openDocuments.Add(uri);

            // Mark ready on the dispatcher thread
            await _dispatcher.InvokeAsync(() =>
            {
                if (_currentUri == uri) // still the active file?
                {
                    _documentReady = true;
                    StatusMessage?.Invoke("LSP: ready");
                    Log($"[LSP] document ready");
                }
            });

            // Request folding ranges after document is ready.
            // Also set _pendingFoldRangeUri so that the first publishDiagnostics triggers a
            // retry — some servers (e.g. csharp-ls) are not ready to answer foldingRange
            // immediately after didOpen and will return an empty list.
            await RequestFoldingRangesInternalAsync(client, uri);
            _pendingFoldRangeUri = uri;
            _ = RefreshDocumentSymbolsAsync(client, uri);
            // Refresh inlay hints if enabled
            if (_inlayHintsEnabled)
                _ = RequestInlayHintsInternalAsync(client, uri, 0, int.MaxValue);
            // Refresh semantic tokens if enabled
            if (_semanticTokensEnabled)
                _ = RequestSemanticTokensInternalAsync(client, uri);
        }
        catch (Exception ex)
        {
            Log($"[LSP] didOpen failed: {ex.Message}");
        }
    }

    private async Task RequestFoldingRangesInternalAsync(LspClient client, string uri)
    {
        if (!client.SupportsFoldingRange)
        {
            Log($"[LSP] foldingRange: server does not support textDocument/foldingRange, skipping");
            await _dispatcher.InvokeAsync(() =>
            {
                if (_currentUri == uri)
                    FoldingRangesChanged?.Invoke([]);   // VimEditorControl 側でフォールバックを適用する
            });
            return;
        }

        try
        {
            var ranges = await client.GetFoldingRangesAsync(uri);
            Log($"[LSP] foldingRange: {ranges.Count} ranges");
            await _dispatcher.InvokeAsync(() =>
            {
                if (_currentUri == uri)
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
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return;
        _ = RequestFoldingRangesInternalAsync(_currentClient, _currentUri);
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
        if (!_inlayHintsEnabled || !_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return;
        _ = RequestInlayHintsInternalAsync(_currentClient, _currentUri, startLine, endLine);
    }

    private void ClearInlayHints()
    {
        _inlayHints = [];
        InlayHintsChanged?.Invoke(_inlayHints);
    }

    private async Task RequestInlayHintsInternalAsync(LspClient client, string uri, int startLine, int endLine)
    {
        try
        {
            var range = new LspRange(new LspPosition(startLine, 0), new LspPosition(endLine, 0));
            var hints = await client.GetInlayHintsAsync(uri, range);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_currentUri != uri) return;
                _inlayHints = hints;
                InlayHintsChanged?.Invoke(_inlayHints);
            });
        }
        catch { }
    }

    /// <summary>Enable or disable semantic token highlighting. When enabled, immediately fetches tokens for the current file.</summary>
    public void SetSemanticTokensEnabled(bool enabled)
    {
        _semanticTokensEnabled = enabled;
        if (enabled)
            RequestSemanticTokens();
        else
            SemanticTokensChanged?.Invoke([]);
    }

    /// <summary>Request semantic tokens for the current document.</summary>
    public void RequestSemanticTokens()
    {
        if (!_semanticTokensEnabled || !_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return;
        _ = RequestSemanticTokensInternalAsync(_currentClient, _currentUri);
    }

    private async Task RequestSemanticTokensInternalAsync(LspClient client, string uri)
    {
        try
        {
            var tokens = await client.GetSemanticTokensAsync(uri);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_currentUri != uri) return;
                SemanticTokensChanged?.Invoke(tokens ?? []);
            });
        }
        catch { }
    }

    private static async Task CloseSafeAsync(LspClient client, string uri)
    {
        try { await client.CloseDocumentAsync(uri); }
        catch { }
    }

    private void OnDiagnosticsChanged(object? sender, DiagnosticsChangedEventArgs e)
    {
        // publishDiagnostics means the server has finished analyzing the file — use this as
        // the signal to retry a fold range request if the initial one came back empty.
        if (e.Uri == _pendingFoldRangeUri && _currentClient?.IsRunning == true)
        {
            _pendingFoldRangeUri = null;
            _ = RequestFoldingRangesInternalAsync(_currentClient, e.Uri);
        }

        _dispatcher.InvokeAsync(() =>
        {
            if (e.Uri != _currentUri) return;
            _diagnostics = e.Diagnostics;
            Log($"[LSP] diagnostics: {e.Diagnostics.Count} items");
            StateChanged?.Invoke();
        });
    }

    private static string PathToUri(string path) =>
        new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>
    /// Walk up from the file's directory to find the workspace root.
    /// Checks for .sln, .git, or common project root markers.
    /// Falls back to the directory containing the nearest project file, then the file's own directory.
    /// </summary>
    private static string FindWorkspaceRoot(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (dir == null) return Environment.CurrentDirectory;

        var current = dir;
        while (current != null)
        {
            if (Directory.EnumerateFiles(current, "*.sln").Any()) return current;
            if (Directory.Exists(Path.Combine(current, ".git"))) return current;
            if (File.Exists(Path.Combine(current, "package.json")) ||
                File.Exists(Path.Combine(current, "Cargo.toml"))   ||
                File.Exists(Path.Combine(current, "go.mod"))        ||
                File.Exists(Path.Combine(current, "pyproject.toml")))
                return current;
            current = Path.GetDirectoryName(current);
        }

        // Fallback: directory containing the nearest project file
        current = dir;
        while (current != null)
        {
            if (Directory.EnumerateFiles(current, "*.csproj").Any() ||
                Directory.EnumerateFiles(current, "*.fsproj").Any())
                return current;
            current = Path.GetDirectoryName(current);
        }

        return dir;
    }

    // ── Debug log ──────────────────────────────────────────────────────────

    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "editor-lsp-debug.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    public void Dispose()
    {
        _symbolDebounce?.Dispose();
        _semanticTokenDebounce?.Dispose();
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}
