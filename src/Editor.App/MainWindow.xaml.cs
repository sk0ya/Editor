using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Editor.Controls;
using Editor.Controls.Themes;

namespace Editor.App;

public partial class MainWindow : Window
{
    private sealed class TabInfo
    {
        public required TabItem Item { get; init; }
        public required VimEditorControl Editor { get; init; }
        public required TextBlock HeaderLabel { get; init; }
        public string? FilePath { get; set; }

        public void UpdateHeader()
        {
            var name = FilePath != null ? Path.GetFileName(FilePath) : "[No Name]";
            var isModified = Editor.Engine.CurrentBuffer.Text.IsModified;
            HeaderLabel.Text = isModified ? $"• {name}" : name;
        }
    }

    private enum SidebarPanel { None, Explorer, Settings }

    private readonly List<TabInfo> _tabs = [];
    private readonly RecentItemsManager _recentItems = new();
    private EditorTheme _currentTheme = EditorTheme.Dracula;
    private string _baseThemeName = "Dracula";
    private string? _customBackground;
    private string? _customAccent;
    private bool _sidebarVisible;
    private double _sidebarWidth = 220;
    private string? _currentFolderPath;
    private SidebarPanel _activeSidebarPanel = SidebarPanel.None;
    private System.Windows.Point _shellMenuScreenPos;

    private VimEditorControl? CurrentEditor =>
        TabCtrl.SelectedItem is TabItem ti ? ti.Content as VimEditorControl : null;

    private TabInfo? CurrentTabInfo =>
        _tabs.Find(t => t.Item == TabCtrl.SelectedItem as TabItem);

