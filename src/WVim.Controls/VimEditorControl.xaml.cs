using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
public class NewTabRequestedEventArgs(string? filePath) : EventArgs
{
    public string? FilePath { get; } = filePath;
}
public class SplitRequestedEventArgs(bool vertical) : EventArgs
{
    public bool Vertical { get; } = vertical;
}
public class CloseTabRequestedEventArgs(bool force) : EventArgs
{
    public bool Force { get; } = force;
}
public class ModeChangedEventArgs(VimMode mode) : EventArgs
{
    public VimMode Mode { get; } = mode;
}

public partial class VimEditorControl : UserControl
{
    private VimEngine _engine;
    private EditorTheme _theme = EditorTheme.Dracula;
    private bool _keyDownHandledByVim;
    private bool _isDragSelecting = false;

    public event EventHandler<SaveRequestedEventArgs>? SaveRequested;
    public event EventHandler<QuitRequestedEventArgs>? QuitRequested;
    public event EventHandler<OpenFileRequestedEventArgs>? OpenFileRequested;
    public event EventHandler<NewTabRequestedEventArgs>? NewTabRequested;
    public event EventHandler<SplitRequestedEventArgs>? SplitRequested;
    public event EventHandler? NextTabRequested;
    public event EventHandler? PrevTabRequested;
    public event EventHandler<CloseTabRequestedEventArgs>? CloseTabRequested;
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

        // Mouse and scroll wiring
        Canvas.MouseClicked += OnCanvasMouseClicked;
        Canvas.MouseDragging += OnCanvasMouseDragging;
        Canvas.MouseDragEnded += OnCanvasMouseDragEnded;
        Canvas.ScrollChanged += OnCanvasScrollChanged;
        Canvas.VisibleLinesChanged += OnCanvasVisibleLinesChanged;

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

    // ─────────────── Scrollbar ───────────────

    private void UpdateScrollbar(double offsetY)
    {
        double lineH = Canvas.LineHeight;
        if (lineH <= 0) return;
        int totalLines = _engine.CurrentBuffer.Text.LineCount;
        int visibleLines = Canvas.VisibleLines;
        int maxFirst = Math.Max(0, totalLines - visibleLines);

        VScrollBar.Minimum = 0;
        VScrollBar.Maximum = maxFirst;
        VScrollBar.ViewportSize = visibleLines;
        VScrollBar.LargeChange = Math.Max(1, visibleLines);
        VScrollBar.SmallChange = 1;
        VScrollBar.Value = Math.Clamp(offsetY / lineH, 0, maxFirst);
    }

