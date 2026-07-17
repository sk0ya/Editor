using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Controls;
using Editor.Controls.Themes;

namespace Editor.App;

/// <summary>Represents an open file in the tab bar.</summary>
internal sealed class FileTab
{
    public required TabItem Item { get; init; }
    public required TextBlock HeaderLabel { get; init; }
    public string? FilePath { get; set; }

    public void UpdateHeader(bool isModified, string? label = null)
    {
        var name = label ?? (FilePath != null ? Path.GetFileName(FilePath) : "[No Name]");
        HeaderLabel.Text = isModified ? $"• {name}" : name;
    }
}

/// <summary>
/// Owns the file-tab bar: FileTab bookkeeping, editor-pane creation/wiring, and tab
/// selection/closing. Extracted from MainWindow (Phase 7). Cross-cutting state it does
/// not own — which editor currently has focus (owned by the window-split/focus-tracking
/// code), the shared per-editor event wiring, outline refresh, and markdown-preview
/// scheduling — is reached through the callbacks passed to the constructor.
/// </summary>
internal sealed class TabManagerController
{
    private readonly Window _owner;
    private readonly TabControl _tabCtrl;
    private readonly VimStatusBar _sharedStatusBar;
    private readonly Func<EditorTheme> _getCurrentTheme;
    private readonly Func<bool> _getVimEnabled;
    private readonly Action<VimEditorControl> _wireEditorEvents;
    private readonly Func<VimEditorControl?> _focusedEditor;
    private readonly Func<IEnumerable<VimEditorControl>> _allEditors;
    private readonly Action<VimEditorControl?> _refreshOutlineForEditor;
    private readonly Action<VimEditorControl> _onAfterTabSelected;

    private readonly List<FileTab> _fileTabs = [];
    private bool _suppressTabSelectionChanged;

    public IReadOnlyList<FileTab> FileTabs => _fileTabs;

    public TabManagerController(
        Window owner,
        TabControl tabCtrl,
        VimStatusBar sharedStatusBar,
        Func<EditorTheme> getCurrentTheme,
        Func<bool> getVimEnabled,
        Action<VimEditorControl> wireEditorEvents,
        Func<VimEditorControl?> focusedEditor,
        Func<IEnumerable<VimEditorControl>> allEditors,
        Action<VimEditorControl?> refreshOutlineForEditor,
        Action<VimEditorControl> onAfterTabSelected)
    {
        _owner = owner;
        _tabCtrl = tabCtrl;
        _sharedStatusBar = sharedStatusBar;
        _getCurrentTheme = getCurrentTheme;
        _getVimEnabled = getVimEnabled;
        _wireEditorEvents = wireEditorEvents;
        _focusedEditor = focusedEditor;
        _allEditors = allEditors;
        _refreshOutlineForEditor = refreshOutlineForEditor;
        _onAfterTabSelected = onAfterTabSelected;
    }

