using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
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
    private EditorTheme _currentTheme = EditorTheme.Dracula;

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
        AddTab(null);

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            OpenFile(args[1]);
    }

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

        if (buf.FilePath == null || e.FilePath != null)
        {
            var dlg = new SaveFileDialog { Filter = "All Files|*.*", Title = "Save File" };
            if (e.FilePath != null) dlg.FileName = e.FilePath;
            if (dlg.ShowDialog() != true) return;
            buf.Save(dlg.FileName);
            tabInfo.FilePath = dlg.FileName;
        }
        else
        {
            buf.Save();
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

    // ─────────── Menu handlers ───────────────

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab(null);

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All Files|*.*|Text Files|*.txt|C# Files|*.cs|Python|*.py", Title = "Open File" };
        if (dlg.ShowDialog() != true) return;

        // 現在タブが空の [No Name] なら置き換え、そうでなければ新規タブで開く
        var current = CurrentTabInfo;
        if (current != null && current.FilePath == null && !current.Editor.Engine.CurrentBuffer.Text.IsModified)
            OpenFile(dlg.FileName);
        else
            AddTab(dlg.FileName);
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
        var dlg = new SaveFileDialog { Filter = "All Files|*.*", Title = "Save File As" };
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
            ? new System.Windows.Thickness(6)
            : new System.Windows.Thickness(0);
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
                e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
