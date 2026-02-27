using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WVim.Controls.Themes;
using WVim.Core.Config;
using WVim.Core.Engine;
using WVim.Core.Models;

namespace WVim.Controls;

public class SaveRequestedEventArgs(string? filePath) : EventArgs
{
    public string? FilePath { get; } = filePath;
}
public class QuitRequestedEventArgs(bool force) : EventArgs
{
    public bool Force { get; } = force;
}
public class OpenFileRequestedEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}
public class ModeChangedEventArgs(VimMode mode) : EventArgs
{
    public VimMode Mode { get; } = mode;
}

public partial class VimEditorControl : UserControl
{
    private VimEngine _engine;
    private EditorTheme _theme = EditorTheme.Dracula;

    public event EventHandler<SaveRequestedEventArgs>? SaveRequested;
    public event EventHandler<QuitRequestedEventArgs>? QuitRequested;
    public event EventHandler<OpenFileRequestedEventArgs>? OpenFileRequested;
    public event EventHandler<ModeChangedEventArgs>? ModeChanged;

    public VimMode CurrentMode => _engine.Mode;
    public string Text => _engine.CurrentBuffer.Text.GetText();
    public string? FilePath => _engine.CurrentBuffer.FilePath;
    public VimEngine Engine => _engine;

    public VimEditorControl()
    {
        InitializeComponent();

        var config = VimConfig.LoadDefault();
        _engine = new VimEngine(config);
        _engine.SetClipboardProvider(new WpfClipboardProvider());

        Focusable = true;
        KeyDown += OnKeyDown;
        TextInput += OnTextInput;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;

        ApplyTheme();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        UpdateAll();
    }

    public void LoadFile(string path)
    {
        _engine.LoadFile(path);
        UpdateAll();
    }

    public void SetText(string text)
    {
        _engine.SetText(text);
        UpdateAll();
    }

    public void ExecuteCommand(string exCommand)
    {
        var events = _engine.ProcessKey(":");
        ProcessVimEvents(events);
        foreach (var ch in exCommand)
        {
            events = _engine.ProcessKey(ch.ToString());
            ProcessVimEvents(events);
        }
        events = _engine.ProcessKey("Return");
        ProcessVimEvents(events);
    }

    public void SetTheme(EditorTheme theme)
    {
        _theme = theme;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Canvas.Theme = _theme;
        StatusBar.Theme = _theme;
        Background = _theme.Background;
    }

    // ─────────────── Key handling ───────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Handle keys that WPF normally consumes (Tab, etc.)
        var mode = _engine.Mode;
        if (mode == VimMode.Insert || mode == VimMode.Replace)
        {
            if (e.Key == Key.Tab)
            {
                ProcessKey("Tab", false, false, false);
                e.Handled = true;
            }
            else if (e.Key == Key.Return)
            {
                ProcessKey("Return", false, false, false);
                e.Handled = true;
            }
            else if (e.Key == Key.Back)
            {
                ProcessKey("Back", false, false, false);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                ProcessKey("Delete", false, false, false);
                e.Handled = true;
            }
        }

        // Always handle Escape
        if (e.Key == Key.Escape)
        {
            ProcessKey("Escape", false, false, false);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        string? keyStr = null;

        // In normal/visual/command mode, handle all key presses as vim keys
        var mode = _engine.Mode;

        if (mode != VimMode.Insert && mode != VimMode.Replace)
        {
            keyStr = GetVimKey(key, shift);
            if (keyStr != null)
            {
                ProcessKey(keyStr, ctrl, shift, alt);
                e.Handled = true;
                return;
            }
        }

        // Navigation keys in all modes
        keyStr = key switch
        {
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Home => "0",
            Key.End => "$",
            Key.PageUp => ctrl ? "b" : null,
            Key.PageDown => ctrl ? "f" : null,
            Key.F1 => null,
            _ => null
        };

        if (keyStr != null)
        {
            ProcessKey(keyStr, ctrl, shift, alt);
            e.Handled = true;
        }
        else if (ctrl && mode != VimMode.Insert && mode != VimMode.Replace)
        {
            var ctrlKey = GetCtrlKey(key);
            if (ctrlKey != null)
            {
                ProcessKey(ctrlKey, true, shift, alt);
                e.Handled = true;
            }
        }
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        var mode = _engine.Mode;
        if (mode == VimMode.Insert || mode == VimMode.Replace ||
            mode == VimMode.Command || mode == VimMode.SearchForward || mode == VimMode.SearchBackward)
        {
            foreach (var ch in e.Text)
            {
                if (ch < 32) continue; // Skip control chars
                ProcessKey(ch.ToString(), false, false, false);
            }
            e.Handled = true;
        }
    }

