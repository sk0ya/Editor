using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Editor.Controls;

namespace Editor.App;

internal enum SidebarPanel { None, Explorer, Settings, Outline }

/// <summary>
/// Owns the Explorer sidebar: panel visibility, the file tree (navigation, keyboard,
/// context menu) and the currently loaded folder. Extracted from MainWindow (Phase 7);
/// shared cross-cutting concerns (opening files, recent-items tracking, the shared
/// input-dialog helper, focus cycling across editor panes) stay in MainWindow and are
/// reached through the callbacks passed to the constructor.
/// </summary>
internal sealed class SidebarController
{
    private readonly Window _owner;
    private readonly TreeView _fileTree;
    private readonly TextBlock _folderNameLabel;
    private readonly ColumnDefinition _sidebarCol;
    private readonly ColumnDefinition _splitterCol;
    private readonly UIElement _explorerPanel;
    private readonly UIElement _settingsPanel;
    private readonly UIElement _outlinePanel;
    private readonly ToggleButton _explorerBtn;
    private readonly ToggleButton _settingsBtn;
    private readonly ToggleButton _outlineBtn;

    private readonly Func<VimEditorControl?> _focusedEditor;
    private readonly Func<IEnumerable<VimEditorControl>> _allEditors;
    private readonly Action<string> _openOrFocusFile;
    private readonly Action<string> _addTab;
    private readonly Func<string, string, string, string?> _showInputDialog;
    private readonly Action<string> _onFolderLoaded;

    private bool _sidebarVisible;
    private double _sidebarWidth = 220;
    private SidebarPanel _activeSidebarPanel = SidebarPanel.None;
    private string? _currentFolderPath;
    private Point _shellMenuScreenPos;
    private bool _suppressFileOpen;
    private bool _fileTreeCtrlWPending;

    public SidebarController(
        Window owner,
        TreeView fileTree,
        TextBlock folderNameLabel,
        ColumnDefinition sidebarCol,
        ColumnDefinition splitterCol,
        UIElement explorerPanel,
        UIElement settingsPanel,
        UIElement outlinePanel,
        ToggleButton explorerBtn,
        ToggleButton settingsBtn,
        ToggleButton outlineBtn,
        Func<VimEditorControl?> focusedEditor,
        Func<IEnumerable<VimEditorControl>> allEditors,
        Action<string> openOrFocusFile,
        Action<string> addTab,
        Func<string, string, string, string?> showInputDialog,
        Action<string> onFolderLoaded)
    {
        _owner = owner;
        _fileTree = fileTree;
        _folderNameLabel = folderNameLabel;
        _sidebarCol = sidebarCol;
        _splitterCol = splitterCol;
        _explorerPanel = explorerPanel;
        _settingsPanel = settingsPanel;
        _outlinePanel = outlinePanel;
        _explorerBtn = explorerBtn;
        _settingsBtn = settingsBtn;
        _outlineBtn = outlineBtn;
        _focusedEditor = focusedEditor;
        _allEditors = allEditors;
        _openOrFocusFile = openOrFocusFile;
        _addTab = addTab;
        _showInputDialog = showInputDialog;
        _onFolderLoaded = onFolderLoaded;
    }

    public bool IsVisible => _sidebarVisible;
    public SidebarPanel ActivePanel => _activeSidebarPanel;
    public string? CurrentFolderPath => _currentFolderPath;

    // ─────────── Sidebar ───────────────────────────────────

    public void Show(SidebarPanel panel = SidebarPanel.Explorer)
    {
        _sidebarCol.Width = new GridLength(_sidebarWidth, GridUnitType.Pixel);
        _sidebarCol.MinWidth = 80;
        _splitterCol.Width = new GridLength(4, GridUnitType.Pixel);
        _splitterCol.MinWidth = 4;
        _sidebarVisible = true;
        _activeSidebarPanel = panel;

        // Hide all panels first, then show the requested one
        _explorerPanel.Visibility = Visibility.Collapsed;
        _settingsPanel.Visibility  = Visibility.Collapsed;
        _outlinePanel.Visibility   = Visibility.Collapsed;
        _explorerBtn.IsChecked = false;
        _settingsBtn.IsChecked  = false;
        _outlineBtn.IsChecked   = false;

        switch (panel)
        {
            case SidebarPanel.Settings:
                _settingsPanel.Visibility = Visibility.Visible;
                _settingsBtn.IsChecked = true;
                break;
            case SidebarPanel.Outline:
                _outlinePanel.Visibility = Visibility.Visible;
                _outlineBtn.IsChecked = true;
                break;
            default: // Explorer
                _explorerPanel.Visibility = Visibility.Visible;
                _explorerBtn.IsChecked = true;
                break;
        }
    }

