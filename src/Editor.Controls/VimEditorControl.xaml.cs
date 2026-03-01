using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Editor.Controls.Lsp;
using Editor.Controls.Themes;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Controls;

public class SaveRequestedEventArgs(string? filePath) : EventArgs
{
    public string? FilePath { get; } = filePath;
}
public class QuitRequestedEventArgs(bool force) : EventArgs
{
    public bool Force { get; } = force;
}
public class OpenFileRequestedEventArgs(string filePath, int line = 0, int column = 0) : EventArgs
{
    public string FilePath { get; } = filePath;
    public int Line { get; } = line;
    public int Column { get; } = column;
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
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern uint ImmGetCandidateListW(IntPtr hImc, uint deIndex, IntPtr lpCandList, uint dwBufLen);
    [DllImport("msctf.dll")] private static extern int TF_GetThreadMgr(out IntPtr pptim);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool   CreateCaret(IntPtr hWnd, IntPtr hBitmap, int nWidth, int nHeight);
    [DllImport("user32.dll")] private static extern bool   DestroyCaret();
    [DllImport("user32.dll")] private static extern bool   SetCaretPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool   HideCaret(IntPtr hWnd);

    private const int  NI_COMPOSITIONSTR      = 0x0015;
    private const int  CPS_CANCEL             = 0x0004;
    private const uint CFS_POINT              = 0x0002;
    private const uint CFS_FORCE_POSITION     = 0x0020;
    private const uint CFS_CANDIDATEPOS       = 0x0040;
    private const int  WM_IME_STARTCOMPOSITION = 0x010D;
    private const int  WM_IME_SETCONTEXT       = 0x0281;
    private const int  WM_IME_NOTIFY           = 0x0282;
    private const int  WM_IME_REQUEST          = 0x0288;
    private const int  IMR_COMPOSITIONWINDOW   = 0x0001;
    private const int  IMR_CANDIDATEWINDOW     = 0x0002;
    private const int  IME_OFFSCREEN_COORD     = -32000;
    private const uint ISC_SHOWUICOMPOSITIONWINDOW  = 0x80000000u;
    private const uint ISC_SHOWUIGUIDELINE          = 0x40000000u;
    private const uint ISC_SHOWUIALLCANDIDATEWINDOW = 0x0000000Fu;
    private const uint ISC_SHOWUISOFTKBD            = 0x00000080u;
    private const int  IMN_CHANGECANDIDATE     = 0x0003;
    private const int  IMN_CLOSECANDIDATE      = 0x0004;
    private const int  IMN_OPENCANDIDATE       = 0x0005;
    private const int  IMN_SETCANDIDATEPOS     = 0x0009;
    private const int  IMN_SETCOMPOSITIONWINDOW = 0x000B;

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
    private const uint TF_INVALID_COOKIE = 0xFFFFFFFFu;
    private static readonly Guid IID_ITfUIElementMgr = new("EA1EA135-19DF-11D7-A6D2-00065B84435C");
    private static readonly Guid IID_ITfUIElementSink = new("EA1EA136-19DF-11D7-A6D2-00065B84435C");
    private static readonly Guid IID_ITfSource = new("4EA48A35-60AE-446F-8FD6-E6A8D82459F7");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT_SEND[] pInputs, int cbSize);

    // ─────────────── Fields ───────────────

    private VimEngine _engine;
    private EditorTheme _theme = EditorTheme.Dracula;
    private bool _keyDownHandledByVim;
    private bool _isDragSelecting = false;
    private LspManager _lspManager = null!;
    private readonly System.Windows.Threading.DispatcherTimer _completionDebounce;

    // Buffer that tracks ImeProcessed key characters typed in Insert mode.
    // Used to detect imap sequences (e.g. "jj" → <Esc>) even when IME is ON.
    private readonly List<string> _imeInsertBuffer = [];

    // Set to true while we are replaying buffered keys back to the IME so
    // that the replayed PreviewKeyDown events bypass imap interception.
    private bool _replayingImeKeys = false;
    private int  _replayCount      = 0;
    private bool _imeWindowUpdateInProgress = false;
    private bool _imeSuppressionCaretCreated = false;
    private ITfSource? _tsfSource;
    private ITfUIElementSink? _tsfUiElementSink;
    private uint _tsfUiElementSinkCookie = TF_INVALID_COOKIE;

    public event EventHandler<SaveRequestedEventArgs>? SaveRequested;
    public event EventHandler<QuitRequestedEventArgs>? QuitRequested;
    public event EventHandler<OpenFileRequestedEventArgs>? OpenFileRequested;
    public event EventHandler<NewTabRequestedEventArgs>? NewTabRequested;
    public event EventHandler<SplitRequestedEventArgs>? SplitRequested;
    public event EventHandler? NextTabRequested;
    public event EventHandler? PrevTabRequested;
    public event EventHandler<CloseTabRequestedEventArgs>? CloseTabRequested;
    public event EventHandler<ModeChangedEventArgs>? ModeChanged;
    public event EventHandler? BufferChanged;

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

        _lspManager = new LspManager(Dispatcher);
        _lspManager.StateChanged += OnLspStateChanged;
        _lspManager.StatusMessage += OnLspStatusMessage;

        _completionDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _completionDebounce.Tick += (_, _) =>
        {
            _completionDebounce.Stop();
            if (_engine.Mode != VimMode.Insert || _lspManager.CompletionVisible) return;
            var cur = _engine.Cursor;
            _ = TriggerCompletionAsync(cur.Line, cur.Column);
        };

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

    private void OnLspStateChanged()
    {
        Canvas.SetDiagnostics(_lspManager.CurrentDiagnostics);
        Canvas.SetCompletionItems(_lspManager.CompletionItems, _lspManager.CompletionSelection, _lspManager.CompletionScrollOffset);
        Canvas.SetSignatureHelp(_lspManager.CurrentSignatureHelp);
    }

    private void OnLspStatusMessage(string msg)
    {
        StatusBar.UpdateStatus(msg);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        UpdateAll();
        TryAttachTsfUiElementSink();
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnPreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnPreviewTextInputUpdate);
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            hwndSource.AddHook(ImeWndProc);
        // LSP: notify for files already loaded before Loaded fired (e.g. command-line arg).
        // Guard against double-open: LoadFile already called OnFileOpened if the
        // file was opened after the control was constructed.
        var fp = _engine.CurrentBuffer.FilePath;
        if (fp != null && _lspManager.CurrentUri == null)
            _lspManager.OnFileOpened(fp, _engine.CurrentBuffer.Text.GetText());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        TextCompositionManager.RemovePreviewTextInputStartHandler(this, OnPreviewTextInputStart);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(this, OnPreviewTextInputUpdate);
        ClearImeCompositionOverlay();
        ClearImeCandidateOverlay();
        DestroyImeSuppressionCaret();
        DetachTsfUiElementSink();
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            hwndSource.RemoveHook(ImeWndProc);
        _completionDebounce.Stop();
        _lspManager.Dispose();
    }

    /// <summary>
    /// Win32 message hook: intercepts WM_IME_STARTCOMPOSITION / WM_IME_SETCONTEXT
    /// and disables the native IME UI because composition is rendered in-editor.
    /// </summary>
    private IntPtr ImeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_IME_SETCONTEXT && wParam != IntPtr.Zero)
        {
            UpdateImeWindowPos(hwnd);
            handled = true;
            return DefWindowProc(hwnd, msg, wParam, FilterImeContextFlags(lParam));
        }

        if (!ShouldSuppressNativeImeUi(_engine.Mode))
            return IntPtr.Zero;

        if (msg == WM_IME_STARTCOMPOSITION)
        {
            UpdateImeWindowPos(hwnd);
        }
        else if (msg == WM_IME_REQUEST)
        {
            int request = wParam.ToInt32();
            if (request == IMR_COMPOSITIONWINDOW && lParam != IntPtr.Zero)
            {
                var compForm = new COMPOSITIONFORM
                {
                    dwStyle = CFS_POINT | CFS_FORCE_POSITION,
                    ptCurrentPos = OffscreenPoint(),
                    rcArea = new RECT
                    {
                        left = IME_OFFSCREEN_COORD,
                        top = IME_OFFSCREEN_COORD,
                        right = IME_OFFSCREEN_COORD + 1,
                        bottom = IME_OFFSCREEN_COORD + 1
                    }
                };
                Marshal.StructureToPtr(compForm, lParam, false);
                handled = true;
                return new IntPtr(1);
            }

            if (request == IMR_CANDIDATEWINDOW && lParam != IntPtr.Zero)
            {
                var candForm = Marshal.PtrToStructure<CANDIDATEFORM>(lParam);
                var offscreen = OffscreenPoint();
                candForm.dwStyle = CFS_CANDIDATEPOS;
                candForm.ptCurrentPos = offscreen;
                candForm.rcArea = new RECT
                {
                    left = offscreen.x,
                    top = offscreen.y,
                    right = offscreen.x + 1,
                    bottom = offscreen.y + 1
                };
                Marshal.StructureToPtr(candForm, lParam, false);
                handled = true;
                return new IntPtr(1);
            }
        }
        else if (msg == WM_IME_NOTIFY)
        {
            int notify = wParam.ToInt32();
            if (notify is IMN_OPENCANDIDATE or IMN_CHANGECANDIDATE
                or IMN_SETCANDIDATEPOS or IMN_SETCOMPOSITIONWINDOW)
            {
                UpdateImeCandidateOverlay(hwnd, lParam);
                UpdateImeWindowPos(hwnd);
                handled = true;
                return IntPtr.Zero;
            }
            if (notify == IMN_CLOSECANDIDATE)
            {
                ClearImeCandidateOverlay();
                handled = true;
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private void TryAttachTsfUiElementSink()
    {
        if (_tsfSource != null && _tsfUiElementSinkCookie != TF_INVALID_COOKIE)
            return;

        IntPtr threadMgrPtr = IntPtr.Zero;
        IntPtr uiElementMgrPtr = IntPtr.Zero;
        IntPtr sourcePtr = IntPtr.Zero;

        try
        {
            if (TF_GetThreadMgr(out threadMgrPtr) < 0 || threadMgrPtr == IntPtr.Zero)
                return;

            var uiElementMgrIid = IID_ITfUIElementMgr;
            if (Marshal.QueryInterface(threadMgrPtr, in uiElementMgrIid, out uiElementMgrPtr) < 0 || uiElementMgrPtr == IntPtr.Zero)
                return;

            var sourceIid = IID_ITfSource;
            if (Marshal.QueryInterface(uiElementMgrPtr, in sourceIid, out sourcePtr) < 0 || sourcePtr == IntPtr.Zero)
                return;

            _tsfSource = (ITfSource)Marshal.GetObjectForIUnknown(sourcePtr);
            _tsfUiElementSink = new TsfUiElementSink(this);

            var sinkIid = IID_ITfUIElementSink;
            if (_tsfSource.AdviseSink(ref sinkIid, _tsfUiElementSink, out _tsfUiElementSinkCookie) < 0)
            {
                _tsfUiElementSinkCookie = TF_INVALID_COOKIE;
                _tsfUiElementSink = null;
                if (Marshal.IsComObject(_tsfSource))
                    Marshal.ReleaseComObject(_tsfSource);
                _tsfSource = null;
            }
        }
        catch
        {
            _tsfUiElementSinkCookie = TF_INVALID_COOKIE;
            _tsfUiElementSink = null;
            if (_tsfSource != null && Marshal.IsComObject(_tsfSource))
                Marshal.ReleaseComObject(_tsfSource);
            _tsfSource = null;
        }
        finally
        {
            if (sourcePtr != IntPtr.Zero)
                Marshal.Release(sourcePtr);
            if (uiElementMgrPtr != IntPtr.Zero)
                Marshal.Release(uiElementMgrPtr);
            if (threadMgrPtr != IntPtr.Zero)
                Marshal.Release(threadMgrPtr);
        }
    }

    private void DetachTsfUiElementSink()
    {
        try
        {
            if (_tsfSource != null && _tsfUiElementSinkCookie != TF_INVALID_COOKIE)
                _ = _tsfSource.UnadviseSink(_tsfUiElementSinkCookie);
        }
        catch
        {
            // Sink detach failures should not affect editor shutdown.
        }
        finally
        {
            _tsfUiElementSinkCookie = TF_INVALID_COOKIE;
            _tsfUiElementSink = null;
            if (_tsfSource != null && Marshal.IsComObject(_tsfSource))
                Marshal.ReleaseComObject(_tsfSource);
            _tsfSource = null;
        }
    }

    private static IntPtr FilterImeContextFlags(IntPtr lParam)
    {
        uint flags = unchecked((uint)lParam.ToInt64());
        flags &= ~(ISC_SHOWUICOMPOSITIONWINDOW
            | ISC_SHOWUIGUIDELINE
            | ISC_SHOWUIALLCANDIDATEWINDOW
            | ISC_SHOWUISOFTKBD);
        return new IntPtr(unchecked((int)flags));
    }

    private static bool ShouldSuppressNativeImeUi(VimMode mode)
        => mode is VimMode.Normal
            or VimMode.Insert
            or VimMode.Visual
            or VimMode.VisualLine
            or VimMode.VisualBlock
            or VimMode.Command
            or VimMode.Replace
            or VimMode.SearchForward
            or VimMode.SearchBackward;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("4EA48A35-60AE-446F-8FD6-E6A8D82459F7")]
    private interface ITfSource
    {
        [PreserveSig]
        int AdviseSink(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);

        [PreserveSig]
        int UnadviseSink(uint dwCookie);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("EA1EA136-19DF-11D7-A6D2-00065B84435C")]
    private interface ITfUIElementSink
    {
        [PreserveSig]
        int BeginUIElement(uint dwUIElementId, [MarshalAs(UnmanagedType.Bool)] out bool pbShow);

        [PreserveSig]
        int UpdateUIElement(uint dwUIElementId);

        [PreserveSig]
        int EndUIElement(uint dwUIElementId);
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class TsfUiElementSink(VimEditorControl owner) : ITfUIElementSink
    {
        private readonly WeakReference<VimEditorControl> _owner = new(owner);

        public int BeginUIElement(uint dwUIElementId, out bool pbShow)
        {
            pbShow = true;
            if (!_owner.TryGetTarget(out var owner))
                return 0;

            if (ShouldSuppressNativeImeUi(owner._engine.Mode))
            {
                pbShow = false;
                owner.UpdateImeWindowPos();
            }
            return 0;
        }

        public int UpdateUIElement(uint dwUIElementId)
        {
            if (_owner.TryGetTarget(out var owner) && ShouldSuppressNativeImeUi(owner._engine.Mode))
                owner.UpdateImeWindowPos();
            return 0;
        }

        public int EndUIElement(uint dwUIElementId)
        {
            if (_owner.TryGetTarget(out var owner))
                owner.ClearImeCandidateOverlay();
            return 0;
        }
    }

    private void UpdateImeCandidateOverlay(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr imc = IntPtr.Zero;
        try
        {
            imc = ImmGetContext(hwnd);
            if (imc == IntPtr.Zero)
            {
                ClearImeCandidateOverlay();
                return;
            }

            uint index = ResolveCandidateListIndex(lParam);
            var candidates = ReadCandidateListWithFallback(imc, index, out int selected);
            if (candidates.Count == 0)
            {
                ClearImeCandidateOverlay();
                return;
            }

            Canvas.SetImeCandidates(candidates, selected);
        }
        catch
        {
            ClearImeCandidateOverlay();
        }
        finally
        {
            if (imc != IntPtr.Zero)
                ImmReleaseContext(hwnd, imc);
        }
    }

    private static List<string> ReadCandidateListWithFallback(IntPtr imc, uint preferredIndex, out int selection)
    {
        var best = ReadCandidateList(imc, preferredIndex, out selection);
        if (best.Count > 0) return best;

        for (uint i = 0; i < 4; i++)
        {
            if (i == preferredIndex) continue;
            var candidates = ReadCandidateList(imc, i, out int idx);
            if (candidates.Count == 0) continue;
            selection = idx;
            return candidates;
        }

        selection = -1;
        return [];
    }

    private void ClearImeCandidateOverlay() => Canvas.SetImeCandidates([], -1);

    private static uint ResolveCandidateListIndex(IntPtr lParam)
    {
        uint mask = unchecked((uint)lParam.ToInt64());
        if (mask == 0) return 0;

        for (uint i = 0; i < 32; i++)
        {
            if ((mask & (1u << (int)i)) != 0)
                return i;
        }
        return 0;
    }

    private static List<string> ReadCandidateList(IntPtr imc, uint index, out int selection)
    {
        selection = -1;
        uint size = ImmGetCandidateListW(imc, index, IntPtr.Zero, 0);
        if (size < 24) return [];

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint written = ImmGetCandidateListW(imc, index, buffer, size);
            if (written < 24) return [];

            int rawCount = Marshal.ReadInt32(buffer, 8);
            int rawSelection = Marshal.ReadInt32(buffer, 12);
            int maxOffsets = Math.Max(0, ((int)written - 24) / 4);
            int count = Math.Clamp(rawCount, 0, maxOffsets);

            var result = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int offset = Marshal.ReadInt32(buffer, 24 + (i * 4));
                if (offset <= 0 || offset >= written) continue;
                string? text = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset));
                if (!string.IsNullOrEmpty(text))
                    result.Add(text);
            }

            if (result.Count == 0) return result;
            selection = Math.Clamp(rawSelection, 0, result.Count - 1);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void EnsureImeSuppressionCaret(IntPtr hwnd)
    {
        if (!_imeSuppressionCaretCreated)
        {
            if (CreateCaret(hwnd, IntPtr.Zero, 1, 1))
                _imeSuppressionCaretCreated = true;
        }

        if (_imeSuppressionCaretCreated)
        {
            HideCaret(hwnd);
            SetCaretPos(IME_OFFSCREEN_COORD, IME_OFFSCREEN_COORD);
        }
    }

    private void DestroyImeSuppressionCaret()
    {
        if (!_imeSuppressionCaretCreated) return;
        DestroyCaret();
        _imeSuppressionCaretCreated = false;
    }

    private static POINTAPI OffscreenPoint() => new() { x = IME_OFFSCREEN_COORD, y = IME_OFFSCREEN_COORD };

    private static void HideCandidateWindows(IntPtr imc)
    {
        var offscreen = OffscreenPoint();
        for (int i = 0; i < 4; i++)
        {
            var candForm = new CANDIDATEFORM
            {
                dwIndex = i,
                dwStyle = CFS_CANDIDATEPOS,
                ptCurrentPos = offscreen
            };
            ImmSetCandidateWindow(imc, ref candForm);
        }
    }

    public void LoadFile(string path)
    {
        _engine.LoadFile(path);
        UpdateAll();
        _lspManager.OnFileOpened(path, _engine.CurrentBuffer.Text.GetText());
    }

    public void NavigateTo(int line, int column)
    {
        var events = _engine.SetCursorPosition(new CursorPosition(line, column));
        ProcessVimEvents(events);
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
        UpdateImeWindowPos();
        if (!string.IsNullOrEmpty(text))
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                UpdateImeCandidateOverlay(source.Handle, IntPtr.Zero);
        }
        else
        {
            ClearImeCandidateOverlay();
        }
    }

    private void ClearImeCompositionOverlay()
    {
        Canvas.SetImeCompositionText(string.Empty);
        ClearImeCandidateOverlay();
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
                    // Let OnKeyDown handle Tab when completion popup is visible.
                    if (_lspManager.CompletionVisible) return;
                    ProcessKey("Tab", false, false, false);
                    e.Handled = true;
                }
                else if (actualKey == Key.Return)
                {
                    // Let OnKeyDown handle Enter when completion popup is visible.
                    if (_lspManager.CompletionVisible) return;
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

        // LSP: Ctrl+Space triggers completion in Insert mode
        if (ctrl && key == Key.Space && mode == VimMode.Insert)
        {
            var cursor = _engine.Cursor;
            _ = TriggerCompletionAsync(cursor.Line, cursor.Column);
            e.Handled = true;
            return;
        }

        // LSP: completion popup navigation
        if (_lspManager.CompletionVisible && mode == VimMode.Insert)
        {
            if (key == Key.Down || (ctrl && key == Key.N))
            {
                _lspManager.MoveCompletionSelection(1);
                e.Handled = true;
                return;
            }
            if (key == Key.Up || (ctrl && key == Key.P))
            {
                _lspManager.MoveCompletionSelection(-1);
                e.Handled = true;
                return;
            }
            if (key == Key.Tab || key == Key.Return)
            {
                InsertLspCompletion();
                e.Handled = true;
                _keyDownHandledByVim = true;
                return;
            }
            if (key == Key.Escape)
            {
                _lspManager.HideCompletion();
                _lspManager.HideSignatureHelp();
                // Also exit insert mode
                ProcessKey("Escape", false, false, false);
                e.Handled = true;
                return;
            }
        }

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

        // LSP: K in Normal mode shows hover info
        if (key == "K" && !ctrl && !shift && !alt && _engine.Mode == VimMode.Normal)
        {
            _ = ShowLspHoverAsync();
            return;
        }

        bool hadCompletion = _lspManager.CompletionVisible;

        var events = _engine.ProcessKey(key, ctrl, shift, alt);
        ProcessVimEvents(events);

        // LSP: notify text changes
        if (events.Any(e => e.Type == VimEventType.TextChanged))
            _lspManager.OnTextChanged(_engine.CurrentBuffer.Text.GetText());

        // LSP: update completion popup after each Insert-mode keypress
        if (_engine.Mode == VimMode.Insert && !ctrl && !alt && key.Length == 1)
        {
            char ch = key[0];
            if (hadCompletion)
            {
                // Popup already visible — re-filter or close
                var cursor = _engine.Cursor;
                var bufLine = _engine.CurrentBuffer.Text.GetLine(cursor.Line);
                int col = cursor.Column;
                int wordStart = col;
                while (wordStart > 0 && (char.IsLetterOrDigit(bufLine[wordStart - 1]) || bufLine[wordStart - 1] == '_'))
                    wordStart--;

                if (ch == '.')
                {
                    // Dot = new member-access context: fresh completion
                    _lspManager.HideCompletion();
                    _completionDebounce.Stop();
                    var cur = _engine.Cursor;
                    _ = TriggerCompletionAsync(cur.Line, cur.Column);
                }
                else
                {
                    _lspManager.FilterCompletion(bufLine[wordStart..col]);
                }
            }
            else
            {
                // Popup not visible — schedule auto-trigger
                if (ch == '.')
                {
                    _completionDebounce.Stop();
                    var cur = _engine.Cursor;
                    _ = TriggerCompletionAsync(cur.Line, cur.Column);
                }
                else if (char.IsLetter(ch) || ch == '_')
                {
                    _completionDebounce.Stop();
                    _completionDebounce.Start();
                }
                else
                {
                    _completionDebounce.Stop();
                }
            }

            // Signature help triggers: ( and ,
            if (ch == '(' || ch == ',')
            {
                var cur = _engine.Cursor;
                _ = _lspManager.RequestSignatureHelpAsync(cur.Line, cur.Column);
            }
            else if (ch == ')')
            {
                _lspManager.HideSignatureHelp();
            }
        }
        else if (_engine.Mode != VimMode.Insert)
        {
            if (hadCompletion)
            {
                _lspManager.HideCompletion();
                _completionDebounce.Stop();
            }
            _lspManager.HideSignatureHelp();
        }
        else if (!ctrl && !alt && (key == "Back" || key == "Delete"))
        {
            // Backspace/Delete in insert with popup: re-filter
            if (hadCompletion && _engine.Mode == VimMode.Insert)
            {
                var cursor = _engine.Cursor;
                var bufLine = _engine.CurrentBuffer.Text.GetLine(cursor.Line);
                int col = cursor.Column;
                int wordStart = col;
                while (wordStart > 0 && (char.IsLetterOrDigit(bufLine[wordStart - 1]) || bufLine[wordStart - 1] == '_'))
                    wordStart--;
                _lspManager.FilterCompletion(bufLine[wordStart..col]);
            }
        }
    }

    private async Task TriggerCompletionAsync(int line, int col)
    {
        var msg = await _lspManager.RequestCompletionAsync(line, col);
        if (!string.IsNullOrEmpty(msg))
        {
            StatusBar.UpdateStatus(msg);
            return;
        }

        // Apply filter for any prefix already typed at (or since) the trigger position.
        // Use the current cursor position, not the trigger position, since the user
        // may have typed more while the async request was in flight.
        if (_lspManager.CompletionVisible)
        {
            var cursor = _engine.Cursor;
            var bufLine = _engine.CurrentBuffer.Text.GetLine(cursor.Line);
            int c = cursor.Column;
            int wordStart = c;
            while (wordStart > 0 && (char.IsLetterOrDigit(bufLine[wordStart - 1]) || bufLine[wordStart - 1] == '_'))
                wordStart--;
            _lspManager.FilterCompletion(bufLine[wordStart..c]);
        }
    }

    private async Task ShowLspHoverAsync()
    {
        var cursor = _engine.Cursor;
        var text = await _lspManager.RequestHoverAsync(cursor.Line, cursor.Column);
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Show first non-empty line of hover in status bar
            var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
            StatusBar.UpdateStatus(firstLine.Trim());
        }
    }

    private void InsertLspCompletion()
    {
        var item = _lspManager.GetSelectedCompletion();
        if (item == null) return;

        _lspManager.HideCompletion();

        // Delete partial word before cursor and insert completion
        var cursor = _engine.Cursor;
        var line = _engine.CurrentBuffer.Text.GetLine(cursor.Line);
        int col = cursor.Column;

        // Find start of current word (walk back over word chars)
        int wordStart = col;
        while (wordStart > 0 && (char.IsLetterOrDigit(line[wordStart - 1]) || line[wordStart - 1] == '_'))
            wordStart--;

        // Delete the partial prefix with Backspace keys, then insert completion
        int deleteCount = col - wordStart;
        for (int i = 0; i < deleteCount; i++)
            ProcessKey("Back", false, false, false);

        var insertText = item.InsertText ?? item.Label;
        foreach (var ch in insertText)
            ProcessKey(ch.ToString(), false, false, false);
    }

    private async Task HandleGoToDefinitionAsync()
    {
        var cursor = _engine.Cursor;
        var result = await _lspManager.RequestDefinitionAsync(cursor.Line, cursor.Column);
        if (result == null)
        {
            StatusBar.UpdateStatus("LSP: definition not found");
            return;
        }
        var (filePath, line, col) = result.Value;
        if (!System.IO.File.Exists(filePath))
        {
            StatusBar.UpdateStatus("LSP: definition in non-navigable location");
            return;
        }
        // Same file: just move the cursor without reopening
        if (string.Equals(filePath, _engine.CurrentBuffer.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            var events = _engine.SetCursorPosition(new CursorPosition(line, col));
            ProcessVimEvents(events);
            return;
        }
        OpenFileRequested?.Invoke(this, new OpenFileRequestedEventArgs(filePath, line, col));
    }

    private async Task HandleFormatDocumentAsync()
    {
        var tabSize = _engine.Options.TabStop;
        var insertSpaces = _engine.Options.ExpandTab;
        var edits = await _lspManager.RequestFormattingAsync(tabSize, insertSpaces);
        if (edits.Count == 0)
        {
            StatusBar.UpdateStatus("Format: no changes");
            return;
        }
        var original = _engine.CurrentBuffer.Text.GetText();
        var formatted = ApplyTextEdits(original, edits);
        _engine.SetText(formatted);
        UpdateAll();
        _lspManager.OnTextChanged(formatted);
        StatusBar.UpdateStatus("Format: document formatted");
    }

    private static string ApplyTextEdits(string originalText, IReadOnlyList<Editor.Core.Lsp.LspTextEdit> edits)
    {
        if (edits.Count == 0) return originalText;
        var lines = originalText.Split('\n').ToList();
        var sorted = edits
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Character)
            .ToList();

        foreach (var edit in sorted)
        {
            int sl = Math.Min(edit.Range.Start.Line, lines.Count - 1);
            int sc = Math.Min(edit.Range.Start.Character, lines[sl].Length);
            int el = Math.Min(edit.Range.End.Line, lines.Count - 1);
            int ec = Math.Min(edit.Range.End.Character, lines[el].Length);
            var newText = edit.NewText.Replace("\r\n", "\n").Replace("\r", "\n");

            if (sl == el)
            {
                lines[sl] = lines[sl][..sc] + newText + lines[sl][ec..];
            }
            else
            {
                var merged = lines[sl][..sc] + newText + lines[el][ec..];
                var newLines = merged.Split('\n').ToList();
                lines.RemoveRange(sl, el - sl + 1);
                lines.InsertRange(sl, newLines);
            }
        }
        return string.Join('\n', lines);
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
    /// Hides native IME windows (composition/candidate) because composition
    /// text is rendered directly inside the editor canvas.
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

            handle = hwnd == default ? source.Handle : hwnd;
            imc = ImmGetContext(handle);
            if (imc == IntPtr.Zero) return;

            EnsureImeSuppressionCaret(handle);
            var offscreen = OffscreenPoint();

            var compForm = new COMPOSITIONFORM
            {
                dwStyle      = CFS_POINT | CFS_FORCE_POSITION,
                ptCurrentPos = offscreen,
                rcArea = new RECT
                {
                    left = offscreen.x,
                    top = offscreen.y,
                    right = offscreen.x + 1,
                    bottom = offscreen.y + 1
                }
            };
            ImmSetCompositionWindow(imc, ref compForm);
            HideCandidateWindows(imc);
        }
        catch
        {
            // IME window suppression failures must not crash the editor.
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
                    BufferChanged?.Invoke(this, EventArgs.Empty);
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
                case VimEventType.GoToDefinitionRequested:
                    _ = HandleGoToDefinitionAsync();
                    break;
                case VimEventType.FormatDocumentRequested:
                    _ = HandleFormatDocumentAsync();
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
