using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Editor.Controls;
using Editor.Core.Models;
using Editor.Core.Search;

namespace Editor.App;

internal enum LocationListSource { Empty, Diagnostics, Search }

internal sealed class ReferenceListItem
{
    public required string FilePath  { get; init; }
    public required string FileName  { get; init; }
    public required string LineCol   { get; init; }
    public required string Preview   { get; init; }
    public required int    Line      { get; init; }
    public required int    Col       { get; init; }
    public bool CurrentBufferOnly { get; init; }
    public string? BufferKey { get; init; }
}

internal sealed class BufferLocationList
{
    public List<ReferenceListItem> Items { get; set; } = [];
    public string Title { get; set; } = "LOCATION LIST";
    public LocationListSource Source { get; set; } = LocationListSource.Empty;
    public string SourceKey { get; set; } = "";
    public LocationListNavigator Navigator { get; } = new();
}

/// <summary>
/// Owns the References/Quickfix panel: LSP find-references results, project grep and
/// replace, and per-buffer location lists (diagnostics/search). Extracted from
/// MainWindow (Phase 7). Reaches shared state it doesn't own (the current/focused
/// editor, all open editors, the sidebar's project folder, the shared input-dialog
/// helper, and opening a file by path) through constructor callbacks.
/// </summary>
internal sealed class ReferencesPanelController
{
    private readonly ListBox _refList;
    private readonly Border _referencesPanel;
    private readonly GridSplitter _refSplitter;
    private readonly RowDefinition _refPanelRow;
    private readonly RowDefinition _refSplitterRow;
    private readonly TextBlock _refPanelTitle;
    private readonly Button _replaceRefResultsBtn;
    private readonly Func<VimEditorControl?> _currentEditor;
    private readonly Func<VimEditorControl?> _focusedEditor;
    private readonly Func<IEnumerable<VimEditorControl>> _allEditors;
    private readonly Func<string?> _getCurrentFolderPath;
    private readonly Func<string, string, string, string?> _showInputDialog;
    private readonly Action<string> _openFile;

    private int _quickfixCurrentIndex = -1;
    private List<ReferenceListItem> _quickfixItems = [];
    private string _quickfixTitle = "REFERENCES";
    private readonly Dictionary<string, BufferLocationList> _locationLists = new(StringComparer.OrdinalIgnoreCase);
    private ProjectSearchOptions? _lastGrepOptions;

    public int QuickfixCurrentIndex { get => _quickfixCurrentIndex; set => _quickfixCurrentIndex = value; }
    public string QuickfixTitle { get => _quickfixTitle; set => _quickfixTitle = value; }
    public ProjectSearchOptions? LastGrepOptions { get => _lastGrepOptions; set => _lastGrepOptions = value; }

    public ReferencesPanelController(
        ListBox refList,
        Border referencesPanel,
        GridSplitter refSplitter,
        RowDefinition refPanelRow,
        RowDefinition refSplitterRow,
        TextBlock refPanelTitle,
        Button replaceRefResultsBtn,
        Func<VimEditorControl?> currentEditor,
        Func<VimEditorControl?> focusedEditor,
        Func<IEnumerable<VimEditorControl>> allEditors,
        Func<string?> getCurrentFolderPath,
        Func<string, string, string, string?> showInputDialog,
        Action<string> openFile)
    {
        _refList = refList;
        _referencesPanel = referencesPanel;
        _refSplitter = refSplitter;
        _refPanelRow = refPanelRow;
        _refSplitterRow = refSplitterRow;
        _refPanelTitle = refPanelTitle;
        _replaceRefResultsBtn = replaceRefResultsBtn;
        _currentEditor = currentEditor;
        _focusedEditor = focusedEditor;
        _allEditors = allEditors;
        _getCurrentFolderPath = getCurrentFolderPath;
        _showInputDialog = showInputDialog;
        _openFile = openFile;
    }