    private TabInfo? FindTabInfo(object sender) =>
        _tabs.Find(t => t.Editor == sender as VimEditorControl);

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            AddTab(null);
            if (Directory.Exists(args[1]))
                LoadFolder(args[1]);
            else if (File.Exists(args[1]))
                OpenFile(args[1]);
        }
        else
        {
            RestoreSession();
        }

        RefreshRecentMenus();
        RefreshJumpList();
        ApplyTabPlacement(_recentItems.TabPlacement);
        ApplyColorTheme(_recentItems.ThemeName, _recentItems.CustomBackground, _recentItems.CustomAccent);
        InitColorPalettes();
    }

    private void RestoreSession()
    {
        var session = _recentItems.LastSession;
        if (session == null || (session.TabFiles.Count == 0 && session.FolderPath == null))
        {
            AddTab(null);
            return;
        }

        // Restore tabs (skip files that no longer exist)
        foreach (var file in session.TabFiles)
        {
            if (File.Exists(file))
                AddTab(file);
        }
        if (_tabs.Count == 0)
            AddTab(null);

        // Restore active tab
        if (session.ActiveFile != null)
        {
            var active = _tabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, session.ActiveFile, StringComparison.OrdinalIgnoreCase));
            if (active != null)
                TabCtrl.SelectedItem = active.Item;
        }

        // Restore sidebar folder
        if (session.FolderPath != null && Directory.Exists(session.FolderPath))
        {
            _currentFolderPath = session.FolderPath;
            FolderNameLabel.Text = Path.GetFileName(session.FolderPath) is { Length: > 0 } n
                ? n : session.FolderPath;
            FileTree.ItemsSource = FileTreeItem.LoadChildren(session.FolderPath);
            ShowSidebar();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleSidebar();
            e.Handled = true;
        }
    }

    // ─────────── Sidebar ───────────────────────────────────

    private void ShowSidebar(SidebarPanel panel = SidebarPanel.Explorer)
    {
        SidebarCol.Width = new GridLength(_sidebarWidth, GridUnitType.Pixel);
        SidebarCol.MinWidth = 80;
        SplitterCol.Width = new GridLength(4, GridUnitType.Pixel);
        SplitterCol.MinWidth = 4;
        _sidebarVisible = true;
        _activeSidebarPanel = panel;

        if (panel == SidebarPanel.Settings)
        {
            ExplorerPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            ExplorerBtn.IsChecked = false;
            SettingsBtn.IsChecked = true;
        }
        else
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            ExplorerPanel.Visibility = Visibility.Visible;
            ExplorerBtn.IsChecked = true;
            SettingsBtn.IsChecked = false;
        }
    }

    private void HideSidebar()
    {
        _sidebarWidth = SidebarCol.ActualWidth > 0 ? SidebarCol.ActualWidth : _sidebarWidth;
        SidebarCol.Width = new GridLength(0);
        SidebarCol.MinWidth = 0;
        SplitterCol.Width = new GridLength(0);
        SplitterCol.MinWidth = 0;
        ExplorerBtn.IsChecked = false;
        SettingsBtn.IsChecked = false;
        _sidebarVisible = false;
        _activeSidebarPanel = SidebarPanel.None;
    }

    private void ToggleSidebar()
    {
        if (_sidebarVisible && _activeSidebarPanel == SidebarPanel.Explorer)
            HideSidebar();
        else
            ShowSidebar(SidebarPanel.Explorer);
    }

    private void LoadFolder(string folderPath)
    {
        _currentFolderPath = folderPath;
        FolderNameLabel.Text = Path.GetFileName(folderPath) is { Length: > 0 } n ? n : folderPath;
        FileTree.ItemsSource = FileTreeItem.LoadChildren(folderPath);
        if (!_sidebarVisible)
            ShowSidebar();
        _recentItems.AddFolder(folderPath);
        RefreshRecentMenus();
        RefreshJumpList();
    }

    // ─────────── File tree events ──────────────────────────

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: FileTreeItem item })
        {
            item.Expand();
            e.Handled = true;
        }
    }

    private void FileTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileTreeItem { IsDirectory: false } item)
            OpenOrFocusFile(item.FullPath);
    }

    // ─────────── File tree context menu ────────────────────────

    private void FileTree_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var tvi = FindVisualAncestor<TreeViewItem>((DependencyObject)e.OriginalSource);
        if (tvi == null) return;
        if (tvi.DataContext is not FileTreeItem item) return;

        tvi.IsSelected = true;
        _shellMenuScreenPos = PointToScreen(e.GetPosition(this));

        var cm = BuildFileTreeContextMenu(item);
        cm.PlacementTarget = tvi;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        cm.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildFileTreeContextMenu(FileTreeItem item)
    {
        var cmStyle  = (Style)FindResource("FileTreeContextMenuStyle");
        var miStyle  = (Style)FindResource("FileTreeMenuItemStyle");
        var sepStyle = (Style)FindResource("FileTreeSeparatorStyle");

        var cm = new ContextMenu { Style = cmStyle };

        if (item.IsDirectory)
        {
            cm.Items.Add(FtMi("新規ファイル...",   miStyle, () => ContextMenu_NewFile(item)));
            cm.Items.Add(FtMi("新規フォルダー...", miStyle, () => ContextMenu_NewFolder(item)));
            cm.Items.Add(new Separator { Style = sepStyle });
        }
        else
        {
            cm.Items.Add(FtMi("開く",             miStyle, () => OpenOrFocusFile(item.FullPath)));
            cm.Items.Add(FtMi("新しいタブで開く", miStyle, () => AddTab(item.FullPath)));
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
        var hwnd = new WindowInteropHelper(this).Handle;
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

        var name = ShowInputDialog("新規ファイル", "ファイル名:", "newfile.txt");
        if (string.IsNullOrWhiteSpace(name)) return;

        var path = Path.Combine(targetDir, name);
        try
        {
            File.WriteAllText(path, "");
            RefreshCurrentFolder();
            OpenOrFocusFile(path);
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

        var name = ShowInputDialog("新規フォルダー", "フォルダー名:", "新しいフォルダー");
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
        var newName = ShowInputDialog("名前の変更", "新しい名前:", item.Name);
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
        FileTree.ItemsSource = FileTreeItem.LoadChildren(_currentFolderPath);
    }

    private string? ShowInputDialog(string title, string message, string defaultValue = "")
    {
        var dlg = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            WindowStyle = WindowStyle.ToolWindow
        };

        var textBox = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(12, 4, 12, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
            CaretBrush = new SolidColorBrush(Colors.White),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71)),
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4)
        };

        var okBtn     = new Button { Content = "OK",       Width = 80, Height = 26, IsDefault = true, Margin = new Thickness(4) };
        var cancelBtn = new Button { Content = "キャンセル", Width = 80, Height = 26, IsCancel = true,  Margin = new Thickness(4) };

        string? result = null;
        okBtn.Click     += (_, _) => { result = textBox.Text; dlg.Close(); };
        cancelBtn.Click += (_, _) => dlg.Close();

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 8, 8, 10)
        };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            Margin = new Thickness(12, 10, 12, 4),
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 12
        });
        panel.Children.Add(textBox);
        panel.Children.Add(btnPanel);
        dlg.Content = panel;
        dlg.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };
        dlg.ShowDialog();

        return result;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj != null)
        {
            if (obj is T t) return t;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private void OpenOrFocusFile(string path)
    {
        // Already open in a tab?
        var existing = _tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabCtrl.SelectedItem = existing.Item;
            existing.Editor.Focus();
            return;
        }

        // Replace empty tab or open new
        var current = CurrentTabInfo;
        if (current != null && current.FilePath == null &&
            !current.Editor.Engine.CurrentBuffer.Text.IsModified)
            OpenFile(path);
        else
            AddTab(path);
    }

    // ─────────── Tab management ────────────────────────────

    private void AddTab(string? filePath)
    {
        var editor = new VimEditorControl();
        editor.SetTheme(_currentTheme);
        WireEditorEvents(editor);

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var closeBtn = new Button { Content = "×", Style = (Style)FindResource("TabCloseButton") };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(label);
        header.Children.Add(closeBtn);

        var tabItem = new TabItem { Header = header, Content = editor };
        var tabInfo = new TabInfo { Item = tabItem, Editor = editor, HeaderLabel = label, FilePath = filePath };
        closeBtn.Click += (_, _) => CloseTab(tabInfo, force: false);
        header.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tabInfo, force: false); };

        if (filePath != null && File.Exists(filePath))
            editor.LoadFile(filePath);
        else
            editor.SetText("");

        tabInfo.UpdateHeader();

        _tabs.Add(tabInfo);
        TabCtrl.Items.Add(tabItem);
        TabCtrl.SelectedItem = tabItem;
        editor.Focus();
    }

    private void WireEditorEvents(VimEditorControl editor)
    {
        editor.SaveRequested     += Editor_SaveRequested;
        editor.QuitRequested     += Editor_QuitRequested;
        editor.OpenFileRequested += Editor_OpenFileRequested;
        editor.NewTabRequested   += Editor_NewTabRequested;
        editor.SplitRequested    += Editor_SplitRequested;
        editor.NextTabRequested  += Editor_NextTabRequested;
        editor.PrevTabRequested  += Editor_PrevTabRequested;
        editor.CloseTabRequested += Editor_CloseTabRequested;
        editor.BufferChanged     += Editor_BufferChanged;
    }

    private void Editor_BufferChanged(object? sender, EventArgs e)
    {
        var tabInfo = FindTabInfo(sender!);
        tabInfo?.UpdateHeader();
    }

    private void CloseTab(TabInfo tabInfo, bool force)
    {
        var buf = tabInfo.Editor.Engine.CurrentBuffer;
        if (buf.Text.IsModified && !force)
        {
            var result = MessageBox.Show(
                $"'{buf.Name}' has unsaved changes. Save before closing?",
                "Editor",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes)
            {
                try { buf.Save(); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Save failed: {ex.Message}", "Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        var idx = _tabs.IndexOf(tabInfo);
        _tabs.Remove(tabInfo);
        TabCtrl.Items.Remove(tabInfo.Item);

        if (_tabs.Count == 0) { Close(); return; }

        var nextIdx = Math.Clamp(idx, 0, _tabs.Count - 1);
        TabCtrl.SelectedItem = _tabs[nextIdx].Item;
        _tabs[nextIdx].Editor.Focus();
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        var tabInfo = CurrentTabInfo;
        if (tabInfo == null) return;
        tabInfo.Editor.LoadFile(path);
        tabInfo.FilePath = path;
        tabInfo.UpdateHeader();
        Title = $"Editor — {Path.GetFileName(path)}";
        _recentItems.AddFile(path);
        RefreshRecentMenus();
        RefreshJumpList();
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var dir = CurrentEditor?.Engine.CurrentBuffer.FilePath != null
            ? Path.GetDirectoryName(CurrentEditor.Engine.CurrentBuffer.FilePath)
            : Directory.GetCurrentDirectory();
        return Path.Combine(dir ?? "", path);
    }

    // ─────────── Events from VimEditorControl ───────────────

    private void Editor_SaveRequested(object? sender, SaveRequestedEventArgs e)
    {
        if (sender == null) return;
        var tabInfo = FindTabInfo(sender);
        if (tabInfo == null) return;
        var buf = tabInfo.Editor.Engine.CurrentBuffer;

        if (e.FilePath != null)
        {
            try { buf.Save(e.FilePath); }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed: {ex.Message}", "Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            tabInfo.FilePath = e.FilePath;
        }
        else if (buf.FilePath == null)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "All Files|*.*", Title = "Save File" };
            if (dlg.ShowDialog() != true) return;
            try { buf.Save(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed: {ex.Message}", "Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            tabInfo.FilePath = dlg.FileName;
        }
        else
        {
            try { buf.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Save failed: {ex.Message}", "Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        tabInfo.UpdateHeader();
    }

    private void Editor_QuitRequested(object? sender, QuitRequestedEventArgs e)
    {
        if (sender == null) return;
        var tabInfo = FindTabInfo(sender);
        if (tabInfo != null)
            CloseTab(tabInfo, e.Force);
    }

    private void Editor_OpenFileRequested(object? sender, OpenFileRequestedEventArgs e)
    {
        var path = ResolvePath(e.FilePath);

        if (!File.Exists(path))
        {
            var result = MessageBox.Show(
                $"'{path}' does not exist. Create it?",
                "Editor",
                MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes) return;
        }

        OpenFile(path);
    }

    private void Editor_NewTabRequested(object? sender, NewTabRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FilePath))
        {
            AddTab(null);
            return;
        }
        AddTab(ResolvePath(e.FilePath));
    }

    private void Editor_SplitRequested(object? sender, SplitRequestedEventArgs e)
    {
        MessageBox.Show(
            $"{(e.Vertical ? "Vertical" : "Horizontal")} split is not implemented yet.",
            "Editor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Editor_NextTabRequested(object? sender, EventArgs e)
    {
        if (TabCtrl.Items.Count == 0) return;
        var current = TabCtrl.SelectedIndex < 0 ? 0 : TabCtrl.SelectedIndex;
        TabCtrl.SelectedIndex = (current + 1) % TabCtrl.Items.Count;
    }

    private void Editor_PrevTabRequested(object? sender, EventArgs e)
    {
        if (TabCtrl.Items.Count == 0) return;
        var current = TabCtrl.SelectedIndex < 0 ? 0 : TabCtrl.SelectedIndex;
        TabCtrl.SelectedIndex = (current - 1 + TabCtrl.Items.Count) % TabCtrl.Items.Count;
    }

    private void Editor_CloseTabRequested(object? sender, CloseTabRequestedEventArgs e)
    {
        if (sender == null) return;
        var tabInfo = FindTabInfo(sender);
        if (tabInfo != null)
            CloseTab(tabInfo, e.Force);
    }

    // ─────────── Recent items ──────────────────────────────────

    private void RefreshRecentMenus()
    {
        PopulateRecentMenu(RecentFilesMenu, _recentItems.RecentFiles, isFolder: false);
        PopulateRecentMenu(RecentFoldersMenu, _recentItems.RecentFolders, isFolder: true);
    }

    private void PopulateRecentMenu(MenuItem menu, IReadOnlyList<string> items, bool isFolder)
    {
        menu.Items.Clear();
        if (items.Count == 0)
        {
            var empty = new MenuItem { Header = "(なし)", IsEnabled = false };
            menu.Items.Add(empty);
            return;
        }

        foreach (var path in items)
        {
            var header = isFolder
                ? Path.GetFileName(path) is { Length: > 0 } n ? n : path
                : Path.GetFileName(path);
            var item = new MenuItem { Header = header, ToolTip = path };
            var captured = path;
            item.Click += (_, _) =>
            {
                if (isFolder)
                {
                    if (Directory.Exists(captured))
                        LoadFolder(captured);
                }
                else
                {
                    if (File.Exists(captured))
                        OpenOrFocusFile(captured);
                }
            };
            menu.Items.Add(item);
        }
    }

    private void RefreshJumpList()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            var jumpList = new JumpList();

            foreach (var folder in _recentItems.RecentFolders)
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = Path.GetFileName(folder) is { Length: > 0 } n ? n : folder,
                    Description = folder,
                    ApplicationPath = exePath,
                    Arguments = $"\"{folder}\"",
                    CustomCategory = "最近のフォルダー"
                });
            }

            foreach (var file in _recentItems.RecentFiles)
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = Path.GetFileName(file),
                    Description = file,
                    ApplicationPath = exePath,
                    Arguments = $"\"{file}\"",
                    CustomCategory = "最近のファイル"
                });
            }

            JumpList.SetJumpList(Application.Current, jumpList);
            jumpList.Apply();
        }
        catch { /* Jump List is best-effort */ }
    }

    // ─────────── Menu handlers ───────────────

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab(null);

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "All Files|*.*|Text Files|*.txt|C# Files|*.cs|Python|*.py",
            Title = "Open File"
        };
        if (dlg.ShowDialog() != true) return;

        var current = CurrentTabInfo;
        if (current != null && current.FilePath == null && !current.Editor.Engine.CurrentBuffer.Text.IsModified)
            OpenFile(dlg.FileName);
        else
            AddTab(dlg.FileName);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var path = NativeFolderPicker.Show(hwnd, "フォルダーを開く");
        if (path != null)
            LoadFolder(path);
    }

    private void ExplorerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarVisible && _activeSidebarPanel == SidebarPanel.Explorer)
            HideSidebar();
        else
            ShowSidebar(SidebarPanel.Explorer);
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarVisible && _activeSidebarPanel == SidebarPanel.Settings)
            HideSidebar();
        else
            ShowSidebar(SidebarPanel.Settings);
    }

    private void TabPosition_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not RadioButton rb || rb.Tag is not string placementStr) return;

        var placement = placementStr switch
        {
            "Bottom" => Dock.Bottom,
            "Left"   => Dock.Left,
            "Right"  => Dock.Right,
            _        => Dock.Top
        };
        TabCtrl.TabStripPlacement = placement;
        _recentItems.SaveTabPlacement(placementStr);
    }

    private void ApplyTabPlacement(string placement)
    {
        var dock = placement switch
        {
            "Bottom" => Dock.Bottom,
            "Left"   => Dock.Left,
            "Right"  => Dock.Right,
            _        => Dock.Top
        };
        TabCtrl.TabStripPlacement = dock;

        var rb = placement switch
        {
            "Bottom" => TabPositionBottom,
            "Left"   => TabPositionLeft,
            "Right"  => TabPositionRight,
            _        => TabPositionTop
        };
        rb.IsChecked = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentEditor != null)
            Editor_SaveRequested(CurrentEditor, new SaveRequestedEventArgs(null));
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var tabInfo = CurrentTabInfo;
        if (tabInfo == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "All Files|*.*", Title = "Save File As" };
        if (dlg.ShowDialog() == true)
        {
            tabInfo.Editor.Engine.CurrentBuffer.Save(dlg.FileName);
            tabInfo.FilePath = dlg.FileName;
            tabInfo.UpdateHeader();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Undo_Click(object sender, RoutedEventArgs e) => CurrentEditor?.Engine.ProcessKey("u");
    private void Redo_Click(object sender, RoutedEventArgs e) => CurrentEditor?.Engine.ProcessKey("r", ctrl: true);

    private void ColorTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string name) return;
        ApplyColorTheme(name, _customBackground, _customAccent);
    }

    private static readonly string[] BgPaletteColors =
    [
        "#282A36", "#1E1E1E", "#0D1117", "#1A1B26",
        "#1E1E2E", "#2B2B2B", "#1B2738", "#0F0E17",
        "#202020", "#1C1F26",
    ];

    private static readonly string[] AccentPaletteColors =
    [
        "#6148DE", "#BD93F9", "#FF79C6", "#50FA7B",
        "#8BE9FD", "#FFB86C", "#0078D4", "#E06C75",
        "#61AFEF", "#98C379", "#C678DD", "#F1FA8C",
    ];

    private void InitColorPalettes()
    {
        var style = (Style)FindResource("ColorSwatchButton");
        foreach (var hex in BgPaletteColors)
            BgColorPalette.Children.Add(MakeColorSwatch(hex, style, BgSwatch_Click));
        foreach (var hex in AccentPaletteColors)
            AccentColorPalette.Children.Add(MakeColorSwatch(hex, style, AccentSwatch_Click));
        UpdatePaletteSelection();
    }

    private static Button MakeColorSwatch(string hex, Style style, RoutedEventHandler handler)
    {
        var btn = new Button
        {
            Style = style,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            Tag = hex,
            ToolTip = hex,
        };
        btn.Click += handler;
        return btn;
    }

    private void UpdatePaletteSelection()
    {
        if (!IsLoaded) return;
        var baseTheme = EditorTheme.GetByName(_baseThemeName);
        var currentBg = _customBackground ?? ColorToHex(GetThemeBackgroundColor(baseTheme));
        var currentAccent = _customAccent ?? ColorToHex(GetThemeAccentColor(baseTheme));

        foreach (Button btn in BgColorPalette.Children)
        {
            bool sel = string.Equals(btn.Tag as string, currentBg, StringComparison.OrdinalIgnoreCase);
            btn.BorderBrush = sel ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            btn.BorderThickness = new Thickness(sel ? 2 : 1);
        }
        foreach (Button btn in AccentColorPalette.Children)
        {
            bool sel = string.Equals(btn.Tag as string, currentAccent, StringComparison.OrdinalIgnoreCase);
            btn.BorderBrush = sel ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            btn.BorderThickness = new Thickness(sel ? 2 : 1);
        }
    }

    private void BgSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        _customBackground = hex;
        ApplyColorTheme(_baseThemeName, _customBackground, _customAccent);
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        _customAccent = hex;
        ApplyColorTheme(_baseThemeName, _customBackground, _customAccent);
    }

    private void ApplyColorTheme(string themeName, string? bgHex, string? accentHex)
    {
        _baseThemeName = themeName;
        _customBackground = bgHex;
        _customAccent = accentHex;

        var theme = EditorTheme.GetByName(themeName);
        if (TryParseHexColor(bgHex, out var bg))
            theme = theme.WithBackground(bg);
        if (TryParseHexColor(accentHex, out var accent))
            theme = theme.WithAccent(accent);

        _currentTheme = theme;
        foreach (var t in _tabs) t.Editor.SetTheme(_currentTheme);

        // Update dynamic accent resource (affects tabs, activity bar)
        var resolvedAccent = accentHex ?? ColorToHex(GetThemeAccentColor(EditorTheme.GetByName(themeName)));
        if (TryParseHexColor(resolvedAccent, out var accentColor))
            Resources["AccentBrush"] = new SolidColorBrush(accentColor);

        // Sync settings panel UI if it's loaded
        if (!IsLoaded) return;

        // Update theme radio buttons
        if (ThemeDraculaRb != null) ThemeDraculaRb.IsChecked = themeName == "Dracula";
        if (ThemeDarkRb != null) ThemeDarkRb.IsChecked = themeName == "Dark";

        // Update current-color swatches
        var baseTheme = EditorTheme.GetByName(themeName);
        var resolvedBgHex = bgHex ?? ColorToHex(GetThemeBackgroundColor(baseTheme));
        var resolvedAccentHex = accentHex ?? ColorToHex(GetThemeAccentColor(baseTheme));

        if (TryParseHexColor(resolvedBgHex, out var swatchBg))
            BgColorSwatch.Background = new SolidColorBrush(swatchBg);
        if (TryParseHexColor(resolvedAccentHex, out var swatchAccent))
            AccentColorSwatch.Background = new SolidColorBrush(swatchAccent);

        UpdatePaletteSelection();
        _recentItems.SaveTheme(themeName, bgHex, accentHex);
    }

    private static Color GetThemeBackgroundColor(EditorTheme theme)
        => theme.Background is SolidColorBrush b ? b.Color : Colors.Black;

    private static Color GetThemeAccentColor(EditorTheme theme)
        => theme.StatusBarNormal is SolidColorBrush b ? b.Color : Colors.DarkBlue;

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        try
        {
            color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch { return false; }
    }

    private void TabCtrl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CurrentEditor?.Focus();
    }

    // ─────────── Title bar controls ─────────────────────────

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "3" : "2";
        RootPanel.Margin = WindowState == WindowState.Maximized
            ? new Thickness(6)
            : new Thickness(0);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var unsaved = _tabs.Where(t => t.Editor.Engine.CurrentBuffer.Text.IsModified).ToList();
        if (unsaved.Count > 0)
        {
            var result = MessageBox.Show(
                "You have unsaved changes. Exit anyway?",
                "Editor",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                base.OnClosing(e);
                return;
            }
        }

        var tabFiles = _tabs
            .Where(t => t.FilePath != null)
            .Select(t => t.FilePath!);
        var activeFile = CurrentTabInfo?.FilePath;
        _recentItems.SaveSession(_currentFolderPath, tabFiles, activeFile);

        base.OnClosing(e);
    }
}

