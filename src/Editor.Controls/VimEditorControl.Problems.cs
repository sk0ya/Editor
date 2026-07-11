using Editor.Controls.HostIntegration;
using Editor.Core.Lsp;

namespace Editor.Controls;

public partial class VimEditorControl
{
    private IReadOnlyList<EditorDiagnostic> _hostDiagnostics = [];
    private IReadOnlyList<EditorQuickfixItem> _hostQuickfixItems = [];
    private string _hostQuickfixTitle = "Quickfix";
    private int _hostQuickfixIndex = -1;

    /// <summary>Current host-supplied diagnostics. The returned snapshot is immutable.</summary>
    public IReadOnlyList<EditorDiagnostic> HostDiagnostics => _hostDiagnostics;

    /// <summary>Effective rendered diagnostics, including both LSP and host entries.</summary>
    public IReadOnlyList<EditorDiagnostic> EffectiveDiagnostics
    {
        get
        {
            Dispatcher.VerifyAccess();
            var result = _lspManager.CurrentDiagnostics.Select(static d => new EditorDiagnostic(
                new(new(d.Range.Start.Line, d.Range.Start.Character), new(d.Range.End.Line, d.Range.End.Character)),
                d.Message,
                d.Severity switch
                {
                    DiagnosticSeverity.Error => EditorDiagnosticSeverity.Error,
                    DiagnosticSeverity.Warning => EditorDiagnosticSeverity.Warning,
                    DiagnosticSeverity.Information => EditorDiagnosticSeverity.Information,
                    _ => EditorDiagnosticSeverity.Hint,
                }, d.Source)).Concat(_hostDiagnostics).ToArray();
            return Array.AsReadOnly(result);
        }
    }

    /// <summary>Current host-supplied quickfix entries. The returned snapshot is immutable.</summary>
    public IReadOnlyList<EditorQuickfixItem> HostQuickfixItems => _hostQuickfixItems;

    /// <summary>Title associated with <see cref="HostQuickfixItems"/>.</summary>
    public string HostQuickfixTitle => _hostQuickfixTitle;
    /// <summary>Zero-based current quickfix index, or -1 when no item is selected.</summary>
    public int HostQuickfixIndex => _hostQuickfixIndex;

    /// <summary>Raised after <see cref="ReplaceQuickfixItems"/> or <see cref="ClearQuickfixItems"/>.</summary>
    public event EventHandler<EditorQuickfixChangedEventArgs>? HostQuickfixItemsChanged;
    /// <summary>Raised when :cnext, :cprev, or :cc resolves an injected item.</summary>
    public event EventHandler<EditorQuickfixNavigationEventArgs>? HostQuickfixNavigationRequested;

    /// <summary>
    /// Atomically replaces host diagnostics for the current document. LSP diagnostics remain
    /// present and are rendered alongside these entries.
    /// </summary>
    public void ReplaceDiagnostics(IEnumerable<EditorDiagnostic> diagnostics)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(diagnostics);
        var validated = diagnostics.Select(ValidateDiagnostic).ToArray();
        _hostDiagnostics = Array.AsReadOnly(validated);
        RefreshCombinedDiagnostics();
    }

    /// <summary>Removes all host diagnostics without affecting LSP diagnostics.</summary>
    public void ClearDiagnostics()
    {
        Dispatcher.VerifyAccess();
        _hostDiagnostics = [];
        RefreshCombinedDiagnostics();
    }

    /// <summary>Atomically replaces the host-owned quickfix list.</summary>
    public void ReplaceQuickfixItems(IEnumerable<EditorQuickfixItem> items, string? title = null)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(items);
        var validated = items.Select(ValidateQuickfixItem).ToArray();
        _hostQuickfixItems = Array.AsReadOnly(validated);
        _hostQuickfixIndex = -1;
        _hostQuickfixTitle = string.IsNullOrWhiteSpace(title) ? "Quickfix" : title;
        HostQuickfixItemsChanged?.Invoke(this,
            new EditorQuickfixChangedEventArgs(_hostQuickfixTitle, _hostQuickfixItems));
    }

    /// <summary>Clears the host-owned quickfix list.</summary>
    public void ClearQuickfixItems(string? title = null) => ReplaceQuickfixItems([], title);

    private void NavigateHostQuickfix(int delta)
    {
        if (_hostQuickfixItems.Count == 0) return;
        int start = _hostQuickfixIndex < 0 ? (delta >= 0 ? -1 : 0) : _hostQuickfixIndex;
        _hostQuickfixIndex = ((start + delta) % _hostQuickfixItems.Count + _hostQuickfixItems.Count) % _hostQuickfixItems.Count;
        HostQuickfixNavigationRequested?.Invoke(this,
            new(_hostQuickfixIndex, _hostQuickfixItems[_hostQuickfixIndex]));
    }

    private void GotoHostQuickfix(int oneBasedIndex)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _hostQuickfixItems.Count) return;
        _hostQuickfixIndex = oneBasedIndex - 1;
        HostQuickfixNavigationRequested?.Invoke(this,
            new(_hostQuickfixIndex, _hostQuickfixItems[_hostQuickfixIndex]));
    }

    private void RefreshCombinedDiagnostics()
    {
        var host = _hostDiagnostics.Select(static d => new LspDiagnostic(
            new LspRange(
                new LspPosition(d.Range.Start.Line, d.Range.Start.Column),
                new LspPosition(d.Range.End.Line, d.Range.End.Column)),
            d.Message,
            d.Severity switch
            {
                EditorDiagnosticSeverity.Error => DiagnosticSeverity.Error,
                EditorDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                EditorDiagnosticSeverity.Information => DiagnosticSeverity.Information,
                _ => DiagnosticSeverity.Hint,
            },
            d.Source));
        Canvas.SetDiagnostics(_lspManager.CurrentDiagnostics.Concat(host).ToArray());
    }

    private static EditorDiagnostic ValidateDiagnostic(EditorDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ValidateRange(diagnostic.Range);
        if (string.IsNullOrWhiteSpace(diagnostic.Message))
            throw new ArgumentException("A diagnostic message is required.", nameof(diagnostic));
        return diagnostic;
    }

    private static EditorQuickfixItem ValidateQuickfixItem(EditorQuickfixItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateRange(item.Range);
        if (string.IsNullOrWhiteSpace(item.DocumentPath))
            throw new ArgumentException("A quickfix document path is required.", nameof(item));
        if (string.IsNullOrWhiteSpace(item.Message))
            throw new ArgumentException("A quickfix message is required.", nameof(item));
        return item;
    }

    private static void ValidateRange(EditorTextRange range)
    {
        if (range.Start.Line < 0 || range.Start.Column < 0 || range.End.Line < 0 || range.End.Column < 0 ||
            range.End.Line < range.Start.Line ||
            (range.End.Line == range.Start.Line && range.End.Column < range.Start.Column))
            throw new ArgumentException("The text range must contain non-negative, ordered positions.");
    }
}
