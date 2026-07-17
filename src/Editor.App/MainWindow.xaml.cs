using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.CompilerServices;
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
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Editor.Core.Config;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Panes;
using Editor.Core.Search;
using Editor.Core.Syntax;
using Microsoft.Web.WebView2.Core;

namespace Editor.App;

public partial class MainWindow : Window
{
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WheelDelta = 120;
    private HwndSource? _windowSource;

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

    private readonly TabManagerController _tabs;
    private PaneNode? _globalRoot;
    private VimEditorControl? _focusedEditor;
    private readonly RecentItemsManager _recentItems = new();
    private EditorTheme _currentTheme = EditorTheme.Dracula;
    private string _baseThemeName = "Dracula";
    private string? _customBackground;
    private string? _customAccent;
    private string _markdownPreviewStyle = "Dracula";
    private bool _vimEnabled = true;
    private readonly SidebarController _sidebar;
    private readonly ReferencesPanelController _refs;
    private bool _focusOutlineAfterPopulate;
    private readonly MarkdownPreviewController _markdownPreview;
    private readonly List<TerminalSession> _terminalSessions = [];
    private int _currentTerminalIndex = -1;
    private int _nextTerminalId = 1;
    private bool _suppressTerminalSelectionChanged;

    private sealed class TerminalSession
    {
        public required int Id { get; init; }
        public required string Title { get; init; }
        public required TerminalPane Pane { get; init; }

        public string DisplayName => $"{Id}: {Title}";
    }

    private VimEditorControl? CurrentEditor => _focusedEditor;

    private IEnumerable<VimEditorControl> AllEditors() =>
        _globalRoot?.AllEditors() ?? Enumerable.Empty<VimEditorControl>();

    public MainWindow()
    {
        InitializeComponent();
        _markdownPreview = new MarkdownPreviewController(
            getGlobalRoot: () => _globalRoot,
            setGlobalRoot: root => _globalRoot = root,
            currentEditor: () => CurrentEditor,
            focusedEditor: () => _focusedEditor,
            getMarkdownPreviewStyle: () => _markdownPreviewStyle,
            EditorContent, MarkdownPreviewPanel, PreviewBtn, PreviewSplitter,
            PreviewSplitterCol, PreviewCol, PreviewBrowser);
        _tabs = new TabManagerController(
            this, TabCtrl, SharedStatusBar,
            getCurrentTheme: () => _currentTheme,
            getVimEnabled: () => _vimEnabled,
            wireEditorEvents: WireEditorEvents,
            focusedEditor: () => _focusedEditor,
            allEditors: AllEditors,
            refreshOutlineForEditor: RefreshOutlineForEditor,
            onAfterTabSelected: _markdownPreview.NotifyEditorActivated);
        _sidebar = new SidebarController(
            this, FileTree, FolderNameLabel, SidebarCol, SplitterCol,
            ExplorerPanel, SettingsPanel, OutlinePanel,
            ExplorerBtn, SettingsBtn, OutlineBtn,
            focusedEditor: () => _focusedEditor,
            allEditors: AllEditors,
            openOrFocusFile: OpenOrFocusFile,
            addTab: path => _tabs.AddTab(path),
            showInputDialog: ShowInputDialog,
            onFolderLoaded: path =>
            {
                _recentItems.AddFolder(path);
                RefreshRecentMenus();
                RefreshJumpList();
            });
        _refs = new ReferencesPanelController(
            RefList, ReferencesPanel, RefSplitter, RefPanelRow, RefSplitterRow,
            RefPanelTitle, ReplaceRefResultsBtn,
            currentEditor: () => CurrentEditor,
            focusedEditor: () => _focusedEditor,
            allEditors: AllEditors,
            getCurrentFolderPath: () => _sidebar.CurrentFolderPath,
            showInputDialog: ShowInputDialog,
            openFile: OpenFile);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageProc);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Restore the Vim on/off setting before any editor is created so the
        // initial editor picks it up via CreateEditor.
        _vimEnabled = _recentItems.VimEnabled;