// ─────────── File tree model ─────────────────────────────────

public sealed class FileTreeItem
{
    private bool _expanded;
    private readonly bool _isPlaceholder;

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public string Icon { get; }
    public Brush IconBrush { get; }
    public ObservableCollection<FileTreeItem> Children { get; } = [];

    public FileTreeItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
        IsDirectory = Directory.Exists(path);
        Icon = IsDirectory ? "\uED41" : "\uE7C3";
        IconBrush = IsDirectory
            ? new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7B))
            : new SolidColorBrush(Color.FromRgb(0x99, 0xBB, 0xDD));

        if (IsDirectory)
            Children.Add(Placeholder);
    }

    private FileTreeItem()
    {
        _isPlaceholder = true;
        FullPath = string.Empty;
        Name = string.Empty;
        Icon = string.Empty;
        IconBrush = Brushes.Transparent;
        _expanded = true;
    }

    private static readonly FileTreeItem Placeholder = new();

    public void Expand()
    {
        if (_expanded || _isPlaceholder) return;
        _expanded = true;
        Children.Clear();
        if (!Directory.Exists(FullPath)) return;
        try
        {
            foreach (var d in Directory.GetDirectories(FullPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FileTreeItem(d));
            foreach (var f in Directory.GetFiles(FullPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FileTreeItem(f));
        }
        catch { /* access denied, etc. */ }
    }

    public void Refresh()
    {
        if (!IsDirectory) return;
        _expanded = false;
        Children.Clear();
        Children.Add(Placeholder);
        Expand();
    }

    /// <summary>Load top-level children of a folder (eager, for root display).</summary>
    public static ObservableCollection<FileTreeItem> LoadChildren(string folderPath)
    {
        var items = new ObservableCollection<FileTreeItem>();
        try
        {
            foreach (var d in Directory.GetDirectories(folderPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                items.Add(new FileTreeItem(d));
            foreach (var f in Directory.GetFiles(folderPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                items.Add(new FileTreeItem(f));
        }
        catch { }
        return items;
    }
}

// ─────────── Native folder picker (COM IFileOpenDialog) ──────

internal static class NativeFolderPicker
{
    public static string? Show(IntPtr ownerHandle, string title = "Select Folder")
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
        try
        {
            dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dialog.SetTitle(title);
            int hr = dialog.Show(ownerHandle);
            if (hr != 0) return null;   // cancelled or error
            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            return path;
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private const uint FOS_PICKFOLDERS    = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST  = 0x00000800;
    private const uint SIGDN_FILESYSPATH  = 0x80058000;

    [ComImport, ClassInterface(ClassInterfaceType.None),
     Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass { }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show([In] IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppenum);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint, out int piOrder);
    }
}

// ─────────── Shell menu item model ───────────────────────────

// ─────────── Shell context menu (COM IContextMenu) ───────────

internal sealed class ShellMenuContext : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public uint   cbSize;
        public uint   fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int    nShow;
        public uint   dwHotKey;
        public IntPtr hIcon;
    }

    private const uint CMF_NORMAL      = 0x00000000;
    private const uint CMF_EXPLORE     = 0x00000020;
    private const uint TPM_RETURNCMD   = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenuCom
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolderCom
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl, ref Guid riid, uint rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    private object? _ctxObj;
    private object? _folderObj;
    private IntPtr  _pidlFull;
    private IntPtr  _hMenu;
    private IContextMenuCom? _ctx;

    private ShellMenuContext(object ctxObj, object folderObj, IntPtr pidlFull,
        IntPtr hMenu, IContextMenuCom ctx)
    {
        _ctxObj    = ctxObj;
        _folderObj = folderObj;
        _pidlFull  = pidlFull;
        _hMenu     = hMenu;
        _ctx       = ctx;
    }

    private static ShellMenuContext? Create(IntPtr hwnd, string path)
    {
        IntPtr pidlFull   = IntPtr.Zero;
        IntPtr hMenu      = IntPtr.Zero;
        object? ctxObj    = null;
        object? folderObj = null;

        try
        {
            int hr = SHParseDisplayName(path, IntPtr.Zero, out pidlFull, 0, out _);
            if (hr != 0 || pidlFull == IntPtr.Zero) return null;

            var iidFolder = new Guid("000214E6-0000-0000-C000-000000000046");
            hr = SHBindToParent(pidlFull, ref iidFolder, out IntPtr psfParent, out IntPtr pidlChild);
            if (hr != 0 || psfParent == IntPtr.Zero) return null;

            folderObj = Marshal.GetObjectForIUnknown(psfParent);
            Marshal.Release(psfParent);

            var iidCtx = new Guid("000214e4-0000-0000-c000-000000000046");
            IntPtr[] pidls = [pidlChild];
            hr = ((IShellFolderCom)folderObj).GetUIObjectOf(hwnd, 1, pidls, ref iidCtx, 0, out IntPtr pCtx);
            if (hr != 0 || pCtx == IntPtr.Zero) return null;

            ctxObj = Marshal.GetObjectForIUnknown(pCtx);
            Marshal.Release(pCtx);
            var ctx = (IContextMenuCom)ctxObj;

            hMenu = CreatePopupMenu();
            ctx.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);

            return new ShellMenuContext(ctxObj, folderObj, pidlFull, hMenu, ctx);
        }
        catch
        {
            if (hMenu     != IntPtr.Zero) DestroyMenu(hMenu);
            if (pidlFull  != IntPtr.Zero) ILFree(pidlFull);
            if (ctxObj    != null) Marshal.ReleaseComObject(ctxObj);
            if (folderObj != null) Marshal.ReleaseComObject(folderObj);
            return null;
        }
    }

    // Show the Win32 popup and invoke the selected command.
    private void Show(IntPtr hwnd, int x, int y)
    {
        if (_ctx == null || _hMenu == IntPtr.Zero) return;

        uint cmd = TrackPopupMenuEx(_hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, hwnd, IntPtr.Zero);
        if (cmd == 0) return;

        var ici = new CMINVOKECOMMANDINFO
        {
            cbSize       = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
            hwnd         = hwnd,
            lpVerb       = new IntPtr((int)(cmd - 1)),
            lpParameters = IntPtr.Zero,
            lpDirectory  = IntPtr.Zero,
            nShow        = 1
        };
        IntPtr pIci = Marshal.AllocHGlobal(Marshal.SizeOf<CMINVOKECOMMANDINFO>());
        try
        {
            Marshal.StructureToPtr(ici, pIci, false);
            _ctx.InvokeCommand(pIci);
        }
        catch { /* best-effort */ }
        finally { Marshal.FreeHGlobal(pIci); }
    }

    public static void ShowDirect(IntPtr hwnd, string path, int x, int y)
    {
        using var ctx = Create(hwnd, path);
        ctx?.Show(hwnd, x, y);
    }

    public void Dispose()
    {
        if (_hMenu    != IntPtr.Zero) { DestroyMenu(_hMenu);   _hMenu    = IntPtr.Zero; }
        if (_pidlFull != IntPtr.Zero) { ILFree(_pidlFull);     _pidlFull = IntPtr.Zero; }
        if (_ctxObj    != null) { Marshal.ReleaseComObject(_ctxObj);    _ctxObj    = null; }
        if (_folderObj != null) { Marshal.ReleaseComObject(_folderObj); _folderObj = null; }
        _ctx = null;
    }
}
