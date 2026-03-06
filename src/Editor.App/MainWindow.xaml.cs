using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Editor.Controls;
using Editor.Controls.Themes;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Panes;
using Editor.Core.Syntax;

namespace Editor.App;

public partial class MainWindow : Window
{
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WheelDelta = 120;
    private HwndSource? _windowSource;

    // ─────────── Pane tree ──────────────────────────────────────

    private abstract class PaneNode
    {
        public abstract IEnumerable<VimEditorControl> AllEditors();
    }

    private sealed class EditorPaneNode : PaneNode
    {
        public required VimEditorControl Editor { get; init; }
        public override IEnumerable<VimEditorControl> AllEditors() { yield return Editor; }
    }

    private sealed class SplitPaneNode : PaneNode
    {
        public bool Vertical { get; init; }
        public required PaneNode First { get; set; }
        public required PaneNode Second { get; set; }
        public required Grid Container { get; set; }

        public override IEnumerable<VimEditorControl> AllEditors()
            => First.AllEditors().Concat(Second.AllEditors());

        public static Grid BuildGrid(bool vertical, UIElement first, UIElement second)
        {
            var grid = new Grid();
            var splitter = new GridSplitter
            {
                Background = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x5A)),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            if (vertical)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                splitter.Width = 4;
                Grid.SetColumn(first, 0); Grid.SetColumn(splitter, 1); Grid.SetColumn(second, 2);
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                splitter.Height = 4;
                Grid.SetRow(first, 0); Grid.SetRow(splitter, 1); Grid.SetRow(second, 2);
            }
            grid.Children.Add(first);
            grid.Children.Add(splitter);
            grid.Children.Add(second);
            return grid;
        }

        public void ReplaceChild(UIElement oldChild, UIElement newChild)
        {
            int idx = Container.Children.IndexOf(oldChild);
            if (idx < 0) return;
            if (Vertical) Grid.SetColumn(newChild, Grid.GetColumn(oldChild));
            else          Grid.SetRow(newChild, Grid.GetRow(oldChild));
            Container.Children.RemoveAt(idx);
            Container.Children.Insert(idx, newChild);
        }
    }

    private static UIElement PaneToElement(PaneNode node) => node switch
    {
        EditorPaneNode e => e.Editor,
        SplitPaneNode s  => s.Container,
        _ => throw new InvalidOperationException()
    };

    private static SplitPaneNode? FindParentSplit(PaneNode root, PaneNode target)
    {
        if (root is SplitPaneNode spn)
        {
            if (spn.First == target || spn.Second == target) return spn;
            return FindParentSplit(spn.First, target) ?? FindParentSplit(spn.Second, target);
        }
        return null;
    }

    private static EditorPaneNode? FindEditorPane(PaneNode root, VimEditorControl editor)
    {
        if (root is EditorPaneNode epn && epn.Editor == editor) return epn;
        if (root is SplitPaneNode spn)
            return FindEditorPane(spn.First, editor) ?? FindEditorPane(spn.Second, editor);
        return null;
    }

    // ─────────── File tab (represents an open file in the tab bar) ─────────────

    private sealed class FileTab
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

    private enum SidebarPanel { None, Explorer, Settings, Outline }

    // ── Search popup ────────────────────────────────────────

    private enum SearchMode { All, Class, File, Symbol, Action, Text }

    private sealed class SearchResultItem
    {
        public required string DisplayName { get; init; }
        public string Detail   { get; init; } = "";
        public string Icon     { get; init; } = "\uE7C3";
        public string IconColor{ get; init; } = "#99BBDD";
        public string? FilePath       { get; init; }
        public Action? ActionCallback { get; init; }
        /// <summary>0-indexed line number; -1 = no line info (file-only result).</summary>
        public int Line { get; init; } = -1;
        public int Col  { get; init; } = 0;
        /// <summary>The active search query used to highlight matching characters.</summary>
        public string SearchQuery { get; set; } = "";
        /// <summary>True only for text-content search results — highlights query matches in the file preview.</summary>
        public bool HighlightQueryInPreview { get; init; } = false;
    }

    private static readonly SearchMode[] SearchModeOrder =
    [
        SearchMode.All, SearchMode.Class, SearchMode.File,
        SearchMode.Symbol, SearchMode.Action, SearchMode.Text
    ];

    private static readonly (string Label, string Color)[] SearchModeDisplays =
    [
        ("すべて",    "#BD93F9"),
        ("クラス名",  "#50FA7B"),
        ("ファイル名","#8BE9FD"),
        ("シンボル",  "#FFB86C"),
        ("アクション","#FF79C6"),
        ("テキスト",  "#F1FA8C"),
    ];

    private SearchMode _searchMode = SearchMode.All;
    private bool _searchActive;
    private bool _searchTabsInitialized;
    private CancellationTokenSource? _searchCts;
    private readonly SyntaxEngine _previewSyntaxEngine = new();

    // ────────────────────────────────────────────────────────

    private readonly List<FileTab> _fileTabs = [];
    private PaneNode? _globalRoot;
    private VimEditorControl? _focusedEditor;
    private bool _suppressTabSelectionChanged;
    private readonly RecentItemsManager _recentItems = new();
    private EditorTheme _currentTheme = EditorTheme.Dracula;
    private string _baseThemeName = "Dracula";
    private string? _customBackground;
    private string? _customAccent;
    private bool _sidebarVisible;
    private double _sidebarWidth = 220;
    private string? _currentFolderPath;
    private int _quickfixCurrentIndex = -1;
    private SidebarPanel _activeSidebarPanel = SidebarPanel.None;
    private System.Windows.Point _shellMenuScreenPos;

    private VimEditorControl? CurrentEditor => _focusedEditor;

    private FileTab? FindFileTabByPath(string? path) =>
        path == null
            ? _fileTabs.FirstOrDefault(t => t.FilePath == null)
            : _fileTabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<VimEditorControl> AllEditors() =>
        _globalRoot?.AllEditors() ?? Enumerable.Empty<VimEditorControl>();

    private void UpdateSelectedTabForEditor(VimEditorControl editor)
    {
        var path = editor.Engine.CurrentBuffer.FilePath;
        var ft = FindFileTabByPath(path);
        _suppressTabSelectionChanged = true;
        TabCtrl.SelectedItem = ft?.Item;
        _suppressTabSelectionChanged = false;
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageProc);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Create the initial global editor and set up the pane tree
        var initialEditor = CreateEditor(null);
        _globalRoot = new EditorPaneNode { Editor = initialEditor };
        _focusedEditor = initialEditor;
        EditorContent.Child = initialEditor;

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            if (Directory.Exists(args[1]))
            {
                AddTab(null);
                LoadFolder(args[1]);
            }
            else if (File.Exists(args[1]))
                AddTab(args[1]);
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

        // Restore file tabs (add entries without loading yet)
        foreach (var file in session.TabFiles)
        {
            if (File.Exists(file))
                AddFileTabEntry(file);
        }
        if (_fileTabs.Count == 0)
        {
            AddTab(null);
        }
        else
        {
            // Load the active file (or the first one)
            var activeTab = session.ActiveFile != null
                ? FindFileTabByPath(session.ActiveFile)
                : null;
            SelectFileTab(activeTab ?? _fileTabs[0]);
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!_searchActive)
                OpenSearch();
            else
                CycleSearchMode();
            e.Handled = true;
            return;
        }

        if (!_searchActive) return;

        switch (e.Key)
        {
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (Keyboard.Modifiers == ModifierKeys.Control)
                    SendSearchResultsToPanel();
                else
                    ExecuteSelectedResult();
                e.Handled = true;
                break;
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

        // Hide all panels first, then show the requested one
        ExplorerPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility  = Visibility.Collapsed;
        OutlinePanel.Visibility   = Visibility.Collapsed;
        ExplorerBtn.IsChecked = false;
        SettingsBtn.IsChecked  = false;
        OutlineBtn.IsChecked   = false;

        switch (panel)
        {
            case SidebarPanel.Settings:
                SettingsPanel.Visibility = Visibility.Visible;
                SettingsBtn.IsChecked = true;
                break;
            case SidebarPanel.Outline:
                OutlinePanel.Visibility = Visibility.Visible;
                OutlineBtn.IsChecked = true;
                break;
            default: // Explorer
                ExplorerPanel.Visibility = Visibility.Visible;
                ExplorerBtn.IsChecked = true;
                break;
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
        SettingsBtn.IsChecked  = false;
        OutlineBtn.IsChecked   = false;
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

    private IntPtr WindowMessageProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL)
            return IntPtr.Zero;

        if (TryHandleHorizontalWheelMessage(wParam, lParam))
            handled = true;

        return IntPtr.Zero;
    }

    private bool TryHandleHorizontalWheelMessage(IntPtr wParam, IntPtr lParam)
    {
        short wheelDelta = unchecked((short)((long)wParam >> 16));
        if (wheelDelta == 0)
            return false;

        long packedPoint = lParam.ToInt64();
        int screenX = unchecked((short)(packedPoint & 0xFFFF));
        int screenY = unchecked((short)((packedPoint >> 16) & 0xFFFF));
        var pointInWindow = PointFromScreen(new Point(screenX, screenY));
        if (InputHitTest(pointInWindow) is not DependencyObject hit)
            return false;

        var editor = FindVisualAncestor<VimEditorControl>(hit);
        if (editor != null && editor.ScrollHorizontalByWheelDelta(wheelDelta))
            return true;

        var scrollViewer = FindHorizontalScrollViewer(hit);
        if (scrollViewer == null)
            return false;

        double step = scrollViewer.ViewportWidth > 0
            ? Math.Max(24.0, scrollViewer.ViewportWidth * 0.08)
            : 48.0;
        double delta = (wheelDelta / (double)WheelDelta) * step;
        double target = Math.Clamp(scrollViewer.HorizontalOffset + delta, 0.0, scrollViewer.ScrollableWidth);
        if (Math.Abs(target - scrollViewer.HorizontalOffset) < 0.01)
            return false;

        scrollViewer.ScrollToHorizontalOffset(target);
        return true;
    }

    private static ScrollViewer? FindHorizontalScrollViewer(DependencyObject? obj)
    {
        while (obj != null)
        {
            if (obj is ScrollViewer sv && sv.ScrollableWidth > 0)
                return sv;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private void OpenOrFocusFile(string path)
    {
        // Already tracked in a tab? Select it (and load into focused pane).
        var existing = FindFileTabByPath(path);
        if (existing != null)
        {
            SelectFileTab(existing);
            return;
        }

        // Otherwise add a new tab entry and load
        AddTab(path);
    }

    // ─────────── Tab management ────────────────────────────

    private VimEditorControl CreateEditor(string? filePath)
    {
        var editor = new VimEditorControl();
        editor.SetTheme(_currentTheme);
        editor.SetSharedStatusBar(SharedStatusBar);
        WireEditorEvents(editor);
        if (filePath != null && File.Exists(filePath))
            editor.LoadFile(filePath);
        else
            editor.SetText("");
        return editor;
    }

    /// <summary>Add a file tab entry to the tab bar (without loading into focused pane).</summary>
    private FileTab AddFileTabEntry(string? filePath)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var closeBtn = new Button { Content = "×", Style = (Style)FindResource("TabCloseButton") };
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
        TabCtrl.Items.Add(tabItem);
        return fileTab;
    }

    /// <summary>Ensure a file is tracked in the tab bar. Returns existing or newly-created FileTab.</summary>
    private FileTab EnsureFileTab(string? filePath)
    {
        var existing = FindFileTabByPath(filePath);
        if (existing != null) return existing;
        return AddFileTabEntry(filePath);
    }

    /// <summary>Add tab entry and immediately load the file into the focused pane.</summary>
    private void AddTab(string? filePath)
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
    private void SelectFileTab(FileTab fileTab)
    {
        _suppressTabSelectionChanged = true;
        TabCtrl.SelectedItem = fileTab.Item;
        _suppressTabSelectionChanged = false;

        if (_focusedEditor == null) return;

        if (fileTab.FilePath != null && File.Exists(fileTab.FilePath))
            _focusedEditor.LoadFile(fileTab.FilePath);
        // FilePath == null → keep current content (new empty buffer shown as-is)

        _focusedEditor.Focus();
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
        editor.CloseTabRequested    += Editor_CloseTabRequested;
        editor.WindowNavRequested   += Editor_WindowNavRequested;
        editor.WindowCloseRequested += Editor_WindowCloseRequested;
        editor.BufferChanged        += Editor_BufferChanged;
        editor.FindReferencesResult += Editor_FindReferencesResult;
        editor.QuickfixOpenRequested  += (_, _) => ShowReferencesPanel();
        editor.QuickfixCloseRequested += (_, _) => CloseRefPanel_Click(editor, new RoutedEventArgs());
        editor.QuickfixNextRequested  += (_, count) => QuickfixNavigate(count);
        editor.QuickfixPrevRequested  += (_, count) => QuickfixNavigate(-count);
        editor.QuickfixGotoRequested  += (_, index) => QuickfixNavigateTo(index);
        editor.GrepRequested          += Editor_GrepRequested;
        editor.GotKeyboardFocus       += Editor_GotKeyboardFocus;
        editor.MkSessionRequested     += Editor_MkSessionRequested;
        editor.SourceRequested        += Editor_SourceRequested;
        editor.TerminalRequested      += Editor_TerminalRequested;
        editor.GitOutputRequested     += Editor_GitOutputRequested;
        editor.GitCommitRequested     += Editor_GitCommitRequested;
        editor.DocumentSymbolsResult  += Editor_DocumentSymbolsResult;
    }

    private void Editor_BufferChanged(object? sender, EventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        var path = editor.Engine.CurrentBuffer.FilePath;
        var ft = FindFileTabByPath(path);
        ft?.UpdateHeader(isModified: editor.Engine.CurrentBuffer.Text.IsModified);
    }

    private void Editor_MkSessionRequested(object? sender, string sessionPath)
    {
        // Collect all open files from all editors
        var files = new List<(string FilePath, int Line, int Col)>();
        int activeIndex = 0;
        int idx = 0;
        foreach (var editor in AllEditors())
        {
            var fp = editor.Engine.CurrentBuffer.FilePath;
            if (fp == null) { idx++; continue; }
            var cursor = editor.Engine.Cursor;
            files.Add((fp, cursor.Line, cursor.Column));
            if (editor == _focusedEditor) activeIndex = idx;
            idx++;
        }

        var resolvedPath = System.IO.Path.IsPathRooted(sessionPath)
            ? sessionPath
            : System.IO.Path.GetFullPath(sessionPath);

        SessionManager.Save(resolvedPath, files, activeIndex);
        MessageBox.Show($"Session saved:\n{resolvedPath}", "Session Saved",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Editor_TerminalRequested(object? sender, string? shellCmd)
    {
        ShowTerminalPanel();
    }

    private void Editor_GitOutputRequested(object? sender, GitOutputRequestedEventArgs e)
    {
        // Open a new tab with in-memory git output content
        var ft = AddFileTabEntry(null);
        SelectFileTab(ft);
        ft.UpdateHeader(isModified: false, label: e.Title);
        _focusedEditor?.SetText(e.Content);
    }

    private void Editor_GitCommitRequested(object? sender, GitCommitRequestedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;

        // Show a commit message input dialog
        var message = ShowInputDialog("Git Commit", "Commit message:", "");
        if (string.IsNullOrWhiteSpace(message)) return;

        var (success, output) = editor.ExecuteGitCommit(message);
        if (success)
            MessageBox.Show(output, "Git Commit", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(output, "Git Commit Failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowTerminalPanel()
    {
        if (TerminalPanel.Visibility == Visibility.Visible)
        {
            TerminalContent.Focus();
            return;
        }

        var terminal = new TerminalPane();
        var workDir = _focusedEditor?.Engine.CurrentBuffer.FilePath is { } fp
            ? Path.GetDirectoryName(fp)
            : null;
        terminal.SetWorkingDirectory(workDir);

        TerminalContent.Content = terminal;
        TerminalPanel.Visibility = Visibility.Visible;
        TermSplitter.Visibility  = Visibility.Visible;
        TermSplitterRow.Height   = new GridLength(4);
        TermPanelRow.Height      = new GridLength(200);
        terminal.Focus();
    }

    private void CloseTermPanel_Click(object sender, RoutedEventArgs e)
    {
        TerminalPanel.Visibility = Visibility.Collapsed;
        TermSplitter.Visibility  = Visibility.Collapsed;
        TermSplitterRow.Height   = new GridLength(0);
        TermPanelRow.Height      = new GridLength(0);
        TerminalContent.Content  = null;
        _focusedEditor?.Focus();
    }

    private void Editor_SourceRequested(object? sender, string filePath)
    {
        var resolved = System.IO.Path.IsPathRooted(filePath)
            ? filePath
            : System.IO.Path.GetFullPath(filePath);

        if (!File.Exists(resolved))
        {
            MessageBox.Show($"E484: Cannot open file: {resolved}", "Source Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var commands = SessionManager.Load(resolved);
        if (commands == null) return;

        bool firstFile = true;
        foreach (var cmd in commands)
        {
            switch (cmd.Type)
            {
                case SessionCommandType.OpenFile:
                    if (cmd.Path != null && File.Exists(cmd.Path))
                    {
                        if (firstFile) { _focusedEditor?.LoadFile(cmd.Path); firstFile = false; }
                        else AddTab(cmd.Path);
                    }
                    break;
                case SessionCommandType.OpenFileInTab:
                    if (cmd.Path != null && File.Exists(cmd.Path))
                        AddTab(cmd.Path);
                    break;
                case SessionCommandType.SetCursor:
                    _focusedEditor?.Engine.SetCursorPosition(
                        new Editor.Core.Models.CursorPosition(cmd.Line, cmd.Col));
                    break;
                case SessionCommandType.SwitchTab:
                    if (cmd.Line >= 0 && cmd.Line < TabCtrl.Items.Count)
                        TabCtrl.SelectedIndex = cmd.Line;
                    break;
            }
        }
    }

    // ─────────────── References panel ───────────────

    private sealed class ReferenceListItem
    {
        public required string FilePath  { get; init; }
        public required string FileName  { get; init; }
        public required string LineCol   { get; init; }
        public required string Preview   { get; init; }
        public required int    Line      { get; init; }
        public required int    Col       { get; init; }
    }

    private void Editor_FindReferencesResult(object? sender, FindReferencesResultEventArgs e)
    {
        var items = e.Items.Select(r =>
        {
            string fileName = Path.GetFileName(r.FilePath);
            string lineCol  = $":{r.Line + 1}:{r.Col + 1}";
            string preview  = ReadSourceLine(r.FilePath, r.Line);
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

        RefList.SelectionChanged -= RefList_SelectionChanged;
        RefList.ItemsSource = items;
        RefList.SelectedIndex = -1;
        _quickfixCurrentIndex = -1;
        RefList.SelectionChanged += RefList_SelectionChanged;

        int fileCount = items.Select(i => i.FilePath).Distinct().Count();
        RefPanelTitle.Text = $"REFERENCES ({items.Count}) — {e.SymbolName}  [{fileCount} file(s)]";

        ShowReferencesPanel();
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
        RefPanelRow.Height     = new System.Windows.GridLength(200);
        RefSplitterRow.Height  = new System.Windows.GridLength(4);
        ReferencesPanel.Visibility = Visibility.Visible;
        RefSplitter.Visibility     = Visibility.Visible;
    }

    private void Editor_GrepRequested(object? sender, GrepRequestedEventArgs e)
    {
        var currentFilePath = _focusedEditor?.Engine.CurrentBuffer.FilePath;
        _ = RunGrepAsync(e.Pattern, e.FileGlob, e.IgnoreCase, currentFilePath);
    }

    private async Task RunGrepAsync(string pattern, string? fileGlob, bool ignoreCase, string? currentFilePath)
    {
        RefList.SelectionChanged -= RefList_SelectionChanged;
        RefList.ItemsSource = null;
        _quickfixCurrentIndex = -1;
        RefPanelTitle.Text = $"GREP \"{pattern}\" — Searching…";
        ShowReferencesPanel();
        RefList.SelectionChanged += RefList_SelectionChanged;

        List<ReferenceListItem> results;
        try
        {
            results = await Task.Run(() => ExecuteGrep(pattern, fileGlob, ignoreCase, currentFilePath));
        }
        catch { results = []; }

        RefList.SelectionChanged -= RefList_SelectionChanged;
        RefList.ItemsSource = results;
        RefList.SelectedIndex = -1;
        _quickfixCurrentIndex = -1;
        RefList.SelectionChanged += RefList_SelectionChanged;

        int fileCount = results.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        RefPanelTitle.Text = results.Count > 0
            ? $"GREP \"{pattern}\" — {results.Count} matches [{fileCount} file(s)]"
            : $"GREP \"{pattern}\" — no matches";

    }

    private List<ReferenceListItem> ExecuteGrep(string pattern, string? fileGlob, bool ignoreCase, string? currentFilePath)
    {
        var results = new List<ReferenceListItem>();

        Regex regex;
        try
        {
            var opts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
            regex = new Regex(pattern, opts);
        }
        catch { return results; }

        IEnumerable<string> files;
        if (fileGlob == "%")
        {
            files = currentFilePath != null ? [currentFilePath] : [];
        }
        else
        {
            var root = _currentFolderPath
                       ?? (currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null);
            if (root == null) return results;
            files = EnumerateSourceFiles(root);

            if (!string.IsNullOrEmpty(fileGlob))
            {
                var exts = GetExtensionsFromGlob(fileGlob);
                if (exts != null)
                    files = files.Where(f => exts.Any(e =>
                        f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
            }
        }

        foreach (var f in files)
        {
            if (new FileInfo(f).Length > 5_000_000) continue;
            try
            {
                var lineIdx = 0;
                foreach (var line in File.ReadLines(f))
                {
                    var m = regex.Match(line);
                    if (m.Success)
                        results.Add(new ReferenceListItem
                        {
                            FilePath = f,
                            FileName = Path.GetFileName(f),
                            LineCol  = $":{lineIdx + 1}:{m.Index + 1}",
                            Preview  = line.Trim(),
                            Line     = lineIdx,
                            Col      = m.Index,
                        });
                    lineIdx++;
                }
            }
            catch { /* skip unreadable */ }
        }

        return results;
    }

    private static string[]? GetExtensionsFromGlob(string glob)
    {
        // e.g. "**/*.cs" → ".cs",  "*.{cs,ts}" → [".cs", ".ts"],  "**" → null
        var lastSlash = glob.LastIndexOfAny(['/', '\\']);
        var pat = lastSlash >= 0 ? glob[(lastSlash + 1)..] : glob;

        if (pat.StartsWith("*.{") && pat.EndsWith('}'))
        {
            return pat[3..^1].Split(',')
                .Select(e => "." + e.Trim())
                .ToArray();
        }

        var dotIdx = pat.IndexOf('.');
        if (dotIdx >= 0 && dotIdx < pat.Length - 1)
            return ["." + pat[(dotIdx + 1)..]];

        return null;
    }

    private void QuickfixNavigate(int delta)
    {
        var count = RefList.Items.Count;
        if (count == 0) return;
        ShowReferencesPanel();
        _quickfixCurrentIndex = Math.Clamp(_quickfixCurrentIndex + delta, 0, count - 1);
        RefList.SelectedIndex = _quickfixCurrentIndex;
        RefList.ScrollIntoView(RefList.SelectedItem);
        CurrentEditor?.Focus();
    }

    private void QuickfixNavigateTo(int index)
    {
        var count = RefList.Items.Count;
        if (count == 0) return;
        ShowReferencesPanel();
        // index == -1 means :cc with no arg — go to current item (or first)
        _quickfixCurrentIndex = index < 0
            ? Math.Max(0, _quickfixCurrentIndex)
            : Math.Clamp(index, 0, count - 1);
        RefList.SelectedIndex = _quickfixCurrentIndex;
        RefList.ScrollIntoView(RefList.SelectedItem);
        CurrentEditor?.Focus();
    }

    private void CloseRefPanel_Click(object sender, RoutedEventArgs e)
    {
        ReferencesPanel.Visibility = Visibility.Collapsed;
        RefSplitter.Visibility     = Visibility.Collapsed;
        RefPanelRow.Height    = new System.Windows.GridLength(0);
        RefSplitterRow.Height = new System.Windows.GridLength(0);
        // Return focus to editor
        CurrentEditor?.Focus();
    }

    private void RefList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RefList.SelectedItem is not ReferenceListItem item) return;

        var editor = CurrentEditor;
        if (editor == null) return;

        if (string.Equals(item.FilePath, editor.Engine.CurrentBuffer.FilePath,
                          StringComparison.OrdinalIgnoreCase))
        {
            editor.NavigateTo(item.Line, item.Col);
            editor.Focus();
        }
        else
        {
            OpenFile(item.FilePath);
            CurrentEditor?.NavigateTo(item.Line, item.Col);
        }
    }

    private void CloseFileTab(FileTab fileTab, bool force)
    {
        // Check ALL panes showing this file for unsaved changes
        var modifiedEditors = AllEditors()
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
        TabCtrl.Items.Remove(fileTab.Item);

        if (_fileTabs.Count == 0)
        {
            // If there are split panes, close them all first; then close app
            Close();
            return;
        }

        // Load the next adjacent tab into the focused pane
        var nextIdx = Math.Clamp(idx, 0, _fileTabs.Count - 1);
        SelectFileTab(_fileTabs[nextIdx]);
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        if (_focusedEditor == null) return;
        _focusedEditor.LoadFile(path);
        // Ensure the file has a tab entry
        var ft = EnsureFileTab(path);
        _suppressTabSelectionChanged = true;
        TabCtrl.SelectedItem = ft.Item;
        _suppressTabSelectionChanged = false;
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
        if (sender is not VimEditorControl editor) return;
        var buf = editor.Engine.CurrentBuffer;

        editor.OnSaveStarted();
        try
        {
            if (e.FilePath != null)
            {
                try { buf.Save(e.FilePath); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Save failed: {ex.Message}", "Editor", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                // Ensure a tab exists for the new path
                EnsureFileTab(e.FilePath);
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
                var ft = FindFileTabByPath(null) ?? EnsureFileTab(dlg.FileName);
                ft.FilePath = dlg.FileName;
                ft.UpdateHeader(isModified: false);
                return;
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
        }
        finally
        {
            // Re-enable the file watcher after a short delay to let the OS
            // finish flushing the file so we don't trigger a spurious reload.
            editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                () => editor.OnSaveFinished());
        }
        var fileTab = FindFileTabByPath(buf.FilePath);
        fileTab?.UpdateHeader(isModified: false);
    }

    private void Editor_QuitRequested(object? sender, QuitRequestedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        // :wq/:x saves first (engine handles it), then closes the current tab.
        // :qa/:qa! closes the current tab; if it's the last one, the app exits.
        var ft = FindFileTabByPath(editor.Engine.CurrentBuffer.FilePath);
        if (ft != null)
            CloseFileTab(ft, e.Force);
        else
            Close();
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

        // Navigate to the target position if provided (e.g. from go-to-definition)
        if ((e.Line > 0 || e.Column > 0) && sender is VimEditorControl navEditor)
            navEditor.NavigateTo(e.Line, e.Column);
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
        if (sender is not VimEditorControl source) return;
        if (_globalRoot == null) return;

        var sourcePane = FindEditorPane(_globalRoot, source);
        if (sourcePane == null) return;

        // Resolve file to open in new pane (default: same file as source)
        var newFilePath = e.FilePath != null
            ? ResolvePath(e.FilePath)
            : source.Engine.CurrentBuffer.FilePath;
        var newEditor = CreateEditor(newFilePath);
        var newLeaf = new EditorPaneNode { Editor = newEditor };

        // WPF requires element removed from its current parent before being added to a new Grid.
        var firstElement = (UIElement)sourcePane.Editor;
        var parentSplit = FindParentSplit(_globalRoot, sourcePane);
        int gridPos = -1;
        if (parentSplit != null)
        {
            gridPos = parentSplit.Vertical
                ? Grid.GetColumn(firstElement)
                : Grid.GetRow(firstElement);
            parentSplit.Container.Children.Remove(firstElement);
        }
        else
        {
            EditorContent.Child = null; // release from Border before adding to Grid
        }

        var newGrid = SplitPaneNode.BuildGrid(e.Vertical, firstElement, (UIElement)newEditor);
        var splitNode = new SplitPaneNode
        {
            Vertical = e.Vertical, First = sourcePane, Second = newLeaf, Container = newGrid
        };

        if (parentSplit == null)
        {
            _globalRoot = splitNode;
            EditorContent.Child = newGrid;
        }
        else
        {
            if (parentSplit.Vertical) Grid.SetColumn(newGrid, gridPos);
            else                      Grid.SetRow(newGrid, gridPos);
            parentSplit.Container.Children.Add(newGrid);

            if (parentSplit.First == sourcePane) parentSplit.First = splitNode;
            else parentSplit.Second = splitNode;
        }

        // Ensure the new file has a tab entry
        if (newFilePath != null)
            EnsureFileTab(newFilePath);

        _focusedEditor = newEditor;
        UpdateSelectedTabForEditor(newEditor);
        newEditor.Focus();
    }

    private void CloseSplitPane(VimEditorControl editor, bool force)
    {
        if (_globalRoot == null) return;
        var editorPane = FindEditorPane(_globalRoot, editor);
        if (editorPane == null) return;
        var parentSplit = FindParentSplit(_globalRoot, editorPane);
        if (parentSplit == null) return;

        var sibling = parentSplit.First == editorPane ? parentSplit.Second : parentSplit.First;
        var siblingElement = PaneToElement(sibling);

        // Detach sibling from parentSplit.Container before reparenting
        parentSplit.Container.Children.Remove(siblingElement);

        var grandParent = FindParentSplit(_globalRoot, parentSplit);
        if (grandParent == null)
        {
            _globalRoot = sibling;
            EditorContent.Child = siblingElement;
        }
        else
        {
            grandParent.ReplaceChild(parentSplit.Container, siblingElement);
            if (grandParent.First == parentSplit) grandParent.First = sibling;
            else grandParent.Second = sibling;
        }

        var nextFocus = sibling.AllEditors().First();
        _focusedEditor = nextFocus;
        UpdateSelectedTabForEditor(nextFocus);
        nextFocus.Focus();
        // WPF Unloaded event fires automatically and disposes LSP resources
    }

    private void Editor_WindowCloseRequested(object? sender, WindowCloseRequestedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;

        if (!AllEditors().Skip(1).Any())
        {
            // Single pane: close the current file's tab
            var path = editor.Engine.CurrentBuffer.FilePath;
            var ft = FindFileTabByPath(path);
            if (ft != null)
                CloseFileTab(ft, e.Force);
            else
                Close();
            return;
        }
        CloseSplitPane(editor, e.Force);
    }

    private void Editor_WindowNavRequested(object? sender, WindowNavRequestedEventArgs e)
    {
        var editors = AllEditors().ToList();
        if (editors.Count <= 1) return;

        var focused = _focusedEditor;
        var idx = focused != null ? editors.IndexOf(focused) : 0;
        if (idx < 0) idx = 0;

        if (e.Dir == WindowNavDir.Next || e.Dir == WindowNavDir.Prev)
        {
            var next = e.Dir == WindowNavDir.Prev
                ? editors[(idx - 1 + editors.Count) % editors.Count]
                : editors[(idx + 1) % editors.Count];
            next.Focus();
            return;
        }

        // Coordinate-based spatial navigation
        var reference = (UIElement)EditorContent;
        var rects = editors
            .Select(ed => GetPaneRect(ed, reference))
            .ToList();

        var targetIdx = PaneNavigator.FindNext(rects[idx], rects, e.Dir);
        if (targetIdx.HasValue)
            editors[targetIdx.Value].Focus();
    }

    // Returns the bounding PaneRect of 'editor' expressed in 'reference' coordinates.
    private static PaneRect GetPaneRect(VimEditorControl editor, UIElement reference)
    {
        var origin = editor.TranslatePoint(new Point(0, 0), reference);
        return new PaneRect(
            origin.X,
            origin.Y,
            origin.X + editor.ActualWidth,
            origin.Y + editor.ActualHeight);
    }

    private void Editor_GotKeyboardFocus(object? sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        _focusedEditor = editor;
        // Sync the selected tab to reflect the focused pane's current file
        UpdateSelectedTabForEditor(editor);
    }

    private void Editor_NextTabRequested(object? sender, EventArgs e)
    {
        if (_fileTabs.Count == 0) return;
        var selectedItem = TabCtrl.SelectedItem as TabItem;
        var current = _fileTabs.FindIndex(t => t.Item == selectedItem);
        var next = (current + 1) % _fileTabs.Count;
        SelectFileTab(_fileTabs[next]);
    }

    private void Editor_PrevTabRequested(object? sender, EventArgs e)
    {
        if (_fileTabs.Count == 0) return;
        var selectedItem = TabCtrl.SelectedItem as TabItem;
        var current = _fileTabs.FindIndex(t => t.Item == selectedItem);
        var prev = (current - 1 + _fileTabs.Count) % _fileTabs.Count;
        SelectFileTab(_fileTabs[prev]);
    }

    private void Editor_CloseTabRequested(object? sender, CloseTabRequestedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        var path = editor.Engine.CurrentBuffer.FilePath;
        var ft = FindFileTabByPath(path);
        if (ft != null)
            CloseFileTab(ft, e.Force);
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
        OpenOrFocusFile(dlg.FileName);
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

    private void OutlineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarVisible && _activeSidebarPanel == SidebarPanel.Outline)
        {
            HideSidebar();
            return;
        }
        ShowSidebar(SidebarPanel.Outline);
        // Request document symbols from the focused editor
        CurrentEditor?.RequestOutlineAsync();
    }

    private void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is not OutlineItem item) return;
        // Deselect so the user can click again to jump
        OutlineList.SelectedItem = null;
        var editor = CurrentEditor;
        if (editor == null) return;
        editor.JumpToLine(item.Line, item.Col);
        editor.Focus();
    }

    /// <summary>Populate the Outline panel from a DocumentSymbolsResult event.</summary>
    private void Editor_DocumentSymbolsResult(object? sender, DocumentSymbolsResultEventArgs e)
    {
        // Always show the outline panel (e.g. triggered via :Outline command)
        if (!_sidebarVisible || _activeSidebarPanel != SidebarPanel.Outline)
            ShowSidebar(SidebarPanel.Outline);
        PopulateOutlineList(e.Items);
    }

    private void PopulateOutlineList(IReadOnlyList<DocumentSymbolItem> items)
    {
        OutlineList.Items.Clear();
        foreach (var item in items)
        {
            var indent = new string(' ', item.Depth * 2);
            OutlineList.Items.Add(new OutlineItem
            {
                Name      = item.Name,
                Indent    = indent,
                KindIcon  = SymbolKindIcon(item.Kind),
                KindColor = SymbolKindColor(item.Kind),
                Line      = item.Line,
                Col       = item.Col,
            });
        }
    }

    private static string SymbolKindIcon(SymbolKind kind) => kind switch
    {
        SymbolKind.Class       => "\uE8B1", // Contact / class-like
        SymbolKind.Interface   => "\uE8EC",
        SymbolKind.Struct      => "\uE8B1",
        SymbolKind.Enum        => "\uE8B1",
        SymbolKind.Method      => "\uE8A3",
        SymbolKind.Constructor => "\uE8A3",
        SymbolKind.Function    => "\uE8A3",
        SymbolKind.Field       => "\uE8D4",
        SymbolKind.Property    => "\uE8D4",
        SymbolKind.Variable    => "\uE8D4",
        SymbolKind.Constant    => "\uE8D4",
        SymbolKind.EnumMember  => "\uE8D4",
        SymbolKind.Module      => "\uED41",
        SymbolKind.Namespace   => "\uED41",
        SymbolKind.Package     => "\uED41",
        SymbolKind.File        => "\uE7C3",
        _                      => "\uE8A5",
    };

    private static string SymbolKindColor(SymbolKind kind) => kind switch
    {
        SymbolKind.Class       => "#8BE9FD",
        SymbolKind.Interface   => "#50FA7B",
        SymbolKind.Struct      => "#8BE9FD",
        SymbolKind.Enum        => "#FFB86C",
        SymbolKind.Method      => "#BD93F9",
        SymbolKind.Constructor => "#BD93F9",
        SymbolKind.Function    => "#BD93F9",
        SymbolKind.Field       => "#F1FA8C",
        SymbolKind.Property    => "#F1FA8C",
        SymbolKind.Variable    => "#FF79C6",
        SymbolKind.Constant    => "#FFB86C",
        SymbolKind.EnumMember  => "#FFB86C",
        SymbolKind.Module      => "#E6C07B",
        SymbolKind.Namespace   => "#E6C07B",
        _                      => "#AAAAAA",
    };

    private void TabPosition_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not RadioButton rb || rb.Tag is not string placementStr) return;
        ApplyTabPlacement(placementStr);
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
        DockPanel.SetDock(TabCtrl, dock);
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
        var editor = _focusedEditor;
        if (editor == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "All Files|*.*", Title = "Save File As" };
        if (dlg.ShowDialog() != true) return;
        editor.Engine.CurrentBuffer.Save(dlg.FileName);
        var ft = EnsureFileTab(dlg.FileName);
        ft.UpdateHeader(isModified: false);
        _suppressTabSelectionChanged = true;
        TabCtrl.SelectedItem = ft.Item;
        _suppressTabSelectionChanged = false;
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
        foreach (var ed in AllEditors())
            ed.SetTheme(_currentTheme);

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
        if (_suppressTabSelectionChanged) return;
        var selectedItem = TabCtrl.SelectedItem as TabItem;
        var fileTab = _fileTabs.Find(t => t.Item == selectedItem);
        if (fileTab == null) return;
        if (fileTab.FilePath != null && File.Exists(fileTab.FilePath))
            _focusedEditor?.LoadFile(fileTab.FilePath);
        _focusedEditor?.Focus();
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

    // ─────────── Search popup ────────────────────────────────

    private void OpenSearch()
    {
        if (!_searchTabsInitialized)
        {
            InitSearchModeTabs();
            _searchTabsInitialized = true;
        }
        _searchMode = SearchMode.All;
        UpdateSearchModeUI();
        SearchBox.Text = "";
        SearchResultList.ItemsSource = null;
        SearchOverlay.Visibility = Visibility.Visible;
        _searchActive = true;
        SearchBox.Focus();
        RunSearch("");
    }

    private void InitSearchModeTabs()
    {
        SearchModeTabs.Children.Clear();
        for (var i = 0; i < SearchModeOrder.Length; i++)
        {
            var (label, _) = SearchModeDisplays[i];
            var idx = i;

            var text = new TextBlock
            {
                Text       = label,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
            };

            var tab = new Border
            {
                Child           = text,
                CornerRadius    = new CornerRadius(3, 3, 0, 0),
                Padding         = new Thickness(10, 4, 10, 4),
                Margin          = new Thickness(0, 0, 2, 0),
                Cursor          = Cursors.Hand,
                Tag             = idx,
            };

            tab.MouseDown += (_, e) =>
            {
                _searchMode = SearchModeOrder[idx];
                UpdateSearchModeUI();
                RunSearch(SearchBox.Text);
                SearchBox.Focus();
                e.Handled = true;
            };

            SearchModeTabs.Children.Add(tab);
        }
    }

    private void CloseSearch()
    {
        SearchOverlay.Visibility = Visibility.Collapsed;
        _searchActive = false;
        CurrentEditor?.Focus();
    }

    private void CycleSearchMode()
    {
        var idx = Array.IndexOf(SearchModeOrder, _searchMode);
        _searchMode = SearchModeOrder[(idx + 1) % SearchModeOrder.Length];
        UpdateSearchModeUI();
        RunSearch(SearchBox.Text);
    }

    private void UpdateSearchModeUI()
    {
        var activeIdx = (int)_searchMode;
        for (var i = 0; i < SearchModeTabs.Children.Count; i++)
        {
            var tab  = (Border)SearchModeTabs.Children[i];
            var text = (TextBlock)tab.Child;
            var (_, colorHex) = SearchModeDisplays[i];
            var accent = (Color)ColorConverter.ConvertFromString(colorHex);

            if (i == activeIdx)
            {
                tab.Background    = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
                tab.BorderBrush   = new SolidColorBrush(accent);
                tab.BorderThickness = new Thickness(0, 0, 0, 2);
                text.Foreground   = new SolidColorBrush(accent);
            }
            else
            {
                tab.Background    = Brushes.Transparent;
                tab.BorderBrush   = Brushes.Transparent;
                tab.BorderThickness = new Thickness(0);
                text.Foreground   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            }
        }
    }

    private void RunSearch(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        if (_searchMode is SearchMode.Class or SearchMode.Symbol)
        {
            RunSearchLspAsync(query, _searchMode == SearchMode.Class, _searchCts.Token);
            return;
        }

        var results = _searchMode switch
        {
            SearchMode.File   => SearchFiles(query),
            SearchMode.Text   => SearchText(query),
            SearchMode.Action => SearchActions(query),
            _                 => SearchAll(query),
        };
        foreach (var r in results) r.SearchQuery = query;
        SearchResultList.ItemsSource = results;
        if (results.Count > 0)
            SearchResultList.SelectedIndex = 0;
    }

    private List<SearchResultItem> SearchAll(string query)
    {
        var results = new List<SearchResultItem>();
        results.AddRange(SearchFiles(query).Take(8));
        results.AddRange(SearchActions(query));
        return results;
    }

    private static readonly HashSet<string> _excludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".git", ".vs", ".idea", ".vscode",
        "node_modules", "__pycache__", "dist", "build", "out",
        "target", "packages", ".nuget", ".gradle", "vendor",
    };

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }
            foreach (var f in files)
                yield return f;

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }
            foreach (var sub in subDirs)
            {
                if (!_excludedDirNames.Contains(Path.GetFileName(sub)))
                    dirs.Push(sub);
            }
        }
    }

    private List<SearchResultItem> SearchFiles(string query)
    {
        if (_currentFolderPath == null) return [];
        try
        {
            var q = query.Trim();
            var ranked = new List<(string FilePath, string FileName, string RelativePath, int Score)>();
            foreach (var f in EnumerateSourceFiles(_currentFolderPath))
            {
                var fileName = Path.GetFileName(f);
                var relativePath = Path.GetRelativePath(_currentFolderPath, f);

                if (string.IsNullOrEmpty(q))
                {
                    ranked.Add((f, fileName, relativePath, 0));
                    continue;
                }

                var nameMatched = TryFuzzyMatch(fileName, q, out var nameScore);
                var pathMatched = TryFuzzyMatch(relativePath, q, out var pathScore);
                if (!nameMatched && !pathMatched)
                    continue;

                // Prefer direct file-name hits over path-only hits.
                var score = nameMatched ? nameScore + 500 : pathScore;
                ranked.Add((f, fileName, relativePath, score));
            }

            return ranked
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .Select(r => new SearchResultItem
                {
                    DisplayName = r.FileName,
                    Detail      = r.RelativePath,
                    Icon        = "\uE7C3",
                    IconColor   = "#99BBDD",
                    FilePath    = r.FilePath
                })
                .ToList();
        }
        catch { return []; }
    }

    private static bool TryFuzzyMatch(string candidate, string query, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(query))
            return true;

        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            var idx = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            score = 4000 - (idx * 40) - (candidate.Length - query.Length);
            return true;
        }

        var qi = 0;
        var firstMatch = -1;
        var prevMatch = -1;
        var consecutiveRun = 0;

        for (var ci = 0; ci < candidate.Length && qi < query.Length; ci++)
        {
            if (char.ToUpperInvariant(candidate[ci]) != char.ToUpperInvariant(query[qi]))
                continue;

            if (firstMatch < 0) firstMatch = ci;
            score += 90;

            if (ci == 0 || IsBoundaryMatch(candidate, ci))
                score += 70;

            if (prevMatch >= 0)
            {
                if (ci == prevMatch + 1)
                {
                    consecutiveRun++;
                    score += 55 + Math.Min(consecutiveRun, 4) * 10;
                }
                else
                {
                    score -= Math.Min((ci - prevMatch - 1) * 7, 70);
                    consecutiveRun = 0;
                }
            }

            prevMatch = ci;
            qi++;
        }

        if (qi != query.Length)
        {
            score = 0;
            return false;
        }

        if (firstMatch > 0)
            score -= Math.Min(firstMatch * 8, 120);
        score -= Math.Min((candidate.Length - query.Length) * 3, 120);
        return true;
    }

    private static bool IsBoundaryMatch(string text, int index)
    {
        if (index <= 0 || index >= text.Length)
            return true;

        var prev = text[index - 1];
        var cur = text[index];
        if (!char.IsLetterOrDigit(prev))
            return true;
        return char.IsLower(prev) && char.IsUpper(cur);
    }

    private List<SearchResultItem> SearchText(string query)
    {
        if (_currentFolderPath == null || string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<SearchResultItem>();
        try
        {
            foreach (var f in EnumerateSourceFiles(_currentFolderPath))
            {
                if (results.Count >= 50) break;
                var info = new FileInfo(f);
                if (info.Length > 1_000_000) continue;
                try
                {
                    var lineNum = 0;
                    foreach (var line in File.ReadLines(f))
                    {
                        lineNum++;
                        if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new SearchResultItem
                            {
                                DisplayName             = $"{Path.GetFileName(f)}:{lineNum}",
                                Detail                  = line.Trim(),
                                Icon                    = "\uE721",
                                IconColor               = "#F1FA8C",
                                FilePath                = f,
                                Line                    = lineNum - 1,   // convert to 0-indexed
                                HighlightQueryInPreview = true,
                            });
                            if (results.Count >= 50) break;
                        }
                    }
                }
                catch { /* skip unreadable */ }
            }
        }
        catch { }
        return results;
    }

    private List<SearchResultItem> SearchActions(string query)
    {
        var all = new (string Name, string Icon, string Color, Action Act)[]
        {
            ("新しいタブを開く",        "\uE710", "#50FA7B", () => { CloseSearch(); AddTab(null); }),
            ("ファイルを開く...",        "\uED25", "#8BE9FD", () => { CloseSearch(); OpenFile_Click(this, new RoutedEventArgs()); }),
            ("フォルダーを開く...",      "\uED41", "#E6C07B", () => { CloseSearch(); OpenFolder_Click(this, new RoutedEventArgs()); }),
            ("ファイルを保存",           "\uE74E", "#BD93F9", () => { CloseSearch(); Save_Click(this, new RoutedEventArgs()); }),
            ("エクスプローラーを切替",   "\uE8B7", "#AAAAAA", () => { CloseSearch(); ToggleSidebar(); }),
            ("設定を開く",               "\uE713", "#AAAAAA", () => { CloseSearch(); ShowSidebar(SidebarPanel.Settings); }),
            ("ウィンドウを閉じる",       "\uE8BB", "#FF79C6", () => { CloseSearch(); Close(); }),
        };
        return all
            .Where(a => string.IsNullOrEmpty(query) ||
                        a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(a => new SearchResultItem
            {
                DisplayName   = a.Name,
                Icon          = a.Icon,
                IconColor     = a.Color,
                ActionCallback = a.Act
            })
            .ToList();
    }

    private async void RunSearchLspAsync(string query, bool isClass, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResultList.ItemsSource = new[]
            {
                new SearchResultItem
                {
                    DisplayName = isClass
                        ? "クラス名検索: キーワードを入力してください"
                        : "シンボル検索: キーワードを入力してください",
                    Icon      = "\uE721",
                    IconColor = "#888888"
                }
            };
            return;
        }

        var q = query.Trim();
        SearchResultList.ItemsSource = new[]
        {
            new SearchResultItem { DisplayName = "検索中...", Icon = "\uE721", IconColor = "#888888" }
        };

        IReadOnlyList<LspSymbolInformation> symbols;
        try
        {
            symbols = await (CurrentEditor?.SearchWorkspaceSymbolsAsync(q, isClass, ct)
                             ?? Task.FromResult<IReadOnlyList<LspSymbolInformation>>([]));
        }
        catch (OperationCanceledException) { return; }
        catch { symbols = []; }

        if (ct.IsCancellationRequested) return;

        var rankedSymbols = RankSymbolsForQuery(symbols, q);
        if (rankedSymbols.Count == 0)
        {
            var fallbackQuery = BuildSymbolFallbackQuery(q);
            if (!string.Equals(fallbackQuery, q, StringComparison.Ordinal))
            {
                try
                {
                    symbols = await (CurrentEditor?.SearchWorkspaceSymbolsAsync(fallbackQuery, isClass, ct)
                                     ?? Task.FromResult<IReadOnlyList<LspSymbolInformation>>([]));
                }
                catch (OperationCanceledException) { return; }
                catch { symbols = []; }

                if (ct.IsCancellationRequested) return;
                rankedSymbols = RankSymbolsForQuery(symbols, q);
            }
        }

        if (rankedSymbols.Count == 0)
        {
            SearchResultList.ItemsSource = new[]
            {
                new SearchResultItem
                {
                    DisplayName = "結果が見つかりませんでした",
                    Icon      = "\uE721",
                    IconColor = "#888888"
                }
            };
            return;
        }

        var results = rankedSymbols
            .Select(s => new SearchResultItem
            {
                DisplayName = s.Name,
                Detail      = s.ContainerName != null
                    ? $"{s.ContainerName} • {SymbolUriToPath(s.Location.Uri)}"
                    : SymbolUriToPath(s.Location.Uri),
                Icon        = GetSymbolIcon(s.Kind),
                IconColor   = GetSymbolColor(s.Kind),
                FilePath    = TryUriToLocalPath(s.Location.Uri),
                Line        = s.Location.Range.Start.Line,
                Col         = s.Location.Range.Start.Character,
                SearchQuery = query,
            })
            .ToList();

        SearchResultList.ItemsSource = results;
        if (results.Count > 0)
            SearchResultList.SelectedIndex = 0;
    }

    private static string BuildSymbolFallbackQuery(string query)
    {
        foreach (var c in query)
        {
            if (char.IsLetterOrDigit(c))
                return c.ToString();
        }
        return "";
    }

    private static List<LspSymbolInformation> RankSymbolsForQuery(
        IReadOnlyList<LspSymbolInformation> symbols, string query)
    {
        var ranked = new List<(LspSymbolInformation Symbol, int Score, string FileName)>();
        foreach (var symbol in symbols)
        {
            if (!TryScoreSymbol(symbol, query, out var score))
                continue;

            ranked.Add((symbol, score, SymbolUriToPath(symbol.Location.Uri)));
        }

        return ranked
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Symbol)
            .ToList();
    }

    private static bool TryScoreSymbol(LspSymbolInformation symbol, string query, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var bestScore = int.MinValue;

        if (TryFuzzyMatch(symbol.Name, query, out var nameScore))
            bestScore = Math.Max(bestScore, nameScore + 1200);

        if (!string.IsNullOrEmpty(symbol.ContainerName) &&
            TryFuzzyMatch(symbol.ContainerName, query, out var containerScore))
            bestScore = Math.Max(bestScore, containerScore + 300);

        var fileName = SymbolUriToPath(symbol.Location.Uri);
        if (TryFuzzyMatch(fileName, query, out var fileScore))
            bestScore = Math.Max(bestScore, fileScore + 150);

        if (bestScore == int.MinValue)
            return false;

        score = bestScore;
        return true;
    }

    private static string SymbolUriToPath(string uri)
    {
        try { return Path.GetFileName(new Uri(uri).LocalPath); }
        catch { return uri; }
    }

    private static string? TryUriToLocalPath(string uri)
    {
        try { return new Uri(uri).LocalPath; }
        catch { return null; }
    }

    private static string GetSymbolIcon(SymbolKind kind) => kind switch
    {
        SymbolKind.Class         => "\uE8F4",
        SymbolKind.Interface     => "\uE8EC",
        SymbolKind.Enum          => "\uE8D2",
        SymbolKind.Struct        => "\uE8F5",
        SymbolKind.Method        => "\uE8F2",
        SymbolKind.Function      => "\uE8F2",
        SymbolKind.Constructor   => "\uE8F2",
        SymbolKind.Field         => "\uE8F1",
        SymbolKind.Property      => "\uE8F1",
        SymbolKind.Variable      => "\uE8EF",
        SymbolKind.Constant      => "\uE8EF",
        SymbolKind.Namespace     => "\uE943",
        SymbolKind.Module        => "\uE943",
        _                        => "\uE8CB",
    };

    private static string GetSymbolColor(SymbolKind kind) => kind switch
    {
        SymbolKind.Class         => "#50FA7B",
        SymbolKind.Interface     => "#8BE9FD",
        SymbolKind.Enum          => "#FFB86C",
        SymbolKind.Struct        => "#50FA7B",
        SymbolKind.Method        => "#BD93F9",
        SymbolKind.Function      => "#BD93F9",
        SymbolKind.Constructor   => "#BD93F9",
        SymbolKind.Field         => "#F8F8F2",
        SymbolKind.Property      => "#F8F8F2",
        SymbolKind.Variable      => "#F1FA8C",
        SymbolKind.Constant      => "#FF79C6",
        SymbolKind.Namespace     => "#AAAAAA",
        SymbolKind.Module        => "#AAAAAA",
        _                        => "#CCCCCC",
    };

    private void ExecuteSelectedResult()
    {
        if (SearchResultList.SelectedItem is not SearchResultItem item) return;
        if (item.ActionCallback != null)
        {
            item.ActionCallback();
        }
        else if (item.FilePath != null)
        {
            CloseSearch();
            OpenOrFocusFile(item.FilePath);
            if (item.Line >= 0)
                CurrentEditor?.NavigateTo(item.Line, item.Col);
        }
    }

    // Search event handlers

    private void SearchOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        CloseSearch();
        e.Handled = true;
    }

    private void SearchPopupBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // prevent overlay from closing
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchActive)
            RunSearch(SearchBox.Text);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Tab / Shift+Tab: handled here because Tab focus-traversal runs
        // after PreviewKeyDown bubbles, so we intercept it at the TextBox level.
        if (e.Key == Key.Tab)
        {
            MoveSelection(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : +1);
            e.Handled = true;
        }
    }

    private void SearchResultList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultList.SelectedItem != null)
            ExecuteSelectedResult();
    }

    private void MoveSelection(int delta)
    {
        var count = SearchResultList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(SearchResultList.SelectedIndex + delta, 0, count - 1);
        SearchResultList.SelectedIndex = next;
        SearchResultList.ScrollIntoView(SearchResultList.SelectedItem);
    }

    private void SearchResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSearchPreview(SearchResultList.SelectedItem as SearchResultItem);
    }

    private void UpdateSearchPreview(SearchResultItem? item)
    {
        PreviewPanel.Children.Clear();
        if (item == null || item.ActionCallback != null || item.FilePath == null)
            return;
        try { RenderFilePreview(item.FilePath, item.Line, item.SearchQuery, item.HighlightQueryInPreview); }
        catch { /* silently ignore unreadable files */ }
    }

    private void RenderFilePreview(string filePath, int targetLine, string query, bool highlightQuery)
    {
        string[] allLines;
        try { allLines = File.ReadAllLines(filePath); }
        catch { return; }
        if (allLines.Length == 0) return;

        const int context = 29;
        int center    = Math.Max(0, targetLine < 0 ? 0 : targetLine);
        int startLine = Math.Max(0, center - context);
        int endLine   = Math.Min(allLines.Length - 1, center + context);

        _previewSyntaxEngine.DetectLanguage(filePath);
        var allTokens = _previewSyntaxEngine.Tokenize(allLines);

        var theme        = _currentTheme;
        var lineNumBrush = theme.LineNumberFg;
        // ヒット行の背景: SearchHighlightBg を薄くした色
        var hitLineBg = theme.SearchHighlightBg is SolidColorBrush scb
            ? new SolidColorBrush(Color.FromArgb(0x35, scb.Color.R, scb.Color.G, scb.Color.B))
            : new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xB8, 0x6C));

        int digits        = (endLine + 1).ToString().Length;
        double lineNumW   = Math.Max(30, digits * 8.5 + 12);

        int hitRowIdx = -1;
        for (int li = startLine; li <= endLine; li++)
        {
            bool isHit  = li == targetLine;
            if (isHit) hitRowIdx = li - startLine;

            var tokens  = li < allTokens.Length ? allTokens[li].Tokens : [];

            var row = new DockPanel { LastChildFill = true };
            if (isHit) row.Background = hitLineBg;

            var lineNumTB = new TextBlock
            {
                Text              = (li + 1).ToString(),
                Foreground        = lineNumBrush,
                Width             = lineNumW,
                TextAlignment     = TextAlignment.Right,
                Padding           = new Thickness(4, 1, 8, 1),
                FontFamily        = new FontFamily("Cascadia Code, Consolas"),
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(lineNumTB, Dock.Left);
            row.Children.Add(lineNumTB);

            var contentTB = new TextBlock
            {
                FontFamily   = new FontFamily("Cascadia Code, Consolas"),
                FontSize     = 12,
                Foreground   = theme.Foreground,
                Padding      = new Thickness(0, 1, 4, 1),
                TextWrapping = TextWrapping.NoWrap,
            };
            BuildLineInlines(contentTB, allLines[li], tokens, highlightQuery ? query : "", theme);
            row.Children.Add(contentTB);

            PreviewPanel.Children.Add(row);
        }

        if (hitRowIdx >= 0)
        {
            var idx = hitRowIdx;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                CenterPreviewRowInViewport(idx);
            });
        }
    }

    private void CenterPreviewRowInViewport(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= PreviewPanel.Children.Count) return;
        if (PreviewPanel.Children[rowIndex] is not FrameworkElement row) return;

        // Ensure actual sizes/offsets are measured before computing target scroll.
        PreviewScrollViewer.UpdateLayout();
        row.UpdateLayout();

        double viewportHeight = PreviewScrollViewer.ViewportHeight;
        if (viewportHeight <= 0 || double.IsNaN(viewportHeight))
        {
            row.BringIntoView();
            return;
        }

        double rowTop      = row.TransformToAncestor(PreviewPanel).Transform(new Point(0, 0)).Y;
        double rowCenter   = rowTop + row.ActualHeight / 2.0;
        double target      = rowCenter - viewportHeight / 2.0;
        double targetClamp = Math.Clamp(target, 0.0, PreviewScrollViewer.ScrollableHeight);
        PreviewScrollViewer.ScrollToVerticalOffset(targetClamp);
    }

    private static void BuildLineInlines(TextBlock tb, string text,
        SyntaxToken[] tokens, string query, EditorTheme theme)
    {
        if (text.Length == 0) return;

        // Build per-character foreground color array from tokens
        var colors = new Brush[text.Length];
        Array.Fill(colors, theme.Foreground);
        foreach (var tok in tokens)
        {
            var brush = theme.GetTokenBrush(tok.Kind);
            int end   = Math.Min(tok.StartColumn + tok.Length, text.Length);
            for (int i = Math.Max(0, tok.StartColumn); i < end; i++)
                colors[i] = brush;
        }

        // Build per-character search-hit mask
        var hit = new bool[text.Length];
        if (!string.IsNullOrEmpty(query))
        {
            int idx = 0;
            while (true)
            {
                int pos = text.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) break;
                for (int i = pos; i < pos + query.Length; i++) hit[i] = true;
                idx = pos + query.Length;
            }
        }

        // Merge consecutive chars with same (color, isHit) into Runs
        var hitBg = theme.SearchHighlightBg;
        int cur   = 0;
        while (cur < text.Length)
        {
            var fg     = colors[cur];
            bool isHit = hit[cur];
            int  end   = cur + 1;
            while (end < text.Length && colors[end] == fg && hit[end] == isHit)
                end++;
            var run = new Run(text[cur..end]) { Foreground = fg };
            if (isHit) run.Background = hitBg;
            tb.Inlines.Add(run);
            cur = end;
        }
    }

    private void SendSearchResultsToPanel()
    {
        var query = SearchBox.Text;
        if (SearchResultList.ItemsSource is not IEnumerable<SearchResultItem> sourceItems) return;

        var fileItems = sourceItems
            .Where(i => i.FilePath != null && i.ActionCallback == null)
            .ToList();
        if (fileItems.Count == 0) return;

        CloseSearch();

        var panelItems = fileItems.Select(i => new ReferenceListItem
        {
            FilePath = i.FilePath!,
            FileName = Path.GetFileName(i.FilePath!),
            LineCol  = i.Line >= 0 ? $":{i.Line + 1}:{i.Col + 1}" : "",
            Preview  = i.Detail,
            Line     = Math.Max(0, i.Line),
            Col      = i.Col
        }).ToList();

        RefList.SelectionChanged -= RefList_SelectionChanged;
        RefList.ItemsSource = panelItems;
        RefList.SelectedIndex = -1;
        _quickfixCurrentIndex = -1;
        RefList.SelectionChanged += RefList_SelectionChanged;

        int fileCount = panelItems.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        RefPanelTitle.Text = string.IsNullOrWhiteSpace(query)
            ? $"SEARCH ({panelItems.Count} results) [{fileCount} file(s)]"
            : $"SEARCH \"{query}\" ({panelItems.Count} results) [{fileCount} file(s)]";

        ShowReferencesPanel();
    }

    // ─────────────────────────────────────────────────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var hasUnsaved = AllEditors().Any(ed => ed.Engine.CurrentBuffer.Text.IsModified);
        if (hasUnsaved)
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

        var tabFiles = _fileTabs
            .Where(t => t.FilePath != null)
            .Select(t => t.FilePath!);
        var activeFile = _focusedEditor?.Engine.CurrentBuffer.FilePath;
        _recentItems.SaveSession(_currentFolderPath, tabFiles, activeFile);

        if (_windowSource != null)
        {
            _windowSource.RemoveHook(WindowMessageProc);
            _windowSource = null;
        }

        base.OnClosing(e);
    }
}

// ─────────── File tree model ─────────────────────────────────

/// <summary>Document symbol entry shown in the Outline sidebar panel.</summary>
public sealed class OutlineItem
{
    public required string Name     { get; init; }
    public required string Indent   { get; init; }
    public required string KindIcon { get; init; }
    public required string KindColor{ get; init; }
    public required int    Line     { get; init; }
    public required int    Col      { get; init; }
}

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
