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

    private readonly List<TabInfo> _tabs = [];
    private readonly RecentItemsManager _recentItems = new();
    private EditorTheme _currentTheme = EditorTheme.Dracula;
    private bool _sidebarVisible;
    private double _sidebarWidth = 220;
    private string? _currentFolderPath;

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

    private void ShowSidebar()
    {
        SidebarCol.Width = new GridLength(_sidebarWidth, GridUnitType.Pixel);
        SidebarCol.MinWidth = 80;
        SplitterCol.Width = new GridLength(4, GridUnitType.Pixel);
        SplitterCol.MinWidth = 4;
        ExplorerBtn.IsChecked = true;
        _sidebarVisible = true;
    }

    private void HideSidebar()
    {
        _sidebarWidth = SidebarCol.ActualWidth > 0 ? SidebarCol.ActualWidth : _sidebarWidth;
        SidebarCol.Width = new GridLength(0);
        SidebarCol.MinWidth = 0;
        SplitterCol.Width = new GridLength(0);
        SplitterCol.MinWidth = 0;
        ExplorerBtn.IsChecked = false;
        _sidebarVisible = false;
    }

    private void ToggleSidebar()
    {
        if (_sidebarVisible)
            HideSidebar();
        else
            ShowSidebar();
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

    private void ExplorerBtn_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

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

    private void LineNumbers_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        CurrentEditor?.ExecuteCommand(mi.IsChecked ? "set number" : "set nonumber");
    }

    private void Syntax_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        CurrentEditor?.ExecuteCommand(mi.IsChecked ? "syntax on" : "syntax off");
    }

    private void ThemeDracula_Click(object sender, RoutedEventArgs e)
    {
        _currentTheme = EditorTheme.Dracula;
        foreach (var t in _tabs) t.Editor.SetTheme(_currentTheme);
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e)
    {
        _currentTheme = EditorTheme.Dark;
        foreach (var t in _tabs) t.Editor.SetTheme(_currentTheme);
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