    public void Editor_FindReferencesResult(object? sender, FindReferencesResultEventArgs e)
    {
        var items = e.Items.Select(r =>
        {
            string fileName = Path.GetFileName(r.FilePath);
            string lineCol  = $":{r.Line + 1}:{r.Col + 1}";
            string preview  = r.Preview ?? ReadSourceLine(r.FilePath, r.Line);
            return new ReferenceListItem
            {
                FilePath = r.FilePath,
                FileName = fileName,
                LineCol  = lineCol,
                Preview  = preview,
                Line     = r.Line,
                Col      = r.Col
            };
        }).ToList();

        SetQuickfixItems(items);
        _quickfixCurrentIndex = -1;

        int fileCount = items.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _quickfixTitle = $"{e.TitlePrefix} ({items.Count}) — {e.SymbolName}  [{fileCount} file(s)]";
        _lastGrepOptions = null;

        ShowQuickfixPanel();
    }

    private static string ReadSourceLine(string filePath, int line)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            for (int i = 0; i < line; i++)
                if (reader.ReadLine() == null) return "";
            return (reader.ReadLine() ?? "").Trim();
        }
        catch { return ""; }
    }

    private void ShowReferencesPanel()
    {
        _refPanelRow.Height     = new GridLength(200);
        _refSplitterRow.Height  = new GridLength(4);
        _referencesPanel.Visibility = Visibility.Visible;
        _refSplitter.Visibility     = Visibility.Visible;
    }

    public void Editor_GrepRequested(object? sender, GrepRequestedEventArgs e)
    {
        var currentFilePath = _focusedEditor()?.Engine.CurrentBuffer.FilePath;
        _ = RunGrepAsync(e.Pattern, e.FileGlob, e.IgnoreCase, currentFilePath);
    }

    private async Task RunGrepAsync(string pattern, string? fileGlob, bool ignoreCase, string? currentFilePath)
    {
        SetQuickfixItems([]);
        _quickfixCurrentIndex = -1;
        _quickfixTitle = $"GREP \"{pattern}\" — Searching…";
        ShowQuickfixPanel();

        List<ReferenceListItem> results;
        var grepOptions = CreateProjectSearchOptions(pattern, fileGlob, ignoreCase, currentFilePath);
        try
        {
            results = grepOptions == null
                ? []
                : await Task.Run(() => ExecuteGrep(grepOptions));
        }
        catch { results = []; }

        SetQuickfixItems(results);
        _quickfixCurrentIndex = -1;

        int fileCount = results.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _quickfixTitle = results.Count > 0
            ? $"GREP \"{pattern}\" — {results.Count} matches [{fileCount} file(s)]"
            : $"GREP \"{pattern}\" — no matches";
        _lastGrepOptions = grepOptions;
        ShowQuickfixPanel();

    }

    private ProjectSearchOptions? CreateProjectSearchOptions(string pattern, string? fileGlob, bool ignoreCase, string? currentFilePath)
    {
        string? root;
        if (fileGlob == "%")
        {
            if (currentFilePath == null) return null;
            root = Path.GetDirectoryName(currentFilePath);
        }
        else
        {
            root = _getCurrentFolderPath()
                   ?? (currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null);
        }

        return root == null
            ? null
            : new ProjectSearchOptions(root, pattern, fileGlob, ignoreCase, currentFilePath);
    }

    private static List<ReferenceListItem> ExecuteGrep(ProjectSearchOptions options)
    {
        try
        {
            return ProjectFindReplaceService.Find(options)
                .Select(match => new ReferenceListItem
                {
                    FilePath = match.FilePath,
                    FileName = Path.GetFileName(match.FilePath),
                    LineCol  = $":{match.Line + 1}:{match.Column + 1}",
                    Preview  = match.LineText.Trim(),
                    Line     = match.Line,
                    Col      = match.Column,
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public async void ReplaceRefResults_Click(object sender, RoutedEventArgs e)
    {
        var replacement = _showInputDialog("Project Replace", "Replacement:", "");
        if (replacement == null)
            return;

        await ReplaceLastGrepAsync(replacement);
    }

    public void Editor_QuickfixReplaceRequested(object? sender, string replacement)
    {
        _ = ReplaceLastGrepAsync(replacement);
    }

    public void Editor_ProjectReplaceRequested(object? sender, ProjectReplaceRequestedEventArgs e)
    {
        _ = ReplaceProjectAsync(e.Pattern, e.Replacement, e.FileGlob, e.IgnoreCase);
    }

    private async Task ReplaceProjectAsync(string pattern, string replacement, string? fileGlob, bool ignoreCase)
    {
        var currentFilePath = _focusedEditor()?.Engine.CurrentBuffer.FilePath;
        var options = CreateProjectSearchOptions(pattern, fileGlob, ignoreCase, currentFilePath);
        if (options == null)
        {
            MessageBox.Show("No project folder or current file is available.", "Project Replace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetQuickfixItems([]);
        _quickfixCurrentIndex = -1;
        _quickfixTitle = $"REPLACE \"{pattern}\" — Searching…";
        ShowQuickfixPanel();

        var results = await Task.Run(() => ExecuteGrep(options));
        SetQuickfixItems(results);
        _quickfixCurrentIndex = -1;
        _lastGrepOptions = options;

        var fileCount = results.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _quickfixTitle = results.Count > 0
            ? $"REPLACE \"{pattern}\" — {results.Count} matches [{fileCount} file(s)]"
            : $"REPLACE \"{pattern}\" — no matches";
        ShowQuickfixPanel();

        if (results.Count > 0)
            await ConfirmAndApplyReplaceAsync(options, replacement, results);
    }

    private async Task ReplaceLastGrepAsync(string replacement)
    {
        if (_lastGrepOptions == null)
        {
            MessageBox.Show("Run :grep first, or use :grepreplace /pattern/replacement/.", "Project Replace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var results = _quickfixItems.ToList();
        if (results.Count == 0)
            results = await Task.Run(() => ExecuteGrep(_lastGrepOptions));

        if (results.Count == 0)
        {
            MessageBox.Show("No grep results to replace.", "Project Replace",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConfirmAndApplyReplaceAsync(_lastGrepOptions, replacement, results);
    }

    private async Task ConfirmAndApplyReplaceAsync(
        ProjectSearchOptions options,
        string replacement,
        IReadOnlyList<ReferenceListItem> results)
    {
        var files = results
            .Select(i => i.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var modifiedOpenFiles = _allEditors()
            .Where(editor => editor.Engine.CurrentBuffer.Text.IsModified &&
                             editor.Engine.CurrentBuffer.FilePath != null &&
                             files.Contains(editor.Engine.CurrentBuffer.FilePath, StringComparer.OrdinalIgnoreCase))
            .Select(editor => editor.Engine.CurrentBuffer.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modifiedOpenFiles.Count > 0)
        {
            MessageBox.Show(
                "Save or discard changes in these open files before replacing:\n\n" +
                string.Join("\n", modifiedOpenFiles.Select(Path.GetFileName)),
                "Project Replace",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Replace {results.Count} match(es) in {files.Count} file(s)?\n\nPattern: {options.Pattern}\nReplacement: {replacement}",
            "Project Replace",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        ProjectReplaceResult replaceResult;
        try
        {
            replaceResult = await Task.Run(() => ProjectFindReplaceService.Replace(options, replacement));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Project Replace Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ReloadOpenEditors(files);
        await RunGrepAsync(options.Pattern, options.FileGlob, options.IgnoreCase, options.CurrentFilePath);

        var errorText = replaceResult.Errors.Count > 0
            ? $"\n\nErrors:\n{string.Join("\n", replaceResult.Errors.Take(5))}"
            : "";
        MessageBox.Show(
            $"Replaced {replaceResult.MatchCount} match(es) in {replaceResult.FileCount} file(s).{errorText}",
            "Project Replace",
            MessageBoxButton.OK,
            replaceResult.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ReloadOpenEditors(IReadOnlyCollection<string> filePaths)
    {
        foreach (var editor in _allEditors())
        {
            var path = editor.Engine.CurrentBuffer.FilePath;
            if (path == null || !filePaths.Contains(path, StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
                continue;

            try
            {
                editor.LoadFile(path);
            }
            catch
            {
                // File watcher or a later explicit open can recover if a reload fails.
            }
        }
    }

    public void SetQuickfixItems(List<ReferenceListItem> items)
    {
        _quickfixItems = items;
    }

    public void ShowQuickfixPanel()
    {
        DisplayReferenceItems(_quickfixItems, _quickfixTitle, _quickfixCurrentIndex,
            _lastGrepOptions != null && _quickfixItems.Count > 0);
    }

    private void DisplayReferenceItems(
        IReadOnlyList<ReferenceListItem> items,
        string title,
        int selectedIndex,
        bool replaceEnabled)
    {
        _refList.SelectionChanged -= RefList_SelectionChanged;
        _refList.ItemsSource = items;
        _refList.SelectedIndex = selectedIndex >= 0 && selectedIndex < items.Count ? selectedIndex : -1;
        _refList.SelectionChanged += RefList_SelectionChanged;

        _refPanelTitle.Text = title;
        _replaceRefResultsBtn.IsEnabled = replaceEnabled;
        ShowReferencesPanel();
    }

    public void QuickfixNavigate(int delta)
    {
        var count = _quickfixItems.Count;
        if (count == 0) return;
        ShowQuickfixPanel();
        _quickfixCurrentIndex = Math.Clamp(_quickfixCurrentIndex + delta, 0, count - 1);
        _refList.SelectedIndex = _quickfixCurrentIndex;
        _refList.ScrollIntoView(_refList.SelectedItem);
        _currentEditor()?.Focus();
    }

    public void QuickfixNavigateTo(int index)
    {
        var count = _quickfixItems.Count;
        if (count == 0) return;
        ShowQuickfixPanel();
        // index == -1 means :cc with no arg — go to current item (or first)
        _quickfixCurrentIndex = index < 0
            ? Math.Max(0, _quickfixCurrentIndex)
            : Math.Clamp(index, 0, count - 1);
        _refList.SelectedIndex = _quickfixCurrentIndex;
        _refList.ScrollIntoView(_refList.SelectedItem);
        _currentEditor()?.Focus();
    }

    public void ShowLocationListPanel()
    {
        var state = RefreshCurrentLocationList();
        DisplayReferenceItems(state.Items, state.Title, state.Navigator.CurrentIndex, false);
    }

    public void LocationListNavigate(int delta)
    {
        var state = RefreshCurrentLocationList();
        var target = state.Navigator.Move(delta);
        if (target == null) return;

        ShowLocationListPanel();
        _refList.SelectedIndex = target.Value;
        _refList.ScrollIntoView(_refList.SelectedItem);
        _currentEditor()?.Focus();
    }

    public void LocationListNavigateTo(int index)
    {
        var state = RefreshCurrentLocationList();
        var target = state.Navigator.Goto(index);
        if (target == null) return;

        ShowLocationListPanel();
        _refList.SelectedIndex = target.Value;
        _refList.ScrollIntoView(_refList.SelectedItem);
        _currentEditor()?.Focus();
    }

    private BufferLocationList RefreshCurrentLocationList()
    {
        var editor = _currentEditor();
        if (editor == null)
            return new BufferLocationList();

        var key = GetLocationListKey(editor);
        if (!_locationLists.TryGetValue(key, out var state))
        {
            state = new BufferLocationList();
            _locationLists[key] = state;
        }

        var (items, title, source, sourceKey) = BuildLocationList(editor);
        bool replaced = state.Source != source || state.SourceKey != sourceKey;
        state.Items = items;
        state.Title = title;
        state.Source = source;
        state.SourceKey = sourceKey;
        if (replaced)
            state.Navigator.Reset(items.Count);
        else
            state.Navigator.SetCount(items.Count);

        return state;
    }

    private (List<ReferenceListItem> Items, string Title, LocationListSource Source, string SourceKey)
        BuildLocationList(VimEditorControl editor)
    {
        var diagnostics = BuildDiagnosticLocationItems(editor);
        if (diagnostics.Count > 0)
        {
            string title = $"LOCATION LIST — diagnostics ({diagnostics.Count})";
            return (diagnostics, title, LocationListSource.Diagnostics, "diagnostics");
        }

        var searchItems = BuildSearchLocationItems(editor, out var pattern);
        if (searchItems.Count > 0)
        {
            string title = $"LOCATION LIST — /{pattern}/ ({searchItems.Count})";
            return (searchItems, title, LocationListSource.Search, $"search:{pattern}");
        }

        return ([], "LOCATION LIST — no diagnostics or search matches", LocationListSource.Empty, "");
    }

    private static List<ReferenceListItem> BuildDiagnosticLocationItems(VimEditorControl editor)
    {
        var filePath = editor.Engine.CurrentBuffer.FilePath ?? "";
        var fileName = string.IsNullOrEmpty(filePath) ? "[No Name]" : Path.GetFileName(filePath);
        var bufferKey = GetLocationListKey(editor);

        return editor.EffectiveDiagnostics
            .OrderBy(d => d.Range.Start.Line)
            .ThenBy(d => d.Range.Start.Column)
            .Select(d => new ReferenceListItem
            {
                FilePath = filePath,
                FileName = fileName,
                LineCol = $":{d.Range.Start.Line + 1}:{d.Range.Start.Column + 1}",
                Preview = $"{d.Severity}: {d.Message}",
                Line = d.Range.Start.Line,
                Col = d.Range.Start.Column,
                CurrentBufferOnly = true,
                BufferKey = bufferKey,
            })
            .ToList();
    }

    private static List<ReferenceListItem> BuildSearchLocationItems(VimEditorControl editor, out string pattern)
    {
        pattern = editor.Engine.SearchPattern;
        if (string.IsNullOrEmpty(pattern))
            return [];

        var ignoreCase = editor.Engine.Options.SmartCase
            ? !pattern.Any(char.IsUpper)
            : editor.Engine.Options.IgnoreCase;
        var buffer = editor.Engine.CurrentBuffer.Text;
        var filePath = editor.Engine.CurrentBuffer.FilePath ?? "";
        var fileName = string.IsNullOrEmpty(filePath) ? "[No Name]" : Path.GetFileName(filePath);
        var bufferKey = GetLocationListKey(editor);

        return buffer.FindAll(pattern, ignoreCase)
            .Select(pos => new ReferenceListItem
            {
                FilePath = filePath,
                FileName = fileName,
                LineCol = $":{pos.Line + 1}:{pos.Column + 1}",
                Preview = buffer.GetLine(pos.Line).Trim(),
                Line = pos.Line,
                Col = pos.Column,
                CurrentBufferOnly = true,
                BufferKey = bufferKey,
            })
            .ToList();
    }

    private static string GetLocationListKey(VimEditorControl editor)
    {
        var filePath = editor.Engine.CurrentBuffer.FilePath;
        return string.IsNullOrEmpty(filePath)
            ? $"editor:{RuntimeHelpers.GetHashCode(editor)}"
            : Path.GetFullPath(filePath);
    }

    public void Close()
    {
        _referencesPanel.Visibility = Visibility.Collapsed;
        _refSplitter.Visibility     = Visibility.Collapsed;
        _refPanelRow.Height    = new GridLength(0);
        _refSplitterRow.Height = new GridLength(0);
        // Return focus to editor
        _currentEditor()?.Focus();
    }

    public void RefList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refList.SelectedItem is not ReferenceListItem item) return;

        var editor = _currentEditor();
        if (editor == null) return;

        if (item.CurrentBufferOnly)
        {
            if (!string.Equals(item.BufferKey, GetLocationListKey(editor), StringComparison.OrdinalIgnoreCase))
            {
                ShowLocationListPanel();
                return;
            }

            editor.NavigateTo(item.Line, item.Col);
            editor.Focus();
        }
        else if (string.Equals(item.FilePath, editor.Engine.CurrentBuffer.FilePath,
                               StringComparison.OrdinalIgnoreCase))
        {
            editor.NavigateTo(item.Line, item.Col);
            editor.Focus();
        }
        else
        {
            _openFile(item.FilePath);
            _currentEditor()?.NavigateTo(item.Line, item.Col);
        }
    }
}
