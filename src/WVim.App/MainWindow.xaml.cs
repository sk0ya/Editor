using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WVim.Controls;
using WVim.Controls.Themes;
using WVim.Core.Engine;

namespace WVim.App;

public partial class MainWindow : Window
{
    private record TabInfo(string? FilePath, string DisplayName);
    private readonly List<TabInfo> _tabs = [];
    private int _currentTab = 0;
    private bool _changingTabs = false;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Add initial tab
        AddTab(null);
        Editor.Focus();

        // Handle command line args
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            OpenFile(args[1]);
        }
    }

    private void AddTab(string? filePath)
    {
        var name = filePath != null ? Path.GetFileName(filePath) : "[No Name]";
        var tab = new TabItem { Header = name };
        TabCtrl.Items.Add(tab);
        _tabs.Add(new TabInfo(filePath, name));
        _changingTabs = true;
        TabCtrl.SelectedIndex = TabCtrl.Items.Count - 1;
        _changingTabs = false;
        _currentTab = TabCtrl.Items.Count - 1;

        if (filePath != null)
            Editor.LoadFile(filePath);
        else
            Editor.SetText("");
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        Editor.LoadFile(path);
        var name = Path.GetFileName(path);
        _tabs[_currentTab] = new TabInfo(path, name);
        if (TabCtrl.Items[_currentTab] is TabItem ti)
            ti.Header = name;
        Title = $"WVIM — {name}";
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        var dir = Editor.Engine.CurrentBuffer.FilePath != null
            ? Path.GetDirectoryName(Editor.Engine.CurrentBuffer.FilePath)
            : Directory.GetCurrentDirectory();
        return Path.Combine(dir ?? "", path);
    }

    private void UpdateTabHeader()
    {
        var buf = Editor.Engine.CurrentBuffer;
        var name = buf.FilePath != null ? Path.GetFileName(buf.FilePath) : "[No Name]";
        if (buf.Text.IsModified) name += " •";
        if (TabCtrl.Items[_currentTab] is TabItem ti)
            ti.Header = name;
        Title = $"WVIM — {name}";
    }

    // ─────────── Events from VimEditorControl ───────────────

    private void Editor_SaveRequested(object sender, SaveRequestedEventArgs e)
    {
        var buf = Editor.Engine.CurrentBuffer;
        if (buf.FilePath == null || e.FilePath != null)
        {
            var dlg = new SaveFileDialog { Filter = "All Files|*.*", Title = "Save File" };
            if (e.FilePath != null) dlg.FileName = e.FilePath;
            if (dlg.ShowDialog() != true) return;
            buf.Save(dlg.FileName);
        }
        else
        {
            buf.Save();
        }
        UpdateTabHeader();
    }

    private void Editor_QuitRequested(object sender, QuitRequestedEventArgs e)
    {
        var buf = Editor.Engine.CurrentBuffer;
        if (buf.Text.IsModified && !e.Force)
        {
            var result = MessageBox.Show(
                $"'{buf.Name}' has unsaved changes. Save before closing?",
                "WVIM",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes)
            {
                try { buf.Save(); } catch (Exception ex)
                {
                    MessageBox.Show($"Save failed: {ex.Message}", "WVIM", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        if (_tabs.Count > 1)
        {
            TabCtrl.Items.RemoveAt(_currentTab);
            _tabs.RemoveAt(_currentTab);
            _currentTab = Math.Clamp(_currentTab, 0, _tabs.Count - 1);
            TabCtrl.SelectedIndex = _currentTab;
        }
        else
        {
            Close();
        }
    }

    private void Editor_OpenFileRequested(object sender, OpenFileRequestedEventArgs e)
    {
        var path = ResolvePath(e.FilePath);

        if (!File.Exists(path))
        {
            var result = MessageBox.Show(
                $"'{path}' does not exist. Create it?",
                "WVIM",
                MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes) return;
        }

        OpenFile(path);
    }

    private void Editor_NewTabRequested(object sender, NewTabRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.FilePath))
        {
            AddTab(null);
            return;
        }

        AddTab(ResolvePath(e.FilePath));
    }

    private void Editor_SplitRequested(object sender, SplitRequestedEventArgs e)
    {
        MessageBox.Show(
            $"{(e.Vertical ? "Vertical" : "Horizontal")} split is not implemented yet.",
            "WVIM",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Editor_NextTabRequested(object? sender, EventArgs e)
    {
        if (TabCtrl.Items.Count == 0) return;
        var current = TabCtrl.SelectedIndex < 0 ? 0 : TabCtrl.SelectedIndex;
        _changingTabs = true;
        TabCtrl.SelectedIndex = (current + 1) % TabCtrl.Items.Count;
        _changingTabs = false;
        _currentTab = TabCtrl.SelectedIndex;
    }

    private void Editor_PrevTabRequested(object? sender, EventArgs e)
    {
        if (TabCtrl.Items.Count == 0) return;
        var current = TabCtrl.SelectedIndex < 0 ? 0 : TabCtrl.SelectedIndex;
        _changingTabs = true;
        TabCtrl.SelectedIndex = (current - 1 + TabCtrl.Items.Count) % TabCtrl.Items.Count;
        _changingTabs = false;
        _currentTab = TabCtrl.SelectedIndex;
    }

    private void Editor_CloseTabRequested(object sender, CloseTabRequestedEventArgs e)
    {
        Editor_QuitRequested(sender, new QuitRequestedEventArgs(e.Force));
    }

    private void Editor_ModeChanged(object sender, ModeChangedEventArgs e)
    {
        // Could update window title or other UI
    }

    // ─────────── Menu handlers ───────────────

    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab(null);

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "All Files|*.*|Text Files|*.txt|C# Files|*.cs|Python|*.py", Title = "Open File" };
        if (dlg.ShowDialog() == true)
            OpenFile(dlg.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Editor_SaveRequested(this, new SaveRequestedEventArgs(null));
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "All Files|*.*", Title = "Save File As" };
        if (dlg.ShowDialog() == true)
        {
            Editor.Engine.CurrentBuffer.Save(dlg.FileName);
            UpdateTabHeader();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Undo_Click(object sender, RoutedEventArgs e) => Editor.Engine.ProcessKey("u");
    private void Redo_Click(object sender, RoutedEventArgs e) => Editor.Engine.ProcessKey("r", ctrl: true);

    private void LineNumbers_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        Editor.ExecuteCommand(mi.IsChecked ? "set number" : "set nonumber");
    }

    private void Syntax_Click(object sender, RoutedEventArgs e)
    {
        var mi = (MenuItem)sender;
        Editor.ExecuteCommand(mi.IsChecked ? "syntax on" : "syntax off");
    }

    private void ThemeDracula_Click(object sender, RoutedEventArgs e) => Editor.SetTheme(EditorTheme.Dracula);
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => Editor.SetTheme(EditorTheme.Dark);

    private void TabCtrl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingTabs || TabCtrl.SelectedIndex < 0 || TabCtrl.SelectedIndex >= _tabs.Count) return;
        _currentTab = TabCtrl.SelectedIndex;
        // In a full implementation, we'd restore that tab's buffer
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var buf = Editor.Engine.CurrentBuffer;
        if (buf.Text.IsModified)
        {
            var result = MessageBox.Show(
                "You have unsaved changes. Exit anyway?",
                "WVIM",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
