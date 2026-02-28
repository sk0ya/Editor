using System.IO;
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

    // State visible to the UI (always accessed on dispatcher thread)
    private IReadOnlyList<LspDiagnostic> _diagnostics = [];
    private IReadOnlyList<LspCompletionItem> _rawCompletionItems = [];  // full server response
    private IReadOnlyList<LspCompletionItem> _completionItems = [];     // filtered view
    private int _completionSelection = -1;
    private int _completionScrollOffset = 0;
    private bool _completionVisible;

    private const int MaxVisibleCompletion = 10;

    private LspSignatureHelp? _signatureHelp;

    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => _diagnostics;
    public IReadOnlyList<LspCompletionItem> CompletionItems => _completionItems;
    public int CompletionSelection => _completionSelection;
    public int CompletionScrollOffset => _completionScrollOffset;
    public bool CompletionVisible => _completionVisible;
    public LspSignatureHelp? CurrentSignatureHelp => _signatureHelp;

    /// <summary>True when the server is running for the current file.</summary>
    public bool IsConnected => _currentClient?.IsRunning == true && _currentUri != null;

    /// <summary>True when initialization + didOpen completed for the current file.</summary>
    public bool IsDocumentReady => _documentReady;

    /// <summary>Fired on the dispatcher thread for status bar messages.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>Fired on the dispatcher thread whenever LSP state changes.</summary>
    public event Action? StateChanged;

    public string? CurrentUri => _currentUri;

    public LspManager(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Call when a file is opened or the active buffer changes.</summary>
    public void OnFileOpened(string? filePath, string text)
    {
        HideCompletion();
        _diagnostics = [];
        _documentReady = false;

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
                client = new LspClient(def.Executable, def.Args, Path.GetDirectoryName(filePath));
                client.DiagnosticsChanged += OnDiagnosticsChanged;
                _clients[def.Executable] = client;
                Log($"[LSP] Process started: {def.Executable}");
            }
            catch (Exception ex)
            {
                Log($"[LSP] Failed to start {def.Executable}: {ex.Message}");
                _currentUri = null;
                _currentClient = null;
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
            _documentReady = true; // already open from a previous visit

        StateChanged?.Invoke();
    }

    /// <summary>Call whenever text content changes.</summary>
    public void OnTextChanged(string text)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return;
        _ = _currentClient.ChangeDocumentAsync(_currentUri, ++_docVersion, text);
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

    /// <summary>Request go-to-definition and return the target file path (or null).</summary>
    public async Task<string?> RequestDefinitionAsync(int line, int character)
    {
        if (!_documentReady || _currentClient?.IsRunning != true || _currentUri == null) return null;
        var uri = await _currentClient.GetDefinitionUriAsync(_currentUri, new LspPosition(line, character));
        if (uri == null) return null;
        try { return new Uri(uri).LocalPath; } catch { return null; }
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

    /// <summary>Request formatting edits for the current document.</summary>
    public async Task<IReadOnlyList<LspTextEdit>> RequestFormattingAsync(int tabSize = 4, bool insertSpaces = true)
    {
        if (_currentClient?.IsRunning != true || _currentUri == null || !_documentReady) return [];
        return await _currentClient.GetFormattingEditsAsync(_currentUri, tabSize, insertSpaces);
    }

    // ── Async helpers ──────────────────────────────────────────────────────

    private async Task InitThenOpenAsync(LspClient client, string filePath, string uri, string languageId, string text)
    {
        await _dispatcher.InvokeAsync(() => StatusMessage?.Invoke("LSP: initializing…"));
        try
        {
            var rootUri = PathToUri(Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory);
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
        }
        catch (Exception ex)
        {
            Log($"[LSP] didOpen failed: {ex.Message}");
        }
    }

    private static async Task CloseSafeAsync(LspClient client, string uri)
    {
        try { await client.CloseDocumentAsync(uri); }
        catch { }
    }

    private void OnDiagnosticsChanged(object? sender, DiagnosticsChangedEventArgs e)
    {
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

    // ── Debug log ──────────────────────────────────────────────────────────

    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "editor-lsp-debug.log");

    private static void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}
