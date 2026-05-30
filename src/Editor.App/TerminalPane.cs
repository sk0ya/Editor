using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using Terminal.Settings;
using Terminal.Tabs;

namespace Editor.App;

public sealed class TerminalPane : UserControl
{
    private readonly TerminalTabView _terminal;
    private bool _ctrlWPending;
    private bool _closed;

    public event EventHandler? EditorFocusRequested;

    public TerminalPane(string? commandLine, string? workingDirectory)
    {
        var resolvedWorkingDirectory = Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var resolvedCommandLine = string.IsNullOrWhiteSpace(commandLine)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : commandLine;

        _terminal = new TerminalTabView(resolvedCommandLine, resolvedWorkingDirectory);
        Content = _terminal;

        PreviewKeyDown += OnPreviewKeyDown;
        LostKeyboardFocus += OnLostKeyboardFocus;
    }

    public void FocusInput()
    {
        _terminal.FocusTerminal();
        Keyboard.Focus(_terminal);
    }

    public async Task CloseAsync()
    {
        if (_closed)
            return;

        _closed = true;
        PreviewKeyDown -= OnPreviewKeyDown;
        LostKeyboardFocus -= OnLostKeyboardFocus;
        await _terminal.CloseAsync();
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ctrlWPending = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.W)
        {
            _ctrlWPending = true;
            e.Handled = true;
            return;
        }

        if (!_ctrlWPending)
            return;

        _ctrlWPending = false;
        switch (e.Key)
        {
            case Key.K:
            case Key.Up:
            case Key.W:
                EditorFocusRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.Escape:
            default:
                e.Handled = true;
                break;
        }
    }

}
