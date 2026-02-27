using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
    // ─────────────── Win32 P/Invoke ───────────────

    // imm32 — cancel an active IME composition
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] private static extern bool   ImmReleaseContext(IntPtr hWnd, IntPtr hImc);
    [DllImport("imm32.dll")] private static extern bool   ImmNotifyIME(IntPtr hImc, int dwAction, int dwIndex, int dwValue);
    [DllImport("imm32.dll")] private static extern bool   ImmSetCompositionWindow(IntPtr hImc, ref COMPOSITIONFORM lpCompForm);
    [DllImport("imm32.dll")] private static extern bool   ImmSetCandidateWindow(IntPtr hImc, ref CANDIDATEFORM lpCandidateForm);

    private const int  NI_COMPOSITIONSTR      = 0x0015;
    private const int  CPS_CANCEL             = 0x0004;
    private const uint CFS_POINT              = 0x0002;
    private const uint CFS_FORCE_POSITION     = 0x0020;
    private const uint CFS_CANDIDATEPOS       = 0x0040;
    private const int  WM_IME_STARTCOMPOSITION = 0x010D;
    private const int  WM_IME_SETCONTEXT       = 0x0281;

    [StructLayout(LayoutKind.Sequential)]
    private struct COMPOSITIONFORM
    {
        public uint dwStyle;
        public POINTAPI ptCurrentPos;
        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CANDIDATEFORM
    {
        public int dwIndex;
        public uint dwStyle;
        public POINTAPI ptCurrentPos;
        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTAPI { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    // user32 — inject synthetic key events back to the IME pipeline.
    // Layout matches native INPUT / KEYBDINPUT on 64-bit Windows:
    //   offset 0  : type   (DWORD)
    //   offset 4  : padding
    //   offset 8  : wVk    (WORD)  ─┐ KEYBDINPUT
    //   offset 10 : wScan  (WORD)   │
    //   offset 12 : dwFlags(DWORD)  │
    //   offset 16 : time   (DWORD)  │
    //   [offset 24: dwExtraInfo – stays 0, covered by Size = 40]
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT_SEND
    {
        [FieldOffset(0)]  public uint   type;
        [FieldOffset(8)]  public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint   dwFlags;
        [FieldOffset(16)] public uint   time;
    }

    private const uint INPUTTYPE_KEYBOARD = 1;
    private const uint KBDEVENTF_KEYUP    = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT_SEND[] pInputs, int cbSize);

    // ─────────────── Fields ───────────────

    private VimEngine _engine;
    private EditorTheme _theme = EditorTheme.Dracula;
    private bool _keyDownHandledByVim;
    private bool _isDragSelecting = false;

    // Buffer that tracks ImeProcessed key characters typed in Insert mode.
    // Used to detect imap sequences (e.g. "jj" → <Esc>) even when IME is ON.
    private readonly List<string> _imeInsertBuffer = [];

    // Set to true while we are replaying buffered keys back to the IME so
    // that the replayed PreviewKeyDown events bypass imap interception.
    private bool _replayingImeKeys = false;
    private int  _replayCount      = 0;
    private bool _imeWindowUpdateInProgress = false;

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
        Unloaded += OnUnloaded;
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
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnPreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnPreviewTextInputUpdate);
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            hwndSource.AddHook(ImeWndProc);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        TextCompositionManager.RemovePreviewTextInputStartHandler(this, OnPreviewTextInputStart);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(this, OnPreviewTextInputUpdate);
        ClearImeCompositionOverlay();
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            hwndSource.RemoveHook(ImeWndProc);
    }

    /// <summary>
    /// Win32 message hook: intercepts WM_IME_STARTCOMPOSITION / WM_IME_SETCONTEXT
    /// so the IME composition and candidate windows appear at the cursor position.
    /// </summary>
    private IntPtr ImeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_engine.Mode is not (VimMode.Insert or VimMode.Replace))
            return IntPtr.Zero;

        if (msg is WM_IME_SETCONTEXT or WM_IME_STARTCOMPOSITION)
        {
            UpdateImeWindowPos(hwnd);
        }

        return IntPtr.Zero;
    }

    private static void SetCandidateWindowPos(IntPtr imc, int x, int y)
    {
        for (int i = 0; i < 4; i++)
        {
            var candForm = new CANDIDATEFORM
            {
                dwIndex = i,
                dwStyle = CFS_CANDIDATEPOS,
                ptCurrentPos = new POINTAPI { x = x, y = y }
            };
            ImmSetCandidateWindow(imc, ref candForm);
        }
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

    private void OnPreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        UpdateImeCompositionOverlay(e);
    }

    private void OnPreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        UpdateImeCompositionOverlay(e);
    }

    private void UpdateImeCompositionOverlay(TextCompositionEventArgs e)
    {
        if (_engine.Mode is not (VimMode.Insert or VimMode.Replace))
        {
            ClearImeCompositionOverlay();
            return;
        }

        var composition = e.TextComposition;
        var text = composition?.CompositionText;
        if (string.IsNullOrEmpty(text))
            text = composition?.SystemCompositionText;

        Canvas.SetImeCompositionText(text ?? string.Empty);
        if (!string.IsNullOrEmpty(text))
            UpdateImeWindowPos();
    }

    private void ClearImeCompositionOverlay()
    {
        Canvas.SetImeCompositionText(string.Empty);
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
            // ── IME insert-mode mapping detection ──────────────────────────────
            // With IME ON every key comes as Key.ImeProcessed.  We need to
            // intercept the keys that are prefixes of configured imap sequences
            // (e.g. the first "j" of "jj → <Esc>") before the IME can consume
            // them.  When the sequence is complete we fire it through VimEngine.
            //
            // When a key breaks a pending sequence we replay the buffered keys +
            // the current key back into the input queue via SendInput so the IME
            // can compose them correctly (e.g. "j"→buffer, "a"→breaks sequence →
            // replay "ja" to IME → IME produces "じゃ").
            //
            //   PREFIX only  → buffer, intercept (e.Handled = true).
            //   EXACT match  → fire mapping through VimEngine.
            //   NO MATCH     → replay buffered + current to IME (if ASCII letters/
            //                  digits), or flush as literal text (fallback), then
            //                  check if the current key starts a new sequence.
            if (e.Key == Key.ImeProcessed)
            {
                // Replayed keys: skip imap detection and let the IME process them.
                if (_replayingImeKeys)
                {
                    if (--_replayCount <= 0)
                        _replayingImeKeys = false;
                    return;
                }

                var mods = e.KeyboardDevice.Modifiers;
                if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0)
                {
                    var keyStr = GetVimKey(actualKey, false);
                    if (keyStr != null && keyStr.Length == 1)
                    {
                        var maps = _engine.Config.InsertMaps;

                        // Build what the sequence *would be* if we accept this key.
                        var tentative = new List<string>(_imeInsertBuffer) { keyStr };
                        var sequence  = string.Concat(tentative);

                        bool hasExact  = maps.ContainsKey(sequence);
                        bool hasLonger = maps.Keys.Any(k => k.Length > sequence.Length
                                                          && k.StartsWith(sequence, StringComparison.Ordinal));

                        if (hasExact && !hasLonger)
                        {
                            // Unambiguous exact match — fire the mapping.
                            CancelImeComposition(); // safety net (usually a no-op)
                            _imeInsertBuffer.Clear();
                            ClearImeCompositionOverlay();
                            e.Handled = true;
                            foreach (var ch in sequence)
                                ProcessKey(ch.ToString(), false, false, false);
                            return;
                        }

                        if (hasExact || hasLonger)
                        {
                            // This key extends a potential mapping — hold it away
                            // from the IME so no kana composition can start.
                            _imeInsertBuffer.Add(keyStr);
                            ClearImeCompositionOverlay();
                            e.Handled = true;
                            return;
                        }

                        // The current key breaks any possible mapping.
                        // If the buffer is non-empty, replay buffer + current key
                        // back to the IME (so "ja" → "じゃ" etc.).
                        if (_imeInsertBuffer.Count > 0)
                        {
                            bool canReplay =
                                _imeInsertBuffer.All(k => k.Length == 1
                                                       && char.IsAsciiLetterOrDigit(k[0]))
                                && char.IsAsciiLetterOrDigit(keyStr[0]);

                            if (canReplay)
                            {
                                // Replay buffered keys + current key to IME.
                                // Cancel the current key event (it is included in the replay).
                                ReplayImeKeySequence([.. _imeInsertBuffer, keyStr]);
                                ClearImeCompositionOverlay();
                                e.Handled = true;
                                return;
                            }

                            // Fallback: insert buffered keys as literal text
                            // (bypasses VimEngine mapping so no stale buffer state).
                            FlushImeInsertBuffer();
                            // Current key: fall through to the "new sequence?" check below.
                        }

                        // Check whether the current key alone starts a new sequence.
                        bool newExact  = maps.ContainsKey(keyStr);
                        bool newLonger = maps.Keys.Any(k => k.StartsWith(keyStr, StringComparison.Ordinal)
                                                          && k.Length > keyStr.Length);
                        if (newExact && !newLonger)
                        {
                            // Single-key exact match with nothing longer — fire immediately.
                            ClearImeCompositionOverlay();
                            e.Handled = true;
                            ProcessKey(keyStr, false, false, false);
                            return;
                        }
                        if (newExact || newLonger)
                        {
                            _imeInsertBuffer.Add(keyStr);
                            ClearImeCompositionOverlay();
                            e.Handled = true;
                        }
                        // else: no mapping — let IME handle the current key normally.
                    }
                    else
                    {
                        // Key can't be mapped (null or multi-char) — flush any buffer.
                        FlushImeInsertBuffer();
                    }
                }
                else
                {
                    // Modifier held — flush any buffer.
                    FlushImeInsertBuffer();
                }
                // Do not fall through to the ImeProcessed/Normal-mode block below.
                return;
            }

            // When e.key is NOT ImeProcessed, a physical (non-IME) key was pressed;
            // reset the IME sequence buffer.
            _imeInsertBuffer.Clear();
            ClearImeCompositionOverlay();

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
            ClearImeCompositionOverlay();
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
        // IME committed a composition — the key sequence is now finalised by the IME,
        // so any partially-tracked imap prefix is no longer valid.
        _imeInsertBuffer.Clear();
        ClearImeCompositionOverlay();

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

    /// <summary>
    /// Flushes any keys held in the IME insert buffer as <em>literal</em> text
    /// to VimEngine, bypassing the imap machinery.  This avoids polluting
    /// VimEngine's internal mapping buffer with a stale partial sequence.
    /// </summary>
    private void FlushImeInsertBuffer()
    {
        foreach (var ch in _imeInsertBuffer)
        {
            Canvas.ResetCursorBlink();
            var events = _engine.ProcessKeyLiteral(ch);
            ProcessVimEvents(events);
        }
        _imeInsertBuffer.Clear();
    }

    /// <summary>
    /// Injects <paramref name="keys"/> back into the Windows input queue via
    /// SendInput so the IME can compose them correctly (e.g. ["j","a"] → "じゃ").
    /// Sets <see cref="_replayingImeKeys"/> so that when the replayed
    /// PreviewKeyDown events arrive they skip imap interception.
    /// </summary>
    private void ReplayImeKeySequence(IReadOnlyList<string> keys)
    {
        var inputs = new List<INPUT_SEND>();
        int replayable = 0;

        foreach (var k in keys)
        {
            if (k.Length != 1) continue;
            char c = k[0];
            ushort vk;
            if      (char.IsAsciiLetter(c))  vk = (ushort)char.ToUpperInvariant(c);
            else if (char.IsAsciiDigit(c))   vk = (ushort)c;
            else continue;

            inputs.Add(new INPUT_SEND { type = INPUTTYPE_KEYBOARD, wVk = vk });
            inputs.Add(new INPUT_SEND { type = INPUTTYPE_KEYBOARD, wVk = vk, dwFlags = KBDEVENTF_KEYUP });
            replayable++;
        }

        if (replayable == 0) return;

        _imeInsertBuffer.Clear();
        _replayingImeKeys = true;
        _replayCount      = replayable;
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT_SEND>());
    }

    /// <summary>
    /// Moves the IME composition and candidate windows to the current cursor position.
    /// Called both from the Win32 hook (WM_IME_STARTCOMPOSITION / WM_IME_SETCONTEXT)
    /// and proactively when entering Insert mode or moving the cursor.
    /// </summary>
    private void UpdateImeWindowPos(IntPtr hwnd = default)
    {
        if (_imeWindowUpdateInProgress) return;
        _imeWindowUpdateInProgress = true;

        IntPtr handle = IntPtr.Zero;
        IntPtr imc = IntPtr.Zero;
        try
        {
            if (PresentationSource.FromVisual(this) is not HwndSource source) return;
            if (source.CompositionTarget == null || source.RootVisual == null) return;

            handle = hwnd == default ? source.Handle : hwnd;
            imc = ImmGetContext(handle);
            if (imc == IntPtr.Zero) return;

            // Canvas-local cursor position (DIPs, top of cursor line)
            var canvasPt = Canvas.GetCursorPixelPosition();

            // Transform to HWND client-area coordinates (DIPs)
            var transform = Canvas.TransformToAncestor(source.RootVisual);
            var clientDip = transform.Transform(canvasPt);
            var canvasTopLeftDip = transform.Transform(new Point(0, 0));
            var canvasBottomRightDip = transform.Transform(new Point(Canvas.RenderSize.Width, Canvas.RenderSize.Height));

            // Convert DIPs → physical pixels (HWND client coordinates)
            var toDevice = source.CompositionTarget.TransformToDevice;
            var physPt = toDevice.Transform(clientDip);
            var canvasTopLeftPx = toDevice.Transform(canvasTopLeftDip);
            var canvasBottomRightPx = toDevice.Transform(canvasBottomRightDip);
            var lineH = (int)(Canvas.LineHeight * toDevice.M22);

            int px = (int)physPt.X;
            int py = (int)physPt.Y;
            int minX = (int)Math.Min(canvasTopLeftPx.X, canvasBottomRightPx.X);
            int maxX = (int)Math.Max(canvasTopLeftPx.X, canvasBottomRightPx.X) - 1;
            int minY = (int)Math.Min(canvasTopLeftPx.Y, canvasBottomRightPx.Y);
            int maxY = (int)Math.Max(canvasTopLeftPx.Y, canvasBottomRightPx.Y);
            if (maxX < minX) maxX = minX;
            if (maxY < minY) maxY = minY;
            int maxCompY = Math.Max(minY, maxY - Math.Max(1, lineH));

            px = Math.Clamp(px, minX, maxX);
            py = Math.Clamp(py, minY, maxCompY);

            var compForm = new COMPOSITIONFORM
            {
                dwStyle      = CFS_POINT | CFS_FORCE_POSITION,
                ptCurrentPos = new POINTAPI { x = px, y = py }
            };
            ImmSetCompositionWindow(imc, ref compForm);

            int candidateY = py + Math.Max(1, lineH);
            if (candidateY > maxCompY)
                candidateY = Math.Max(minY, py - Math.Max(1, lineH));
            candidateY = Math.Clamp(candidateY, minY, maxCompY);

            SetCandidateWindowPos(imc, px, candidateY);
        }
        catch
        {
            // IME window positioning failures must not crash the editor.
        }
        finally
        {
            if (imc != IntPtr.Zero && handle != IntPtr.Zero)
                ImmReleaseContext(handle, imc);
            _imeWindowUpdateInProgress = false;
        }
    }

    /// <summary>
    /// Cancels any active IME composition without committing its text.
    /// Called when an imap sequence is detected so that the partially-composed
    /// kana in the composition window is discarded before we replay the raw
    /// keystroke sequence through VimEngine.
    /// </summary>
    private void CancelImeComposition()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        var imc = ImmGetContext(source.Handle);
        if (imc == IntPtr.Zero) return;
        ImmNotifyIME(imc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
        ImmReleaseContext(source.Handle, imc);
        ClearImeCompositionOverlay();
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
                        if (_engine.Mode is VimMode.Insert or VimMode.Replace)
                            UpdateImeWindowPos();
                    }
                    break;
                case VimEventType.ModeChanged when evt is ModeChangedEvent me:
                    if (me.Mode is not (VimMode.Insert or VimMode.Replace))
                    {
                        _imeInsertBuffer.Clear();
                        _replayingImeKeys = false;
                        _replayCount = 0;
                        ClearImeCompositionOverlay();
                    }
                    else
                    {
                        UpdateImeWindowPos();
                    }
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