    private void ProcessKey(string key, bool ctrl, bool shift, bool alt)
    {
        Canvas.ResetCursorBlink();
        var events = _engine.ProcessKey(key, ctrl, shift, alt);
        ProcessVimEvents(events);
    }

    private void ProcessVimEvents(IReadOnlyList<VimEvent> events)
    {
        bool needFullUpdate = false;
        bool needCursorUpdate = false;

        foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case VimEventType.TextChanged:
                    needFullUpdate = true;
                    break;
                case VimEventType.CursorMoved when evt is CursorMovedEvent ce:
                    needCursorUpdate = true;
                    if (!needFullUpdate)
                    {
                        Canvas.SetCursor(ce.Position);
                        StatusBar.UpdateCursor(ce.Position, _engine.CurrentBuffer.Text.LineCount);
                    }
                    break;
                case VimEventType.ModeChanged when evt is ModeChangedEvent me:
                    StatusBar.UpdateMode(me.Mode);
                    Canvas.SetMode(me.Mode);
                    ModeChanged?.Invoke(this, new ModeChangedEventArgs(me.Mode));
                    break;
                case VimEventType.SelectionChanged when evt is SelectionChangedEvent se:
                    Canvas.SetSelection(se.Selection);
                    break;
                case VimEventType.StatusMessage when evt is StatusMessageEvent sme:
                    StatusBar.UpdateStatus(sme.Message);
                    break;
                case VimEventType.CommandLineChanged when evt is CommandLineChangedEvent cle:
                    CmdLine.Update(cle.Text);
                    break;
                case VimEventType.SaveRequested when evt is SaveRequestedEvent sre:
                    SaveRequested?.Invoke(this, new SaveRequestedEventArgs(sre.FilePath));
                    break;
                case VimEventType.QuitRequested when evt is QuitRequestedEvent qre:
                    QuitRequested?.Invoke(this, new QuitRequestedEventArgs(qre.Force));
                    break;
                case VimEventType.OpenFileRequested when evt is OpenFileRequestedEvent ofre:
                    OpenFileRequested?.Invoke(this, new OpenFileRequestedEventArgs(ofre.FilePath));
                    break;
                case VimEventType.SearchResultChanged when evt is SearchResultChangedEvent srce:
                    UpdateSearchHighlights(srce.Pattern);
                    break;
            }
        }

        if (needFullUpdate)
            UpdateAll();
        else if (needCursorUpdate && !needFullUpdate)
            Canvas.SetCursor(_engine.Cursor);
    }

    private void UpdateAll()
    {
        var buf = _engine.CurrentBuffer;
        var lines = Enumerable.Range(0, buf.Text.LineCount)
            .Select(i => buf.Text.GetLine(i)).ToArray();

        Canvas.SetLines(lines);
        Canvas.SetCursor(_engine.Cursor);
        Canvas.SetMode(_engine.Mode);
        Canvas.ShowLineNumbers(_engine.Options.Number);

        // Syntax tokens
        if (_engine.Options.Syntax)
        {
            var tokens = _engine.Syntax.Tokenize(lines);
            Canvas.SetTokens(tokens);
        }
        else
        {
            Canvas.SetTokens([]);
        }

        StatusBar.UpdateMode(_engine.Mode);
        StatusBar.UpdateFile(buf.FilePath, buf.Text.IsModified);
        StatusBar.UpdateCursor(_engine.Cursor, buf.Text.LineCount);
    }

    private void UpdateSearchHighlights(string pattern)
    {
        if (!_engine.Options.HlSearch || string.IsNullOrEmpty(pattern))
        {
            Canvas.SetSearchMatches([], "");
            return;
        }
        var buf = _engine.CurrentBuffer.Text;
        var ignoreCase = _engine.Options.SmartCase
            ? !pattern.Any(char.IsUpper)
            : _engine.Options.IgnoreCase;
        var matches = buf.FindAll(pattern, ignoreCase);
        Canvas.SetSearchMatches(matches, pattern);
    }

    private static string? GetVimKey(Key key, bool shift)
    {
        // Letter keys
        return key switch
        {
            Key.H => shift ? "H" : "h",
            Key.J => shift ? "J" : "j",
            Key.K => shift ? "K" : "k",
            Key.L => shift ? "L" : "l",
            Key.W => shift ? "W" : "w",
            Key.B => shift ? "B" : "b",
            Key.E => shift ? "E" : "e",
            Key.G => shift ? "G" : "g",
            Key.I => shift ? "I" : "i",
            Key.A => shift ? "A" : "a",
            Key.O => shift ? "O" : "o",
            Key.R => shift ? "R" : "r",
            Key.S => shift ? "S" : "s",
            Key.D => shift ? "D" : "d",
            Key.C => shift ? "C" : "c",
            Key.Y => shift ? "Y" : "y",
            Key.P => shift ? "P" : "p",
            Key.U => shift ? "U" : "u",
            Key.V => shift ? "V" : "v",
            Key.X => shift ? "X" : "x",
            Key.N => shift ? "N" : "n",
            Key.M => shift ? "M" : "m",
            Key.F => shift ? "F" : "f",
            Key.T => shift ? "T" : "t",
            Key.Z => shift ? "Z" : "z",
            Key.Q => shift ? "Q" : "q",
            // Punctuation
            Key.OemSemicolon => shift ? ":" : ";",
            Key.OemQuestion => shift ? "?" : "/",
            Key.OemPeriod => shift ? ">" : ".",
            Key.OemComma => shift ? "<" : ",",
            Key.OemOpenBrackets => shift ? "{" : "[",
            Key.OemCloseBrackets => shift ? "}" : "]",
            Key.OemPipe => shift ? "|" : "\\",
            Key.Oem7 => shift ? "\"" : "'",
            Key.OemTilde => shift ? "~" : "`",
            Key.OemMinus => shift ? "_" : "-",
            Key.OemPlus => shift ? "+" : "=",
            // Number row (with shift = symbol)
            Key.D0 => shift ? ")" : "0",
            Key.D1 => shift ? "!" : "1",
            Key.D2 => shift ? "@" : "2",
            Key.D3 => shift ? "#" : "3",
            Key.D4 => shift ? "$" : "4",
            Key.D5 => shift ? "%" : "5",
            Key.D6 => shift ? "^" : "6",
            Key.D7 => shift ? "&" : "7",
            Key.D8 => shift ? "*" : "8",
            Key.D9 => shift ? "(" : "9",
            // Special
            Key.Escape => "Escape",
            Key.Return => "Return",
            Key.Back => "Back",
            Key.Delete => "Delete",
            Key.Tab => "Tab",
            _ => null
        };
    }

    private static string? GetCtrlKey(Key key) => key switch
    {
        Key.D => "d",
        Key.U => "u",
        Key.F => "f",
        Key.B => "b",
        Key.R => "r",
        Key.O => "o",
        Key.I => "i",
        Key.V => "v",
        Key.W => "w",
        Key.OemOpenBrackets => "[",
        _ => null
    };
}
