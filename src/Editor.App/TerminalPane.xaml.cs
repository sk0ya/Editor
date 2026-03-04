using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Editor.App;

public partial class TerminalPane : UserControl
{
    private Process? _process;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string _workingDir;
    private readonly StringBuilder _outputBuffer = new();
    private const int MaxOutputChars = 50_000;

    public TerminalPane()
    {
        InitializeComponent();
        _workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        StartShell();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        KillProcess();
    }

    public void SetWorkingDirectory(string? dir)
    {
        if (dir != null && Directory.Exists(dir))
            _workingDir = dir;
    }

    private void StartShell()
    {
        AppendOutput($"Terminal — {_workingDir}\r\n");
    }

    private void KillProcess()
    {
        try
        {
            _process?.Kill(entireProcessTree: true);
            _process?.Dispose();
            _process = null;
        }
        catch { /* ignore */ }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Return:
                ExecuteCommand(InputBox.Text);
                InputBox.Clear();
                e.Handled = true;
                break;
            case Key.Up:
                NavigateHistory(-1);
                e.Handled = true;
                break;
            case Key.Down:
                NavigateHistory(1);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                KillProcess();
                AppendOutput("^C\r\n");
                e.Handled = true;
                break;
        }
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count - 1);
        InputBox.Text = _history[_historyIndex];
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void ExecuteCommand(string input)
    {
        input = input.Trim();
        AppendOutput($"$ {input}\r\n");
        if (input.Length == 0) return;

        _history.Insert(0, input);
        _historyIndex = -1;

        // Built-in: cd
        if (input.StartsWith("cd ") || input == "cd")
        {
            var arg = input.Length > 3 ? input[3..].Trim() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var newDir = Path.IsPathRooted(arg) ? arg : Path.GetFullPath(Path.Combine(_workingDir, arg));
            if (Directory.Exists(newDir))
            {
                _workingDir = newDir;
                PromptLabel.Text = $"{TruncatePath(_workingDir)}$ ";
            }
            else
            {
                AppendOutput($"cd: no such directory: {arg}\r\n");
            }
            return;
        }

        // Built-in: clear
        if (input is "clear" or "cls")
        {
            _outputBuffer.Clear();
            OutputText.Text = "";
            return;
        }

        // Run via cmd.exe on Windows, /bin/sh elsewhere
        bool isWindows = OperatingSystem.IsWindows();
        string shell    = isWindows ? "cmd.exe" : "/bin/sh";
        string shellArgs = isWindows ? $"/c \"{input}\"" : $"-c \"{input}\"";

        var psi = new ProcessStartInfo(shell, shellArgs)
        {
            WorkingDirectory = _workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        KillProcess();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnData(object _, DataReceivedEventArgs a) { if (a.Data != null) Dispatcher.Invoke(() => AppendOutput(a.Data + "\r\n")); }
        _process.OutputDataReceived += OnData;
        _process.ErrorDataReceived  += OnData;
        _process.Exited += (_, _) => Dispatcher.Invoke(() => { _process = null; });

        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendOutput($"Error: {ex.Message}\r\n");
            _process?.Dispose();
            _process = null;
        }
    }

    private void AppendOutput(string text)
    {
        _outputBuffer.Append(text);
        if (_outputBuffer.Length > MaxOutputChars)
        {
            // Trim from the start at a line boundary
            var s = _outputBuffer.ToString();
            var trim = s.IndexOf('\n', s.Length - MaxOutputChars);
            _outputBuffer.Clear();
            _outputBuffer.Append(trim >= 0 ? s[(trim + 1)..] : s[^MaxOutputChars..]);
        }
        OutputText.Text = _outputBuffer.ToString();
        OutputScroll.ScrollToEnd();
    }

    private static string TruncatePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + path[home.Length..];
        return path.Length > 30 ? "..." + path[^27..] : path;
    }
}