        // Create the initial global editor and set up the pane tree
        var initialEditor = _tabs.CreateEditor(null);
        _globalRoot = new EditorPaneNode { Editor = initialEditor };
        _focusedEditor = initialEditor;
        EditorContent.Child = initialEditor;

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            if (Directory.Exists(args[1]))
            {
                _tabs.AddTab(null);
                LoadFolder(args[1]);
            }
            else if (File.Exists(args[1]))
                _tabs.AddTab(args[1]);
        }
        else
        {
            RestoreSession();
        }

        RefreshRecentMenus();
        RefreshJumpList();
        ApplyTabPlacement(_recentItems.TabPlacement);
        ApplyColorTheme(_recentItems.ThemeName, _recentItems.CustomBackground, _recentItems.CustomAccent);
        ApplyMarkdownPreviewStyle(_recentItems.MarkdownPreviewStyle);
        VimEnabledCheck.IsChecked = _vimEnabled;
        InitColorPalettes();

        // Restore command/search history from viminfo
        var (cmdHist, searchHist) = ViminfoManager.Load();
        foreach (var ed in AllEditors())
            ed.Engine.ExProcessor.LoadHistory(cmdHist, searchHist);
    }

    private void RestoreSession()
    {
        var session = _recentItems.LastSession;
        if (session == null || (session.TabFiles.Count == 0 && session.FolderPath == null))
        {
            _tabs.AddTab(null);
            return;
        }

        // Restore file tabs (add entries without loading yet)
        foreach (var file in session.TabFiles)
        {
            if (File.Exists(file))
                _tabs.AddFileTabEntry(file);
        }
        if (_tabs.FileTabs.Count == 0)
        {
            _tabs.AddTab(null);
        }
        else
        {
            // Load the active file (or the first one)
            var activeTab = session.ActiveFile != null
                ? _tabs.FindFileTabByPath(session.ActiveFile)
                : null;
            _tabs.SelectFileTab(activeTab ?? _tabs.FileTabs[0]);
        }

        // Restore sidebar folder
        if (session.FolderPath != null && Directory.Exists(session.FolderPath))
            _sidebar.LoadFolder(session.FolderPath, recordRecent: false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // Intercept Ctrl+B here (before VimEditorControl sees it as page-up)
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleSidebar();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!_searchActive)
                OpenSearch();
            else
                CycleSearchMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            OpenCommandPalette();
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
    }

    // ─────────── Sidebar (delegates to SidebarController) ─────
    // See SidebarController.cs — owns panel visibility, file-tree navigation/keyboard
    // handling and the file-tree context menu. These wrappers exist only because
    // MainWindow.xaml wires event handlers to methods by name.

    private void ShowSidebar(SidebarPanel panel = SidebarPanel.Explorer) => _sidebar.Show(panel);

    private void HideSidebar() => _sidebar.Hide();

    private void ToggleSidebar() => _sidebar.Toggle();

    private void LoadFolder(string folderPath) => _sidebar.LoadFolder(folderPath);

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e) =>
        _sidebar.TreeViewItem_Expanded(sender, e);

    private void TreeViewItem_Collapsed(object sender, RoutedEventArgs e) =>
        _sidebar.TreeViewItem_Collapsed(sender, e);

    private void FileTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e) =>
        _sidebar.FileTree_SelectedItemChanged(sender, e);

    private void FileTree_KeyDown(object sender, KeyEventArgs e) =>
        _sidebar.FileTree_KeyDown(sender, e);

    private void FileTree_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _sidebar.FileTree_LostKeyboardFocus(sender, e);

    private void FileTree_MouseRightButtonUp(object sender, MouseButtonEventArgs e) =>
        _sidebar.FileTree_MouseRightButtonUp(sender, e);

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

    internal static T? FindVisualAncestor<T>(DependencyObject? obj) where T : DependencyObject
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
        var existing = _tabs.FindFileTabByPath(path);
        if (existing != null)
        {
            _tabs.SelectFileTab(existing);
            RecordRecentFile(path);
            return;
        }

        // Otherwise add a new tab entry and load
        _tabs.AddTab(path);
        RecordRecentFile(path);
    }

    private void RecordRecentFile(string path)
    {
        if (!File.Exists(path)) return;
        _recentItems.AddFile(path);
        RefreshRecentMenus();
        RefreshJumpList();
    }

    // ─────────── Tab management (delegates to TabManagerController) ─
    // See TabManagerController.cs — owns the FileTab bar, editor-pane creation, and
    // tab selection/closing. WireEditorEvents stays here since most of the ~25 events
    // it wires are handled by MainWindow methods spanning unrelated concerns (git,
    // terminal, quickfix, session, markdown preview); TabManagerController reaches it
    // through the wireEditorEvents callback instead.

    private void WireEditorEvents(VimEditorControl editor)
    {
        editor.SaveRequested     += Editor_SaveRequested;
        editor.QuitRequested     += Editor_QuitRequested;
        editor.OpenFileRequested += Editor_OpenFileRequested;
        editor.NewTabRequested   += Editor_NewTabRequested;
        editor.SplitRequested    += Editor_SplitRequested;
        editor.NextTabRequested  += _tabs.Editor_NextTabRequested;
        editor.PrevTabRequested  += _tabs.Editor_PrevTabRequested;
        editor.CloseTabRequested    += _tabs.Editor_CloseTabRequested;
        editor.WindowNavRequested   += Editor_WindowNavRequested;
        editor.WindowCloseRequested += Editor_WindowCloseRequested;
        editor.BufferChanged        += Editor_BufferChanged;
        editor.FindReferencesResult += _refs.Editor_FindReferencesResult;
        editor.QuickfixOpenRequested  += (_, _) => _refs.ShowQuickfixPanel();
        editor.QuickfixCloseRequested += (_, _) => _refs.Close();
        editor.QuickfixNextRequested  += (_, count) => _refs.QuickfixNavigate(count);
        editor.QuickfixPrevRequested  += (_, count) => _refs.QuickfixNavigate(-count);
        editor.QuickfixGotoRequested  += (_, index) => _refs.QuickfixNavigateTo(index);
        editor.LocationListOpenRequested  += (_, _) => _refs.ShowLocationListPanel();
        editor.LocationListCloseRequested += (_, _) => _refs.Close();
        editor.LocationListNextRequested  += (_, count) => _refs.LocationListNavigate(count);
        editor.LocationListPrevRequested  += (_, count) => _refs.LocationListNavigate(-count);
        editor.LocationListGotoRequested  += (_, index) => _refs.LocationListNavigateTo(index);
        editor.GrepRequested          += _refs.Editor_GrepRequested;
        editor.ProjectReplaceRequested += _refs.Editor_ProjectReplaceRequested;
        editor.QuickfixReplaceRequested += _refs.Editor_QuickfixReplaceRequested;
        editor.GotKeyboardFocus       += Editor_GotKeyboardFocus;
        editor.MkSessionRequested     += Editor_MkSessionRequested;
        editor.SourceRequested        += Editor_SourceRequested;
        editor.TerminalRequested          += Editor_TerminalRequested;
        editor.TerminalCommandRequested   += Editor_TerminalCommandRequested;
        editor.MarkdownPreviewRequested   += (_, _) => _markdownPreview.Toggle();
        editor.GitOutputRequested         += Editor_GitOutputRequested;
        editor.GitCommitRequested     += Editor_GitCommitRequested;
        editor.BlameCommitClicked     += Editor_BlameCommitClicked;
        editor.ContextMenuBuilding    += Editor_ContextMenuBuilding;
        editor.DocumentSymbolsResult  += Editor_DocumentSymbolsResult;
    }

    private void Editor_BufferChanged(object? sender, EventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        var path = editor.Engine.CurrentBuffer.FilePath;
        var ft = _tabs.FindFileTabByPath(path);
        ft?.UpdateHeader(isModified: editor.Engine.CurrentBuffer.Text.IsModified);

        _markdownPreview.NotifyEditorActivated(editor);
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
        ShowTerminalPanel(shellCmd);
    }

    private void Editor_TerminalCommandRequested(object? sender, TerminalCommandRequestedEvent e)
    {
        switch (e.Kind)
        {
            case TerminalCommandKind.List:
                ShowTerminalList(sender as VimEditorControl ?? _focusedEditor);
                break;
            case TerminalCommandKind.Next:
                SelectRelativeTerminal(1);
                break;
            case TerminalCommandKind.Previous:
                SelectRelativeTerminal(-1);
                break;
            case TerminalCommandKind.Select:
                SelectTerminalById(e.TerminalNumber);
                break;
            case TerminalCommandKind.Close:
                CloseTerminalById(e.TerminalNumber);
                break;
        }
    }

    private void Editor_GitOutputRequested(object? sender, GitOutputRequestedEventArgs e)
    {
        // Open a new tab with in-memory git output content
        var ft = _tabs.AddFileTabEntry(null);
        _tabs.SelectFileTab(ft);
        ft.UpdateHeader(isModified: false, label: e.Title);
        _focusedEditor?.SetText(e.Content);
    }

    // Clicking a blame annotation opens the Git log pane and selects that commit's line.
    private async void Editor_BlameCommitClicked(object? sender, BlameCommitClickedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        await ShowCommitInGitLogAsync(editor, e.CommitHash);
    }

    // Blame gutter right-click menu: offer opening the commit history (git log) with the
    // blame-selected commit highlighted in the list.
    private void Editor_ContextMenuBuilding(object? sender, EditorContextMenuBuildingEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        if (e.BlameLine is not { } blame) return;

        var item = new MenuItem { Header = $"Show Commit {blame.CommitHash} in Git History" };
        item.Click += async (_, _) => await ShowCommitInGitLogAsync(editor, blame.CommitHash);
        e.Menu.Items.Add(item);
    }

    // Open the Git history (commit list) in the pane and visibly select the given commit's line.
    private async Task ShowCommitInGitLogAsync(VimEditorControl editor, string commitHash)
    {
        var log = await Task.Run(editor.GetGitLogOutput);

        // Keep the selected tab and its content in the pane that initiated the request, even if
        // focus moved to another split while git was running.
        var ft = _tabs.AddFileTabEntry(null);
        _tabs.SelectFileTab(ft, editor);
        ft.UpdateHeader(isModified: false, label: "[Git Log]");
        editor.SetText(log);

        var line = FindCommitLine(log, commitHash);
        if (line < 0) return;

        // Select the commit only after the history list has been laid out and rendered. Running the
        // selection inline (before the freshly-set content is measured) can't scroll it into view
        // yet, so it wouldn't visibly get selected. Defer to Background so the list is shown first.
        _ = editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => editor.SelectLine(line));
    }

    /// <summary>Find the <c>git log --oneline</c> line whose leading hash matches <paramref name="hash"/>
    /// (blame's short hash and log's abbreviation may differ in length, so match by common prefix).
    /// Returns the 0-based line index, or -1 if not found.</summary>
    private static int FindCommitLine(string log, string hash)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(log)) return -1;
        var lines = log.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var text = lines[i].TrimStart();
            int sp = text.IndexOf(' ');
            var token = sp < 0 ? text : text[..sp];
            if (token.Length == 0) continue;
            if (token.StartsWith(hash, StringComparison.OrdinalIgnoreCase) ||
                hash.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
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

    private void ShowTerminalPanel(string? shellCmd)
    {
        var workDir = _focusedEditor?.Engine.CurrentBuffer.FilePath is { } fp
            ? Path.GetDirectoryName(fp)
            : null;
        var terminal = new TerminalPane(shellCmd, workDir);
        terminal.EditorFocusRequested += Terminal_EditorFocusRequested;

        var session = new TerminalSession
        {
            Id = _nextTerminalId++,
            Title = string.IsNullOrWhiteSpace(shellCmd) ? "shell" : shellCmd,
            Pane = terminal
        };

        _terminalSessions.Add(session);
        TerminalPanel.Visibility = Visibility.Visible;
        TermSplitter.Visibility  = Visibility.Visible;
        TermSplitterRow.Height   = new GridLength(4);
        TermPanelRow.Height      = new GridLength(200);
        SelectTerminal(_terminalSessions.Count - 1);
    }

    private void Terminal_EditorFocusRequested(object? sender, EventArgs e)
    {
        _focusedEditor?.Focus();
    }

    private void CloseTermPanel_Click(object sender, RoutedEventArgs e)
    {
        CloseTerminalById(null);
    }

    private void TerminalSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTerminalSelectionChanged)
            return;

        if (TerminalSelector.SelectedIndex >= 0 && TerminalSelector.SelectedIndex < _terminalSessions.Count)
            SelectTerminal(TerminalSelector.SelectedIndex);
    }

    private void SelectRelativeTerminal(int offset)
    {
        if (_terminalSessions.Count == 0)
        {
            _focusedEditor?.ShowStatusMessage("No terminals");
            return;
        }

        var current = _currentTerminalIndex >= 0 ? _currentTerminalIndex : 0;
        var next = (current + offset + _terminalSessions.Count) % _terminalSessions.Count;
        SelectTerminal(next);
    }

    private void SelectTerminalById(int? terminalId)
    {
        if (terminalId is null)
        {
            _focusedEditor?.ShowStatusMessage("Invalid terminal number");
            return;
        }

        var index = _terminalSessions.FindIndex(t => t.Id == terminalId.Value);
        if (index < 0)
        {
            _focusedEditor?.ShowStatusMessage($"No terminal #{terminalId.Value}");
            return;
        }

        SelectTerminal(index);
    }

    private void SelectTerminal(int index)
    {
        if (index < 0 || index >= _terminalSessions.Count)
            return;

        _currentTerminalIndex = index;
        TerminalContent.Content = _terminalSessions[index].Pane;
        UpdateTerminalSelector();
        TerminalPanel.Visibility = Visibility.Visible;
        TermSplitter.Visibility  = Visibility.Visible;
        TermSplitterRow.Height   = new GridLength(4);
        TermPanelRow.Height      = new GridLength(200);
        _terminalSessions[index].Pane.FocusInput();
    }

    private void UpdateTerminalSelector()
    {
        _suppressTerminalSelectionChanged = true;
        TerminalSelector.Items.Clear();
        foreach (var session in _terminalSessions)
            TerminalSelector.Items.Add(session.DisplayName);
        TerminalSelector.SelectedIndex = _currentTerminalIndex;
        _suppressTerminalSelectionChanged = false;
    }

    private void ShowTerminalList(VimEditorControl? editor)
    {
        if (_terminalSessions.Count == 0)
        {
            editor?.ShowStatusMessage("No terminals");
            return;
        }

        var terminals = _terminalSessions
            .Select((terminal, index) => index == _currentTerminalIndex
                ? $"[{terminal.DisplayName}]"
                : terminal.DisplayName);
        editor?.ShowStatusMessage("Terminals: " + string.Join(" | ", terminals));
    }

    private async void CloseTerminalById(int? terminalId)
    {
        if (_terminalSessions.Count == 0)
        {
            _focusedEditor?.ShowStatusMessage("No terminals");
            return;
        }

        var index = terminalId is null
            ? _currentTerminalIndex
            : _terminalSessions.FindIndex(t => t.Id == terminalId.Value);
        if (index < 0 || index >= _terminalSessions.Count)
        {
            if (terminalId is not null)
                _focusedEditor?.ShowStatusMessage($"No terminal #{terminalId.Value}");
            return;
        }

        var session = _terminalSessions[index];
        if (ReferenceEquals(TerminalContent.Content, session.Pane))
            TerminalContent.Content = null;
        session.Pane.EditorFocusRequested -= Terminal_EditorFocusRequested;
        _terminalSessions.RemoveAt(index);

        if (_terminalSessions.Count == 0)
        {
            _currentTerminalIndex = -1;
            UpdateTerminalSelector();
            CollapseTerminalPanel();
            await CloseTerminalPaneAsync(session);
            _focusedEditor?.Focus();
            return;
        }

        if (_currentTerminalIndex >= _terminalSessions.Count)
            _currentTerminalIndex = _terminalSessions.Count - 1;
        else if (index < _currentTerminalIndex)
            _currentTerminalIndex--;

        UpdateTerminalSelector();
        SelectTerminal(_currentTerminalIndex);
        await CloseTerminalPaneAsync(session);
    }

    private async Task CloseTerminalPaneAsync(TerminalSession session, bool showErrors = true)
    {
        try
        {
            await session.Pane.CloseAsync();
        }
        catch (Exception ex)
        {
            if (showErrors)
                _focusedEditor?.ShowStatusMessage($"Failed to close terminal #{session.Id}: {ex.Message}");
        }
    }

    private void CollapseTerminalPanel()
    {
        TerminalPanel.Visibility = Visibility.Collapsed;
        TermSplitter.Visibility  = Visibility.Collapsed;
        TermSplitterRow.Height   = new GridLength(0);
        TermPanelRow.Height      = new GridLength(0);
        TerminalContent.Content  = null;
    }

    // ─────────── Markdown Preview (delegates to MarkdownPreviewController) ─
    // See MarkdownPreviewController.cs — owns preview pane splicing, WebView2
    // rendering, and scroll sync between the source editor and the preview.

    private void PreviewBtn_Click(object sender, RoutedEventArgs e) => _markdownPreview.Toggle();

    private void ClosePreview_Click(object sender, RoutedEventArgs e) => _markdownPreview.Close();

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

        if (sender is VimEditorControl editor)
            editor.Engine.Config.SourceFile(resolved);

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
                        else _tabs.AddTab(cmd.Path);
                    }
                    break;
                case SessionCommandType.OpenFileInTab:
                    if (cmd.Path != null && File.Exists(cmd.Path))
                        _tabs.AddTab(cmd.Path);
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

    // ─────────── References panel (delegates to ReferencesPanelController) ─
    // See ReferencesPanelController.cs — owns the quickfix/references panel: LSP
    // find-references results, project grep/replace, and per-buffer location lists.

    private void ReplaceRefResults_Click(object sender, RoutedEventArgs e) => _refs.ReplaceRefResults_Click(sender, e);

    private void CloseRefPanel_Click(object sender, RoutedEventArgs e) => _refs.Close();

    private void RefList_SelectionChanged(object sender, SelectionChangedEventArgs e) => _refs.RefList_SelectionChanged(sender, e);

    private void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        if (_focusedEditor == null) return;
        _focusedEditor.LoadFile(path);
        // Ensure the file has a tab entry
        var ft = _tabs.EnsureFileTab(path);
        _tabs.SetSelectedTabItemQuietly(ft);
        Title = $"Editor — {Path.GetFileName(path)}";
        RecordRecentFile(path);
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
                _tabs.EnsureFileTab(e.FilePath);
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
                var ft = _tabs.FindFileTabByPath(null) ?? _tabs.EnsureFileTab(dlg.FileName);
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
        var fileTab = _tabs.FindFileTabByPath(buf.FilePath);
        fileTab?.UpdateHeader(isModified: false);
    }

    private void Editor_QuitRequested(object? sender, QuitRequestedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;
        // :wq/:x saves first (engine handles it), then closes the current tab.
        // :qa/:qa! closes the current tab; if it's the last one, the app exits.
        var ft = _tabs.FindFileTabByPath(editor.Engine.CurrentBuffer.FilePath);
        if (ft != null)
            _tabs.CloseFileTab(ft, e.Force);
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
            _tabs.AddTab(null);
            return;
        }
        _tabs.AddTab(ResolvePath(e.FilePath));
    }

    private void Editor_SplitRequested(object? sender, SplitRequestedEventArgs e)
    {
        if (sender is not VimEditorControl source) return;
        if (_globalRoot == null) return;

        var sourcePane = PaneTreeHelpers.FindEditorPane(_globalRoot, source);
        if (sourcePane == null) return;

        // Resolve file to open in new pane (default: same file as source)
        var newFilePath = e.FilePath != null
            ? ResolvePath(e.FilePath)
            : source.Engine.CurrentBuffer.FilePath;
        var newEditor = _tabs.CreateEditor(newFilePath);
        var newLeaf = new EditorPaneNode { Editor = newEditor };

        // WPF requires element removed from its current parent before being added to a new Grid.
        var firstElement = (UIElement)sourcePane.Editor;
        var parentSplit = PaneTreeHelpers.FindParentSplit(_globalRoot, sourcePane);
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
            _tabs.EnsureFileTab(newFilePath);

        _focusedEditor = newEditor;
        _tabs.UpdateSelectedTabForEditor(newEditor);
        newEditor.Focus();
    }

    private void CloseSplitPane(VimEditorControl editor, bool force)
    {
        if (_globalRoot == null) return;
        var editorPane = PaneTreeHelpers.FindEditorPane(_globalRoot, editor);
        if (editorPane == null) return;
        var parentSplit = PaneTreeHelpers.FindParentSplit(_globalRoot, editorPane);
        if (parentSplit == null) return;

        var sibling = parentSplit.First == editorPane ? parentSplit.Second : parentSplit.First;
        var siblingElement = PaneTreeHelpers.PaneToElement(sibling);

        // Detach sibling from parentSplit.Container before reparenting
        parentSplit.Container.Children.Remove(siblingElement);

        var grandParent = PaneTreeHelpers.FindParentSplit(_globalRoot, parentSplit);
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
        _tabs.UpdateSelectedTabForEditor(nextFocus);
        nextFocus.Focus();
        // Release the closed editor's LSP/file-watcher resources. (Unloaded no longer does this —
        // it fires on transient detaches too — so disposal is now the host's responsibility.)
        editor.Dispose();
    }

    private void Editor_WindowCloseRequested(object? sender, WindowCloseRequestedEventArgs e)
    {
        if (sender is not VimEditorControl editor) return;

        if (_markdownPreview.SourceEditor == editor)
            _markdownPreview.Close(focusEditor: false);

        if (!AllEditors().Skip(1).Any())
        {
            // Single pane: close the current file's tab
            var path = editor.Engine.CurrentBuffer.FilePath;
            var ft = _tabs.FindFileTabByPath(path);
            if (ft != null)
                _tabs.CloseFileTab(ft, e.Force);
            else
                Close();
            return;
        }
        CloseSplitPane(editor, e.Force);
    }

    private void Editor_WindowNavRequested(object? sender, WindowNavRequestedEventArgs e)
    {
        var editors = AllEditors().ToList();

        var focused = _focusedEditor;
        var idx = focused != null ? editors.IndexOf(focused) : 0;
        if (idx < 0) idx = 0;

        if (e.Dir == WindowNavDir.Next || e.Dir == WindowNavDir.Prev)
        {
            if (editors.Count <= 1) return;
            var next = e.Dir == WindowNavDir.Prev
                ? editors[(idx - 1 + editors.Count) % editors.Count]
                : editors[(idx + 1) % editors.Count];
            next.Focus();
            return;
        }

        // Coordinate-based spatial navigation (only when multiple panes exist)
        if (editors.Count > 1)
        {
            var reference = (UIElement)EditorContent;
            var rects = editors.Select(ed => GetPaneRect(ed, reference)).ToList();
            var targetIdx = PaneNavigator.FindNext(rects[idx], rects, e.Dir);
            if (targetIdx.HasValue)
            {
                editors[targetIdx.Value].Focus();
                return;
            }
        }

        // No editor pane in that direction — move to Explorer sidebar if applicable
        if (e.Dir == WindowNavDir.Left
            && _sidebar.IsVisible && _sidebar.ActivePanel == SidebarPanel.Explorer)
        {
            FileTree.Focus();
            return;
        }

        if (e.Dir == WindowNavDir.Down
            && TerminalPanel.Visibility == Visibility.Visible
            && _currentTerminalIndex >= 0
            && _currentTerminalIndex < _terminalSessions.Count)
        {
            _terminalSessions[_currentTerminalIndex].Pane.FocusInput();
        }
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
        _tabs.UpdateSelectedTabForEditor(editor);
        RefreshOutlineForEditor(editor);

        _markdownPreview.NotifyEditorActivated(editor);
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

    private void NewTab_Click(object sender, RoutedEventArgs e) => _tabs.AddTab(null);

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

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDropEffect(e);

    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDropEffect(e);

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = ((string[]?)e.Data.GetData(DataFormats.FileDrop)) ?? [];
        OpenDroppedPaths(paths);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static void UpdateDropEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OpenDroppedPaths(IEnumerable<string> paths)
    {
        var openedAnyFile = false;

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (Directory.Exists(path))
            {
                LoadFolder(path);
                continue;
            }

            if (!File.Exists(path))
                continue;

            OpenOrFocusFile(path);
            openedAnyFile = true;
        }

        if (openedAnyFile)
            CurrentEditor?.Focus();
    }

    private void ExplorerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebar.IsVisible && _sidebar.ActivePanel == SidebarPanel.Explorer)
            HideSidebar();
        else
            ShowSidebar(SidebarPanel.Explorer);
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebar.IsVisible && _sidebar.ActivePanel == SidebarPanel.Settings)
            HideSidebar();
        else
            ShowSidebar(SidebarPanel.Settings);
    }

    private void OutlineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebar.IsVisible && _sidebar.ActivePanel == SidebarPanel.Outline)
        {
            HideSidebar();
            return;
        }
        ShowSidebar(SidebarPanel.Outline);
        _focusOutlineAfterPopulate = true;
        RefreshOutlineForEditor(CurrentEditor);
    }

    private void OutlineList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.J:
            case Key.Down:
                OutlineMoveSelection(+1);
                e.Handled = true;
                return;
            case Key.K:
            case Key.Up:
                OutlineMoveSelection(-1);
                e.Handled = true;
                return;
            case Key.Return:
                OutlineActivateSelected();
                e.Handled = true;
                return;
            case Key.Escape:
                CurrentEditor?.Focus();
                e.Handled = true;
                return;
        }
    }

    private void OutlineList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualAncestor<ListBoxItem>((DependencyObject)e.OriginalSource) == null)
            return;
        OutlineActivateSelected();
    }

    private void OutlineMoveSelection(int delta)
    {
        if (OutlineList.Items.Count == 0) return;

        int idx = OutlineList.SelectedIndex;
        int next = idx < 0
            ? (delta > 0 ? 0 : OutlineList.Items.Count - 1)
            : Math.Clamp(idx + delta, 0, OutlineList.Items.Count - 1);
        OutlineList.SelectedIndex = next;
        OutlineList.ScrollIntoView(OutlineList.Items[next]);
    }

    private void OutlineActivateSelected()
    {
        if (OutlineList.SelectedItem is not OutlineItem item) return;
        var editor = CurrentEditor;
        if (editor == null) return;
        editor.JumpToLine(item.Line, item.Col);
        editor.Focus();
    }

    /// <summary>Populate the Outline panel from a DocumentSymbolsResult event.</summary>
    private void Editor_DocumentSymbolsResult(object? sender, DocumentSymbolsResultEventArgs e)
    {
        if (sender is VimEditorControl editor &&
            CurrentEditor != null &&
            !ReferenceEquals(editor, CurrentEditor))
            return;

        // Always show the outline panel (e.g. triggered via :Outline command)
        if (!_sidebar.IsVisible || _sidebar.ActivePanel != SidebarPanel.Outline)
        {
            ShowSidebar(SidebarPanel.Outline);
        }
        _focusOutlineAfterPopulate = true;
        PopulateOutlineList(e.Items);
    }

    private void PopulateOutlineList(IReadOnlyList<DocumentSymbolItem> items)
    {
        OutlineList.Items.Clear();
        foreach (var item in items)
        {
            OutlineList.Items.Add(new OutlineItem
            {
                Name      = item.Name,
                Depth     = item.Depth,
                KindIcon  = SymbolKindIcon(item.Kind),
                KindColor = SymbolKindColor(item.Kind),
                Line      = item.Line,
                Col       = item.Col,
            });
        }

        if (OutlineList.Items.Count > 0 && OutlineList.SelectedIndex < 0)
            OutlineList.SelectedIndex = 0;

        if (_focusOutlineAfterPopulate)
        {
            _focusOutlineAfterPopulate = false;
            OutlineList.Focus();
            if (OutlineList.SelectedItem != null)
                OutlineList.ScrollIntoView(OutlineList.SelectedItem);
        }
    }

    private void RefreshOutlineForEditor(VimEditorControl? editor)
    {
        if (!_sidebar.IsVisible || _sidebar.ActivePanel != SidebarPanel.Outline)
            return;

        PopulateOutlineList([]);
        editor?.RequestOutlineAsync();
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

    private void VimEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not CheckBox cb) return;
        bool enabled = cb.IsChecked == true;
        if (enabled == _vimEnabled) return;
        _vimEnabled = enabled;
        foreach (var ed in AllEditors())
            ed.VimEnabled = _vimEnabled;
        _recentItems.SaveVimEnabled(_vimEnabled);
        _focusedEditor?.Focus();
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
        var ft = _tabs.EnsureFileTab(dlg.FileName);
        ft.UpdateHeader(isModified: false);
        _tabs.SetSelectedTabItemQuietly(ft);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Undo_Click(object sender, RoutedEventArgs e) => CurrentEditor?.Engine.ProcessKey("u");
    private void Redo_Click(object sender, RoutedEventArgs e) => CurrentEditor?.Engine.ProcessKey("r", ctrl: true);
    private void CommandPalette_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void ColorTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string name) return;
        ApplyColorTheme(name, _customBackground, _customAccent);
    }

    private void MarkdownPreviewStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string name) return;
        ApplyMarkdownPreviewStyle(name);
    }

    private void ApplyMarkdownPreviewStyle(string styleName)
    {
        _markdownPreviewStyle = MarkdownRenderer.NormalizeStyle(styleName);

        if (IsLoaded)
        {
            MdPreviewDraculaRb.IsChecked = _markdownPreviewStyle == "Dracula";
            MdPreviewDarkRb.IsChecked = _markdownPreviewStyle == "Dark";
            MdPreviewLightRb.IsChecked = _markdownPreviewStyle == "Light";
            MdPreviewGitHubRb.IsChecked = _markdownPreviewStyle == "GitHub";
        }

        _recentItems.SaveMarkdownPreviewStyle(_markdownPreviewStyle);
        if (_markdownPreview.IsVisible)
            _markdownPreview.Refresh();
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
        if (ThemeNordRb != null) ThemeNordRb.IsChecked = themeName == "Nord";
        if (ThemeTokyoNightRb != null) ThemeTokyoNightRb.IsChecked = themeName == "TokyoNight";
        if (ThemeOneDarkRb != null) ThemeOneDarkRb.IsChecked = themeName == "OneDark";

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

    private void TabCtrl_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _tabs.TabCtrl_SelectionChanged(sender, e);

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

    private void OpenCommandPalette()
    {
        if (!_searchTabsInitialized)
        {
            InitSearchModeTabs();
            _searchTabsInitialized = true;
        }
        _searchMode = SearchMode.Action;
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
        if (_sidebar.CurrentFolderPath == null) return [];
        try
        {
            var q = query.Trim();
            var ranked = new List<(string FilePath, string FileName, string RelativePath, int Score)>();
            foreach (var f in EnumerateSourceFiles(_sidebar.CurrentFolderPath))
            {
                var fileName = Path.GetFileName(f);
                var relativePath = Path.GetRelativePath(_sidebar.CurrentFolderPath, f);

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
        if (_sidebar.CurrentFolderPath == null || string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<SearchResultItem>();
        try
        {
            foreach (var f in EnumerateSourceFiles(_sidebar.CurrentFolderPath))
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
        var all = new (string Name, string Detail, string Icon, string Color, Action Act)[]
        {
            ("新しいタブを開く",        "", "\uE710", "#50FA7B", () => { CloseSearch(); _tabs.AddTab(null); }),
            ("ファイルを開く...",        "", "\uED25", "#8BE9FD", () => { CloseSearch(); OpenFile_Click(this, new RoutedEventArgs()); }),
            ("フォルダーを開く...",      "", "\uED41", "#E6C07B", () => { CloseSearch(); OpenFolder_Click(this, new RoutedEventArgs()); }),
            ("ファイルを保存",           "", "\uE74E", "#BD93F9", () => { CloseSearch(); Save_Click(this, new RoutedEventArgs()); }),
            ("エクスプローラーを切替",   "", "\uE8B7", "#AAAAAA", () => { CloseSearch(); ToggleSidebar(); }),
            ("設定を開く",               "", "\uE713", "#AAAAAA", () => { CloseSearch(); ShowSidebar(SidebarPanel.Settings); }),
            ("ウィンドウを閉じる",       "", "\uE8BB", "#FF79C6", () => { CloseSearch(); Close(); }),
        };

        var results = all
            .Where(a => string.IsNullOrEmpty(query) ||
                        a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(a => new SearchResultItem
            {
                DisplayName   = a.Name,
                Detail        = a.Detail,
                Icon          = a.Icon,
                IconColor     = a.Color,
                ActionCallback = a.Act
            })
            .ToList();

        foreach (var file in _recentItems.RecentFiles.Where(File.Exists))
        {
            var name = $"最近開いたファイル: {Path.GetFileName(file)}";
            if (!MatchesActionQuery(name, file, query)) continue;
            var captured = file;
            results.Add(new SearchResultItem
            {
                DisplayName = name,
                Detail = file,
                Icon = "\uE7C3",
                IconColor = "#8BE9FD",
                ActionCallback = () => { CloseSearch(); OpenOrFocusFile(captured); }
            });
        }

        foreach (var folder in _recentItems.RecentFolders.Where(Directory.Exists))
        {
            var folderName = Path.GetFileName(folder) is { Length: > 0 } n ? n : folder;
            var name = $"最近開いたフォルダー: {folderName}";
            if (!MatchesActionQuery(name, folder, query)) continue;
            var captured = folder;
            results.Add(new SearchResultItem
            {
                DisplayName = name,
                Detail = folder,
                Icon = "\uED41",
                IconColor = "#E6C07B",
                ActionCallback = () => { CloseSearch(); LoadFolder(captured); }
            });
        }

        return results;
    }

    private static bool MatchesActionQuery(string name, string detail, string query) =>
        string.IsNullOrWhiteSpace(query) ||
        name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        detail.Contains(query, StringComparison.OrdinalIgnoreCase);

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

        _refs.SetQuickfixItems(panelItems);
        _refs.QuickfixCurrentIndex = -1;

        int fileCount = panelItems.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _refs.QuickfixTitle = string.IsNullOrWhiteSpace(query)
            ? $"SEARCH ({panelItems.Count} results) [{fileCount} file(s)]"
            : $"SEARCH \"{query}\" ({panelItems.Count} results) [{fileCount} file(s)]";
        _refs.LastGrepOptions = null;

        _refs.ShowQuickfixPanel();
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

        // Persist command/search history to viminfo — merge across all editors
        var allEditors = AllEditors().ToList();
        if (allEditors.Count > 0)
        {
            var ex = allEditors[0].Engine.ExProcessor;
            // All editors share history (loaded identically at startup), so any one suffices
            ViminfoManager.Save(ex.CommandHistory, ex.SearchHistory);
        }

        var tabFiles = _tabs.FileTabs
            .Where(t => t.FilePath != null)
            .Select(t => t.FilePath!);
        var activeFile = _focusedEditor?.Engine.CurrentBuffer.FilePath;
        _recentItems.SaveSession(_sidebar.CurrentFolderPath, tabFiles, activeFile);

        _markdownPreview.StopPendingWork();

        foreach (var session in _terminalSessions)
        {
            session.Pane.EditorFocusRequested -= Terminal_EditorFocusRequested;
            _ = CloseTerminalPaneAsync(session, showErrors: false);
        }
        _terminalSessions.Clear();

        if (_windowSource != null)
        {
            _windowSource.RemoveHook(WindowMessageProc);
            _windowSource = null;
        }

        // Release every editor's LSP/file-watcher resources on exit (Unloaded no longer does this).
        foreach (var ed in AllEditors())
            ed.Dispose();

        base.OnClosing(e);
    }
}