    public void Hide()
    {
        _sidebarWidth = _sidebarCol.ActualWidth > 0 ? _sidebarCol.ActualWidth : _sidebarWidth;
        _sidebarCol.Width = new GridLength(0);
        _sidebarCol.MinWidth = 0;
        _splitterCol.Width = new GridLength(0);
        _splitterCol.MinWidth = 0;
        _explorerBtn.IsChecked = false;
        _settingsBtn.IsChecked  = false;
        _outlineBtn.IsChecked   = false;
        _fileTreeCtrlWPending = false;
        _sidebarVisible = false;
        _activeSidebarPanel = SidebarPanel.None;
    }

    public void Toggle()
    {
        if (_sidebarVisible && _activeSidebarPanel == SidebarPanel.Explorer)
            Hide();
        else
        {
            Show(SidebarPanel.Explorer);
            _fileTree.Focus();
        }
    }

    public void LoadFolder(string folderPath, bool recordRecent = true)
    {
        _currentFolderPath = folderPath;
        _folderNameLabel.Text = Path.GetFileName(folderPath) is { Length: > 0 } n ? n : folderPath;
        _fileTree.ItemsSource = FileTreeItem.LoadChildren(folderPath);
        if (!_sidebarVisible)
            Show();
        if (recordRecent)
            _onFolderLoaded(folderPath);
    }

    // ─────────── File tree events ──────────────────────────