    public FileTab? FindFileTabByPath(string? path) =>
        path == null
            ? _fileTabs.FirstOrDefault(t => t.FilePath == null)
            : _fileTabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));

    public void UpdateSelectedTabForEditor(VimEditorControl editor)
    {
        var path = editor.Engine.CurrentBuffer.FilePath;
        var ft = FindFileTabByPath(path);
        _suppressTabSelectionChanged = true;
        _tabCtrl.SelectedItem = ft?.Item;
        _suppressTabSelectionChanged = false;
    }

    public VimEditorControl CreateEditor(string? filePath)
    {
        var editor = new VimEditorControl(VimEditorControlDefaults.CreateOptions());
        editor.SetTheme(_getCurrentTheme());
        editor.VimEnabled = _getVimEnabled();
        editor.SetSharedStatusBar(_sharedStatusBar);
        _wireEditorEvents(editor);
        if (filePath != null && File.Exists(filePath))
            editor.LoadFile(filePath);
        else
            editor.SetText("");
        return editor;
    }

    /// <summary>Add a file tab entry to the tab bar (without loading into focused pane).</summary>
    public FileTab AddFileTabEntry(string? filePath)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var closeBtn = new Button { Content = "×", Style = (Style)_owner.FindResource("TabCloseButton") };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(label);
        header.Children.Add(closeBtn);

        var tabItem = new TabItem { Header = header };  // No Content — editor is in EditorContent
        var fileTab = new FileTab { Item = tabItem, HeaderLabel = label, FilePath = filePath };
        closeBtn.Click += (_, _) => CloseFileTab(fileTab, force: false);
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseFileTab(fileTab, force: false);
        };

        fileTab.UpdateHeader(isModified: false);
        _fileTabs.Add(fileTab);
        _tabCtrl.Items.Add(tabItem);
        return fileTab;
    }

    /// <summary>Ensure a file is tracked in the tab bar. Returns existing or newly-created FileTab.</summary>
    public FileTab EnsureFileTab(string? filePath)
    {
        var existing = FindFileTabByPath(filePath);
        if (existing != null) return existing;
        return AddFileTabEntry(filePath);
    }

    /// <summary>Add tab entry and immediately load the file into the focused pane.</summary>
    public void AddTab(string? filePath)
    {
        // If file already tracked, just select it
        if (filePath != null)
        {
            var existing = FindFileTabByPath(filePath);
            if (existing != null)
            {
                SelectFileTab(existing);
                return;
            }
        }
        var fileTab = AddFileTabEntry(filePath);
        SelectFileTab(fileTab);
    }

    /// <summary>Select a file tab and load its file into the focused pane.</summary>
    public void SelectFileTab(FileTab fileTab)
    {
        SelectFileTab(fileTab, _focusedEditor());
    }

    /// <summary>Select a file tab and load its file into the specified pane.</summary>
    public void SelectFileTab(FileTab fileTab, VimEditorControl? editor)
    {
        _suppressTabSelectionChanged = true;
        _tabCtrl.SelectedItem = fileTab.Item;
        _suppressTabSelectionChanged = false;

        if (editor == null) return;

        if (fileTab.FilePath != null && File.Exists(fileTab.FilePath))
            editor.LoadFile(fileTab.FilePath);
        // FilePath == null → keep current content (new empty buffer shown as-is)

        editor.Focus();
        _refreshOutlineForEditor(editor);
        _onAfterTabSelected(editor);
    }

    /// <summary>Set the selected TabItem without triggering TabCtrl_SelectionChanged's reaction.</summary>
    public void SetSelectedTabItemQuietly(FileTab fileTab)
    {
        _suppressTabSelectionChanged = true;
        _tabCtrl.SelectedItem = fileTab.Item;
        _suppressTabSelectionChanged = false;
    }

    public void CloseFileTab(FileTab fileTab, bool force)
    {
        // Check ALL panes showing this file for unsaved changes
        var modifiedEditors = _allEditors()
            .Where(e =>
                string.Equals(e.Engine.CurrentBuffer.FilePath, fileTab.FilePath,
                    StringComparison.OrdinalIgnoreCase) &&
                e.Engine.CurrentBuffer.Text.IsModified)
            .ToList();

        if (!force && modifiedEditors.Count > 0)
        {
            var name = fileTab.FilePath != null ? Path.GetFileName(fileTab.FilePath) : "[No Name]";
            var result = MessageBox.Show(
                $"'{name}' has unsaved changes. Save before closing?",
                "Editor",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes)
            {
                foreach (var ed in modifiedEditors)
                {
                    try { ed.Engine.CurrentBuffer.Save(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Save failed: {ex.Message}", "Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }
        }

        var idx = _fileTabs.IndexOf(fileTab);
        _fileTabs.Remove(fileTab);
        _tabCtrl.Items.Remove(fileTab.Item);

        if (_fileTabs.Count == 0)
        {
            // If there are split panes, close them all first; then close app
            _owner.Close();
            return;
        }

        // Load the next adjacent tab into the focused pane
        var nextIdx = Math.Clamp(idx, 0, _fileTabs.Count - 1);
        SelectFileTab(_fileTabs[nextIdx]);
    }

    public void Editor_NextTabRequested(object? sender, EventArgs e)
    {
        if (_fileTabs.Count == 0) return;
        var selectedItem = _tabCtrl.SelectedItem as TabItem;
        var current = _fileTabs.FindIndex(t => t.Item == selectedItem);
        var next = (current + 1) % _fileTabs.Count;
        SelectFileTab(_fileTabs[next]);
    }

    public void Editor_PrevTabRequested(object? sender, EventArgs e)
    {
        if (_fileTabs.Count == 0) return;
        var selectedItem = _tabCtrl.SelectedItem as TabItem;
        var current = _fileTabs.FindIndex(t => t.Item == selectedItem);
        var prev = (current - 1 + _fileTabs.Count) % _fileTabs.Count;
        SelectFileTab(_fileTabs[prev]);
    }

    public void Editor_CloseTabRequested(object? sender, CloseTabRequestedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        var path = editor.Engine.CurrentBuffer.FilePath;
        var ft = FindFileTabByPath(path);
        if (ft != null)
            CloseFileTab(ft, e.Force);
    }

    public void TabCtrl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabSelectionChanged) return;
        var selectedItem = _tabCtrl.SelectedItem as TabItem;
        var fileTab = _fileTabs.Find(t => t.Item == selectedItem);
        if (fileTab == null) return;
        var focused = _focusedEditor();
        if (fileTab.FilePath != null && File.Exists(fileTab.FilePath))
            focused?.LoadFile(fileTab.FilePath);
        focused?.Focus();
        _refreshOutlineForEditor(focused);
    }
}