    private void VScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        double lineH = Canvas.LineHeight;
        if (lineH <= 0) return;
        Canvas.ScrollTo(e.NewValue * lineH);
        Focus();
    }

    private void OnCanvasScrollChanged(double offsetY, double offsetX)
    {
        UpdateScrollbar(offsetY);
    }

    private void OnCanvasVisibleLinesChanged(int visibleLines)
    {
        UpdateScrollbar(Canvas.FirstVisibleLine * Canvas.LineHeight);
    }

    // ─────────────── Mouse handling ───────────────

    private void OnCanvasMouseClicked(int line, int col)
    {
        Focus();
        // Exit visual mode if active, then move cursor
        if (_engine.Mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock)
        {
            var escEvents = _engine.ProcessKey("Escape");
            ProcessVimEvents(escEvents);
        }

        var events = _engine.SetCursorPosition(new CursorPosition(line, col));
        ProcessVimEvents(events);
    }

    private void OnCanvasMouseDragging(int line, int col)
    {
        if (!_isDragSelecting)
        {
            _isDragSelecting = true;
            if (_engine.Mode is not (VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock))
            {
                var vEvents = _engine.ProcessKey("v");
                ProcessVimEvents(vEvents);
            }
        }

        var events = _engine.SetCursorPosition(new CursorPosition(line, col));
        ProcessVimEvents(events);
    }

    private void OnCanvasMouseDragEnded()
    {
        _isDragSelecting = false;
    }

    // ─────────────── Key handling ───────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Resolve actual key — handles IME (ImeProcessed) and Alt combos (System)
        Key actualKey = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        // Handle keys that WPF normally consumes (Tab, etc.)
        var mode = _engine.Mode;
        if (mode == VimMode.Insert || mode == VimMode.Replace)
        {
            // When e.Key == Key.ImeProcessed, the IME is mid-composition (e.g. Enter to
            // confirm kanji). Do NOT intercept — let the IME finalize the composition.
            if (e.Key != Key.ImeProcessed)
            {
                if (actualKey == Key.Tab)
                {
                    ProcessKey("Tab", false, false, false);
                    e.Handled = true;
                }
                else if (actualKey == Key.Return)
                {
                    ProcessKey("Return", false, false, false);
                    e.Handled = true;
                }
                else if (actualKey == Key.Back)
                {
                    ProcessKey("Back", false, false, false);
                    e.Handled = true;
                }
                else if (actualKey == Key.Delete)
                {
                    ProcessKey("Delete", false, false, false);
                    e.Handled = true;
                }
            }
        }

        // Handle Escape — but not when IME is composing (ImeProcessed Escape cancels
        // the composition; a subsequent Escape will then exit Insert mode normally).
        if (actualKey == Key.Escape && e.Key != Key.ImeProcessed)
        {
            ProcessKey("Escape", false, false, false);
            e.Handled = true;
        }

        // In Normal/Visual/Command/Search mode, intercept IME-processed keys before
        // the IME can consume them and turn them into Japanese text.
        if (e.Key == Key.ImeProcessed &&
            mode is VimMode.Normal or VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock
                 or VimMode.Command or VimMode.SearchForward or VimMode.SearchBackward)
        {
            bool ctrl = (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0;

            if (ctrl)
            {
                var ctrlKey = GetCtrlKey(actualKey);
                if (ctrlKey != null)
                {
                    ProcessKey(ctrlKey, true, shift, alt);
                    e.Handled = true;
                    return;
                }
            }

            var keyStr = GetVimKey(actualKey, shift);
            if (keyStr != null)
            {
                ProcessKey(keyStr, ctrl, shift, alt);
                e.Handled = true;
            }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        _keyDownHandledByVim = false;

        bool ctrl = (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        string? keyStr = null;

        // In normal/visual/command mode, handle all key presses as vim keys
        var mode = _engine.Mode;

        // Ctrl combinations are valid in every mode for the subset the engine supports.
        if (ctrl)
        {
            var ctrlKey = GetCtrlKey(key);
            if (ctrlKey != null)
            {
                ProcessKey(ctrlKey, true, shift, alt);
                _keyDownHandledByVim = true;
                e.Handled = true;
                return;
            }
        }

        if (!ctrl && !alt && ShouldPreferTextInput(mode, key))
            return;

        if (mode != VimMode.Insert && mode != VimMode.Replace)
        {
            keyStr = GetVimKey(key, shift);
            if (keyStr != null)
            {
                ProcessKey(keyStr, ctrl, shift, alt);
                _keyDownHandledByVim = true;
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
            _keyDownHandledByVim = true;
            e.Handled = true;
        }
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_keyDownHandledByVim)
        {
            _keyDownHandledByVim = false;
            return;
        }

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
            return;
        }

        if (mode == VimMode.Normal || mode == VimMode.Visual ||
            mode == VimMode.VisualLine || mode == VimMode.VisualBlock)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
                return;

            bool handled = false;
            foreach (var raw in e.Text)
            {
                var ch = NormalizeVimInputChar(raw);
                if (ch is < (char)32 or > (char)126) continue;
                ProcessKey(ch.ToString(), false, false, false);
                handled = true;
            }

            if (handled)
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
                    StatusBar.UpdateCommandLine(cle.Text);
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
                case VimEventType.NewTabRequested when evt is NewTabRequestedEvent ntre:
                    NewTabRequested?.Invoke(this, new NewTabRequestedEventArgs(ntre.FilePath));
                    break;
                case VimEventType.SplitRequested when evt is SplitRequestedEvent stre:
                    SplitRequested?.Invoke(this, new SplitRequestedEventArgs(stre.Vertical));
                    break;
                case VimEventType.NextTabRequested:
                    NextTabRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case VimEventType.PrevTabRequested:
                    PrevTabRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case VimEventType.CloseTabRequested when evt is CloseTabRequestedEvent ctre:
                    CloseTabRequested?.Invoke(this, new CloseTabRequestedEventArgs(ctre.Force));
                    break;
                case VimEventType.ViewportAlignRequested when evt is ViewportAlignRequestedEvent vare:
                    AlignViewport(vare.Align);
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

        UpdateScrollbar(Canvas.FirstVisibleLine * Canvas.LineHeight);
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

    private void AlignViewport(ViewportAlign align)
    {
        if (Canvas.LineHeight <= 0) return;

        var visible = Canvas.VisibleLines;
        var targetTopLine = align switch
        {
            ViewportAlign.Top => _engine.Cursor.Line,
            ViewportAlign.Center => _engine.Cursor.Line - (visible / 2),
            ViewportAlign.Bottom => _engine.Cursor.Line - visible + 1,
            _ => _engine.Cursor.Line
        };

        targetTopLine = Math.Clamp(targetTopLine, 0, Math.Max(0, _engine.CurrentBuffer.Text.LineCount - 1));
        Canvas.ScrollTo(targetTopLine * Canvas.LineHeight);
        Canvas.SetCursor(_engine.Cursor);
    }

    private static string? GetVimKey(Key key, bool shift)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            var offset = (int)key - (int)Key.A;
            var letter = (char)('A' + offset);
            return shift
                ? letter.ToString()
                : char.ToLowerInvariant(letter).ToString();
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            const string plain = "0123456789";
            const string shifted = ")!@#$%^&*(";
            var offset = (int)key - (int)Key.D0;
            return (shift ? shifted[offset] : plain[offset]).ToString();
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            var offset = (int)key - (int)Key.NumPad0;
            return offset.ToString();
        }

        return key switch
        {
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
            // Special
            Key.Space => " ",
            Key.Escape => "Escape",
            Key.Return => "Return",
            Key.Back => "Back",
            Key.Delete => "Delete",
            Key.Tab => "Tab",
            Key.Add => "+",
            Key.Subtract => "-",
            Key.Multiply => "*",
            Key.Divide => "/",
            Key.Decimal => ".",
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
        Key.H => "h",
        Key.J => "j",
        Key.M => "m",
        Key.OemOpenBrackets => "[",
        _ => null
    };

    private static bool ShouldPreferTextInput(VimMode mode, Key key)
    {
        if (mode is not (VimMode.Normal or VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock or
            VimMode.Command or VimMode.SearchForward or VimMode.SearchBackward))
            return false;

        if (key >= Key.A && key <= Key.Z) return true;
        if (key >= Key.D0 && key <= Key.D9) return true;
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return true;

        return key is
            Key.Space or
            Key.OemSemicolon or Key.OemQuestion or Key.OemPeriod or Key.OemComma or
            Key.OemOpenBrackets or Key.OemCloseBrackets or Key.OemPipe or Key.Oem7 or
            Key.OemTilde or Key.OemMinus or Key.OemPlus or
            Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal;
    }

    private static char NormalizeVimInputChar(char ch)
    {
        if (ch == '\u3000') return ' ';
        if (ch >= '\uFF01' && ch <= '\uFF5E')
            return (char)(ch - 0xFEE0);
        return ch;
    }
}