    public void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: FileTreeItem item })
        {
            item.IsExpanded = true;
            item.Expand();
            e.Handled = true;
        }
    }

    public void TreeViewItem_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: FileTreeItem item })
        {
            item.IsExpanded = false;
            e.Handled = true;
        }
    }

    public void FileTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_suppressFileOpen && e.NewValue is FileTreeItem { IsDirectory: false } item)
            _openOrFocusFile(item.FullPath);
    }

    public void FileTree_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl  = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);
        var selected = _fileTree.SelectedItem as FileTreeItem;

        // Ctrl+W prefix — buffer and wait for second key
        if (ctrl && e.Key == Key.W)
        {
            _fileTreeCtrlWPending = true;
            e.Handled = true;
            return;
        }

        // Second key of Ctrl+W sequence
        if (_fileTreeCtrlWPending)
        {
            _fileTreeCtrlWPending = false;
            switch (e.Key)
            {
                case Key.L:   // Ctrl+W l — move right to editor
                    _focusedEditor()?.Focus();
                    e.Handled = true;
                    return;
                case Key.W:   // Ctrl+W w — cycle to next editor
                    FocusEditorCycleFromFileTree(reverse: shift);
                    e.Handled = true;
                    return;
                case Key.H:   // Ctrl+W h — already leftmost, no-op
                case Key.Escape:
                    e.Handled = true;
                    return;
                default:
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.J:
                FileTreeMoveSelection(+1);
                e.Handled = true;
                return;
            case Key.K:
                FileTreeMoveSelection(-1);
                e.Handled = true;
                return;
            case Key.L:
            case Key.Return:
                FileTreeActivate(selected);
                e.Handled = true;
                return;
            case Key.H:
                FileTreeCollapseOrParent(selected);
                e.Handled = true;
                return;
            case Key.A:
                if (selected == null) return;
                if (shift) ContextMenu_NewFolder(selected);
                else       ContextMenu_NewFile(selected);
                e.Handled = true;
                return;
            case Key.R:
                if (selected != null) ContextMenu_Rename(selected);
                e.Handled = true;
                return;
            case Key.D:
            case Key.Delete:
                if (selected != null) ContextMenu_Delete(selected);
                e.Handled = true;
                return;
            case Key.Escape:
                _focusedEditor()?.Focus();
                e.Handled = true;
                return;
        }
    }

    public void FileTree_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _fileTreeCtrlWPending = false;
    }

    // ── File tree navigation helpers ──────────────────────────

    private void FileTreeMoveSelection(int delta)
    {
        var flat = FileTreeFlatItems();
        if (flat.Count == 0) return;

        var current = _fileTree.SelectedItem as FileTreeItem;
        int idx = current == null ? -1 : flat.IndexOf(current);
        int next = idx < 0
            ? (delta > 0 ? 0 : flat.Count - 1)
            : Math.Clamp(idx + delta, 0, flat.Count - 1);
        if (next == idx) return;

        SelectFileTreeItem(flat[next], suppressFileOpen: true);
    }

    private void FileTreeActivate(FileTreeItem? item)
    {
        if (item == null) return;
        if (item.IsDirectory)
        {
            var tvi = FindTreeViewItem(_fileTree, item);
            item.IsExpanded = !item.IsExpanded;
            if (item.IsExpanded)
                item.Expand();
            if (tvi != null)
                tvi.IsExpanded = item.IsExpanded;
        }
        else
        {
            _openOrFocusFile(item.FullPath);
            _focusedEditor()?.Focus();
        }
    }

    private void FileTreeCollapseOrParent(FileTreeItem? item)
    {
        if (item == null) return;

        if (item.IsDirectory && item.IsExpanded)
        {
            item.IsExpanded = false;
            if (FindTreeViewItem(_fileTree, item) is { } tvi)
                tvi.IsExpanded = false;
        }
        else if (item.Parent != null)
        {
            SelectFileTreeItem(item.Parent, suppressFileOpen: true);
        }
    }

    /// <summary>Flat list of currently visible FileTreeItems (respects expansion).</summary>
    private List<FileTreeItem> FileTreeFlatItems()
    {
        var result = new List<FileTreeItem>();
        foreach (var item in _fileTree.Items.OfType<FileTreeItem>())
            CollectVisibleTreeItems(item, result);
        return result;
    }

    private void SelectFileTreeItem(FileTreeItem item, bool suppressFileOpen)
    {
        bool previous = _suppressFileOpen;
        _suppressFileOpen = suppressFileOpen || previous;
        try
        {
            _fileTree.UpdateLayout();
            var tvi = FindTreeViewItem(_fileTree, item);
            if (tvi == null) return;
            tvi.IsSelected = true;
            tvi.BringIntoView();
        }
        finally
        {
            _suppressFileOpen = previous;
        }
    }

    private static void CollectVisibleTreeItems(FileTreeItem item, List<FileTreeItem> result)
    {
        if (item.FullPath.Length == 0) return;

        result.Add(item);
        if (!item.IsDirectory || !item.IsExpanded) return;

        foreach (var child in item.Children)
            CollectVisibleTreeItems(child, result);
    }

    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, FileTreeItem target)
    {
        parent.UpdateLayout();
        if (parent.ItemContainerGenerator.ContainerFromItem(target) is TreeViewItem direct)
            return direct;

        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem tvi) continue;
            if (tvi.DataContext == target)
                return tvi;
            if (tvi.IsExpanded)
            {
                var found = FindTreeViewItem(tvi, target);
                if (found != null) return found;
            }
        }
        return null;
    }

    private void FocusEditorCycleFromFileTree(bool reverse)
    {
        var editors = _allEditors().ToList();
        if (editors.Count == 0)
            return;

        var focused = _focusedEditor();
        if (focused == null)
        {
            editors[0].Focus();
            return;
        }

        int idx = editors.IndexOf(focused);
        if (idx < 0)
        {
            editors[0].Focus();
            return;
        }

        if (editors.Count == 1)
        {
            editors[0].Focus();
            return;
        }

        var next = reverse
            ? editors[(idx - 1 + editors.Count) % editors.Count]
            : editors[(idx + 1) % editors.Count];
        next.Focus();
    }

    // ─────────── File tree context menu ────────────────────────

    public void FileTree_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var tvi = MainWindow.FindVisualAncestor<TreeViewItem>((DependencyObject)e.OriginalSource);
        if (tvi == null) return;
        if (tvi.DataContext is not FileTreeItem item) return;

        tvi.IsSelected = true;
        _shellMenuScreenPos = _owner.PointToScreen(e.GetPosition(_owner));

        var cm = BuildFileTreeContextMenu(item);
        cm.PlacementTarget = tvi;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        cm.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildFileTreeContextMenu(FileTreeItem item)
    {
        var cmStyle  = (Style)_owner.FindResource("FileTreeContextMenuStyle");
        var miStyle  = (Style)_owner.FindResource("FileTreeMenuItemStyle");
        var sepStyle = (Style)_owner.FindResource("FileTreeSeparatorStyle");

        var cm = new ContextMenu { Style = cmStyle };

        if (item.IsDirectory)
        {
            cm.Items.Add(FtMi("新規ファイル...",   miStyle, () => ContextMenu_NewFile(item)));
            cm.Items.Add(FtMi("新規フォルダー...", miStyle, () => ContextMenu_NewFolder(item)));
            cm.Items.Add(new Separator { Style = sepStyle });
        }
        else
        {
            cm.Items.Add(FtMi("開く",             miStyle, () => _openOrFocusFile(item.FullPath)));
            cm.Items.Add(FtMi("新しいタブで開く", miStyle, () => _addTab(item.FullPath)));
            cm.Items.Add(new Separator { Style = sepStyle });
        }

        cm.Items.Add(FtMi("名前の変更", miStyle, () => ContextMenu_Rename(item)));
        cm.Items.Add(FtMi("削除",       miStyle, () => ContextMenu_Delete(item)));
        cm.Items.Add(new Separator { Style = sepStyle });

        var explorerLabel = item.IsDirectory ? "エクスプローラーで開く" : "エクスプローラーで表示";
        cm.Items.Add(FtMi(explorerLabel, miStyle, () => ContextMenu_OpenInExplorer(item)));
        cm.Items.Add(new Separator { Style = sepStyle });
        cm.Items.Add(FtMi("Windowsのコンテキストメニュー", miStyle, () => ContextMenu_ShowWindowsMenu(item)));

        return cm;
    }

    private void ContextMenu_ShowWindowsMenu(FileTreeItem item)
    {
        var hwnd = new WindowInteropHelper(_owner).Handle;
        ShellMenuContext.ShowDirect(hwnd, item.FullPath,
            (int)_shellMenuScreenPos.X, (int)_shellMenuScreenPos.Y);
    }

    private static MenuItem FtMi(string header, Style style, Action handler)
    {
        var mi = new MenuItem { Header = header, Style = style };
        mi.Click += (_, _) => handler();
        return mi;
    }

    private void ContextMenu_NewFile(FileTreeItem folder)
    {
        var targetDir = folder.IsDirectory
            ? folder.FullPath
            : Path.GetDirectoryName(folder.FullPath) ?? _currentFolderPath ?? "";

        var name = _showInputDialog("新規ファイル", "ファイル名:", "newfile.txt");
        if (string.IsNullOrWhiteSpace(name)) return;

        var path = Path.Combine(targetDir, name);
        try
        {
            File.WriteAllText(path, "");
            RefreshCurrentFolder();
            _openOrFocusFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ファイルの作成に失敗しました: {ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ContextMenu_NewFolder(FileTreeItem folder)
    {
        var targetDir = folder.IsDirectory
            ? folder.FullPath
            : Path.GetDirectoryName(folder.FullPath) ?? _currentFolderPath ?? "";

        var name = _showInputDialog("新規フォルダー", "フォルダー名:", "新しいフォルダー");
        if (string.IsNullOrWhiteSpace(name)) return;

        var path = Path.Combine(targetDir, name);
        try
        {
            Directory.CreateDirectory(path);
            RefreshCurrentFolder();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"フォルダーの作成に失敗しました: {ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ContextMenu_Rename(FileTreeItem item)
    {
        var newName = _showInputDialog("名前の変更", "新しい名前:", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;

        var dir     = Path.GetDirectoryName(item.FullPath) ?? "";
        var newPath = Path.Combine(dir, newName);
        try
        {
            if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
            else                  File.Move(item.FullPath, newPath);
            RefreshCurrentFolder();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"名前の変更に失敗しました: {ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ContextMenu_Delete(FileTreeItem item)
    {
        var result = MessageBox.Show(
            $"「{item.Name}」を削除しますか？",
            "削除の確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (item.IsDirectory) Directory.Delete(item.FullPath, recursive: true);
            else                  File.Delete(item.FullPath);
            RefreshCurrentFolder();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"削除に失敗しました: {ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ContextMenu_OpenInExplorer(FileTreeItem item)
    {
        var path = item.IsDirectory
            ? item.FullPath
            : Path.GetDirectoryName(item.FullPath) ?? item.FullPath;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void RefreshCurrentFolder()
    {
        if (_currentFolderPath == null) return;
        _fileTree.ItemsSource = FileTreeItem.LoadChildren(_currentFolderPath);
    }
}
