using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.IO;
using System.Linq;
using Editor.Controls.Formatting;
using Editor.Controls.Git;
using Editor.Controls.Lsp;
using Editor.Controls.Themes;
using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Folds;
using Editor.Core.Formatting;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Snippets;
using Editor.Core.Extensibility;

namespace Editor.Controls;

public class SaveRequestedEventArgs(string? filePath, bool isVirtual = false, string? documentId = null) : EventArgs
{
    public string? FilePath { get; } = filePath;
    /// <summary>True when the saved buffer is a virtual document (see <see cref="VimEditorControl.OpenVirtualDocument"/>).</summary>
    public bool IsVirtual { get; } = isVirtual;
    /// <summary>The id returned by <see cref="VimEditorControl.OpenVirtualDocument"/>, or null for file-backed buffers.</summary>
    public string? DocumentId { get; } = documentId;
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
public class SplitRequestedEventArgs(bool vertical, string? filePath = null) : EventArgs
{
    public bool Vertical { get; } = vertical;
    public string? FilePath { get; } = filePath;
}
public class WindowNavRequestedEventArgs(WindowNavDir dir) : EventArgs
{
    public WindowNavDir Dir { get; } = dir;
}
public class WindowCloseRequestedEventArgs(bool force) : EventArgs
{
    public bool Force { get; } = force;
}
public class CloseTabRequestedEventArgs(bool force) : EventArgs
{
    public bool Force { get; } = force;
}
public class ModeChangedEventArgs(VimMode mode) : EventArgs
{
    public VimMode Mode { get; } = mode;
}
public class FindReferenceItem(string filePath, int line, int col, string? preview = null)
{
    public string FilePath { get; } = filePath;
    public int Line { get; } = line;
    public int Col { get; } = col;
    public string? Preview { get; } = preview;
}
public class FindReferencesResultEventArgs(
    IReadOnlyList<FindReferenceItem> items,
    string symbolName,
    string titlePrefix = "REFERENCES") : EventArgs
{
    public IReadOnlyList<FindReferenceItem> Items { get; } = items;
    public string SymbolName { get; } = symbolName;
    public string TitlePrefix { get; } = titlePrefix;
}
public class GrepRequestedEventArgs(string pattern, string? fileGlob, bool ignoreCase) : EventArgs
{
    public string Pattern { get; } = pattern;
    public string? FileGlob { get; } = fileGlob;
    public bool IgnoreCase { get; } = ignoreCase;
}

public class ProjectReplaceRequestedEventArgs(string pattern, string replacement, string? fileGlob, bool ignoreCase) : EventArgs
{
    public string Pattern { get; } = pattern;
    public string Replacement { get; } = replacement;
    public string? FileGlob { get; } = fileGlob;
    public bool IgnoreCase { get; } = ignoreCase;
}
public class DocumentSymbolItem(string name, SymbolKind kind, int line, int col, int depth)
{
    public string Name { get; } = name;
    public SymbolKind Kind { get; } = kind;
    public int Line { get; } = line;
    public int Col { get; } = col;
    public int Depth { get; } = depth;
}
public class DocumentSymbolsResultEventArgs(IReadOnlyList<DocumentSymbolItem> items) : EventArgs
{
    public IReadOnlyList<DocumentSymbolItem> Items { get; } = items;
}
public class GitOutputRequestedEventArgs(string title, string content) : EventArgs
{
    public string Title { get; } = title;
    public string Content { get; } = content;
}
public class GitCommitRequestedEventArgs(string? filePath, string template) : EventArgs
{
    public string? FilePath { get; } = filePath;
    public string Template { get; } = template;
}
/// <summary>
/// Raised when a link is activated (Ctrl+Click on a detected URL, or <c>gx</c>).
/// Set <see cref="Handled"/> to <c>true</c> to suppress the default behavior
/// (opening the URL with the OS shell via <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>).
/// </summary>
public class LinkClickedEventArgs(string url) : EventArgs
{
    public string Url { get; } = url;
    public bool Handled { get; set; }
}

/// <summary>
/// Raised when a file-path link is activated (Ctrl+Click on a detected path).
/// <see cref="Path"/> is an absolute path; <see cref="IsDirectory"/> indicates whether it
/// points at a directory. Set <see cref="Handled"/> to <c>true</c> to suppress the default
/// behavior (opening a file in the editor via <c>OpenFileRequested</c>, or a directory in the
/// OS file explorer).
/// </summary>
public class FileLinkClickedEventArgs(string path, bool isDirectory) : EventArgs
{
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;
    public bool Handled { get; set; }
}

/// <summary>
/// Raised by <see cref="VimEditorControl.ContextMenuBuilding"/> while the right-click menu is built,
/// after the editor's own items. Hosts append their own entries to <see cref="Menu"/> (typically
/// guarded by <see cref="HasSelection"/> and acting on <see cref="SelectedText"/>).
/// </summary>
public class EditorContextMenuBuildingEventArgs(
    string selectedText, bool hasSelection, System.Windows.Controls.ContextMenu menu,
    Git.EditorBlameLine? blameLine = null) : EventArgs
{
    /// <summary>The current selection text (empty when nothing is selected).</summary>
    public string SelectedText { get; } = selectedText;

    /// <summary>Whether there is a non-empty selection.</summary>
    public bool HasSelection { get; } = hasSelection;

    /// <summary>The menu being built. Append <see cref="System.Windows.Controls.MenuItem"/>s or
    /// separators here; unstyled additions inherit the editor's dark menu style.</summary>
    public System.Windows.Controls.ContextMenu Menu { get; } = menu;

    /// <summary>
    /// When the right-click landed on the blame gutter (<c>:Gblame</c> active), the commit info for
    /// that line; otherwise <c>null</c>. Hosts can use this to offer commit-specific actions
    /// (open the diff, show the file history, …). When set, the editor omits its own text-editing
    /// items so the menu is blame-focused.
    /// </summary>
    public Git.EditorBlameLine? BlameLine { get; } = blameLine;
}

/// <summary>The kind of a text selection (mirrors the engine's selection type).</summary>
public enum SelectionKind { Character, Line, Block }

/// <summary>
/// Caret (cursor) position exposed to hosts. <see cref="Line"/> and <see cref="Column"/>
/// are 0-based; use <see cref="DisplayLine"/>/<see cref="DisplayColumn"/> for 1-based display.
/// </summary>
public sealed record CaretInfo(int Line, int Column)
{
    /// <summary>1-based line number for display.</summary>
    public int DisplayLine => Line + 1;
    /// <summary>1-based column number for display.</summary>
    public int DisplayColumn => Column + 1;
}

/// <summary>
/// A text selection exposed to hosts. Positions are 0-based and normalized so
/// (<see cref="StartLine"/>, <see cref="StartColumn"/>) is never after the end.
/// </summary>
public sealed record TextSelectionInfo(
    int StartLine, int StartColumn,
    int EndLine, int EndColumn,
    SelectionKind Kind,
    string Text);

/// <summary>Snapshot of metadata about the document currently shown in the editor.</summary>
public sealed record DocumentMeta(
    string? FilePath,
    bool IsVirtual,
    string? DocumentId,
    bool IsModified,
    int LineCount,
    string? Language,
    VimMode Mode);

public partial class VimEditorControl : UserControl, Editor.Controls.Ime.IEditorTextStoreHost, IDisposable
{
    /// <summary>Commands available to host palettes and programmatic invocation.</summary>
    public IReadOnlyList<EditorCommandDescriptor> ExtensionCommands => _engine.ExProcessor.CommandRegistry.Commands;

    /// <summary>Executes a host-registered command using the current editor cursor and raw Ex syntax.</summary>
    public ValueTask<EditorCommandResult?> ExecuteExtensionCommandAsync(string rawCommand,
        CancellationToken cancellationToken = default)
        => _engine.ExecuteExtensionCommandAsync(rawCommand, cancellationToken);
    // ─────────────── Win32 P/Invoke ───────────────

    // imm32 — inspect/cancel an active IME composition
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] private static extern bool   ImmReleaseContext(IntPtr hWnd, IntPtr hImc);
    [DllImport("imm32.dll")] private static extern bool   ImmNotifyIME(IntPtr hImc, int dwAction, int dwIndex, int dwValue);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(IntPtr hImc, int dwIndex, IntPtr lpBuf, int dwBufLen);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern bool ImmSetCompositionStringW(IntPtr hImc, int dwIndex, string? lpComp, int dwCompLen, string? lpRead, int dwReadLen);
    [DllImport("imm32.dll")] private static extern bool   ImmSetCompositionWindow(IntPtr hImc, ref COMPOSITIONFORM lpCompForm);
    [DllImport("imm32.dll")] private static extern bool   ImmSetCandidateWindow(IntPtr hImc, ref CANDIDATEFORM lpCandidateForm);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern uint ImmGetCandidateListW(IntPtr hImc, uint deIndex, IntPtr lpCandList, uint dwBufLen);
    [DllImport("msctf.dll")] private static extern int TF_GetThreadMgr(out IntPtr pptim);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool   CreateCaret(IntPtr hWnd, IntPtr hBitmap, int nWidth, int nHeight);
    [DllImport("user32.dll")] private static extern bool   DestroyCaret();
    [DllImport("user32.dll")] private static extern bool   SetCaretPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool   HideCaret(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint   MapVirtualKey(uint uCode, uint uMapType);

    private const int  NI_COMPOSITIONSTR      = 0x0015;
    private const int  CPS_CANCEL             = 0x0004;
    private const int  GCS_COMPSTR            = 0x0008;
    private const int  GCS_CURSORPOS          = 0x0080;
    private const int  SCS_SETSTR             = 0x0009; // GCS_COMPREADSTR | GCS_COMPSTR
    private const uint CFS_POINT              = 0x0002;
    private const uint CFS_FORCE_POSITION     = 0x0020;
    private const uint CFS_CANDIDATEPOS       = 0x0040;
    private const int  WM_SETFOCUS             = 0x0007;
    private const int  WM_IME_STARTCOMPOSITION = 0x010D;
    private const int  WM_IME_COMPOSITION       = 0x010F;
    private const int  WM_IME_ENDCOMPOSITION   = 0x010E;
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
    private static readonly Guid IID_ITfTextEditSink = new("8127D409-CCD3-4683-967A-B43D5B482BF7");
    private const uint TF_DEFAULT_SELECTION = 0xFFFFFFFFu;
    // Target-clause (注目文節) reading: GUID_PROP_ATTRIBUTE carries per-range display
    // attribute atoms; resolve them via the category + display-attribute managers.
    private static readonly Guid GUID_PROP_ATTRIBUTE = new("34B45670-7526-11D2-A147-00105A2799B5");
    private static readonly Guid CLSID_TF_CategoryMgr = new("A4B544A1-438D-4B41-9325-869523E2D6C7");
    private static readonly Guid CLSID_TF_DisplayAttributeMgr = new("3CE74DE4-53D3-4D74-8B83-431B3828BA53");
    // TF_ATTR_TARGET_CONVERTED = 1, TF_ATTR_TARGET_NOTCONVERTED = 3 mark the focused clause.
    private const int TF_ATTR_TARGET_CONVERTED = 1;
    private const int TF_ATTR_TARGET_NOTCONVERTED = 3;
    private const int TF_ANCHOR_END = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT_SEND[] pInputs, int cbSize);

    // ─────────────── Fields ───────────────

    private VimEngine _engine;
    private EditorTheme _theme = EditorTheme.Dracula;
    private bool _keyDownHandledByVim;
    private bool _isDragSelecting = false;
    private CursorPosition _dragAnchor;
    private readonly IEditorLspManager _lspManager;
    private readonly IEditorGitService _gitProvider;
    private bool _blameActive;
    private string? _gitBranchName;
    private string? _gitBranchFilePath;
    private string? _saveStartedFilePath;
    private VimStatusBar? _sharedStatusBar;
    private VimStatusBar ActiveStatusBar => _sharedStatusBar ?? StatusBar;
    private readonly System.Windows.Threading.DispatcherTimer _completionDebounce;
    // Fires after 'timeoutlen' when a multi-key mapping (e.g. `jj`) is half-typed
    // so the dangling prefix is flushed as literal text instead of hanging forever.
    private readonly System.Windows.Threading.DispatcherTimer _mappingTimeout;
    // ── Insert-mode filesystem path completion (LSP-independent) ──────────────
    private readonly PathCompletionManager _pathCompletionManager;
    // ── Popup key navigation (Down/Up move, Tab/Return apply, Escape hide) ─────
    private readonly PopupKeyNavigator _pathCompletionNavigator;
    private readonly PopupKeyNavigator _completionNavigator;
    private readonly PopupKeyNavigator _codeActionNavigator;
    private VimBuffer? _cachedLinesBuffer;
    private long _cachedLinesVersion = -1;
    private string[] _cachedLines = [];
    private IReadOnlyList<Selection> _lspSelectionRangeSelections = [];
    private int _lspSelectionRangeIndex = -1;
    private CursorPosition? _lspSelectionRangeOrigin;

    // ─── Breadcrumb fallback (non-LSP) symbol cache, keyed by buffer + text version ───
    private IReadOnlyList<DocumentSymbol> _fallbackSymbols = [];
    private long _fallbackSymbolsVersion = -1;
    private VimBuffer? _fallbackSymbolsBuffer;
    // Identity of the segments currently rendered, to skip rebuilding the bar when unchanged.
    private string _lastBreadcrumbKey = "\0";

    // ─── Clipboard image → Markdown link paste ───
    private readonly ImagePasteHandler _imagePasteHandler;

    /// <summary>
    /// Rules for pasting a clipboard image into a Markdown file (destination directory +
    /// file-name templates, see <see cref="Editor.Core.Editing.ImagePasteOptions"/>). Mutate
    /// in place to reconfigure at runtime; seeded from
    /// <see cref="VimEditorControlOptions.ImagePasteOptions"/>.
    /// </summary>
    public Editor.Core.Editing.ImagePasteOptions ImagePasteOptions
    {
        get => _imagePasteHandler.Options;
        set => _imagePasteHandler.Options = value ?? new Editor.Core.Editing.ImagePasteOptions();
    }

    // ─── Multi-cursor ───
    private readonly MultiCursorManager _multiCursorManager;

    // ─── Snippet tab-stop state ───
    private readonly SnippetTabStopManager _snippetTabStopManager;

    // ─── File Watcher ───
    private FileSystemWatcher? _fileWatcher;
    private volatile bool _suppressFileWatcher;
    private bool _pendingWatcherReload;
    private string? _lastSavedFilePath;
    private DateTime _lastSavedWriteTimeUtc;

    // Buffer that tracks ImeProcessed key characters typed in Insert mode.
    // Used to detect imap sequences (e.g. "jj" → <Esc>) even when IME is ON.
    private readonly List<string> _imeInsertBuffer = [];

    // Set to true while we are replaying buffered keys back to the IME so
    // that the replayed PreviewKeyDown events bypass imap interception.
    private bool _replayingImeKeys = false;
    private int  _replayCount      = 0;
    private bool _exitInsertAfterImeCommit = false;
    private string _lastImeCompositionText = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _imeOverlayClearTimer;
    // Bumped on every WPF text-composition update/commit. Used to detect the case
    // where Backspace empties the composition: WPF raises no update event for the
    // final 1→0 char deletion, so the in-editor overlay would otherwise linger.
    private int _imeCompositionSeq = 0;
    private bool _imeWindowUpdateInProgress = false;
    private bool _imeSuppressionCaretCreated = false;

    // ─── Custom TSF text store (ITextStoreACP) ───
    // When active, the editor is a real TSF application: the IME writes its composition
    // and display attributes into our own store (Editor.Controls.Ime.EditorTextStore),
    // which we read to render composition in-editor and commit through the engine.
    private Editor.Controls.Ime.EditorTextStore? _customTextStore;
    private Editor.Controls.Ime.ITfThreadMgrTs? _tsfStoreThreadMgr;
    private Editor.Controls.Ime.ITfDocumentMgrTs? _tsfStoreDocMgr;
    private Editor.Controls.Ime.ITfDocumentMgrTs? _tsfStorePrevDocMgr;
    private Editor.Controls.Ime.ITfContextTs? _tsfStoreContext;
    private Editor.Controls.Ime.ITfSourceTs? _tsfStoreContextSource;
    private uint _tsfStoreCompositionCookie = TF_INVALID_COOKIE;
    private bool _customTextStoreActive;

    private ITfSource? _tsfSource;
    private ITfUIElementSink? _tsfUiElementSink;
    private uint _tsfUiElementSinkCookie = TF_INVALID_COOKIE;

    // TSF text-edit sink: tracks the composition caret within TSF-based IMEs (modern
    // Microsoft IME), which don't populate the legacy IMM composition string. OnEndEdit
    // fires for every edit — including arrow-key caret moves inside the composition —
    // and hands us a read-only cookie, so no foreign synchronous lock is needed.
    private ITfContext? _tsfContext;
    private ITfSource? _tsfContextSource;
    private ITfTextEditSink? _tsfTextEditSink;
    private uint _tsfTextEditCookie = TF_INVALID_COOKIE;
    // Cached for the control lifetime; used to resolve target-clause display attributes.
    private ITfCategoryMgr? _tsfCategoryMgr;
    private ITfDisplayAttributeMgr? _tsfDisplayAttrMgr;

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
    public event EventHandler<FindReferencesResultEventArgs>? FindReferencesResult;
    public event EventHandler<DocumentSymbolsResultEventArgs>? DocumentSymbolsResult;
    public event EventHandler? QuickfixOpenRequested;
    public event EventHandler? QuickfixCloseRequested;
    public event EventHandler<int>? QuickfixNextRequested;
    public event EventHandler<int>? QuickfixPrevRequested;
    public event EventHandler<int>? QuickfixGotoRequested;
    public event EventHandler? LocationListOpenRequested;
    public event EventHandler? LocationListCloseRequested;
    public event EventHandler<int>? LocationListNextRequested;
    public event EventHandler<int>? LocationListPrevRequested;
    public event EventHandler<int>? LocationListGotoRequested;
    public event EventHandler<GrepRequestedEventArgs>? GrepRequested;
    public event EventHandler<ProjectReplaceRequestedEventArgs>? ProjectReplaceRequested;
    public event EventHandler<string>? QuickfixReplaceRequested;
    public event EventHandler<GitOutputRequestedEventArgs>? GitOutputRequested;
    public event EventHandler<GitCommitRequestedEventArgs>? GitCommitRequested;
    /// <summary>blame 左カラム（:Gblame 表示中）の行がクリックされた。ホストはコミット差分の表示等に使う。</summary>
    public event EventHandler<Git.BlameCommitClickedEventArgs>? BlameCommitClicked;
    public event EventHandler<LinkClickedEventArgs>? LinkClicked;
    public event EventHandler<FileLinkClickedEventArgs>? FileLinkClicked;
    public event EventHandler<WindowNavRequestedEventArgs>? WindowNavRequested;
    public event EventHandler<WindowCloseRequestedEventArgs>? WindowCloseRequested;
    public event EventHandler<string>? MkSessionRequested;
    public event EventHandler<string>? SourceRequested;
    public event EventHandler<string?>? TerminalRequested;
    public event EventHandler<TerminalCommandRequestedEvent>? TerminalCommandRequested;
    public event EventHandler? MarkdownPreviewRequested;
    public event EventHandler? ViewportScrolled;
    /// <summary>Raised whenever the caret moves, carrying the new caret position.</summary>
    public event EventHandler<CaretInfo>? CaretMoved;
    /// <summary>Raised whenever the selection changes; the argument is null when the selection is cleared.</summary>
    public event EventHandler<TextSelectionInfo?>? SelectionChanged;
    /// <summary>
    /// Raised while the right-click context menu is being built, after the editor's own items have
    /// been added. The host can append custom <see cref="MenuItem"/>s (and separators) to
    /// <see cref="EditorContextMenuBuildingEventArgs.Menu"/>; appended items that have no explicit
    /// <c>Style</c> get the editor's dark menu style applied automatically so they blend in.
    /// A fresh menu is built on every right-click, so handlers don't need to remove prior additions.
    /// </summary>
    public event EventHandler<EditorContextMenuBuildingEventArgs>? ContextMenuBuilding;

    public VimMode CurrentMode => _engine.Mode;

    /// <summary>
    /// Enables or disables Vim modal editing. When set to <c>false</c> the control
    /// behaves like an ordinary text editor: keys insert text, Escape does nothing,
    /// and there is no Normal/Visual mode. Defaults to <c>true</c>.
    /// </summary>
    public bool VimEnabled
    {
        get => _engine.VimEnabled;
        set
        {
            if (!value)
            {
                // Entering plain mode: tear down any open LSP sub-mode UI so a popup
                // left over from Vim editing can't linger and hijack keys.
                _lspManager.HideCompletion();
                _lspManager.HideSignatureHelp();
                _completionDebounce.Stop();
            }
            ProcessVimEvents(_engine.SetVimEnabled(value));
        }
    }

    /// <summary>
    /// When <c>true</c>, hides all editor chrome — the line-number gutter (and fold/git
    /// column), the status bar, the scrollbar, and the minimap — so the control looks like
    /// a plain <c>TextBox</c>. This is purely visual: Vim modal editing still works (use
    /// <see cref="VimEnabled"/> to turn that off). Defaults to <c>false</c>.
    /// </summary>
    public bool MinimalChrome
    {
        get => _minimalChrome;
        set
        {
            if (_minimalChrome == value) return;
            _minimalChrome = value;
            // Status bar follows the preset, but stays collapsed if a shared bar is in use.
            StatusBar.Visibility = (value || _sharedStatusBar != null)
                ? Visibility.Collapsed : Visibility.Visible;
            UpdateAll();
        }
    }
    private bool _minimalChrome;

    public string Text => _engine.CurrentBuffer.Text.GetText();
    public string? FilePath => _engine.CurrentBuffer.FilePath;
    /// <summary>True when the current buffer has unsaved changes.</summary>
    public bool IsModified => _engine.CurrentBuffer.Text.IsModified;
    /// <summary>True when the current buffer is a virtual (file-less) document.</summary>
    public bool IsVirtualDocument => _engine.CurrentBuffer.IsVirtual;

    /// <summary>Current caret position (0-based line/column).</summary>
    public CaretInfo Caret
    {
        get { var c = _engine.Cursor; return new CaretInfo(c.Line, c.Column); }
    }

    /// <summary>True when there is an active (non-empty) visual-mode selection.</summary>
    public bool HasSelection => _engine.Selection is { } s && !s.IsEmpty;

    /// <summary>The current selection, or null when nothing is selected.</summary>
    public TextSelectionInfo? Selection => BuildSelectionInfo();

    /// <summary>The currently selected text, or an empty string when nothing is selected.</summary>
    public string SelectedText => _engine.GetSelectionText();

    /// <summary>A metadata snapshot for the document currently shown in the editor.</summary>
    public DocumentMeta DocumentInfo
    {
        get
        {
            var buf = _engine.CurrentBuffer;
            return new DocumentMeta(
                buf.FilePath,
                buf.IsVirtual,
                buf.DocumentId,
                buf.Text.IsModified,
                buf.Text.LineCount,
                _engine.Syntax.LanguageName,
                _engine.Mode);
        }
    }

    private TextSelectionInfo? BuildSelectionInfo()
    {
        if (_engine.Selection is not { } sel || sel.IsEmpty) return null;
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;
        var kind = sel.Type switch
        {
            SelectionType.Line => SelectionKind.Line,
            SelectionType.Block => SelectionKind.Block,
            _ => SelectionKind.Character,
        };
        return new TextSelectionInfo(
            start.Line, start.Column, end.Line, end.Column, kind, _engine.GetSelectionText());
    }

    public VimEngine Engine => _engine;
    /// <summary>LSP-only diagnostics. Prefer <see cref="EffectiveDiagnostics"/> for host integration.</summary>
    [Obsolete("This property exposes Editor.Core LSP types and excludes host diagnostics. Use EffectiveDiagnostics.")]
    public IReadOnlyList<LspDiagnostic> CurrentDiagnostics => _lspManager.CurrentDiagnostics;
    public double VerticalScrollRatio
    {
        get
        {
            double maxOffset = Math.Max(0, Canvas.TotalContentHeight - Canvas.ViewportHeight);
            return maxOffset <= 0 ? 0 : Canvas.VerticalOffset / maxOffset;
        }
    }

    private static int _warmedUp;

    /// <summary>
    /// Primes the JIT for the most expensive part of constructing a <see cref="VimEditorControl"/>:
    /// the <c>.vimrc</c> parsing path (<see cref="VimConfig.LoadDefault"/>), which JIT-compiles a
    /// large amount of map/key-notation parsing code (~280&#160;ms one-time) the first time it runs.
    /// Because the JIT is process-wide, doing this early — ideally on a background thread while the
    /// host window initializes — means the first real editor construction hits the already-warm path
    /// (~1&#160;ms) instead of blocking the UI thread.
    /// </summary>
    /// <remarks>
    /// Idempotent and thread-safe; the work runs at most once per process. Call it as early as
    /// possible (e.g. from <c>Application.OnStartup</c>). The returned <see cref="Task"/> completes
    /// when warm-up finishes; awaiting it is optional — fire-and-forget is the intended usage.
    /// </remarks>
    public static Task WarmUpAsync()
    {
        if (Interlocked.Exchange(ref _warmedUp, 1) != 0)
            return Task.CompletedTask;

        return Task.Run(static () =>
        {
            try
            {
                // Touch the heavy parsing path and engine construction so their methods are JITted
                // off the UI thread. The instances are discarded — this only primes the JIT.
                var config = VimConfig.LoadDefault();
                _ = new VimEngine(config);
            }
            catch { /* warm-up only; ignore failures */ }
        });
    }

    public VimEditorControl()
        : this(null)
    {
    }

    public VimEditorControl(VimEditorControlOptions? options)
    {
        InitializeComponent();

        options ??= new VimEditorControlOptions();

        var config = options.ConfigFactory?.Invoke() ?? VimConfig.LoadDefault();
        _engine = new VimEngine(config, options.SyntaxLanguages, options.Commands, options.CommandServices);
        _engine.VerticalColumnResolver = Canvas.ResolveVerticalColumn;
        _engine.SetClipboardProvider(options.ClipboardProviderFactory?.Invoke() ?? new WpfClipboardProvider());
        _imagePasteHandler = new ImagePasteHandler { Options = options.ImagePasteOptions ?? new Editor.Core.Editing.ImagePasteOptions() };
        Canvas.WrapLines = _engine.Options.Wrap;
        _multiCursorManager = new MultiCursorManager(_engine, Canvas, msg => ActiveStatusBar.UpdateStatus(msg), UpdateAll);
        _snippetTabStopManager = new SnippetTabStopManager(_engine, ProcessKey, ClearSelectionRangeState, ProcessVimEvents, UpdateAll);

        _gitProvider = options.GitServiceFactory?.Invoke() ?? NullEditorGitService.Instance;
        _lspManager = options.LspManagerFactory?.Invoke(Dispatcher) ?? new NullLspManager();
        _pathCompletionManager = new PathCompletionManager(_engine, Canvas, _lspManager, ProcessKey);
        _pathCompletionNavigator = new PopupKeyNavigator(
            move: d => _pathCompletionManager.MoveSelection(d),
            apply: () => { _pathCompletionManager.Insert(); _keyDownHandledByVim = true; },
            hide: () => { _pathCompletionManager.Hide(); _keyDownHandledByVim = true; });
        _completionNavigator = new PopupKeyNavigator(
            move: d => _lspManager.MoveCompletionSelection(d),
            apply: () => { InsertLspCompletion(); _keyDownHandledByVim = true; },
            hide: () =>
            {
                _lspManager.HideCompletion();
                _lspManager.HideSignatureHelp();
                // Also exit insert mode
                ProcessKey("Escape", false, false, false);
            });
        _codeActionNavigator = new PopupKeyNavigator(
            move: d => _lspManager.MoveCodeActionsSelection(d),
            apply: () =>
            {
                var acts = _lspManager.CurrentCodeActions;
                int sel = _lspManager.CodeActionsSelection;
                if (sel >= 0 && sel < acts.Count) ApplyCodeAction(acts[sel]);
            },
            hide: () => _lspManager.HideCodeActions(),
            acceptCtrlNav: false, acceptJK: true, acceptTab: false);
        _lspManager.StateChanged += OnLspStateChanged;
        _lspManager.StatusMessage += OnLspStatusMessage;
        _lspManager.FoldingRangesChanged += OnLspFoldingRangesChanged;
        _lspManager.BreadcrumbChanged += OnLspBreadcrumbChanged;
        _lspManager.InlayHintsChanged += OnLspInlayHintsChanged;
        _lspManager.SemanticTokensChanged += OnLspSemanticTokensChanged;
        _lspManager.DocumentHighlightsChanged += OnLspDocumentHighlightsChanged;

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

        _mappingTimeout = new System.Windows.Threading.DispatcherTimer();
        _mappingTimeout.Tick += (_, _) => FlushMappingTimeout();

        Focusable = true;
        KeyDown += OnKeyDown;
        TextInput += OnTextInput;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;

        // Active-pane cursor and status bar
        GotKeyboardFocus  += (_, _) => { Canvas.IsActive = true;  SyncStatusBar(); FocusCustomTextStore(); };
        LostKeyboardFocus += (_, _) => Canvas.IsActive = false;

        // Mouse and scroll wiring
        Canvas.MouseClicked += OnCanvasMouseClicked;
        Canvas.MouseRightClicked += OnCanvasMouseRightClicked;
        Canvas.MouseDragging += OnCanvasMouseDragging;
        Canvas.MouseDragEnded += OnCanvasMouseDragEnded;
        Canvas.FoldGutterClicked += OnFoldGutterClicked;
        Canvas.BlameClicked += (line, blame) =>
            BlameCommitClicked?.Invoke(this, new Git.BlameCommitClickedEventArgs(line, blame));
        Canvas.BreakpointToggled += OnCanvasBreakpointToggled;
        Canvas.DataTipHoverChanged += OnCanvasDataTipHoverChanged;
        Canvas.DataTipHoverEnded += OnCanvasDataTipHoverEnded;
        Canvas.LinkClicked += OnCanvasLinkClicked;
        Canvas.FileLinkClicked += OnCanvasFileLinkClicked;

        // Keep VimEngine informed of viewport state for H/M/L motions
        Canvas.ScrollChanged += (_, _) =>
        {
            SyncViewportState();
            UpdateViewportDecorations();
            HideDataTip();
            NotifyImeLayoutChanged();
            ViewportScrolled?.Invoke(this, EventArgs.Empty);
        };
        Canvas.SizeChanged += (_, _) =>
        {
            SyncViewportState();
            UpdateViewportDecorations();
            NotifyImeLayoutChanged();
        };

        ApplyTheme();
    }

    private void OnLspStateChanged()
    {
        RefreshCombinedDiagnostics();
        if (!_pathCompletionManager.Visible)
            Canvas.SetCompletionItems(_lspManager.CompletionItems, _lspManager.CompletionSelection, _lspManager.CompletionScrollOffset);
        Canvas.SetSignatureHelp(_lspManager.CurrentSignatureHelp);
        Canvas.SetCodeActions(_lspManager.CurrentCodeActions, _lspManager.CodeActionsSelection, _lspManager.CodeActionsScrollOffset);
        // Document symbols arrive asynchronously after a file opens; refresh the breadcrumb
        // bar so it populates without waiting for the next cursor move.
        if (_engine.Options.Breadcrumb)
            RefreshBreadcrumbBar();
    }

    private void OnLspStatusMessage(string msg)
    {
        ActiveStatusBar.UpdateStatus(msg);
    }

    public void ShowStatusMessage(string message)
    {
        ActiveStatusBar.UpdateStatus(message);
    }

    private void OnLspBreadcrumbChanged(string breadcrumb)
    {
        // The breadcrumb is rendered as a clickable bar above the editor. The string
        // payload only tells us the path changed; we re-query the segments (with jump
        // positions) for the current cursor and rebuild the bar.
        RefreshBreadcrumbBar();
    }

    /// <summary>Rebuilds the breadcrumb bar from the symbol path at the current cursor.
    /// Collapses the bar when the feature is off or no symbol contains the cursor.</summary>
    private void RefreshBreadcrumbBar()
    {
        if (!_engine.Options.Breadcrumb)
        {
            BreadcrumbBar.Visibility = Visibility.Collapsed;
            _lastBreadcrumbKey = "\0"; // force a rebuild when re-enabled
            return;
        }

        var cur = _engine.Cursor;
        var segments = _lspManager.GetBreadcrumbSegments(cur.Line, cur.Column);
        if (segments.Count == 0)
        {
            // No LSP symbols (server absent or still loading): fall back to a heuristic
            // extractor so the breadcrumb works without a language server.
            segments = Editor.Core.Lsp.BreadcrumbBuilder.GetSegments(GetFallbackSymbols(), cur.Line, cur.Column);
        }
        BuildBreadcrumbBar(segments);
    }

    /// <summary>Heuristic document symbols for the breadcrumb fallback, recomputed only when the
    /// buffer text version changes (re-parsing the whole file on every cursor move would be wasteful).</summary>
    private IReadOnlyList<DocumentSymbol> GetFallbackSymbols()
    {
        var buf = _engine.CurrentBuffer;
        long v = buf.Text.Version;
        if (!ReferenceEquals(_fallbackSymbolsBuffer, buf) || _fallbackSymbolsVersion != v)
        {
            _fallbackSymbolsBuffer = buf;
            _fallbackSymbolsVersion = v;
            _fallbackSymbols = Editor.Core.Navigation.DocumentSymbolExtractor.Extract(GetCachedLines(buf), buf.FilePath);
        }
        return _fallbackSymbols;
    }

    private void BuildBreadcrumbBar(IReadOnlyList<BreadcrumbSegment> segments)
    {
        // Skip the rebuild when the rendered path is unchanged (cursor moving within the
        // same symbol fires this on every move).
        var key = segments.Count == 0
            ? ""
            : string.Join("|", segments.Select(s => $"{s.Name}{s.Line}{s.Column}"));
        if (key == _lastBreadcrumbKey) return;
        _lastBreadcrumbKey = key;

        BreadcrumbPanel.Children.Clear();

        if (segments.Count == 0)
        {
            BreadcrumbBar.Visibility = Visibility.Collapsed;
            return;
        }

        var mono = new System.Windows.Media.FontFamily("Consolas");

        // Symbol path only (no leading file-name label — the outermost symbol is usually
        // named like the file, which otherwise reads as "file › file").
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0) BreadcrumbPanel.Children.Add(MakeBreadcrumbSeparator(mono));
            BreadcrumbPanel.Children.Add(MakeBreadcrumbButton(segments[i], mono));
        }

        BreadcrumbBar.Visibility = Visibility.Visible;
    }

    private TextBlock MakeBreadcrumbSeparator(System.Windows.Media.FontFamily mono) => new()
    {
        Text = "›",
        FontFamily = mono,
        FontSize = 12,
        Foreground = _theme.LineNumberFg,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 2, 0)
    };

    // A clickable label (plain TextBlock — no Button chrome) for one breadcrumb segment.
    private TextBlock MakeBreadcrumbButton(BreadcrumbSegment seg, System.Windows.Media.FontFamily mono)
    {
        var tb = new TextBlock
        {
            Text = seg.Name,
            Tag = seg,
            FontFamily = mono,
            FontSize = 12,
            Foreground = _theme.Foreground,
            Padding = new Thickness(3, 0, 3, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = seg.Kind.ToString()
        };
        tb.MouseLeftButtonUp += BreadcrumbSegment_Click;
        // Subtle hover affordance.
        tb.MouseEnter += (_, _) => tb.Foreground = _theme.LinkColor;
        tb.MouseLeave += (_, _) => tb.Foreground = _theme.Foreground;
        return tb;
    }

    private void BreadcrumbSegment_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BreadcrumbSegment seg })
        {
            NavigateTo(seg.Line, seg.Column);
            Canvas.Focus();
        }
    }

    private void OnLspInlayHintsChanged(IReadOnlyList<InlayHint> hints)
    {
        Canvas.SetInlayHints(hints);
    }

    private void OnLspSemanticTokensChanged(SemanticToken[] tokens)
    {
        Canvas.SetSemanticTokens(tokens);
    }

    private void OnLspDocumentHighlightsChanged(IReadOnlyList<DocumentHighlight>? highlights)
    {
        Canvas.SetDocumentHighlights(highlights);
    }

    private void OnLspFoldingRangesChanged(IReadOnlyList<LspFoldingRange> ranges)
    {
        var method = _engine.Options.FoldMethod;
        if (method is "indent" or "marker")
        {
            // indent/marker モードでは LSP フォールドを無視して専用検出器を使用
            ApplyFoldMethod();
        }
        else if (ranges.Count > 0)
        {
            // LSP がフォールド範囲を返した → そのまま使用
            _engine.LoadFoldRanges(ranges.Select(r => (r.StartLine, r.EndLine)));
        }
        else
        {
            // LSP が空リストを返した（非対応 or まだ未解析）→ シンタックスベースの検出にフォールバック
            ApplySyntaxFolds();
        }
        UpdateAll();
    }

    private void ApplySyntaxFolds()
    {
        var fp = _engine.CurrentBuffer.FilePath;
        if (fp == null) return;
        var ext = Path.GetExtension(fp);
        var lines = _engine.CurrentBuffer.Text.Snapshot();
        var ranges = SyntaxFoldDetector.Detect(ext, lines);
        _engine.LoadFoldRanges(ranges);
    }

    // Applies folds according to the current foldmethod option.
    // Called when the option changes and when a file is opened (after LSP decides).
    private void ApplyFoldMethod()
    {
        var method = _engine.Options.FoldMethod;
        var lines  = _engine.CurrentBuffer.Text.Snapshot();
        switch (method)
        {
            case "indent":
                _engine.LoadFoldRanges(IndentFoldDetector.Detect(lines, _engine.Options.TabStop));
                break;
            case "marker":
                _engine.LoadFoldRanges(MarkerFoldDetector.Detect(lines));
                break;
            case "syntax":
                ApplySyntaxFolds();
                break;
            // "manual", "expr", "diff": no auto-detection
        }
    }

    private void ApplyInlayHintsOption()
    {
        _lspManager.SetInlayHintsEnabled(_engine.Options.InlayHints);
    }

    private void ApplySemanticTokensOption()
    {
        _lspManager.SetSemanticTokensEnabled(_engine.Options.SemanticTokens);
    }

    private void ApplyBreadcrumbOption()
    {
        if (!_engine.Options.Breadcrumb)
        {
            _lspManager.ClearBreadcrumb();
            BreadcrumbBar.Visibility = Visibility.Collapsed;
            _lastBreadcrumbKey = "\0"; // force a rebuild when re-enabled
        }
        else
        {
            // Immediately populate the bar for the current cursor position when enabled.
            var cur = _engine.Cursor;
            _lspManager.UpdateBreadcrumb(cur.Line, cur.Column);
            RefreshBreadcrumbBar();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        UpdateAll();
        TryAttachTsfUiElementSink();

        // Install our own TSF text store so the IME composes directly into us. On any
        // failure, fall back to WPF's default IME path (the handlers registered below).
        bool customStore = InitializeCustomTextStore();

        if (!customStore)
        {
            TextCompositionManager.AddPreviewTextInputStartHandler(this, OnPreviewTextInputStart);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnPreviewTextInputUpdate);
        }
        AttachImeWindowHook();
        // LSP: notify for files already loaded before Loaded fired (e.g. command-line arg).
        // Guard against double-open: LoadFile already called OnFileOpened if the
        // file was opened after the control was constructed.
        var fp = _engine.CurrentBuffer.FilePath;
        if (fp != null && _lspManager.CurrentUri == null)
            _lspManager.OnFileOpened(fp, _engine.CurrentBuffer.Text.GetText());
        if (fp != null)
            _ = RefreshGitDiffAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachImeWindowHook();
        TextCompositionManager.RemovePreviewTextInputStartHandler(this, OnPreviewTextInputStart);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(this, OnPreviewTextInputUpdate);
        ClearImeCompositionOverlay();
        ClearImeCandidateOverlay();
        DestroyImeSuppressionCaret();
        DetachTsfUiElementSink();
        DetachTsfTextEditSink();
        DisposeCustomTextStore();
        _completionDebounce.Stop();
        // NOTE: the LSP client (language-server processes) and the file watcher are deliberately
        // NOT torn down here. Unloaded fires on every detach from the visual tree — including the
        // transient detach hosts perform when they cache the control across workspace/tab switches —
        // and killing the language server on each of those made LSP die on workspace switch and
        // re-index churn on switch-back. They are owned resources, released only in Dispose().
    }

    private bool _disposed;
    // Keep the exact source used by AddHook. At Unloaded time the control may already be
    // detached, so PresentationSource.FromVisual(this) can be null even though the hook is
    // still registered on the host HWND. This is common in hosts that reparent editors.
    private HwndSource? _imeHookSource;

    private void AttachImeWindowHook()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;
        if (ReferenceEquals(_imeHookSource, source))
            return;

        DetachImeWindowHook();
        source.AddHook(ImeWndProc);
        _imeHookSource = source;
    }

    private void DetachImeWindowHook()
    {
        if (_imeHookSource is not { } source)
            return;
        source.RemoveHook(ImeWndProc);
        _imeHookSource = null;
    }

    /// <summary>
    /// Releases resources that must outlive a transient detach from the visual tree: the LSP
    /// client (language-server processes) and the file-change watcher. Call this only on a real
    /// teardown — closing the editor or discarding the host that owns it — NOT on
    /// <see cref="FrameworkElement.Unloaded"/>, which also fires when a host detaches the control
    /// to cache it (e.g. workspace switching). Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachImeWindowHook();
        _completionDebounce.Stop();
        _imeOverlayClearTimer?.Stop();
        _lspManager.Dispose();
        _fileWatcher?.Dispose();
        _fileWatcher = null;
    }

    /// <summary>
    /// Win32 message hook: intercepts WM_IME_STARTCOMPOSITION / WM_IME_SETCONTEXT
    /// and disables the native IME UI because composition is rendered in-editor.
    /// </summary>
    private IntPtr ImeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // With the custom TSF text store active the IME composes inline into our store
        // (no legacy IMM composition window), and the native candidate UI is positioned
        // via ITextStoreACP.GetTextExt — so the IMM-based suppression below must stand down.
        if (_customTextStoreActive)
        {
            // The window (this shared HWND) just regained focus from another top-level
            // window. If the editor is the focused element, re-point the thread's TSF
            // focus at our store — otherwise a competing control's document manager (or
            // WPF's default) may still own it and IME input would land elsewhere.
            if (msg == WM_SETFOCUS && IsKeyboardFocusWithin)
                FocusCustomTextStore();
            return IntPtr.Zero;
        }

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
        else if (msg == WM_IME_COMPOSITION)
        {
            // The IME caret may move within the composition (e.g. arrow keys re-select
            // a clause) without changing the composition text, so WPF raises no
            // text-input update. Re-read GCS_CURSORPOS after the message settles and
            // refresh the rendered caret. We don't mark it handled — WPF still needs to
            // process this message for text commits.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (_engine.Mode is (VimMode.Insert or VimMode.Replace)
                    && !string.IsNullOrEmpty(_lastImeCompositionText))
                    Canvas.SetImeCompositionText(_lastImeCompositionText, GetImeCursorPos());
            });
        }
        else if (msg == WM_IME_ENDCOMPOSITION)
        {
            // Legacy IMM IMEs: the composition ended (e.g. backspaced away). Clear the
            // in-editor overlay so no character lingers. (TSF IMEs don't send this; the
            // ScheduleImeOverlayResync path covers them.)
            ScheduleImeCompositionOverlayClear();
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

    /// <summary>
    /// Advises an <see cref="ITfTextEditSink"/> on the currently focused TSF context so
    /// that composition caret moves (arrow keys inside the composition) are observed.
    /// No-op if already advised or if TSF is unavailable. Failures fall back silently to
    /// the IMM / end-of-composition caret behaviour.
    /// </summary>
    private void EnsureTsfTextEditSink()
    {
        if (_tsfTextEditCookie != TF_INVALID_COOKIE) return;

        if (_customTextStoreActive)
        {
            EnsureCustomTsfTextEditSink();
            return;
        }

        IntPtr threadMgrPtr = IntPtr.Zero;
        ITfThreadMgr? threadMgr = null;
        ITfDocumentMgr? docMgr = null;
        ITfContext? ctx = null;
        try
        {
            if (TF_GetThreadMgr(out threadMgrPtr) < 0 || threadMgrPtr == IntPtr.Zero) return;
            threadMgr = (ITfThreadMgr)Marshal.GetObjectForIUnknown(threadMgrPtr);

            if (threadMgr.GetFocus(out docMgr) < 0 || docMgr == null) return;
            if (docMgr.GetTop(out ctx) < 0 || ctx == null) return;

            var source = (ITfSource)ctx; // QI ITfSource on the context
            var sink = new TsfTextEditSink(this);
            var iid = IID_ITfTextEditSink;
            if (source.AdviseSink(ref iid, sink, out uint cookie) >= 0)
            {
                _tsfContext = ctx;
                _tsfContextSource = source;
                _tsfTextEditSink = sink;
                _tsfTextEditCookie = cookie;
                ctx = null; // ownership transferred to the fields; don't release below
            }
        }
        catch
        {
            // TSF caret tracking is best-effort; never let it disrupt input.
        }
        finally
        {
            if (ctx != null && Marshal.IsComObject(ctx)) Marshal.ReleaseComObject(ctx);
            if (docMgr != null && Marshal.IsComObject(docMgr)) Marshal.ReleaseComObject(docMgr);
            if (threadMgr != null && Marshal.IsComObject(threadMgr)) Marshal.ReleaseComObject(threadMgr);
            if (threadMgrPtr != IntPtr.Zero) Marshal.Release(threadMgrPtr);
        }
    }

    private void EnsureCustomTsfTextEditSink()
    {
        if (_tsfTextEditCookie != TF_INVALID_COOKIE) return;
        if (_tsfStoreContext == null) return;

        ITfContext? ctx = null;
        ITfSource? source = null;
        try
        {
            ctx = (ITfContext)_tsfStoreContext;
            source = (ITfSource)_tsfStoreContext;
            var sink = new TsfTextEditSink(this);
            var iid = IID_ITfTextEditSink;
            if (source.AdviseSink(ref iid, sink, out uint cookie) >= 0)
            {
                _tsfContext = ctx;
                _tsfContextSource = source;
                _tsfTextEditSink = sink;
                _tsfTextEditCookie = cookie;
                ctx = null;
                source = null;
            }
        }
        catch
        {
            // Custom-store edit tracking is best-effort; composition still commits through the store.
        }
        finally
        {
            if (source != null && Marshal.IsComObject(source)) Marshal.ReleaseComObject(source);
            if (ctx != null && !ReferenceEquals(ctx, source) && Marshal.IsComObject(ctx)) Marshal.ReleaseComObject(ctx);
        }
    }

    private void DetachTsfTextEditSink()
    {
        try
        {
            if (_tsfContextSource != null && _tsfTextEditCookie != TF_INVALID_COOKIE)
                _ = _tsfContextSource.UnadviseSink(_tsfTextEditCookie);
        }
        catch
        {
            // Sink detach failures should not affect editor shutdown.
        }
        finally
        {
            _tsfTextEditCookie = TF_INVALID_COOKIE;
            _tsfTextEditSink = null;
            // _tsfContextSource is a QI of _tsfContext, so for COM the runtime hands back
            // the same RCW instance — release it only once to avoid over-releasing.
            var src = _tsfContextSource;
            var ctx = _tsfContext;
            _tsfContextSource = null;
            _tsfContext = null;
            if (src != null && Marshal.IsComObject(src))
                Marshal.ReleaseComObject(src);
            if (ctx != null && !ReferenceEquals(ctx, src) && Marshal.IsComObject(ctx))
                Marshal.ReleaseComObject(ctx);
            if (_tsfCategoryMgr != null && Marshal.IsComObject(_tsfCategoryMgr))
                Marshal.ReleaseComObject(_tsfCategoryMgr);
            _tsfCategoryMgr = null;
            if (_tsfDisplayAttrMgr != null && Marshal.IsComObject(_tsfDisplayAttrMgr))
                Marshal.ReleaseComObject(_tsfDisplayAttrMgr);
            _tsfDisplayAttrMgr = null;
        }
    }

    /// <summary>
    /// Called from <see cref="ITfTextEditSink.OnEndEdit"/>. Reads the current selection
    /// (the composition caret) under the provided read-only cookie and updates the
    /// in-editor composition caret. The WPF text store holds only the in-flight
    /// composition, so the selection's character anchor is the caret offset within it.
    /// </summary>
    private void OnTsfEndEdit(uint ecReadOnly)
    {
        if (_engine.Mode is not (VimMode.Insert or VimMode.Replace)) return;
        if (_tsfContext == null || string.IsNullOrEmpty(_lastImeCompositionText)) return;

        try
        {
            var selection = new TF_SELECTION[1];
            if (_tsfContext.GetSelection(ecReadOnly, TF_DEFAULT_SELECTION, 1, selection, out uint fetched) < 0
                || fetched < 1 || selection[0].range == IntPtr.Zero)
                return;

            int caret = -1;
            try
            {
                var range = (ITfRangeACP)Marshal.GetObjectForIUnknown(selection[0].range);
                try
                {
                    if (range.GetExtent(out int anchor, out int length) >= 0)
                        caret = anchor + length; // caret sits at the end of the selection
                }
                finally
                {
                    if (Marshal.IsComObject(range)) Marshal.ReleaseComObject(range);
                }
            }
            finally
            {
                Marshal.Release(selection[0].range);
            }

            int[] clauseStarts = [];
            int tcStart = -1, tcEnd = -1;
            try { ReadImeClauses(ecReadOnly, out clauseStarts, out tcStart, out tcEnd); }
            catch { clauseStarts = []; tcStart = tcEnd = -1; }

            Canvas.SetImeCompositionText(_lastImeCompositionText, caret);
            Canvas.SetImeClauses(clauseStarts, tcStart, tcEnd);
        }
        catch
        {
            // A failed read just leaves the caret at its current (end) position.
        }
    }

    /// <summary>
    /// Walks the GUID_PROP_ATTRIBUTE property ranges of the composition to obtain the
    /// clause (文節) segmentation. Each property range is a maximal run of one display
    /// attribute, so its start is a clause boundary; the range whose attribute is
    /// TARGET_CONVERTED / TARGET_NOTCONVERTED is the focused clause. Returns false when
    /// no segmentation is available.
    /// </summary>
    private bool ReadImeClauses(uint ec, out int[] clauseStarts, out int targetStart, out int targetEnd)
    {
        clauseStarts = [];
        targetStart = -1;
        targetEnd = -1;
        if (_tsfContext == null) return false;

        // Lazily create the category / display-attribute managers (cached for lifetime).
        if (_tsfCategoryMgr == null || _tsfDisplayAttrMgr == null)
        {
            if (Type.GetTypeFromCLSID(CLSID_TF_CategoryMgr) is { } catType
                && Activator.CreateInstance(catType) is ITfCategoryMgr cm)
                _tsfCategoryMgr = cm;
            if (Type.GetTypeFromCLSID(CLSID_TF_DisplayAttributeMgr) is { } daType
                && Activator.CreateInstance(daType) is ITfDisplayAttributeMgr dm)
                _tsfDisplayAttrMgr = dm;
            if (_tsfCategoryMgr == null || _tsfDisplayAttrMgr == null) return false;
        }

        var propGuid = GUID_PROP_ATTRIBUTE;
        int propHr = _tsfContext.GetProperty(ref propGuid, out var prop);
        if (propHr < 0 || prop == null) return false;

        var starts = new List<int>();
        ITfRangeACP? docRange = null;
        ITfRangeACP? docEnd = null;
        IEnumTfRanges? ranges = null;
        try
        {
            // Build a range covering the whole document (the in-flight composition).
            int startHr = _tsfContext.GetStart(ec, out docRange);
            if (startHr < 0 || docRange == null) return false;
            int endHr = _tsfContext.GetEnd(ec, out docEnd);
            if (endHr < 0 || docEnd == null) return false;
            docRange.ShiftEndToRange(ec, docEnd, TF_ANCHOR_END);

            int enumHr = prop.EnumRanges(ec, out ranges, docRange);
            if (enumHr < 0 || ranges == null) return false;

            var one = new ITfRangeACP?[1];
            while (ranges.Next(1, one, out uint fetched) == 0 && fetched == 1 && one[0] != null)
            {
                var range = one[0]!;
                try
                {
                    if (range.GetExtent(out int acp, out int len) < 0 || len <= 0) continue;
                    starts.Add(acp); // each attribute run start is a clause boundary

                    int valueHr = prop.GetValue(ec, range, out object val);
                    if (valueHr < 0 || val is not int atom) continue;
                    if (_tsfCategoryMgr!.GetGUID(unchecked((uint)atom), out Guid attrGuid) < 0) continue;
                    if (_tsfDisplayAttrMgr!.GetDisplayAttributeInfo(ref attrGuid, out var info, out _) < 0
                        || info == null) continue;
                    try
                    {
                        if (info.GetAttributeInfo(out var da) >= 0
                            && (da.bAttr == TF_ATTR_TARGET_CONVERTED || da.bAttr == TF_ATTR_TARGET_NOTCONVERTED))
                        {
                            targetStart = acp;
                            targetEnd = acp + len;
                        }
                    }
                    finally
                    {
                        if (Marshal.IsComObject(info)) Marshal.ReleaseComObject(info);
                    }
                }
                finally
                {
                    if (Marshal.IsComObject(range)) Marshal.ReleaseComObject(range);
                }
            }
            clauseStarts = [.. starts];
            return starts.Count > 0;
        }
        finally
        {
            if (ranges != null && Marshal.IsComObject(ranges)) Marshal.ReleaseComObject(ranges);
            if (docEnd != null && Marshal.IsComObject(docEnd)) Marshal.ReleaseComObject(docEnd);
            if (docRange != null && Marshal.IsComObject(docRange)) Marshal.ReleaseComObject(docRange);
            if (Marshal.IsComObject(prop)) Marshal.ReleaseComObject(prop);
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

    // Minimal TSF interfaces for reading the composition caret. Only the methods we call
    // are given real signatures; preceding vtable slots are declared as no-arg
    // placeholders (never invoked) purely to preserve the COM vtable layout — the IID
    // guarantees the slot order, so the declared method lands on the correct slot.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("AA80E801-2021-11D2-93E0-0060B067B86E")]
    private interface ITfThreadMgr
    {
        [PreserveSig] int Activate(out uint ptid);
        [PreserveSig] int Deactivate();
        [PreserveSig] int CreateDocumentMgr(out IntPtr ppdim);
        [PreserveSig] int EnumDocumentMgrs(out IntPtr ppEnum);
        [PreserveSig] int GetFocus(out ITfDocumentMgr? ppdimFocus);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("AA80E7F4-2021-11D2-93E0-0060B067B86E")]
    private interface ITfDocumentMgr
    {
        [PreserveSig] int CreateContext(uint tidOwner, uint dwFlags, IntPtr punk, out IntPtr ppic, out uint pecTextStore);
        [PreserveSig] int Push(IntPtr pic);
        [PreserveSig] int Pop(uint dwFlags);
        [PreserveSig] int GetTop(out ITfContext? ppic);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("AA80E7FD-2021-11D2-93E0-0060B067B86E")]
    private interface ITfContext
    {
        [PreserveSig] int RequestEditSession(uint tid, IntPtr pes, uint dwFlags, out int phrSession);
        [PreserveSig] int InWriteSession(uint tid, out bool pfWriteSession);
        [PreserveSig] int GetSelection(uint ec, uint ulIndex, uint ulCount,
            [Out, MarshalAs(UnmanagedType.LPArray)] TF_SELECTION[] pSelection, out uint pcFetched);
        [PreserveSig] int SetSelection();   // slot 4 — placeholder
        [PreserveSig] int GetStart(uint ec, out ITfRangeACP? ppStart);
        [PreserveSig] int GetEnd(uint ec, out ITfRangeACP? ppEnd);
        [PreserveSig] int GetActiveView(); // slot 7 — placeholder
        [PreserveSig] int EnumViews();      // slot 8 — placeholder
        [PreserveSig] int GetStatus();      // slot 9 — placeholder
        [PreserveSig] int GetProperty(ref Guid guidProp, out ITfReadOnlyProperty? ppProp);
    }

    // ITfRangeACP derives from ITfRange; the 22 ITfRange methods precede GetExtent.
    // Slot 9 is ITfRange::ShiftEndToRange, which we use to build a whole-document range.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("057A6296-029B-4154-B79A-0D461D4EA94C")]
    private interface ITfRangeACP
    {
        [PreserveSig] int _01(); [PreserveSig] int _02(); [PreserveSig] int _03();
        [PreserveSig] int _04(); [PreserveSig] int _05(); [PreserveSig] int _06();
        [PreserveSig] int _07(); [PreserveSig] int _08();
        [PreserveSig] int ShiftEndToRange(uint ec, ITfRangeACP pRange, int aPos); // slot 9
        [PreserveSig] int _10(); [PreserveSig] int _11(); [PreserveSig] int _12();
        [PreserveSig] int _13(); [PreserveSig] int _14(); [PreserveSig] int _15();
        [PreserveSig] int _16(); [PreserveSig] int _17(); [PreserveSig] int _18();
        [PreserveSig] int _19(); [PreserveSig] int _20(); [PreserveSig] int _21();
        [PreserveSig] int _22();
        [PreserveSig] int GetExtent(out int pacpAnchor, out int pcch);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("17D49A3D-F8B8-4B2F-B254-52319DD64C53")]
    private interface ITfReadOnlyProperty
    {
        [PreserveSig] int GetType(out Guid pguid); // slot 1
        [PreserveSig] int EnumRanges(uint ec, out IEnumTfRanges? ppEnum, ITfRangeACP pTargetRange);
        [PreserveSig] int GetValue(uint ec, ITfRangeACP pRange,
            [MarshalAs(UnmanagedType.Struct)] out object pvarValue);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("F99D3F40-8E32-11D2-BF46-00105A2799B5")]
    private interface IEnumTfRanges
    {
        [PreserveSig] int Clone(out IEnumTfRanges? ppEnum); // slot 1
        [PreserveSig] int Next(uint ulCount,
            [Out, MarshalAs(UnmanagedType.LPArray)] ITfRangeACP?[] rgRange, out uint pcFetched);
    }

    // ITfCategoryMgr::GetGUID is slot 13; the preceding 12 are placeholders.
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("C3ACEFB5-F69D-4905-938F-FCADCF4BE830")]
    private interface ITfCategoryMgr
    {
        [PreserveSig] int _01(); [PreserveSig] int _02(); [PreserveSig] int _03();
        [PreserveSig] int _04(); [PreserveSig] int _05(); [PreserveSig] int _06();
        [PreserveSig] int _07(); [PreserveSig] int _08(); [PreserveSig] int _09();
        [PreserveSig] int _10(); [PreserveSig] int _11(); [PreserveSig] int _12();
        [PreserveSig] int GetGUID(uint guidatom, out Guid pguid);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("8DED7393-5DB1-475C-9E71-A39111B0FF67")]
    private interface ITfDisplayAttributeMgr
    {
        [PreserveSig] int OnUpdateInfo();              // slot 1 — placeholder
        [PreserveSig] int EnumDisplayAttributeInfo();  // slot 2 — placeholder
        [PreserveSig] int GetDisplayAttributeInfo(ref Guid guid,
            out ITfDisplayAttributeInfo? ppInfo, out Guid pguidOwner);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("70528852-2F26-4AEA-8C96-215150578932")]
    private interface ITfDisplayAttributeInfo
    {
        [PreserveSig] int GetGUID(out Guid pguid);            // slot 1 — placeholder
        [PreserveSig] int GetDescription(out IntPtr pbstrDesc); // slot 2 — placeholder
        [PreserveSig] int GetAttributeInfo(out TF_DISPLAYATTRIBUTE pda);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TF_DA_COLOR
    {
        public int type;  // TF_DA_COLORTYPE
        public uint cr;   // union: COLORREF or system-color index
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TF_DISPLAYATTRIBUTE
    {
        public TF_DA_COLOR crText;
        public TF_DA_COLOR crBk;
        public int lsStyle;     // TF_DA_LINESTYLE
        public int fBoldLine;   // BOOL
        public TF_DA_COLOR crLine;
        public int bAttr;       // TF_DA_ATTR_INFO
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("8127D409-CCD3-4683-967A-B43D5B482BF7")]
    private interface ITfTextEditSink
    {
        [PreserveSig] int OnEndEdit(IntPtr pic, uint ecReadOnly, IntPtr pEditRecord);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TF_SELECTIONSTYLE
    {
        public int ase;          // TfActiveSelEnd
        public int fInterimChar; // BOOL
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TF_SELECTION
    {
        public IntPtr range;     // ITfRange*
        public TF_SELECTIONSTYLE style;
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

            // The custom TSF store renders composition in-editor but leaves the candidate
            // list to the native (correctly positioned) popup, so don't suppress it.
            if (owner._customTextStoreActive)
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
            if (_owner.TryGetTarget(out var owner) && !owner._customTextStoreActive
                && ShouldSuppressNativeImeUi(owner._engine.Mode))
                owner.UpdateImeWindowPos();
            return 0;
        }

        public int EndUIElement(uint dwUIElementId)
        {
            if (_owner.TryGetTarget(out var owner) && !owner._customTextStoreActive)
                owner.ClearImeCandidateOverlay();
            return 0;
        }
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class TsfTextEditSink(VimEditorControl owner) : ITfTextEditSink
    {
        private readonly WeakReference<VimEditorControl> _owner = new(owner);

        public int OnEndEdit(IntPtr pic, uint ecReadOnly, IntPtr pEditRecord)
        {
            // Runs on the UI thread (the TSF document lock is held on the owning thread),
            // so we can read the selection and update the canvas directly.
            if (_owner.TryGetTarget(out var owner))
                owner.OnTsfEndEdit(ecReadOnly);
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
        _multiCursorManager.Exit();
        ClearSelectionRangeState();
        _engine.LoadFile(path);
        UpdateAll();
        _lspManager.OnFileOpened(path, _engine.CurrentBuffer.Text.GetText());
        _ = RefreshGitDiffAsync();
        SetupFileWatcher(path);
    }

    private void ReloadCurrentFile()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
        if (!TryReadAllBytesShared(filePath, out var bytes))
        {
            // ライターがまだハンドルを保持していてロック解放を待っても読めなかった。
            // 投機的な外部リロードなので、落とさずステータス表示に留める。
            ActiveStatusBar.UpdateStatus($"Could not reload \"{filePath}\" (file is locked)");
            return;
        }
        var detectedEnc = Editor.Core.Buffer.VimBuffer.DetectEncoding(bytes);
        var enc = Editor.Core.Buffer.VimBuffer.GetEncoding(detectedEnc);
        var bomLen = Editor.Core.Buffer.VimBuffer.GetBomLength(bytes, enc);
        var text = enc.GetString(bytes, bomLen, bytes.Length - bomLen);
        var detectedFmt = Editor.Core.Buffer.VimBuffer.DetectFileFormat(text);
        _engine.CurrentBuffer.FileFormat   = detectedFmt;
        _engine.CurrentBuffer.FileEncoding = detectedEnc;
        _engine.Options.FileFormat   = detectedFmt;
        _engine.Options.FileEncoding = detectedEnc;
        _engine.CurrentBuffer.Text.SetText(text);
        _engine.CurrentBuffer.Text.MarkSaved();
        _engine.CurrentBuffer.Undo.Clear();
        _engine.CurrentBuffer.Folds.Clear();
        _engine.SetCursorPosition(CursorPosition.Zero);
        ClearSelectionRangeState();
        UpdateAll();
        _lspManager.OnFileOpened(filePath, text);
        _ = RefreshGitDiffAsync();
        ActiveStatusBar.UpdateStatus($"\"{filePath}\" reloaded");
        // 外部変更の取り込み（ウォッチャの自動リロード／:e!）でも本文は変わるので、編集と同じく
        // BufferChanged で通知する。これがないと、本文を購読するホスト（プレビュー等）が取り残される。
        BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    // 外部書き込み直後はライターがまだファイルハンドルを保持していることがあり、
    // File.ReadAllBytes は FileShare.Read で開くため IOException（使用中）になる。
    // 共有読み取り（FileShare.ReadWrite）で開き、ロック中は短くリトライして取り込む。
    private static bool TryReadAllBytesShared(string path, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[fs.Length];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = fs.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                bytes = read == buffer.Length ? buffer : buffer[..read];
                return true;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(30);
            }
            catch (UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(30);
            }
        }
        return false;
    }

    /// <summary>Call before writing a file to disk to prevent the watcher from triggering a reload prompt.</summary>
    public void OnSaveStarted()
    {
        _suppressFileWatcher = true;
        _saveStartedFilePath = _engine.CurrentBuffer.FilePath;
    }
    /// <summary>Call after the file has been written to disk to re-enable the watcher.</summary>
    public void OnSaveFinished()
    {
        var currentPath = _engine.CurrentBuffer.FilePath;
        if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
        {
            _lastSavedFilePath = currentPath;
            try { _lastSavedWriteTimeUtc = File.GetLastWriteTimeUtc(currentPath); }
            catch { _lastSavedWriteTimeUtc = default; }
        }

        _suppressFileWatcher = false;
        RefreshSaveDiff();
        if (!string.Equals(_saveStartedFilePath, currentPath, StringComparison.OrdinalIgnoreCase))
            _ = RefreshGitDiffAsync();
        else
            SyncStatusBar();
        _saveStartedFilePath = null;
    }

    /// <summary>
    /// Host-facing save shortcut for use inside a <see cref="SaveRequested"/> handler. Writes the
    /// current buffer to disk (to <paramref name="path"/> when given, otherwise the buffer's own
    /// path), clears the modified flag, and brackets the write with
    /// <see cref="OnSaveStarted"/>/<see cref="OnSaveFinished"/> so the file watcher does not raise a
    /// reload prompt. Hosts no longer need to reach into <c>Engine.CurrentBuffer</c>.
    /// Not valid for virtual documents (which have no path) — use <see cref="MarkSaved"/> instead.
    /// </summary>
    public void Save(string? path = null)
    {
        OnSaveStarted();
        try
        {
            _engine.CurrentBuffer.Save(path);
        }
        finally
        {
            OnSaveFinished();
        }
    }

    /// <summary>
    /// Opens an in-memory document that is not backed by a file; nothing is written to disk.
    /// On <c>:w</c> the control raises <see cref="SaveRequested"/> with
    /// <see cref="SaveRequestedEventArgs.IsVirtual"/> set and the returned id in
    /// <see cref="SaveRequestedEventArgs.DocumentId"/>, letting the host persist the content
    /// (available via <see cref="Text"/>) wherever it wants. After persisting, call
    /// <see cref="MarkSaved"/> to clear the modified flag.
    /// </summary>
    /// <param name="title">Display title for the document (used instead of a file name).</param>
    /// <param name="content">Initial text content.</param>
    /// <param name="syntax">Syntax language name for highlighting (e.g. "Markdown", "C#"), or null for none.</param>
    /// <returns>An opaque document id reported back on save.</returns>
    public string OpenVirtualDocument(string title, string content = "", string? syntax = null)
    {
        _multiCursorManager.Exit();
        ClearSelectionRangeState();
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        var id = Guid.NewGuid().ToString("N");
        var buf = _engine.CurrentBuffer;
        _engine.SetText(content);
        _engine.SetCursorPosition(CursorPosition.Zero);
        buf.FilePath = null;
        buf.Text.MarkSaved();
        buf.Undo.Clear();
        buf.Folds.Clear();
        buf.IsVirtual = true;
        buf.DocumentId = id;
        buf.DisplayName = title;

        _engine.Syntax.SetLanguage(syntax ?? "");

        UpdateAll();
        SyncStatusBar();
        return id;
    }

    /// <summary>
    /// Clears the modified flag without writing to disk. Hosts call this after persisting a
    /// virtual document's content themselves. When <paramref name="documentId"/> is supplied it
    /// acts as a guard: the flag is only cleared if it matches the current document's id.
    /// </summary>
    public void MarkSaved(string? documentId = null)
    {
        if (documentId != null && _engine.CurrentBuffer.DocumentId != documentId)
            return;
        _engine.CurrentBuffer.Text.MarkSaved();
        RefreshSaveDiff();
        SyncStatusBar();
    }

    private void SetupFileWatcher(string filePath)
    {
        // Dispose previous watcher
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        var watcher = new FileSystemWatcher(dir, Path.GetFileName(filePath))
        {
            NotifyFilter        = NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        watcher.Changed += OnWatcherChanged;
        watcher.Renamed += OnWatcherRenamed;
        watcher.Deleted += OnWatcherDeleted;
        _fileWatcher = watcher;
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (_suppressFileWatcher) return;
        // FSW fires Changed multiple times per save; guard against duplicate dispatches.
        Dispatcher.BeginInvoke(() =>
        {
            if (_pendingWatcherReload) return;
            _pendingWatcherReload = true;
            try
            {
                HandleExternalFileChange(e.FullPath);
            }
            catch (Exception ex)
            {
                // 外部変更の取り込みは投機的処理。読み込み失敗などで UI スレッドの
                // 未ハンドル例外（＝プロセス即死）にしないよう、ここで握り潰す。
                ActiveStatusBar.UpdateStatus($"Reload failed: {ex.Message}");
            }
            finally
            {
                _pendingWatcherReload = false;
            }
        });
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
            ActiveStatusBar.UpdateStatus($"File renamed to \"{e.Name}\""));
    }

    private void OnWatcherDeleted(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
            ActiveStatusBar.UpdateStatus($"Warning: \"{e.Name}\" was deleted on disk"));
    }

    private void HandleExternalFileChange(string fullPath)
    {
        // Verify the change is still for the currently loaded file.
        if (!string.Equals(fullPath, _engine.CurrentBuffer.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        if (IsOwnRecentSave(fullPath))
            return;

        if (!_engine.CurrentBuffer.Text.IsModified)
        {
            ReloadCurrentFile();  // sets status to "\"<path>\" reloaded"
        }
        else
        {
            var result = MessageBox.Show(
                "File changed on disk. Reload? (loses unsaved changes)",
                "File Changed",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                ReloadCurrentFile();
        }
    }

    private bool IsOwnRecentSave(string fullPath)
    {
        if (!string.Equals(fullPath, _lastSavedFilePath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_lastSavedWriteTimeUtc == default)
            return true;

        try
        {
            if (File.Exists(fullPath)
                && File.GetLastWriteTimeUtc(fullPath) == _lastSavedWriteTimeUtc)
            {
                return true;
            }
        }
        catch
        {
            return true;
        }

        _lastSavedFilePath = null;
        _lastSavedWriteTimeUtc = default;
        return false;
    }

    public void RefreshGitDiff() => _ = RefreshGitDiffAsync();

    private async Task RefreshGitDiffAsync()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            _gitBranchName = null;
            _gitBranchFilePath = null;
            UpdateGitBranchStatusBarIfActive(null);
            return;
        }

        var gitInfo = await Task.Run(() => (
            Diff: _gitProvider.GetDiff(filePath),
            Branch: _gitProvider.GetBranchName(filePath)));
        if (!string.Equals(filePath, _engine.CurrentBuffer.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        _gitBranchName = gitInfo.Branch;
        _gitBranchFilePath = filePath;
        UpdateGitBranchStatusBarIfActive(_gitBranchName);
        var diff = gitInfo.Diff;
        Canvas.SetGitDiff(diff);

        if (_blameActive)
        {
            var blame = await Task.Run(() => _gitProvider.GetBlameLines(filePath));
            Canvas.SetBlameLines(blame);
        }
    }

    private async void ToggleBlame()
    {
        _blameActive = !_blameActive;
        if (!_blameActive)
        {
            Canvas.SetBlameLines(null);
            ActiveStatusBar.UpdateStatus("Git blame: off");
            return;
        }

        var filePath = _engine.CurrentBuffer.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            _blameActive = false;
            ActiveStatusBar.UpdateStatus("Git blame: no file open");
            return;
        }

        ActiveStatusBar.UpdateStatus("Git blame: loading...");
        var blame = await Task.Run(() => _gitProvider.GetBlameLines(filePath));
        if (_blameActive)
        {
            Canvas.SetBlameLines(blame);
            ActiveStatusBar.UpdateStatus(blame.Count > 0
                ? $"Git blame: {blame.Count} lines annotated"
                : "Git blame: no blame data (file not committed?)");
        }
    }

    private async Task ShowGitDiffAsync()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            ActiveStatusBar.UpdateStatus("Git diff: no file open");
            return;
        }
        await ShowGitOutputAsync("[Git Diff]", () => _gitProvider.GetDiffOutput(filePath));
    }

    private async Task ShowGitStatusAsync()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        var repoPath = string.IsNullOrEmpty(filePath) ? Environment.CurrentDirectory : filePath;
        await ShowGitOutputAsync("[Git Status]", () => _gitProvider.GetStatusOutput(repoPath));
    }

    private async Task ShowGitLogAsync()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        var repoPath = string.IsNullOrEmpty(filePath) ? Environment.CurrentDirectory : filePath;
        await ShowGitOutputAsync("[Git Log]", () => _gitProvider.GetLogOutput(repoPath));
    }

    /// <summary>現在のバッファの Git 履歴一覧（<c>git log --oneline -- &lt;file&gt;</c>）を返す。
    /// blame 行のコミットは必ずこの履歴に含まれるので、blame からの操作
    /// （<see cref="BlameCommitClicked"/> やコンテキストメニュー）で履歴一覧を開き、該当コミットを
    /// 選択するために使う。ファイル未保存などでパスが無い場合はリポジトリ全体のログにフォールバックする。</summary>
    public string GetGitLogOutput()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        if (!string.IsNullOrEmpty(filePath))
            return _gitProvider.GetFileHistoryOutput(filePath);
        return _gitProvider.GetLogOutput(Environment.CurrentDirectory);
    }

    private Task RunGitPushAsync()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        var repoPath = string.IsNullOrEmpty(filePath) ? Environment.CurrentDirectory : filePath;
        return RunGitCommandAsync("[Git Push]", () => _gitProvider.RunPush(repoPath));
    }

    private Task RunGitPullAsync()
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        var repoPath = string.IsNullOrEmpty(filePath) ? Environment.CurrentDirectory : filePath;
        return RunGitCommandAsync("[Git Pull]", () => _gitProvider.RunPull(repoPath));
    }

    private void ShowGitCommit()
    {
        if (GitCommitRequested != null)
            GitCommitRequested.Invoke(this, new GitCommitRequestedEventArgs(_engine.CurrentBuffer.FilePath, ""));
        else
            ActiveStatusBar.UpdateStatus("Git commit: not supported in this host");
    }

    public (bool Success, string Message) ExecuteGitCommit(string message)
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        var (success, output) = _gitProvider.RunCommit(filePath ?? "", message);
        if (success)
            _ = RefreshGitDiffAsync();
        return (success, output);
    }

    private void NavigateHunk(bool forward)
    {
        var diff = Canvas.GetGitDiff();
        if (diff == null || diff.Count == 0)
        {
            ActiveStatusBar.UpdateStatus("No changes");
            return;
        }

        int curLine = _engine.Cursor.Line;

        // Build hunk-start list: a line is a hunk start when its predecessor is not in the diff
        var allLines = diff.Keys.Order().ToList();
        var hunkStarts = new List<int>(allLines.Count);
        for (int i = 0; i < allLines.Count; i++)
        {
            if (i == 0 || allLines[i - 1] != allLines[i] - 1)
                hunkStarts.Add(allLines[i]);
        }

        if (hunkStarts.Count == 0)
        {
            ActiveStatusBar.UpdateStatus("No hunks");
            return;
        }

        int targetIndex;
        if (forward)
        {
            targetIndex = hunkStarts.FindIndex(l => l > curLine);
            if (targetIndex < 0) targetIndex = 0; // wrap around
        }
        else
        {
            targetIndex = hunkStarts.FindLastIndex(l => l < curLine);
            if (targetIndex < 0) targetIndex = hunkStarts.Count - 1; // wrap around
        }

        ClearSelectionRangeState();
        var events = _engine.SetCursorPosition(new CursorPosition(hunkStarts[targetIndex], 0));
        ProcessVimEvents(events);
        ActiveStatusBar.UpdateStatus($"Hunk {targetIndex + 1}/{hunkStarts.Count}");
    }

    private async Task StageCurrentHunkAsync(bool stage)
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        if (string.IsNullOrEmpty(filePath))
        {
            ActiveStatusBar.UpdateStatus(stage ? "Git stage: no file open" : "Git unstage: no file open");
            return;
        }

        var line = _engine.Cursor.Line;
        ActiveStatusBar.UpdateStatus(stage ? "Git stage hunk: running..." : "Git unstage hunk: running...");
        var result = await Task.Run(() => stage
            ? _gitProvider.StageHunk(filePath, line)
            : _gitProvider.UnstageHunk(filePath, line));

        ActiveStatusBar.UpdateStatus(result.Output);
        if (result.Success)
            _ = RefreshGitDiffAsync();
    }

    private async Task ShowGitOutputAsync(string title, Func<string> fetch)
    {
        ActiveStatusBar.UpdateStatus($"{title}: loading...");
        var output = await Task.Run(fetch);
        GitOutputRequested?.Invoke(this, new GitOutputRequestedEventArgs(title, output));
        ActiveStatusBar.UpdateStatus($"{title}: done");
    }

    private async Task RunGitCommandAsync(string title, Func<(bool Success, string Output)> run)
    {
        ActiveStatusBar.UpdateStatus($"{title}: running...");
        var result = await Task.Run(run);
        GitOutputRequested?.Invoke(this, new GitOutputRequestedEventArgs(title, result.Output));
        ActiveStatusBar.UpdateStatus(result.Success ? $"{title}: done" : $"{title}: failed");
        if (result.Success)
            _ = RefreshGitDiffAsync();
    }

    public void NavigateTo(int line, int column)
    {
        ClearSelectionRangeState();
        var events = _engine.SetCursorPosition(new CursorPosition(line, column));
        ProcessVimEvents(events);
    }

    /// <summary>Visibly selects the whole of <paramref name="line"/> — a plain, highlighted
    /// selection that stays in Normal mode — and scrolls it into view. Used to mark a chosen row,
    /// e.g. the blame commit inside the Git history list, so it reads as "selected" rather than
    /// just having the caret on it.</summary>
    public void SelectLine(int line)
    {
        var buf = _engine.CurrentBuffer.Text;
        if (line < 0 || line >= buf.LineCount) return;
        int len = buf.GetLineLength(line);
        var events = _engine.SetPlainSelection(
            new CursorPosition(line, 0), new CursorPosition(line, len));
        ProcessVimEvents(events);
    }

    /// <summary>
    /// Highlights every occurrence of <paramref name="pattern"/> in the buffer as
    /// if it were the active search pattern (honours <c>hlsearch</c>/<c>ignorecase</c>/
    /// <c>smartcase</c>), without moving the cursor or touching search history. Pass an
    /// empty string to clear. Useful for highlighting a query (e.g. a grep hit opened
    /// from a host's sidebar) — typically called right after <see cref="NavigateTo"/>.
    /// Matching is literal substring (same as <c>hlsearch</c>), so regex is treated literally.
    /// </summary>
    public void HighlightSearch(string pattern)
    {
        var events = _engine.SetSearchHighlight(pattern ?? "");
        ProcessVimEvents(events);
    }

    public void SetText(string text)
    {
        ClearSelectionRangeState();
        _engine.SetText(text);
        UpdateAll();
    }

    /// <summary>
    /// Executes an ex command (without the leading ':') programmatically, e.g.
    /// <c>ExecuteCommand("Gblame")</c>. The command runs directly through the ex
    /// processor, so it works in any mode (including Insert) and also when
    /// <see cref="VimEnabled"/> is false — synthesizing the keystrokes instead
    /// would insert the text into the buffer in those states.
    /// </summary>
    public void ExecuteCommand(string exCommand)
        => ProcessVimEvents(_engine.ExecuteExCommand(exCommand));

    // ── Split / tab window APIs ────────────────────────────────────────────
    // These raise the same events that the corresponding Vim commands
    // (`:split`, `:vsplit`, `:tabnew`, `Ctrl+W`, …) fire, so a host can drive
    // window/tab management programmatically without synthesizing keystrokes.
    // The actual pane/tab layout is owned by the host (e.g. MainWindow), which
    // realizes these requests by handling the matching events.

    /// <summary>
    /// Requests a horizontal split (a new editor below the current one), as
    /// with <c>:split</c>. Optionally opens <paramref name="filePath"/> in the
    /// new window. Raises <see cref="SplitRequested"/> with
    /// <see cref="SplitRequestedEventArgs.Vertical"/> = <c>false</c>.
    /// </summary>
    public void SplitHorizontal(string? filePath = null)
        => SplitRequested?.Invoke(this, new SplitRequestedEventArgs(vertical: false, filePath));

    /// <summary>
    /// Requests a vertical split (a new editor beside the current one), as with
    /// <c>:vsplit</c>. Optionally opens <paramref name="filePath"/> in the new
    /// window. Raises <see cref="SplitRequested"/> with
    /// <see cref="SplitRequestedEventArgs.Vertical"/> = <c>true</c>.
    /// </summary>
    public void SplitVertical(string? filePath = null)
        => SplitRequested?.Invoke(this, new SplitRequestedEventArgs(vertical: true, filePath));

    /// <summary>
    /// Requests a new editor tab, as with <c>:tabnew</c>. Optionally opens
    /// <paramref name="filePath"/> in it. Raises <see cref="NewTabRequested"/>.
    /// </summary>
    public void NewTab(string? filePath = null)
        => NewTabRequested?.Invoke(this, new NewTabRequestedEventArgs(filePath));

    /// <summary>Activates the next tab, as with <c>gt</c>. Raises <see cref="NextTabRequested"/>.</summary>
    public void NextTab() => NextTabRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Activates the previous tab, as with <c>gT</c>. Raises <see cref="PrevTabRequested"/>.</summary>
    public void PrevTab() => PrevTabRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Requests closing the current tab, as with <c>:tabclose</c>. When
    /// <paramref name="force"/> is <c>true</c>, unsaved changes are discarded.
    /// Raises <see cref="CloseTabRequested"/>.
    /// </summary>
    public void CloseTab(bool force = false)
        => CloseTabRequested?.Invoke(this, new CloseTabRequestedEventArgs(force));

    /// <summary>
    /// Moves window focus in the given direction, as with the <c>Ctrl+W</c>
    /// window commands. Raises <see cref="WindowNavRequested"/>.
    /// </summary>
    public void FocusWindow(WindowNavDir dir)
        => WindowNavRequested?.Invoke(this, new WindowNavRequestedEventArgs(dir));

    /// <summary>
    /// Requests closing the current split window, as with <c>Ctrl+W q</c> /
    /// <c>:close</c>. When <paramref name="force"/> is <c>true</c>, unsaved
    /// changes are discarded. Raises <see cref="WindowCloseRequested"/>.
    /// </summary>
    public void CloseWindow(bool force = false)
        => WindowCloseRequested?.Invoke(this, new WindowCloseRequestedEventArgs(force));

    /// <summary>
    /// The color theme currently applied to the editor. Defaults to
    /// <see cref="EditorTheme.Dracula"/>. Assign a new value (or call
    /// <see cref="SetTheme(EditorTheme)"/> / <see cref="SetTheme(string)"/>) to change it.
    /// </summary>
    public EditorTheme Theme
    {
        get => _theme;
        set => SetTheme(value);
    }

    /// <summary>Applies a color theme to the editor and all of its chrome.</summary>
    public void SetTheme(EditorTheme theme)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        ApplyTheme();
    }

    /// <summary>
    /// Applies a built-in color theme by name (case-insensitive), e.g. "dracula",
    /// "nord", "tokyonight", "onedark" or "dark". Unknown names fall back to the
    /// default dark theme. See <see cref="EditorTheme.BuiltInThemeNames"/>.
    /// </summary>
    public void SetTheme(string name) => SetTheme(EditorTheme.GetByName(name));

    /// <summary>
    /// Opens <paramref name="url"/> as a link. Raises <see cref="LinkClicked"/> first so a
    /// host can customize how links are opened (e.g. show them in an embedded browser); set
    /// <see cref="LinkClickedEventArgs.Handled"/> to <c>true</c> on that event to suppress the
    /// default behavior. If left unhandled, the URL is opened with the OS shell via
    /// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
    /// Triggered by Ctrl+Click on a detected URL, and by the <c>gx</c> normal-mode command.
    /// </summary>
    public void OpenLink(string url)
    {
        var args = new LinkClickedEventArgs(url);
        LinkClicked?.Invoke(this, args);
        if (!args.Handled)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"Could not open link: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> as a file-path link. Raises <see cref="FileLinkClicked"/>
    /// first so a host can customize how the path is opened; set
    /// <see cref="FileLinkClickedEventArgs.Handled"/> to <c>true</c> on that event to suppress
    /// the default behavior. By default a file is opened in the editor (via
    /// <see cref="OpenFileRequested"/>) and a directory is opened in the OS file explorer via
    /// <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>.
    /// Triggered by Ctrl+Click on a detected file path.
    /// </summary>
    public void OpenFileLink(string path)
    {
        bool isDir = Directory.Exists(path);
        var args = new FileLinkClickedEventArgs(path, isDir);
        FileLinkClicked?.Invoke(this, args);
        if (args.Handled)
            return;

        if (isDir)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"Could not open folder: {ex.Message}");
            }
            return;
        }

        // A file: open it in the editor, unless it's already the current buffer.
        if (string.Equals(path, _engine.CurrentBuffer.FilePath, StringComparison.OrdinalIgnoreCase))
            return;
        OpenFileRequested?.Invoke(this, new OpenFileRequestedEventArgs(path));
    }

    /// <summary>
    /// Search workspace symbols via LSP. When isClass=true, returns only type-definition kinds
    /// (Class/Interface/Enum/Struct/TypeParameter).
    /// </summary>
    public Task<IReadOnlyList<LspSymbolInformation>> SearchWorkspaceSymbolsAsync(
        string query, bool isClass, CancellationToken ct = default)
        => _lspManager.GetWorkspaceSymbolsAsync(query, isClass, ct);

    private void ApplyTheme()
    {
        Canvas.Theme = _theme;
        StatusBar.Theme = _theme;
        if (_sharedStatusBar != null) _sharedStatusBar.Theme = _theme;
        Background = _theme.Background;
        BreadcrumbBar.Background = _theme.LineNumberBg;
        BreadcrumbBar.BorderBrush = _theme.IndentGuideBrush;
        RefreshBreadcrumbBar();
        ApplyFindReplaceBarTheme();
        Canvas.InvalidateVisual();
    }

    // ─────────────── Font ───────────────

    private string _editorFontFamily = "Consolas";
    private double _editorFontSize = 14;

    /// <summary>
    /// The font family used to render editor text (the gutter, code, and
    /// completion/diagnostic overlays). Defaults to "Consolas". Use a monospaced
    /// font for correct column alignment.
    /// </summary>
    public string EditorFontFamily
    {
        get => _editorFontFamily;
        set => SetFont(value, _editorFontSize);
    }

    /// <summary>
    /// The font size (in device-independent pixels) used to render editor text.
    /// Defaults to 14. Values are clamped to a minimum of 1.
    /// </summary>
    public double EditorFontSize
    {
        get => _editorFontSize;
        set => SetFont(_editorFontFamily, value);
    }

    /// <summary>
    /// Sets the editor font family and size in a single call, then re-renders.
    /// A null/blank <paramref name="family"/> keeps the current family; the size
    /// is clamped to a minimum of 1.
    /// </summary>
    public void SetFont(string family, double size)
    {
        if (!string.IsNullOrWhiteSpace(family)) _editorFontFamily = family;
        _editorFontSize = Math.Max(1, size);
        Canvas.UpdateFont(_editorFontFamily, _editorFontSize);
        StatusBar.SetEditorFontSize(_editorFontSize);
        _sharedStatusBar?.SetEditorFontSize(_editorFontSize);
    }

    // ─────────────── Indentation (tab width) ───────────────

    /// <summary>
    /// The indentation width in columns. Maps to both the <c>tabstop</c> (how wide a
    /// tab renders) and <c>shiftwidth</c> (how far <c>&gt;&gt;</c>/<c>&lt;&lt;</c> and
    /// auto-indent move) options. Defaults to 2. Setting this re-renders the editor.
    /// Values are clamped to a minimum of 1.
    /// </summary>
    public int TabWidth
    {
        get => _engine.Options.TabStop;
        set => SetTabWidth(value, _engine.Options.ExpandTab);
    }

    /// <summary>
    /// When <c>true</c> (the default), pressing Tab and auto-indent insert spaces;
    /// when <c>false</c>, a literal tab character is inserted. Maps to the
    /// <c>expandtab</c> option. Setting this re-renders the editor.
    /// </summary>
    public bool ExpandTabs
    {
        get => _engine.Options.ExpandTab;
        set => SetTabWidth(_engine.Options.TabStop, value);
    }

    /// <summary>
    /// Sets the indentation width and whether Tab inserts spaces in a single call,
    /// then re-renders. Updates the <c>tabstop</c>, <c>shiftwidth</c> and
    /// <c>expandtab</c> options. The width is clamped to a minimum of 1.
    /// </summary>
    public void SetTabWidth(int width, bool expandTabs = true)
    {
        width = Math.Max(1, width);
        _engine.Options.TabStop = width;
        _engine.Options.ShiftWidth = width;
        _engine.Options.ExpandTab = expandTabs;
        UpdateAll();
    }

    // ─────────────── Shared status bar ───────────────

    /// <summary>
    /// Assigns a status bar that lives outside this control (e.g. in the host window).
    /// When set, this control's built-in status bar is hidden and all status updates are
    /// routed to the shared bar instead.
    /// </summary>
    public void SetSharedStatusBar(VimStatusBar? bar)
    {
        _sharedStatusBar = bar;
        StatusBar.Visibility = (bar != null || _minimalChrome) ? Visibility.Collapsed : Visibility.Visible;
        if (bar != null) { bar.Theme = _theme; bar.SetEditorFontSize(_editorFontSize); }
    }

    /// <summary>
    /// Pushes the current mode / file / cursor state to the active status bar.
    /// Call this when this editor gains focus so the shared bar reflects its state.
    /// </summary>
    public void SyncStatusBar()
    {
        var buf = _engine.CurrentBuffer;
        bool branchMatchesCurrentFile = string.Equals(_gitBranchFilePath, buf.FilePath, StringComparison.OrdinalIgnoreCase);
        if (!branchMatchesCurrentFile)
            _ = RefreshGitDiffAsync();
        ActiveStatusBar.UpdateMode(_engine.Mode, _engine.VimEnabled, _engine.Options.ShowMode);
        ActiveStatusBar.UpdateFile(buf.FilePath, buf.Text.IsModified, buf.FileFormat);
        UpdateGitBranchStatusBarIfActive(branchMatchesCurrentFile ? _gitBranchName : null);
        ActiveStatusBar.UpdateCursor(_engine.Cursor, buf.Text.LineCount, _engine.Options.Ruler);
    }

    private void UpdateGitBranchStatusBarIfActive(string? branchName)
    {
        if (_sharedStatusBar == null || IsKeyboardFocusWithin)
            ActiveStatusBar.UpdateGitBranch(branchName);
    }

    private void SyncViewportState()
    {
        _engine.SetViewportState(Canvas.FirstVisibleLine, Canvas.VisibleLines);
    }

    public bool ScrollHorizontalByWheelDelta(int wheelDelta)
    {
        if (wheelDelta == 0 || Canvas.WrapLines)
            return false;

        double maxOffsetX = Math.Max(0, Canvas.TotalContentWidth - Canvas.ViewportWidth);
        if (maxOffsetX <= 0)
            return false;

        // Keep wheel movement predictable: 1 notch = about 8 characters.
        double step = Math.Max(Canvas.CharWidth * 8, 32);
        double deltaX = (wheelDelta / 120.0) * step;
        double target = Math.Clamp(Canvas.HorizontalOffset + deltaX, 0, maxOffsetX);
        if (Math.Abs(target - Canvas.HorizontalOffset) < 0.01)
            return false;

        Canvas.ScrollTo(Canvas.VerticalOffset, target);
        Focus();
        return true;
    }

    private void ToggleWordWrap()
    {
        bool enabled = !Canvas.WrapLines;
        Canvas.WrapLines = enabled;
        _engine.Options.Wrap = enabled;
        Canvas.ScrollTo(Canvas.VerticalOffset, enabled ? 0 : Canvas.HorizontalOffset);
        ActiveStatusBar.UpdateStatus(enabled ? "Wrap: ON" : "Wrap: OFF");
    }

    // ─────────────── Mouse handling ───────────────

    private void OnCanvasLinkClicked(string url)
    {
        Focus();
        OpenLink(url);
    }

    private void OnCanvasFileLinkClicked(string filePath)
    {
        Focus();
        OpenFileLink(filePath);
    }

    private void OnCanvasMouseClicked(int line, int col)
    {
        Focus();
        // Clicking into this editor makes it the active IME target. Re-point TSF focus to
        // our store regardless of where WPF thinks keyboard focus is (shared window HWND).
        AssertImeStoreFocus();
        ClearSelectionRangeState();

        // Vim disabled: drop any plain selection and move the caret like a text box.
        if (!_engine.VimEnabled)
        {
            ProcessVimEvents(_engine.ClearPlainSelection());
            ProcessVimEvents(_engine.SetCursorPosition(new CursorPosition(line, col)));
            return;
        }

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
        ClearSelectionRangeState();

        // Vim disabled: drive a plain (non-modal) character selection from the
        // anchor instead of entering Visual mode (which would insert a literal 'v').
        if (!_engine.VimEnabled)
        {
            if (!_isDragSelecting)
            {
                _isDragSelecting = true;
                _dragAnchor = _engine.Cursor;
            }
            ProcessVimEvents(_engine.SetPlainSelection(_dragAnchor, new CursorPosition(line, col)));
            return;
        }

        if (!_isDragSelecting)
        {
            if (new CursorPosition(line, col) == _engine.Cursor)
                return;

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

    private void OnFoldGutterClicked(int bufferLine)
    {
        _engine.CurrentBuffer.Folds.ToggleFold(bufferLine);
        UpdateAll();
        Focus();
    }

    private void OnCanvasMouseRightClicked(int line, int col)
    {
        Focus();
        ClearSelectionRangeState();
        // In Normal/Insert mode: move cursor to the click position
        if (_engine.Mode is not (VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock))
        {
            // Escape insert mode first
            if (_engine.Mode is VimMode.Insert or VimMode.Replace)
            {
                var escEvents = _engine.ProcessKey("Escape");
                ProcessVimEvents(escEvents);
            }
            var moveEvents = _engine.SetCursorPosition(new CursorPosition(line, col));
            ProcessVimEvents(moveEvents);
        }
        var menu = BuildContextMenu();
        // blame ガター右クリックでホストが何も足さなかった場合など、空メニューは出さない。
        Canvas.ContextMenu = menu.Items.Count > 0 ? menu : null;
    }

    private ContextMenu BuildContextMenu()
    {
        bool isVisual = _engine.Mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock;
        var sep = (Style)FindResource("DarkMenuSeparator");
        var itemStyle = (Style)FindResource("DarkMenuItem");

        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };

        MenuItem MakeItem(string header, string gesture, Action onClick)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, Style = itemStyle };
            item.Click += (_, _) => onClick();
            return item;
        }

        // 右クリックが blame ガター上のときは、テキスト編集/LSP 項目を出さず blame 専用メニューにする
        // （ホストがコミット固有の操作＝差分表示・履歴表示などを ContextMenuBuilding で足す）。
        var blameLine = Canvas.RightClickBlame;
        if (blameLine is null)
        {
        // ── Vim editing operations ──────────────────────────────
        menu.Items.Add(MakeItem(
            isVisual ? "Copy Selection" : "Copy Line",
            isVisual ? "y" : "yy",
            () =>
            {
                if (isVisual)
                    ProcessKey("y", false, false, false);
                else
                { ProcessKey("y", false, false, false); ProcessKey("y", false, false, false); }
            }));

        menu.Items.Add(MakeItem(
            isVisual ? "Cut Selection" : "Cut Line",
            isVisual ? "d" : "dd",
            () =>
            {
                if (isVisual)
                    ProcessKey("d", false, false, false);
                else
                { ProcessKey("d", false, false, false); ProcessKey("d", false, false, false); }
            }));

        menu.Items.Add(MakeItem("Paste", "p",
            () => ProcessKey("p", false, false, false)));

        menu.Items.Add(new Separator { Style = sep });

        menu.Items.Add(MakeItem("Undo", "u",
            () => ProcessKey("u", false, false, false)));
        menu.Items.Add(MakeItem("Redo", "Ctrl+R",
            () => ProcessKey("r", true, false, false)));

        menu.Items.Add(new Separator { Style = sep });

        menu.Items.Add(MakeItem("Select All", "ggVG", () =>
        {
            ProcessKey("g", false, false, false);
            ProcessKey("g", false, false, false);
            ProcessKey("V", false, false, false);
            ProcessKey("G", false, false, false);
        }));

        menu.Items.Add(new Separator { Style = sep });
        menu.Items.Add(MakeItem(
            Canvas.WrapLines ? "Word Wrap: Off" : "Word Wrap: On",
            "Alt+Z",
            ToggleWordWrap));

        // ── LSP operations (only when a language server is connected) ──
        if (_lspManager.IsConnected)
        {
            menu.Items.Add(new Separator { Style = sep });

            menu.Items.Add(MakeItem("Go to Definition", "gd",
                () => _ = HandleGoToDefinitionAsync()));
            menu.Items.Add(MakeItem("Find References", "LSP",
                () => _ = HandleFindReferencesAsync()));
            menu.Items.Add(MakeItem("Rename Symbol", "LSP",
                () => _ = HandleRenameAsync()));
            menu.Items.Add(new Separator { Style = sep });
            menu.Items.Add(MakeItem("Hover Info", "K",
                () => _ = ShowLspHoverAsync()));
            menu.Items.Add(MakeItem("Format Document", ":Format",
                () => _ = HandleFormatDocumentAsync()));
            // Capture the selected lines now — invoking the menu item clears the selection.
            if (_engine.Selection is { IsEmpty: false } fmtSel)
            {
                var fmtLines = (fmtSel.NormalizedStart.Line, fmtSel.NormalizedEnd.Line);
                menu.Items.Add(MakeItem("Format Selection", ":'<,'>Format",
                    () => _ = HandleFormatDocumentAsync(fmtLines)));
            }
        }
        } // end: not a blame-gutter right-click

        // ── Host-provided items ──────────────────────────────────
        // Let the embedding application append its own entries (e.g. "Ask AI", "Search the web")
        // that act on the current selection, or commit actions when the right-click was on the blame
        // gutter (BlameLine is non-null then). They are added after the editor's own items.
        if (ContextMenuBuilding is { } handler)
        {
            var before = menu.Items.Count;
            handler(this, new EditorContextMenuBuildingEventArgs(SelectedText, HasSelection, menu, blameLine));

            // Apply the dark menu styling to any items the host added without their own style,
            // so host entries match the editor's native menu look.
            for (int i = before; i < menu.Items.Count; i++)
            {
                if (menu.Items[i] is MenuItem { Style: null } mi)
                    mi.Style = itemStyle;
                else if (menu.Items[i] is Separator { Style: null } s)
                    s.Style = sep;
            }
        }

        return menu;
    }

    private void OnPreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        if (_customTextStoreActive) return;

        // Ensure the TSF caret tracker is attached to the focused context before the
        // composition becomes active, so arrow-key caret moves inside it are observed.
        EnsureTsfTextEditSink();
        UpdateImeCompositionOverlay(e);
    }

    private void OnPreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        if (_customTextStoreActive) return;
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

        if (string.IsNullOrEmpty(text))
        {
            ScheduleImeCompositionOverlayClear();
            return;
        }

        CancelScheduledImeCompositionOverlayClear();
        _imeCompositionSeq++;
        _lastImeCompositionText = text;
        Canvas.SetImeCompositionText(_lastImeCompositionText, GetImeCursorPos());
        UpdateImeWindowPos();
        if (PresentationSource.FromVisual(this) is HwndSource source)
            UpdateImeCandidateOverlay(source.Handle, IntPtr.Zero);
    }

    private void ClearImeCompositionOverlay()
    {
        CancelScheduledImeCompositionOverlayClear();
        _lastImeCompositionText = string.Empty;
        Canvas.SetImeCompositionText(string.Empty);
        Canvas.SetImeClauses([], -1, -1);
        ClearImeCandidateOverlay();
    }

    private void ScheduleImeCompositionOverlayClear()
    {
        _imeOverlayClearTimer ??= CreateImeOverlayClearTimer();
        _imeOverlayClearTimer.Stop();
        _imeOverlayClearTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateImeOverlayClearTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ClearImeCompositionOverlay();
        };
        return timer;
    }

    private void CancelScheduledImeCompositionOverlayClear()
        => _imeOverlayClearTimer?.Stop();

    private bool HasActiveImeComposition()
        => !string.IsNullOrEmpty(_lastImeCompositionText);

    /// <summary>
    /// Inserts IME-committed (or directly typed) <paramref name="text"/> through the engine,
    /// one character at a time, mirroring the legacy OnTextInput insert path. Used both by
    /// WPF's OnTextInput (ASCII) and by the custom TSF store's composition-commit callback.
    /// </summary>
    private void InsertCommittedText(string text)
    {
        var mode = _engine.Mode;
        if (!IsImeTextInputMode(mode))
            return;

        foreach (var ch in text)
        {
            if (ch < 32) continue; // Skip control chars
            ProcessKey(ch.ToString(), false, false, false);
        }

        // Escape-commit (ImeProcessed Escape) finalised the composition via an injected
        // Return; now that the committed text is inserted, exit Insert. This is the single
        // completion point for the Escape-commit flow.
        if (_exitInsertAfterImeCommit && (mode is VimMode.Insert or VimMode.Replace))
        {
            _exitInsertAfterImeCommit = false;
            _snippetTabStopManager.Clear();
            ProcessKey("Escape", false, false, false);
        }
    }

    // ─────────────── Custom TSF text store wiring ───────────────

    /// <summary>
    /// Installs our own <see cref="Editor.Controls.Ime.EditorTextStore"/> as the IME input
    /// target for the hosting window. Returns false (leaving WPF's default IME path intact)
    /// if any step fails, so a TSF failure never leaves the editor without input.
    /// </summary>
    private bool InitializeCustomTextStore()
    {
        if (_customTextStoreActive) return true;

        IntPtr threadMgrPtr = IntPtr.Zero;
        try
        {
            if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == IntPtr.Zero)
                return false;
            if (TF_GetThreadMgr(out threadMgrPtr) < 0 || threadMgrPtr == IntPtr.Zero)
                return false;

            var threadMgr = (Editor.Controls.Ime.ITfThreadMgrTs)Marshal.GetObjectForIUnknown(threadMgrPtr);
            if (threadMgr.Activate(out uint clientId) < 0)
                return false;
            if (threadMgr.CreateDocumentMgr(out var docMgr) < 0 || docMgr == null)
                return false;

            var store = new Editor.Controls.Ime.EditorTextStore(this);
            if (docMgr.CreateContext(clientId, 0, store, out var ctx, out _) < 0 || ctx == null)
                return false;

            // ITfContextOwnerCompositionSink lets the store learn when composition ends
            // (used to clear stale text/candidates instead of leaving them behind). It's an
            // enhancement on top of the core text store, not a requirement for it: some TIPs
            // fail this particular AdviseSink, and that failure must not take down the whole
            // custom TSF text store — doing so silently falls back to the legacy IMM path,
            // which shows the editor's own candidate UI instead of the TIP's native one.
            var contextSource = (Editor.Controls.Ime.ITfSourceTs)ctx;
            uint compositionCookie = TF_INVALID_COOKIE;
            try
            {
                var compositionSinkIid = Editor.Controls.Ime.TsfConst.IID_ITfContextOwnerCompositionSink;
                if (contextSource.AdviseSink(ref compositionSinkIid, store, out uint cookie) >= 0)
                    compositionCookie = cookie;
            }
            catch { /* composition-end notification is best-effort */ }

            if (docMgr.Push(ctx) < 0)
            {
                if (compositionCookie != TF_INVALID_COOKIE)
                    _ = contextSource.UnadviseSink(compositionCookie);
                return false;
            }

            // Bind our document manager to the window so the IME composes into our store,
            // then focus it explicitly for the current keyboard-focus state.
            _ = threadMgr.AssociateFocus(source.Handle, docMgr, out var prevDocMgr);
            _ = threadMgr.SetFocus(docMgr);

            _tsfStoreThreadMgr = threadMgr;
            _tsfStoreDocMgr = docMgr;
            _tsfStorePrevDocMgr = prevDocMgr;
            _tsfStoreContext = ctx;
            _tsfStoreContextSource = contextSource;
            _tsfStoreCompositionCookie = compositionCookie;
            _customTextStore = store;
            _customTextStoreActive = true;
            EnsureTsfTextEditSink();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (threadMgrPtr != IntPtr.Zero)
                Marshal.Release(threadMgrPtr);
        }
    }

    /// <summary>
    /// Force the IME (the TSF thread-manager focus) to point at THIS editor's document
    /// store. In an embedding host the window HWND is shared by several editors (and by
    /// the host's own native text controls); the window-level <c>AssociateFocus</c>
    /// default is simply whichever editor loaded last, and WPF keyboard focus may never
    /// land on the editor (<see cref="UIElement.IsKeyboardFocusWithin"/> stays false) even
    /// while it is the one receiving keystrokes. So we re-point TSF focus the moment this
    /// editor becomes the active IME target (enters a text-input mode, is clicked, or gains
    /// focus) rather than trusting a WPF focus event or the window-global association.
    /// </summary>
    private void AssertImeStoreFocus()
    {
        if (!_customTextStoreActive || _tsfStoreThreadMgr == null || _tsfStoreDocMgr == null) return;
        try { _ = _tsfStoreThreadMgr.SetFocus(_tsfStoreDocMgr); }
        catch { /* focus hand-off is best-effort */ }
    }

    /// <summary>Re-focuses our document manager when the control regains keyboard focus.</summary>
    private void FocusCustomTextStore()
    {
        AssertImeStoreFocus();

        // WPF's own TextServicesContext may set the thread's TSF focus to its default
        // document manager *after* this focus change settles; re-assert once more so our
        // store wins. Harmless/idempotent in a standalone editor-only window.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            () => AssertImeStoreFocus());
    }

    private void DisposeCustomTextStore()
    {
        if (!_customTextStoreActive) return;
        _customTextStoreActive = false;

        try
        {
            if (_tsfStoreContextSource != null && _tsfStoreCompositionCookie != TF_INVALID_COOKIE)
                _ = _tsfStoreContextSource.UnadviseSink(_tsfStoreCompositionCookie);
        }
        catch { }

        try
        {
            // Hand the window association back to whoever held it before us — but only if
            // we are the editor that currently owns keyboard focus. Several editors can
            // share one window HWND in an embedding host (tabs / splits / a host's other
            // editor panes); a backgrounded editor being unloaded must NOT overwrite the
            // *focused* editor's live association with our now-stale previous document
            // manager (which would silently kill the focused editor's IME input).
            if (_tsfStoreThreadMgr != null && IsKeyboardFocusWithin
                && PresentationSource.FromVisual(this) is HwndSource source && source.Handle != IntPtr.Zero)
                _ = _tsfStoreThreadMgr.AssociateFocus(source.Handle, _tsfStorePrevDocMgr, out _);
        }
        catch { }

        try { _ = _tsfStoreDocMgr?.Pop(0); } catch { }
        try { InputMethod.SetIsInputMethodEnabled(this, true); } catch { }

        _tsfStoreCompositionCookie = TF_INVALID_COOKIE;

        var ctx = _tsfStoreContext;
        var src = _tsfStoreContextSource;
        var prev = _tsfStorePrevDocMgr;
        var docMgr = _tsfStoreDocMgr;
        var threadMgr = _tsfStoreThreadMgr;
        _tsfStoreContext = null;
        _tsfStoreContextSource = null;
        _tsfStorePrevDocMgr = null;
        _tsfStoreDocMgr = null;
        _tsfStoreThreadMgr = null;
        _customTextStore = null;

        // src is a QI of ctx (same COM identity → same RCW), so release it only once.
        if (src != null && Marshal.IsComObject(src)) Marshal.ReleaseComObject(src);
        if (ctx != null && !ReferenceEquals(ctx, src) && Marshal.IsComObject(ctx)) Marshal.ReleaseComObject(ctx);
        if (prev != null && Marshal.IsComObject(prev)) Marshal.ReleaseComObject(prev);
        if (docMgr != null && Marshal.IsComObject(docMgr)) Marshal.ReleaseComObject(docMgr);
        if (threadMgr != null && Marshal.IsComObject(threadMgr)) Marshal.ReleaseComObject(threadMgr);
    }

    // ─────────────── IEditorTextStoreHost ───────────────

    private static bool IsImeTextInputMode(VimMode mode)
        => mode is VimMode.Insert or VimMode.Replace or VimMode.Command
            or VimMode.SearchForward or VimMode.SearchBackward;

    bool Editor.Controls.Ime.IEditorTextStoreHost.IsCompositionAllowed
        => IsImeTextInputMode(_engine.Mode);

    IntPtr Editor.Controls.Ime.IEditorTextStoreHost.WindowHandle
        => PresentationSource.FromVisual(this) is HwndSource s ? s.Handle : IntPtr.Zero;

    void Editor.Controls.Ime.IEditorTextStoreHost.OnCompositionUpdated(string text, int caret)
    {
        if (!IsImeTextInputMode(_engine.Mode))
        {
            ClearImeCompositionOverlay();
            return;
        }
        // Attach the read-only edit sink to our (now focused) context so clause boundaries
        // and the focused clause are read from GUID_PROP_ATTRIBUTE and rendered.
        EnsureTsfTextEditSink();
        _imeCompositionSeq++;
        text ??= string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            ScheduleImeCompositionOverlayClear();
            return;
        }

        CancelScheduledImeCompositionOverlayClear();
        _lastImeCompositionText = text;
        Canvas.SetImeCompositionText(_lastImeCompositionText, caret);
        // The caret advanced with the composition (or a clause-caret move that changes no
        // text); re-anchor the native candidate window to the new on-screen position.
        NotifyImeLayoutChanged();
    }

    void Editor.Controls.Ime.IEditorTextStoreHost.OnCompositionCommitted(string text)
    {
        _imeCompositionSeq++;
        _imeInsertBuffer.Clear();
        InsertCommittedText(text);
        ClearImeCompositionOverlay();
    }

    void Editor.Controls.Ime.IEditorTextStoreHost.OnCompositionCanceled()
    {
        _imeCompositionSeq++;
        _imeInsertBuffer.Clear();
        ScheduleImeCompositionOverlayClear();
    }

    /// <summary>
    /// Re-anchors the native IME candidate window while a composition is active. The custom
    /// TSF store positions that window from <see cref="IEditorTextStoreHost.TryGetCaretScreenRect"/>,
    /// which TSF only re-queries after an OnLayoutChange — so a scroll or resize mid-composition
    /// would otherwise leave the candidate list stranded (or hidden if the caret scrolled away).
    /// </summary>
    private void NotifyImeLayoutChanged()
    {
        if (_customTextStoreActive && _customTextStore is { IsComposing: true } store)
            store.NotifyLayoutChange();
    }

    bool Editor.Controls.Ime.IEditorTextStoreHost.TryGetCaretScreenRect(out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        try
        {
            if (Canvas.ActualWidth <= 0 || Canvas.ActualHeight <= 0) return false;
            var p = Canvas.GetImeCaretPixelPosition();
            var topPt = Canvas.PointToScreen(p);
            var botPt = Canvas.PointToScreen(new Point(p.X, p.Y + Canvas.LineHeight));
            left = (int)topPt.X;
            top = (int)topPt.Y;
            right = (int)topPt.X + 1;
            bottom = (int)botPt.Y;
            return true;
        }
        catch { return false; }
    }

    bool Editor.Controls.Ime.IEditorTextStoreHost.TryGetClientScreenRect(out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        try
        {
            if (Canvas.ActualWidth <= 0 || Canvas.ActualHeight <= 0) return false;
            var tl = Canvas.PointToScreen(new Point(0, 0));
            var br = Canvas.PointToScreen(new Point(Canvas.ActualWidth, Canvas.ActualHeight));
            left = (int)tl.X;
            top = (int)tl.Y;
            right = (int)br.X;
            bottom = (int)br.Y;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Clears the in-editor composition overlay if no composition update or commit
    /// happened after a Backspace that emptied the composition. WPF raises no
    /// text-composition update for the final 1→0 char deletion, so without this the
    /// last character stays drawn until the next keystroke.
    /// </summary>
    private void ScheduleImeOverlayResync()
    {
        int seq = _imeCompositionSeq;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            // A real update/commit bumps _imeCompositionSeq — if it changed, the
            // overlay is already in sync and must not be cleared.
            if (_imeCompositionSeq == seq && !string.IsNullOrEmpty(_lastImeCompositionText))
                ScheduleImeCompositionOverlayClear();
        }));
    }

    // ─────────────── Key handling ───────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Any key dismisses an open debug DataTip (it tracks the mouse-hovered value).
        if (_dataTipPopup is { IsOpen: true }) HideDataTip();

        // Reset the "Vim already handled this key" flag at the start of EVERY key.
        // OnKeyDown also resets it, but OnKeyDown does NOT fire for Key.ImeProcessed
        // keys — so when a Normal/Visual-mode IME key is handled here (which sets the
        // flag and marks the event handled), nothing would clear it. A stale `true`
        // then survives into a later Insert-mode composition and makes OnTextInput drop
        // the committed text (the kana vanishes on the confirming Enter). PreviewKeyDown
        // fires for every key including ImeProcessed, so resetting here is the reliable
        // single point. The same-key IME echo still arrives before the next key's
        // PreviewKeyDown, so legitimate echo-dropping is preserved.
        _keyDownHandledByVim = false;

        // Resolve actual key — handles IME (ImeProcessed) and Alt combos (System)
        Key actualKey = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        // Handle keys that WPF normally consumes (Tab, etc.)
        var mode = _engine.Mode;

        // Ctrl+F / Ctrl+H — open (or refocus) the VSCode-style Find/Replace bar.
        // Handled at the very top so it works in every Vim mode and independently of
        // VimEnabled. Skipped mid Ctrl+X Ctrl+F path-completion cycling, where Ctrl+F
        // instead advances to the next candidate (see the ctrlXMode handling below).
        bool ctrlDown = (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0;
        if (ctrlDown && actualKey == Key.F && !(_pathCompletionManager.Visible && mode == VimMode.Insert))
        {
            ShowFindReplace(_findBarVisible && ReplaceRow.Visibility == Visibility.Visible);
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
        }
        if (ctrlDown && actualKey == Key.H)
        {
            ShowFindReplace(withReplace: true);
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
        }

        // While the Find/Replace bar's own text boxes have focus, let WPF (and their
        // dedicated PreviewKeyDown handlers) process the key instead of routing it
        // through Vim.
        if (_findBarVisible && (FindSearchBox.IsKeyboardFocusWithin || FindReplaceBox.IsKeyboardFocusWithin))
            return;

        // Ctrl+Space → completion. Handled here (not only OnKeyDown) so it also works
        // while the IME is active, where the key arrives as Key.ImeProcessed and
        // OnKeyDown never fires. Marking the preview event handled suppresses the
        // bubbling KeyDown, so the OnKeyDown branch won't double-trigger.
        if (actualKey == Key.Space
            && (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0
            && (mode == VimMode.Insert || mode == VimMode.Replace)
            && _engine.VimEnabled)
        {
            TriggerCtrlSpaceCompletion();
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
        }

        if (mode == VimMode.Insert || mode == VimMode.Replace)
        {
            if (actualKey == Key.Tab && HasActiveImeComposition())
            {
                _imeInsertBuffer.Clear();
                return;
            }

            if (e.Key == Key.ImeProcessed && actualKey == Key.Escape)
            {
                // Commit the active composition by letting the IME finalize it itself:
                // inject a real Return, which the IME consumes to commit the kana (no
                // newline) and — crucially — tears the TSF-backed composition down
                // cleanly. IMM-level CPS_CANCEL / emptying the string does NOT reach
                // the TSF composition, so leftover kana otherwise resurfaces on the
                // next keystroke. The committed text arrives via OnTextInput, which is
                // the single completion point: it inserts the text and then exits
                // Insert (Input priority, queue order — no Background inversion).
                _imeInsertBuffer.Clear();
                _exitInsertAfterImeCommit = true;
                SendVirtualKeyToIme((ushort)KeyInterop.VirtualKeyFromKey(Key.Return));
                // Safety net: if the IME produced no commit text (e.g. empty
                // composition), OnTextInput never fires — exit Insert anyway so we are
                // not stuck. Background priority so it runs only AFTER the injected
                // Return's commit has been delivered; it no-ops in the normal case
                // because OnTextInput already cleared the flag.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!_exitInsertAfterImeCommit) return;
                    _exitInsertAfterImeCommit = false;
                    _snippetTabStopManager.Clear();
                    if (_engine.Mode is VimMode.Insert or VimMode.Replace)
                        ProcessKey("Escape", false, false, false);
                }));
                e.Handled = true;
                return;
            }

            // Backspace inside a composition: when it deletes the LAST char (1→0), WPF
            // raises no text-composition update, so the in-editor overlay would linger.
            // Schedule a deferred resync that clears the overlay only if no update/commit
            // arrives after the IME processes this Backspace. Mode-independent so it also
            // covers plain (Vim-disabled) Insert.
            if (e.Key == Key.ImeProcessed && actualKey == Key.Back
                && !string.IsNullOrEmpty(_lastImeCompositionText))
                ScheduleImeOverlayResync();

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
            if (e.Key == Key.ImeProcessed && _engine.VimEnabled)
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
                            ArmMappingTimeout();
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
                                ArmMappingTimeout();
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
                ArmMappingTimeout();
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
                    // Let OnKeyDown handle Tab when a completion popup is visible.
                    if (_lspManager.CompletionVisible || _pathCompletionManager.Visible) return;

                    bool shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0;

                    // Snippet: Shift+Tab → go back to previous tab stop
                    if (shift && _snippetTabStopManager.TryGoBack())
                    {
                        e.Handled = true;
                        return;
                    }

                    // Snippet: Tab → advance to next tab stop (if active snippet)
                    if (!shift && _snippetTabStopManager.TryAdvance())
                    {
                        e.Handled = true;
                        return;
                    }

                    // No active snippet or past last stop — try to expand a trigger word
                    if (!shift && _snippetTabStopManager.TryExpandTriggerAtCursor())
                    {
                        e.Handled = true;
                        return;
                    }

                    // Fall through to normal Tab behaviour (indent). Shift+Tab is
                    // routed too so markdown list lines can be outdented; the engine
                    // treats it as a no-op elsewhere. Either way we mark it handled
                    // to suppress WPF focus traversal.
                    ProcessKey("Tab", false, shift, false);
                    e.Handled = true;
                }
                else if (actualKey == Key.Return)
                {
                    // Let OnKeyDown handle Enter when a completion popup is visible.
                    if (_lspManager.CompletionVisible || _pathCompletionManager.Visible) return;
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

        // In Command mode, intercept Tab for completion cycling (prevent WPF focus traversal)
        if (actualKey == Key.Tab && mode == VimMode.Command)
        {
            bool ctrl = (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (e.KeyboardDevice.Modifiers & ModifierKeys.Alt) != 0;
            ProcessKey("Tab", ctrl, shift, alt);
            e.Handled = true;
            return;
        }

        // Handle Escape — but not when IME is composing (ImeProcessed Escape cancels
        // the composition; a subsequent Escape will then exit Insert mode normally).
        if (actualKey == Key.Escape && e.Key != Key.ImeProcessed)
        {
            ClearImeCompositionOverlay();
            // Clear any active snippet session on Escape
            _snippetTabStopManager.Clear();
            // Multi-cursor: Escape in Normal mode exits multi-cursor mode instead of passing to engine
            if (_multiCursorManager.IsActive && mode == VimMode.Normal)
            {
                _multiCursorManager.Exit();
                e.Handled = true;
                return;
            }
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
                    // Suppress the duplicate OnTextInput the IME still emits for this
                    // key (see below).
                    _keyDownHandledByVim = true;
                    e.Handled = true;
                    return;
                }
            }

            var keyStr = GetVimKey(actualKey, shift);
            if (keyStr != null)
            {
                ProcessKey(keyStr, ctrl, shift, alt);
                // With IME ON the same key is also committed as text — e.g. Space
                // becomes a full-width '　' (U+3000) that arrives via OnTextInput and
                // is normalized back to ' '. Marking it handled here makes OnTextInput
                // drop that echo; otherwise the key is fed to the engine twice (a
                // stray second leader space corrupts maps like `<Space>w`, and `i`
                // would insert a literal 'い'). Mirrors the OnKeyDown path.
                _keyDownHandledByVim = true;
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

        // Ctrl+Space triggers completion in Insert mode. Skipped when Vim is
        // disabled — plain mode parks the engine in Insert as a resting state but
        // must not expose completion sub-modes. Note: with IME ON this is handled
        // in OnPreviewKeyDown instead (OnKeyDown doesn't fire for ImeProcessed).
        if (ctrl && key == Key.Space && mode == VimMode.Insert && _engine.VimEnabled)
        {
            TriggerCtrlSpaceCompletion();
            e.Handled = true;
            return;
        }

        // LSP: completion popup navigation (never active while Vim is disabled).
        // Skip while the IME is mid-composition (e.Key == Key.ImeProcessed): the
        // Enter/Tab/↑/↓ belong to the IME (confirm conversion, move candidate) and
        // must not be hijacked as completion actions — doing so steals the commit
        // Enter and sets _keyDownHandledByVim, which then drops the committed text
        // in OnTextInput. Mirrors the same guard in OnPreviewKeyDown.
        // Filesystem path completion popup navigation (LSP-independent). Same IME
        // guard as the LSP popup below.
        if (_pathCompletionManager.Visible && mode == VimMode.Insert && _engine.VimEnabled
            && e.Key != Key.ImeProcessed && !HasActiveImeComposition())
        {
            if (_pathCompletionNavigator.TryHandle(key, ctrl))
            {
                e.Handled = true;
                return;
            }
        }

        if (_lspManager.CompletionVisible && mode == VimMode.Insert && _engine.VimEnabled
            && e.Key != Key.ImeProcessed && !HasActiveImeComposition())
        {
            if (_completionNavigator.TryHandle(key, ctrl))
            {
                e.Handled = true;
                return;
            }
        }

        // LSP: code actions popup navigation (Normal mode). Same IME guard as above —
        // never consume keys the IME is still composing with.
        if (_lspManager.CodeActionsVisible && mode == VimMode.Normal
            && e.Key != Key.ImeProcessed)
        {
            if (_codeActionNavigator.TryHandle(key, ctrl))
            {
                e.Handled = true;
                return;
            }
        }

        if (!ctrl && alt && shift && mode is VimMode.Normal or VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock &&
            (key == Key.Right || key == Key.Left))
        {
            _ = HandleSelectionRangeAsync(expand: key == Key.Right);
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
        }

        if (!ctrl && alt && !shift && key == Key.Z)
        {
            ToggleWordWrap();
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
        }

        // ─── Multi-cursor: Ctrl+Alt+Down / Ctrl+Alt+Up — add cursor below/above ───
        if (ctrl && alt && key == Key.Down)
        {
            _multiCursorManager.AddCursorVertical(+1);
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
        }
        if (ctrl && alt && key == Key.Up)
        {
            _multiCursorManager.AddCursorVertical(-1);
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
        }

        // ─── Multi-cursor: Ctrl+D — add cursor at next occurrence of word under cursor ───
        if (ctrl && !alt && key == Key.D && mode == VimMode.Normal)
        {
            _multiCursorManager.AddCursorAtNextOccurrence();
            _keyDownHandledByVim = true;
            e.Handled = true;
            return;
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

        // Navigation keys in all modes. When Vim is disabled the editor is a plain
        // text box, so Home/End must move the caret (engine "Home"/"End"), not be
        // fed as the Vim motions "0"/"$" which would insert literal characters.
        keyStr = key switch
        {
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Home => _engine.VimEnabled ? "0" : "Home",
            Key.End => _engine.VimEnabled ? "$" : "End",
            Key.PageUp => ctrl ? "b" : null,
            Key.PageDown => ctrl ? "f" : null,
            Key.F1 => null,
            Key.F2 => "F2",
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
        _imeCompositionSeq++;
        _imeInsertBuffer.Clear();

        if (_keyDownHandledByVim)
        {
            _keyDownHandledByVim = false;
            ClearImeCompositionOverlay();
            return;
        }

        var mode = _engine.Mode;
        if (mode == VimMode.Insert || mode == VimMode.Replace ||
            mode == VimMode.Command || mode == VimMode.SearchForward || mode == VimMode.SearchBackward)
        {
            InsertCommittedText(e.Text);
            ClearImeCompositionOverlay();
            e.Handled = true;
            return;
        }

        ClearImeCompositionOverlay();

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

    /// <summary>
    /// Intercepts a paste keystroke (p/P in Normal mode, Ctrl+V in Insert mode) when the current
    /// file is Markdown and the clipboard holds an image: saves the image and inserts a Markdown
    /// link in place of the paste. Returns true when it consumed the keystroke.
    /// </summary>
    private bool TryHandleImagePaste(string key, bool ctrl, bool alt)
    {
        var mode = _engine.Mode;
        bool isNormalPaste = mode is VimMode.Normal && !ctrl && !alt && (key == "p" || key == "P");
        bool isInsertPaste = mode is VimMode.Insert && ctrl && !alt && key == "v";
        if (!isNormalPaste && !isInsertPaste) return false;

        var filePath = _engine.CurrentBuffer.FilePath;
        if (!ImagePasteHandler.IsMarkdownFile(filePath) || !ImagePasteHandler.ClipboardHasImage())
            return false;

        var link = _imagePasteHandler.TryBuildMarkdownLink(filePath, out var error);
        if (link == null)
        {
            if (error != null) ActiveStatusBar.UpdateStatus(error);
            return false; // fall through to normal paste
        }

        var events = _engine.PasteText(link, after: key != "P");
        ProcessVimEvents(events);
        if (events.Any(e => e.Type == VimEventType.TextChanged))
            _lspManager.OnTextChanged(_engine.CurrentBuffer.Text.GetText());
        UpdateAll();
        return true;
    }

    private void ProcessKey(string key, bool ctrl, bool shift, bool alt)
    {
        // Never let a single keystroke crash the host app. A bug anywhere in the engine or the
        // event handling would otherwise surface as an unhandled exception and take the process
        // down; instead, swallow it (logged for diagnosis) and keep the editor usable.
        try
        {
            ProcessKeyCore(key, ctrl, shift, alt);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VimEditorControl: ProcessKey('{key}', ctrl:{ctrl}, shift:{shift}, alt:{alt}) failed: {ex}");
            try { Canvas?.InvalidateVisual(); } catch { /* rendering guarded separately */ }
        }
    }

    private void ProcessKeyCore(string key, bool ctrl, bool shift, bool alt)
    {
        Canvas.ResetCursorBlink();
        ClearSelectionRangeState();

        // Clipboard image → Markdown link: when pasting (p/P in Normal, Ctrl+V in Insert)
        // into a Markdown file while the clipboard holds an image, save the image per
        // ImagePasteOptions and insert a link instead of the (empty) clipboard text.
        if (TryHandleImagePaste(key, ctrl, alt))
            return;

        bool hadCompletion = _lspManager.CompletionVisible;

        var events = _engine.ProcessKey(key, ctrl, shift, alt);
        ProcessVimEvents(events);

        // Multi-cursor: apply the same edit at each extra cursor position
        if (_multiCursorManager.IsActive && _multiCursorManager.Count > 0 && !ctrl && !alt)
        {
            bool isInsertEdit = (_engine.Mode == VimMode.Insert || events.Any(e => e.Type == VimEventType.TextChanged))
                && (key.Length == 1 || key == "Back" || key == "Delete" || key == "Return");
            if (isInsertEdit)
                _multiCursorManager.ApplyKeyToExtraCursors(key);
        }

        // LSP: notify text changes
        if (events.Any(e => e.Type == VimEventType.TextChanged))
            _lspManager.OnTextChanged(_engine.CurrentBuffer.Text.GetText());

        // LSP: update completion popup after each Insert-mode keypress. Plain mode
        // (Vim disabled) sits in Insert as a resting state but must not drive LSP
        // completion / signature help, so gate on VimEnabled.
        if (_engine.Mode == VimMode.Insert && _engine.VimEnabled && !ctrl && !alt && key.Length == 1)
        {
            char ch = key[0];

            // A path delimiter (space, quote, …) ends the path and dismisses the popup.
            if (!_pathCompletionManager.Suppressed && PathCompletionManager.IsPathDelimiter(ch))
                _pathCompletionManager.Hide();

            // Keep an active path popup alive while the token is being refined
            // (forced), but only auto-START one when a separator has been typed.
            bool keepPath = !_pathCompletionManager.Suppressed
                && !PathCompletionManager.IsPathDelimiter(ch)
                && _pathCompletionManager.Update(forced: _pathCompletionManager.Visible);

            if (_pathCompletionManager.Suppressed)
            {
                // Programmatic insert from accepting a path item — drive nothing.
            }
            else if (keepPath)
            {
                // A filesystem path popup is active; it owns the completion UI, so
                // suppress LSP completion / signature help for this keystroke.
                _lspManager.HideCompletion();
                _completionDebounce.Stop();
                _lspManager.HideSignatureHelp();
            }
            else if (hadCompletion)
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
            _pathCompletionManager.Hide();
        }
        else if (!ctrl && !alt && (key == "Back" || key == "Delete"))
        {
            // Backspace/Delete: while a path popup session is open, keep it live
            // (forced) so refining the token doesn't dismiss it; otherwise re-filter
            // the LSP popup.
            bool wasPath = _pathCompletionManager.Visible;
            if (!_pathCompletionManager.Suppressed && wasPath && _pathCompletionManager.Update(forced: true))
            {
                // path popup refreshed
            }
            // Backspace/Delete in insert with popup: re-filter
            else if (!wasPath && hadCompletion && _engine.Mode == VimMode.Insert)
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

        ArmMappingTimeout();
    }

    // After a key is processed, a multi-key mapping (e.g. `jj`) may be half-typed
    // and its prefix held back — either in the engine (`_pendingMappedInput`, the
    // IME-OFF path) or in `_imeInsertBuffer` (the IME-ON path). Arm a 'timeoutlen'
    // timer so that if no following key arrives the prefix is emitted instead of
    // hanging forever; the next keypress cancels and reschedules this.
    private void ArmMappingTimeout()
    {
        _mappingTimeout.Stop();
        if (!_engine.Options.Timeout)
            return;
        if (!_engine.HasPendingMappedInput && _imeInsertBuffer.Count == 0)
            return;

        _mappingTimeout.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _engine.Options.TimeoutLen));
        _mappingTimeout.Start();
    }

    private void FlushMappingTimeout()
    {
        _mappingTimeout.Stop();

        // IME-ON insert path: replay the held keys to the IME so a lone prefix
        // (e.g. "j") can still be typed/composed, or insert it as literal text
        // when it can't be replayed.
        if (_imeInsertBuffer.Count > 0)
        {
            bool canReplay = _imeInsertBuffer.All(k => k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]));
            if (canReplay)
                ReplayImeKeySequence([.. _imeInsertBuffer]);
            else
                FlushImeInsertBuffer();
            return;
        }

        var events = _engine.FlushPendingMappings();
        if (events.Count == 0)
            return;

        ProcessVimEvents(events);
        if (events.Any(e => e.Type == VimEventType.TextChanged))
            _lspManager.OnTextChanged(_engine.CurrentBuffer.Text.GetText());
    }

    private async Task TriggerCompletionAsync(int line, int col)
    {
        var msg = await _lspManager.RequestCompletionAsync(line, col);
        if (!string.IsNullOrEmpty(msg))
        {
            ActiveStatusBar.UpdateStatus(msg);
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
            ActiveStatusBar.UpdateStatus(firstLine.Trim());
        }
    }

    private async Task HandleSelectionRangeAsync(bool expand)
    {
        if (!_lspManager.IsConnected || !_lspManager.IsDocumentReady)
        {
            ActiveStatusBar.UpdateStatus("Selection range: LSP not ready");
            return;
        }

        if (_lspSelectionRangeSelections.Count == 0)
        {
            _lspSelectionRangeOrigin = _engine.Cursor;
            var root = await _lspManager.RequestSelectionRangeAsync(
                _lspSelectionRangeOrigin.Value.Line,
                _lspSelectionRangeOrigin.Value.Column);

            if (root is null)
            {
                ActiveStatusBar.UpdateStatus("Selection range: not supported");
                return;
            }

            var text = _engine.CurrentBuffer.Text;
            _lspSelectionRangeSelections = SelectionRangeNavigator.BuildSelections(
                root,
                text.LineCount,
                line => text.GetLineLength(line));
            _lspSelectionRangeIndex = -1;

            if (_lspSelectionRangeSelections.Count == 0)
            {
                ClearSelectionRangeState();
                ActiveStatusBar.UpdateStatus("Selection range: not available");
                return;
            }
        }

        int? nextIndex = expand
            ? SelectionRangeNavigator.GetExpansionIndex(
                _lspSelectionRangeSelections,
                _engine.Selection,
                _lspSelectionRangeIndex)
            : SelectionRangeNavigator.GetShrinkIndex(
                _lspSelectionRangeSelections,
                _engine.Selection,
                _lspSelectionRangeIndex);

        if (nextIndex is null)
        {
            ActiveStatusBar.UpdateStatus(expand ? "Selection range: outermost range" : "Selection range: innermost range");
            return;
        }

        _lspSelectionRangeIndex = nextIndex.Value;
        var events = _engine.SetSelection(_lspSelectionRangeSelections[_lspSelectionRangeIndex]);
        ProcessVimEvents(events);
        ActiveStatusBar.UpdateStatus($"Selection range: {_lspSelectionRangeIndex + 1}/{_lspSelectionRangeSelections.Count}");
    }

    private void ClearSelectionRangeState()
    {
        _lspSelectionRangeSelections = [];
        _lspSelectionRangeIndex = -1;
        _lspSelectionRangeOrigin = null;
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

        // Delete the partial prefix with Backspace keys
        int deleteCount = col - wordStart;
        for (int i = 0; i < deleteCount; i++)
            ProcessKey("Back", false, false, false);

        var insertText = item.InsertText ?? item.Label;

        // LSP Snippet format (insertTextFormat == 2): expand tab stops
        if (item.TextFormat == InsertTextFormat.Snippet && insertText.Contains('$'))
        {
            var insertCursor = _engine.Cursor;
            var expansion = SnippetManager.ExpandLsp(
                insertText,
                insertCursor.Line, insertCursor.Column,
                _engine.Options.TabStop, _engine.Options.ExpandTab);
            _snippetTabStopManager.Apply(expansion);
            return;
        }

        foreach (var ch in insertText)
            ProcessKey(ch.ToString(), false, false, false);
    }

    // ─── Filesystem path completion (Insert mode, LSP-independent) ─────────────

    /// <summary>
    /// Ctrl+Space handler. Offers filesystem path completion when the cursor is in
    /// a path-like token (or always, when no language server is connected); falls
    /// back to LSP completion otherwise. Shared by OnKeyDown (IME off) and
    /// OnPreviewKeyDown (IME on, where OnKeyDown never fires).
    /// </summary>
    private void TriggerCtrlSpaceCompletion()
    {
        // forced only when there is no server: then a bare token (no separator)
        // still lists the current directory. With a server, require a separator so
        // Ctrl+Space on a normal identifier keeps going to LSP.
        if (_pathCompletionManager.Update(forced: !_lspManager.IsConnected))
        {
            _lspManager.HideCompletion();
            return;
        }
        var cursor = _engine.Cursor;
        _ = TriggerCompletionAsync(cursor.Line, cursor.Column);
    }

    private async Task HandleGoToDefinitionAsync()
    {
        var cursor = _engine.Cursor;
        var result = await _lspManager.RequestDefinitionAsync(cursor.Line, cursor.Column);
        if (result == null)
        {
            ActiveStatusBar.UpdateStatus("LSP: definition not found");
            return;
        }
        var (filePath, line, col) = result.Value;
        if (!System.IO.File.Exists(filePath))
        {
            ActiveStatusBar.UpdateStatus("LSP: definition in non-navigable location");
            return;
        }
        // Same file: just move the cursor without reopening
        if (string.Equals(filePath, _engine.CurrentBuffer.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectionRangeState();
            var events = _engine.SetCursorPosition(new CursorPosition(line, col));
            ProcessVimEvents(events);
            return;
        }
        OpenFileRequested?.Invoke(this, new OpenFileRequestedEventArgs(filePath, line, col));
    }

    /// <summary>
    /// Format the buffer, or — when <paramref name="lines"/> is given (0-based, inclusive) — only those lines.
    /// A range comes from an ex range prefix (`:'&lt;,'&gt;Format` over a visual selection) or the context menu.
    /// </summary>
    private async Task HandleFormatDocumentAsync((int Start, int End)? lines = null)
    {
        var filePath = _engine.CurrentBuffer.FilePath;
        var ext = string.IsNullOrEmpty(filePath) ? "" : Path.GetExtension(filePath);
        var original = _engine.CurrentBuffer.Text.GetText();
        if (lines is { } raw) lines = LineRangeText.Clamp(original, raw.Start, raw.End);
        var scope = lines is null ? "document" : "selection";

        // 1) A configured CLI formatter for this extension wins over LSP.
        var def = FormatterRegistry.Default.GetForExtension(ext);
        if (def is not null)
        {
            await RunCliFormatterAsync(def, filePath, original, registeredFor: null, lines);
            return;
        }

        // 2) Otherwise fall back to the language server's textDocument/{range,}Formatting.
        if (lines is not null && _lspManager.IsConnected && !_lspManager.ServerSupportsRangeFormatting)
        {
            // Don't silently widen a selection format into a whole-document one — that rewrites lines
            // the user never selected. Say so and let them run :Format for the document instead.
            ActiveStatusBar.UpdateStatus("Format: language server cannot format a range — use :Format for the whole document");
            return;
        }
        var edits = lines is { } r
            ? await _lspManager.RequestRangeFormattingAsync(ToLspRange(original, r), _engine.Options.TabStop, _engine.Options.ExpandTab)
            : await _lspManager.RequestFormattingAsync(_engine.Options.TabStop, _engine.Options.ExpandTab);
        if (edits.Count > 0)
        {
            var formatted = ApplyTextEdits(original, edits);
            _engine.SetText(formatted);
            UpdateAll();
            _lspManager.OnTextChanged(formatted);
            ActiveStatusBar.UpdateStatus($"Format: {scope} formatted");
            return;
        }

        // 3) Neither a configured CLI formatter nor an LSP that formats — investigate known candidates.
        var candidates = ext.Length == 0 ? [] : KnownFormatters.ForExtension(ext);
        if (candidates.Count == 0)
        {
            ActiveStatusBar.UpdateStatus(ext.Length == 0 ? "Format: no changes" : $"Format: no formatter for {ext}");
            return;
        }
        // Use the first installed candidate and register it (the host persists the mapping).
        var installed = candidates.FirstOrDefault(c => FormatterRunner.IsOnPath(c.Executable));
        if (installed is not null)
        {
            await RunCliFormatterAsync(new FormatterDef(installed.Executable, installed.Args), filePath, original, registeredFor: ext, lines);
            return;
        }
        var names = string.Join(", ", candidates.Select(c => c.Executable));
        ActiveStatusBar.UpdateStatus($"Format: no formatter for {ext}. Install one of: {names}, or :FmtSet {ext} <cmd>");
    }

    /// <summary>Run a CLI formatter off the UI thread, apply its stdout to the buffer, and (optionally) persist the mapping.</summary>
    private async Task RunCliFormatterAsync(FormatterDef def, string? filePath, string original, string? registeredFor,
        (int Start, int End)? lines = null)
    {
        var indent = lines is { } sel ? LineRangeText.CommonIndent(original, sel.Start, sel.End) : "";
        var input = lines is { } s
            ? LineRangeText.Dedent(LineRangeText.Extract(original, s.Start, s.End), indent)
            : original;

        var result = await Task.Run(() => FormatterRunner.Run(def, filePath, input));
        if (!result.Ok)
        {
            ShowFormatStatus($"Format: {def.Executable} failed — {result.Error}");
            return;
        }
        if (registeredFor is not null)
            FormatterRegistry.Default.Set(registeredFor, def);

        var output = PreserveEol(input, result.Output ?? "");
        // A formatter normally emits a trailing newline; keep it out of the splice so a range format
        // doesn't insert a blank line after the selection.
        var formatted = lines is { } rr
            ? LineRangeText.Replace(original, rr.Start, rr.End, LineRangeText.Indent(output.TrimEnd('\r', '\n'), indent))
            : output;

        var where = registeredFor is not null ? $" (registered for {registeredFor})" : "";
        var scope = lines is null ? "" : " selection";
        if (string.Equals(formatted, original, StringComparison.Ordinal))
        {
            ActiveStatusBar.UpdateStatus($"Format: no changes — {def.Executable}{where}");
            return;
        }
        _engine.SetText(formatted);
        UpdateAll();
        _lspManager.OnTextChanged(formatted);
        ActiveStatusBar.UpdateStatus($"Format:{scope} formatted with {def.Executable}{where}");
    }

    /// <summary>The LSP range covering whole lines <paramref name="r"/>.Start..<paramref name="r"/>.End.</summary>
    private static Editor.Core.Lsp.LspRange ToLspRange(string text, (int Start, int End) r) =>
        new(new Editor.Core.Lsp.LspPosition(r.Start, 0),
            new Editor.Core.Lsp.LspPosition(r.End, LineRangeText.LineLength(text, r.End)));

    /// <summary>
    /// Surface a "Format: …" status message. The status bar is a single fixed-height line, so a multi-line
    /// message (typically a formatter's stderr) would be clipped to nothing — when the text spans more than
    /// one line, show the full text in a popup and keep only a one-line summary in the status bar.
    /// </summary>
    private void ShowFormatStatus(string message)
    {
        var trimmed = message.TrimEnd();
        int nl = trimmed.IndexOf('\n');
        if (nl < 0)
        {
            ActiveStatusBar.UpdateStatus(trimmed);
            return;
        }
        ActiveStatusBar.UpdateStatus(trimmed[..nl].TrimEnd('\r') + " …");
        ShowCopyableMessage("Format", trimmed);
    }

    /// <summary>
    /// Show <paramref name="text"/> in a sleek, chromeless dark popup whose body is a read-only, selectable
    /// monospace text box (so it can be copied). A header carries the title + accent and the footer shows
    /// line/char counts alongside Copy/Close actions.
    /// </summary>
    private void ShowCopyableMessage(string title, string text)
    {
        // Palette pulled from the active editor theme so the popup matches whatever theme is in use.
        var bg       = _theme.LineNumberBg;     // shell — the darker gutter shade for contrast against the body
        var bodyBg   = _theme.Background;        // text area — the main editor background
        var fg       = _theme.Foreground;
        var muted    = _theme.LineNumberFg;      // de-emphasised metadata
        var border   = _theme.IndentGuideBrush;
        bool isError = text.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("error", StringComparison.OrdinalIgnoreCase);
        var accent   = isError ? _theme.DiagnosticError : _theme.LinkColor;
        var mono     = new System.Windows.Media.FontFamily("Cascadia Code, Consolas");

        int lineCount = text.Length == 0 ? 0 : text.Split('\n').Length;

        // ── Header ──────────────────────────────────────────────────────
        var accentBar = new Border { Width = 3, Background = accent, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
        var titleText = new TextBlock { Text = title, Foreground = fg, FontFamily = mono, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var badgeText = new TextBlock { Text = isError ? "stderr" : "message", Foreground = accent, FontFamily = mono, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0) };
        var closeGlyph = new TextBlock { Text = "✕", Foreground = muted, FontFamily = mono, FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center };

        var header = new Grid { Margin = new Thickness(14, 11, 8, 11) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(accentBar, 0); Grid.SetColumn(titleText, 1); Grid.SetColumn(badgeText, 2); Grid.SetColumn(closeGlyph, 4);
        header.Children.Add(accentBar); header.Children.Add(titleText); header.Children.Add(badgeText); header.Children.Add(closeGlyph);

        // ── Body ────────────────────────────────────────────────────────
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = mono,
            FontSize = 12.5,
            Foreground = fg,
            Background = bodyBg,
            CaretBrush = accent,
            SelectionBrush = accent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
        };
        var bodyWrap = new Border { Background = bodyBg, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(12, 0, 12, 0), Child = box };

        // ── Footer ──────────────────────────────────────────────────────
        var stats = new TextBlock { Text = $"{lineCount} lines · {text.Length} chars", Foreground = muted, FontFamily = mono, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

        Button MakeButton(string label, bool primary)
        {
            var b = new Button { Content = label, FontFamily = mono, FontSize = 12, Height = 28, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), Foreground = primary ? bodyBg : fg, Background = primary ? accent : _theme.CurrentLineBg, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            var tpl = new System.Windows.Controls.ControlTemplate(typeof(Button));
            var bd = new System.Windows.FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            b.Template = tpl;
            return b;
        }

        var copyButton = MakeButton("Copy", primary: true);
        copyButton.IsDefault = true;
        copyButton.Click += (_, _) => { try { Clipboard.SetText(text); copyButton.Content = "Copied ✓"; } catch { /* clipboard may be busy */ } };
        var closeButton = MakeButton("Close", primary: false);
        closeButton.IsCancel = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);

        var footer = new Grid { Margin = new Thickness(14, 11, 12, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stats, 0); Grid.SetColumn(buttons, 1);
        footer.Children.Add(stats); footer.Children.Add(buttons);

        // ── Assemble ────────────────────────────────────────────────────
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0); Grid.SetRow(bodyWrap, 1); Grid.SetRow(footer, 2);
        grid.Children.Add(header); grid.Children.Add(bodyWrap); grid.Children.Add(footer);

        var shell = new Border { Background = bg, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = grid };

        var window = new Window
        {
            Title = title,
            Content = shell,
            Width = 680,
            Height = 380,
            MinWidth = 360,
            MinHeight = 200,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        // Drag the chromeless window by its header; click the glyph / Esc to close.
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) window.DragMove(); };
        closeGlyph.MouseLeftButtonDown += (_, e) => { e.Handled = true; window.Close(); };
        closeButton.Click += (_, _) => window.Close();

        var owner = Window.GetWindow(this);
        if (owner != null && owner != window) window.Owner = owner;
        window.ShowDialog();
    }

    /// <summary>Keep the document's existing newline style: if it used CRLF but the formatter emitted bare LF, restore CRLF.</summary>
    private static string PreserveEol(string original, string formatted)
    {
        if (original.Contains("\r\n", StringComparison.Ordinal) && !formatted.Contains("\r\n", StringComparison.Ordinal))
            return formatted.Replace("\n", "\r\n");
        return formatted;
    }

    private async Task HandleRenameAsync(string? prefilledName = null)
    {
        var currentWord = GetWordAtCursor();
        var newName = prefilledName ?? ShowRenameDialog(currentWord);
        if (string.IsNullOrWhiteSpace(newName) || newName == currentWord) return;

        var cursor = _engine.Cursor;
        var edit = await _lspManager.RequestRenameAsync(cursor.Line, cursor.Column, newName);
        if (edit == null || edit.Changes.Count == 0)
        {
            ActiveStatusBar.UpdateStatus("Rename: no changes returned by server");
            return;
        }

        int otherFileCount = ApplyWorkspaceEditToCurrentBuffer(edit);
        string msg = otherFileCount > 0
            ? $"Renamed to '{newName}' ({otherFileCount} other file(s) may need saving)"
            : $"Renamed to '{newName}'";
        ActiveStatusBar.UpdateStatus(msg);
    }

    private async Task HandleFindReferencesAsync()
    {
        var symbol = GetWordAtCursor();
        ActiveStatusBar.UpdateStatus("References: searching…");

        var cursor = _engine.Cursor;
        var refs = await _lspManager.RequestReferencesAsync(cursor.Line, cursor.Column);
        if (refs.Count == 0)
        {
            ActiveStatusBar.UpdateStatus("References: none found");
            return;
        }

        int fileCount = refs.Select(r => r.Uri).Distinct().Count();
        ActiveStatusBar.UpdateStatus($"{refs.Count} reference(s) in {fileCount} file(s)");

        var items = refs.Select(r =>
        {
            try { return new FindReferenceItem(new Uri(r.Uri).LocalPath, r.Range.Start.Line, r.Range.Start.Character); }
            catch { return new FindReferenceItem(r.Uri, r.Range.Start.Line, r.Range.Start.Character); }
        }).ToList();

        FindReferencesResult?.Invoke(this, new FindReferencesResultEventArgs(items, symbol));
    }

    private async Task HandleWorkspaceDiagnosticsAsync()
    {
        var result = await _lspManager.RequestWorkspaceDiagnosticsAsync();
        if (result == null)
            return;

        var items = result.Documents
            .SelectMany(document => document.Diagnostics.Select(diagnostic =>
                new FindReferenceItem(
                    UriToLocalPath(document.Uri),
                    diagnostic.Range.Start.Line,
                    diagnostic.Range.Start.Character,
                    FormatDiagnosticPreview(diagnostic))))
            .OrderBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Line)
            .ThenBy(i => i.Col)
            .ToList();

        var summary = result.Summary;
        var fileCount = items.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        ActiveStatusBar.UpdateStatus(
            $"Workspace diagnostics: {summary.DiagnosticCount} item(s) in {fileCount} file(s)");

        var label = $"workspace: {summary.ErrorCount} error(s), {summary.WarningCount} warning(s)";
        FindReferencesResult?.Invoke(
            this,
            new FindReferencesResultEventArgs(items, label, "DIAGNOSTICS"));
    }

    private static string FormatDiagnosticPreview(LspDiagnostic diagnostic)
    {
        var source = string.IsNullOrWhiteSpace(diagnostic.Source) ? "" : $"{diagnostic.Source}: ";
        return $"{diagnostic.Severity}: {source}{diagnostic.Message}";
    }

    private async Task HandleCallHierarchyAsync()
    {
        if (!_lspManager.IsConnected) { ActiveStatusBar.UpdateStatus("Call hierarchy: LSP not connected"); return; }
        var symbol = GetWordAtCursor();
        ActiveStatusBar.UpdateStatus("Call hierarchy: searching…");

        var cursor = _engine.Cursor;
        var item = await _lspManager.PrepareCallHierarchyAsync(cursor.Line, cursor.Column);
        if (item is null)
        {
            ActiveStatusBar.UpdateStatus("Call hierarchy: no symbol found");
            return;
        }

        var incomingTask = _lspManager.GetIncomingCallsAsync(item);
        var outgoingTask = _lspManager.GetOutgoingCallsAsync(item);
        await Task.WhenAll(incomingTask, outgoingTask);
        var incoming = incomingTask.Result;
        var outgoing = outgoingTask.Result;

        var resultItems = new List<FindReferenceItem>();

        if (incoming is { Length: > 0 })
        {
            foreach (var call in incoming)
            {
                // Use first fromRange if available, else selectionRange
                int line = call.FromRanges.Length > 0
                    ? call.FromRanges[0].Start.Line
                    : call.From.SelectionRange.Start.Line;
                int col = call.FromRanges.Length > 0
                    ? call.FromRanges[0].Start.Character
                    : call.From.SelectionRange.Start.Character;
                resultItems.Add(new FindReferenceItem(UriToLocalPath(call.From.Uri), line, col));
            }
        }

        if (outgoing is { Length: > 0 })
        {
            foreach (var call in outgoing)
            {
                int line = call.FromRanges.Length > 0
                    ? call.FromRanges[0].Start.Line
                    : call.To.SelectionRange.Start.Line;
                int col = call.FromRanges.Length > 0
                    ? call.FromRanges[0].Start.Character
                    : call.To.SelectionRange.Start.Character;
                resultItems.Add(new FindReferenceItem(UriToLocalPath(call.To.Uri), line, col));
            }
        }

        if (resultItems.Count == 0)
        {
            ActiveStatusBar.UpdateStatus($"Call hierarchy: no callers/callees found for '{symbol}'");
            return;
        }

        int inCount = incoming?.Length ?? 0;
        int outCount = outgoing?.Length ?? 0;
        ActiveStatusBar.UpdateStatus($"Call hierarchy: {inCount} caller(s), {outCount} callee(s) for '{symbol}'");
        FindReferencesResult?.Invoke(this, new FindReferencesResultEventArgs(resultItems, $"Call hierarchy: {symbol}"));
    }

    private async Task HandleTypeHierarchyAsync()
    {
        if (!_lspManager.IsConnected) { ActiveStatusBar.UpdateStatus("Type hierarchy: LSP not connected"); return; }
        var symbol = GetWordAtCursor();
        ActiveStatusBar.UpdateStatus("Type hierarchy: searching…");

        var cursor = _engine.Cursor;
        var item = await _lspManager.PrepareTypeHierarchyAsync(cursor.Line, cursor.Column);
        if (item is null)
        {
            ActiveStatusBar.UpdateStatus("Type hierarchy: no type found");
            return;
        }

        var supertypesTask = _lspManager.GetSupertypesAsync(item);
        var subtypesTask   = _lspManager.GetSubtypesAsync(item);
        await Task.WhenAll(supertypesTask, subtypesTask);
        var supertypes = supertypesTask.Result;
        var subtypes   = subtypesTask.Result;

        var resultItems = new List<FindReferenceItem>();

        if (supertypes is { Length: > 0 })
        {
            foreach (var t in supertypes)
                resultItems.Add(new FindReferenceItem(UriToLocalPath(t.Uri), t.SelectionRange.Start.Line, t.SelectionRange.Start.Character));
        }

        if (subtypes is { Length: > 0 })
        {
            foreach (var t in subtypes)
                resultItems.Add(new FindReferenceItem(UriToLocalPath(t.Uri), t.SelectionRange.Start.Line, t.SelectionRange.Start.Character));
        }

        if (resultItems.Count == 0)
        {
            ActiveStatusBar.UpdateStatus($"Type hierarchy: no supertypes/subtypes found for '{symbol}'");
            return;
        }

        int superCount = supertypes?.Length ?? 0;
        int subCount   = subtypes?.Length ?? 0;
        ActiveStatusBar.UpdateStatus($"Type hierarchy: {superCount} supertype(s), {subCount} subtype(s) for '{symbol}'");
        FindReferencesResult?.Invoke(this, new FindReferencesResultEventArgs(resultItems, $"Type hierarchy: {symbol}"));
    }

    private async Task HandleDocumentSymbolsAsync()
    {
        // Use cached symbols if available
        var cached = _lspManager.GetDocumentSymbols();
        if (cached.Count > 0)
        {
            ShowDocumentSymbols(cached);
            return;
        }

        if (!_lspManager.IsConnected || _lspManager.CurrentUri == null)
        {
            ActiveStatusBar.UpdateStatus("Symbols: LSP not connected or no file open");
            DocumentSymbolsResult?.Invoke(this, new DocumentSymbolsResultEventArgs([]));
            return;
        }

        // Cache empty — request fresh symbols directly (bypasses debounce)
        ActiveStatusBar.UpdateStatus("Symbols: loading…");
        var symbols = await _lspManager.RequestDocumentSymbolsAsync();
        ShowDocumentSymbols(symbols);
    }

    private void ShowDocumentSymbols(IReadOnlyList<DocumentSymbol> symbols)
    {
        if (symbols.Count == 0)
        {
            ActiveStatusBar.UpdateStatus("Symbols: none found (LSP not ready or no symbols)");
            DocumentSymbolsResult?.Invoke(this, new DocumentSymbolsResultEventArgs([]));
            return;
        }
        var items = new List<DocumentSymbolItem>();
        FlattenSymbols(symbols, items, 0);
        ActiveStatusBar.UpdateStatus($"Symbols: {items.Count} symbol(s)");
        DocumentSymbolsResult?.Invoke(this, new DocumentSymbolsResultEventArgs(items));
    }

    private static void FlattenSymbols(IReadOnlyList<DocumentSymbol> symbols, List<DocumentSymbolItem> result, int depth)
    {
        foreach (var sym in symbols)
        {
            result.Add(new DocumentSymbolItem(sym.Name, sym.Kind,
                sym.SelectionRange.Start.Line, sym.SelectionRange.Start.Character, depth));
            if (sym.Children != null && sym.Children.Length > 0)
                FlattenSymbols(sym.Children, result, depth + 1);
        }
    }

    /// <summary>
    /// Called by the host (MainWindow) when the user opens the Outline sidebar panel.
    /// Requests document symbols and fires <see cref="DocumentSymbolsResult"/>.
    /// </summary>
    public void RequestOutlineAsync() => _ = HandleDocumentSymbolsAsync();

    /// <summary>
    /// Move the editor cursor to the given 0-indexed line and column and centre the viewport.
    /// </summary>
    public void JumpToLine(int line, int col)
    {
        var totalLines = _engine.CurrentBuffer.Text.LineCount;
        line = Math.Max(0, Math.Min(line, totalLines - 1));
        col  = Math.Max(0, col);
        ClearSelectionRangeState();
        _engine.SetCursorPosition(new Editor.Core.Models.CursorPosition(line, col));
        Canvas.SetCursor(_engine.Cursor);
        AlignViewport(Editor.Core.Models.ViewportAlign.Center);
        Canvas.InvalidateVisual();
    }

    private async Task HandleWorkspaceSymbolsAsync(string query)
    {
        if (!_lspManager.IsConnected)
        {
            ActiveStatusBar.UpdateStatus("Symbols: LSP not connected");
            return;
        }

        ActiveStatusBar.UpdateStatus($"Symbols: searching '{query}'…");
        var symbols = await _lspManager.GetWorkspaceSymbolsAsync(query, false);
        if (symbols.Count == 0)
        {
            ActiveStatusBar.UpdateStatus($"Symbols: no results for '{query}'");
            return;
        }

        var seenUris = new HashSet<string>();
        var items = symbols.Select(s =>
        {
            seenUris.Add(s.Location.Uri);
            return new FindReferenceItem(UriToLocalPath(s.Location.Uri), s.Location.Range.Start.Line, s.Location.Range.Start.Character);
        }).ToList();

        ActiveStatusBar.UpdateStatus($"Symbols: {items.Count} result(s) in {seenUris.Count} file(s) for '{query}'");
        FindReferencesResult?.Invoke(this, new FindReferencesResultEventArgs(items, query));
    }

    private async Task HandleCodeActionAsync()
    {
        if (!_lspManager.IsConnected) { ActiveStatusBar.UpdateStatus("Code actions: LSP not connected"); return; }
        ActiveStatusBar.UpdateStatus("Code actions: searching…");

        var cursor = _engine.Cursor;
        var actions = await _lspManager.RequestCodeActionsAsync(cursor.Line, cursor.Column);
        if (actions.Count == 0)
        {
            ActiveStatusBar.UpdateStatus("Code actions: none available");
            return;
        }

        _lspManager.ShowCodeActions(actions);
        ActiveStatusBar.UpdateStatus($"Code actions: {actions.Count} available — j/k to select, Enter to apply, Esc to dismiss");
    }

    private void ApplyCodeAction(LspCodeAction action)
    {
        _lspManager.HideCodeActions();
        if (action.Edit == null || action.Edit.Changes.Count == 0)
        {
            ActiveStatusBar.UpdateStatus($"Code action '{action.Title}': no edits to apply");
            return;
        }

        int otherFileCount = ApplyWorkspaceEditToCurrentBuffer(action.Edit);
        string msg = otherFileCount > 0
            ? $"Code action '{action.Title}' applied ({otherFileCount} other file(s) may need saving)"
            : $"Code action '{action.Title}' applied";
        ActiveStatusBar.UpdateStatus(msg);
    }

    /// <summary>Apply a workspace edit to the current buffer; returns count of other modified files.</summary>
    private int ApplyWorkspaceEditToCurrentBuffer(LspWorkspaceEdit edit)
    {
        var currentPath = _engine.CurrentBuffer.FilePath ?? "";
        int otherFileCount = 0;
        foreach (var (fileUri, fileEdits) in edit.Changes)
        {
            string localPath = "";
            try { localPath = new Uri(fileUri).LocalPath; } catch { }
            if (string.Equals(localPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                var text = ApplyTextEdits(_engine.CurrentBuffer.Text.GetText(), fileEdits);
                _engine.SetText(text);
                UpdateAll();
                _lspManager.OnTextChanged(text);
            }
            else if (fileEdits.Count > 0)
                otherFileCount++;
        }
        return otherFileCount;
    }

    private string GetWordAtCursor()
    {
        var cursor = _engine.Cursor;
        var line = _engine.CurrentBuffer.Text.GetLine(cursor.Line);
        int col = Math.Min(cursor.Column, line.Length - 1);
        if (col < 0 || line.Length == 0) return "";
        int start = col, end = col;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_'))
            start--;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;
        return line[start..end];
    }

    private string? ShowRenameDialog(string currentName)
    {
        var bg = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x28, 0x2A, 0x36));
        var fg = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF8, 0xF8, 0xF2));
        var inputBg = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1E, 0x1F, 0x29));
        var accent = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x44, 0x47, 0x5A));

        var textBox = new TextBox
        {
            Text = currentName,
            Background = inputBg,
            Foreground = fg,
            CaretBrush = fg,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 6, 0, 10),
            SelectionBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x80, 0xBD, 0x93, 0xF9))
        };

        var okBtn = new Button
        {
            Content = "Rename",
            Width = 80,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x50, 0xFA, 0x7B)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x28, 0x2A, 0x36)),
            BorderThickness = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            IsDefault = true
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 70,
            Height = 26,
            Background = accent,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            IsCancel = true
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "New name:",
            Foreground = fg,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12
        });
        panel.Children.Add(textBox);
        panel.Children.Add(btnRow);

        var win = new Window
        {
            Title = "Rename Symbol",
            Content = panel,
            Background = bg,
            Width = 300,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = Window.GetWindow(this)
        };

        okBtn.Click += (_, _) => win.DialogResult = true;
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) win.DialogResult = true;
        };

        win.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        return win.ShowDialog() == true ? textBox.Text.Trim() : null;
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
    /// Synchronously commits the active IME composition: inserts the in-progress
    /// composition string at the cursor, then discards the native composition so the
    /// IME does not also deliver the same text later via WM_IME_COMPOSITION →
    /// OnTextInput (which would duplicate it). Runs in-line with the triggering
    /// keystroke so no subsequent input can be reordered ahead of the commit.
    /// </summary>
    private static void SendVirtualKeyToIme(ushort vk)
    {
        INPUT_SEND[] inputs =
        [
            new() { type = INPUTTYPE_KEYBOARD, wVk = vk },
            new() { type = INPUTTYPE_KEYBOARD, wVk = vk, dwFlags = KBDEVENTF_KEYUP }
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT_SEND>());
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
        // Empty the composition string before cancelling. CPS_CANCEL on its own has
        // been observed to leave a TSF-backed composition alive, so the leftover kana
        // is flushed into the *next* keystroke. Explicitly zeroing the composition
        // string (SCS_SETSTR with an empty string) guarantees nothing survives the
        // cancel. SCS_SETSTR updates GCS_COMPSTR (not GCS_RESULTSTR), so it produces
        // no committed text / OnTextInput echo.
        ImmSetCompositionStringW(imc, SCS_SETSTR, string.Empty, 0, string.Empty, 0);
        ImmNotifyIME(imc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
        ImmReleaseContext(source.Handle, imc);
        ClearImeCompositionOverlay();
    }

    /// <summary>
    /// Reads the IME caret position (in characters from the start of the composition
    /// string) via GCS_CURSORPOS. Returns -1 when no IME context is available, so the
    /// caller falls back to placing the caret at the end of the composition.
    /// </summary>
    private int GetImeCursorPos()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return -1;
        var imc = ImmGetContext(source.Handle);
        if (imc == IntPtr.Zero) return -1;
        try
        {
            // TSF-based IMEs (e.g. the modern Microsoft IME) don't populate the legacy
            // IMM composition string, so GCS_CURSORPOS reads back as 0 and would pin the
            // caret to the start of the composition. Only trust the cursor position when
            // the IMM composition string actually exists; otherwise fall back to the end.
            if (ImmGetCompositionStringW(imc, GCS_COMPSTR, IntPtr.Zero, 0) <= 0) return -1;

            // GCS_CURSORPOS returns the position directly (low word), not via a buffer.
            int pos = ImmGetCompositionStringW(imc, GCS_CURSORPOS, IntPtr.Zero, 0);
            return pos < 0 ? -1 : (pos & 0xFFFF);
        }
        finally
        {
            ImmReleaseContext(source.Handle, imc);
        }
    }

    private string GetImeCompositionText()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return string.Empty;
        var imc = ImmGetContext(source.Handle);
        if (imc == IntPtr.Zero) return string.Empty;

        try
        {
            int byteCount = ImmGetCompositionStringW(imc, GCS_COMPSTR, IntPtr.Zero, 0);
            if (byteCount <= 0) return string.Empty;

            var buffer = Marshal.AllocHGlobal(byteCount);
            try
            {
                int copied = ImmGetCompositionStringW(imc, GCS_COMPSTR, buffer, byteCount);
                return copied > 0 ? Marshal.PtrToStringUni(buffer, copied / 2) ?? string.Empty : string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ImmReleaseContext(source.Handle, imc);
        }
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
                    CaretMoved?.Invoke(this, new CaretInfo(ce.Position.Line, ce.Position.Column));
                    if (!needFullUpdate)
                    {
                        Canvas.SetCursor(ce.Position);
                        ActiveStatusBar.UpdateCursor(ce.Position, _engine.CurrentBuffer.Text.LineCount, _engine.Options.Ruler);
                        if (_engine.Mode is VimMode.Insert or VimMode.Replace)
                            UpdateImeWindowPos();
                        else
                        {
                            if (_engine.Options.Breadcrumb)
                            {
                                _lspManager.UpdateBreadcrumb(ce.Position.Line, ce.Position.Column);
                                RefreshBreadcrumbBar(); // also covers the non-LSP fallback (no LSP event fires)
                            }
                            // Request document highlights for the symbol under cursor (Normal mode)
                            var uri = _lspManager.CurrentUri;
                            if (uri != null)
                                _ = _lspManager.RequestDocumentHighlightAsync(uri, ce.Position.Line, ce.Position.Column);
                        }
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
                        // Clear document highlights when entering insert mode
                        _lspManager.ClearDocumentHighlights();
                    }
                    // Entering a text-input mode makes this editor the active IME target.
                    // Claim the TSF thread focus now (before any composition starts) so the
                    // IME composes into our store and not whichever editor happened to win
                    // the shared window's AssociateFocus default. See AssertImeStoreFocus.
                    if (IsImeTextInputMode(me.Mode))
                        AssertImeStoreFocus();
                    ActiveStatusBar.UpdateMode(me.Mode, _engine.VimEnabled, _engine.Options.ShowMode);
                    Canvas.SetMode(me.Mode);
                    ModeChanged?.Invoke(this, new ModeChangedEventArgs(me.Mode));
                    break;
                case VimEventType.SelectionChanged when evt is SelectionChangedEvent se:
                    Canvas.SetSelection(se.Selection);
                    SelectionChanged?.Invoke(this, BuildSelectionInfo());
                    break;
                case VimEventType.StatusMessage when evt is StatusMessageEvent sme:
                    ActiveStatusBar.UpdateStatus(sme.Message);
                    break;
                case VimEventType.CommandLineChanged when evt is CommandLineChangedEvent cle:
                    ActiveStatusBar.UpdateCommandLine(cle.Text);
                    // Hide completions when command line is cleared
                    if (string.IsNullOrEmpty(cle.Text))
                        ActiveStatusBar.HideCompletions();
                    break;
                case VimEventType.CommandCompletionChanged when evt is CommandCompletionChangedEvent cce:
                    if (cce.Items.Length > 0)
                        ActiveStatusBar.ShowCompletions(cce.Items, cce.SelectedIndex);
                    else
                        ActiveStatusBar.HideCompletions();
                    break;
                case VimEventType.SubstitutePreviewChanged when evt is SubstitutePreviewChangedEvent spe:
                    Canvas.SetSubstitutePreview(spe.PreviewLines);
                    break;
                case VimEventType.SaveRequested when evt is SaveRequestedEvent sre:
                {
                    var saveBuf = _engine.CurrentBuffer;
                    SaveRequested?.Invoke(this, new SaveRequestedEventArgs(
                        sre.FilePath, saveBuf.IsVirtual, saveBuf.DocumentId));
                    break;
                }
                case VimEventType.QuitRequested when evt is QuitRequestedEvent qre:
                    QuitRequested?.Invoke(this, new QuitRequestedEventArgs(qre.Force));
                    break;
                case VimEventType.OpenFileRequested when evt is OpenFileRequestedEvent ofre:
                    OpenFileRequested?.Invoke(this, new OpenFileRequestedEventArgs(ofre.FilePath));
                    break;
                case VimEventType.OpenUrlRequested when evt is OpenUrlRequestedEvent oure:
                    OpenLink(oure.Url);
                    break;
                case VimEventType.MkSessionRequested when evt is MkSessionRequestedEvent mksre:
                    MkSessionRequested?.Invoke(this, mksre.FilePath);
                    break;
                case VimEventType.SourceRequested when evt is SourceRequestedEvent sre:
                    SourceRequested?.Invoke(this, sre.FilePath);
                    break;
                case VimEventType.TerminalRequested when evt is TerminalRequestedEvent tre:
                    TerminalRequested?.Invoke(this, tre.ShellCmd);
                    break;
                case VimEventType.TerminalCommandRequested when evt is TerminalCommandRequestedEvent tce:
                    TerminalCommandRequested?.Invoke(this, tce);
                    break;
                case VimEventType.MarkdownPreviewRequested:
                    MarkdownPreviewRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case VimEventType.ReloadFileRequested:
                    ReloadCurrentFile();
                    break;
                case VimEventType.NewTabRequested when evt is NewTabRequestedEvent ntre:
                    NewTabRequested?.Invoke(this, new NewTabRequestedEventArgs(ntre.FilePath));
                    break;
                case VimEventType.SplitRequested when evt is SplitRequestedEvent stre:
                    SplitRequested?.Invoke(this, new SplitRequestedEventArgs(stre.Vertical, stre.FilePath));
                    break;
                case VimEventType.WindowNavRequested when evt is WindowNavRequestedEvent wnre:
                    WindowNavRequested?.Invoke(this, new WindowNavRequestedEventArgs(wnre.Dir));
                    break;
                case VimEventType.WindowCloseRequested when evt is WindowCloseRequestedEvent wcre:
                    WindowCloseRequested?.Invoke(this, new WindowCloseRequestedEventArgs(wcre.Force));
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
                case VimEventType.ScrollLinesRequested when evt is ScrollLinesRequestedEvent slre:
                    ScrollLines(slre.Lines);
                    break;
                case VimEventType.SearchResultChanged when evt is SearchResultChangedEvent srce:
                    UpdateSearchHighlights(srce.Pattern);
                    break;
                case VimEventType.GoToDefinitionRequested:
                    _ = HandleGoToDefinitionAsync();
                    break;
                case VimEventType.FindReferencesRequested:
                    _ = HandleFindReferencesAsync();
                    break;
                case VimEventType.WorkspaceDiagnosticsRequested:
                    _ = HandleWorkspaceDiagnosticsAsync();
                    break;
                case VimEventType.CallHierarchyRequested:
                    _ = HandleCallHierarchyAsync();
                    break;
                case VimEventType.TypeHierarchyRequested:
                    _ = HandleTypeHierarchyAsync();
                    break;
                case VimEventType.LspRenameRequested when evt is LspRenameRequestedEvent rre:
                    _ = HandleRenameAsync(rre.NewName);
                    break;
                case VimEventType.CodeActionRequested:
                    _ = HandleCodeActionAsync();
                    break;
                case VimEventType.LspHoverRequested:
                    _ = ShowLspHoverAsync();
                    break;
                case VimEventType.FormatDocumentRequested when evt is FormatDocumentRequestedEvent fde:
                    _ = HandleFormatDocumentAsync(
                        fde is { StartLine: { } fs, EndLine: { } fe } ? (fs, fe) : null);
                    break;
                case VimEventType.QuickfixOpenRequested:
                    QuickfixOpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case VimEventType.QuickfixCloseRequested:
                    QuickfixCloseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case VimEventType.QuickfixNextRequested when evt is QuickfixNextEvent qne:
                    NavigateHostQuickfix(qne.Count);
                    QuickfixNextRequested?.Invoke(this, qne.Count);
                    break;
                case VimEventType.QuickfixPrevRequested when evt is QuickfixPrevEvent qpe:
                    NavigateHostQuickfix(-qpe.Count);
                    QuickfixPrevRequested?.Invoke(this, qpe.Count);
                    break;
                case VimEventType.QuickfixGotoRequested when evt is QuickfixGotoEvent qge:
                    GotoHostQuickfix(qge.Index);
                    QuickfixGotoRequested?.Invoke(this, qge.Index);
                    break;
                case VimEventType.LocationListOpenRequested:
                    LocationListOpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case VimEventType.LocationListCloseRequested:
                    LocationListCloseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case VimEventType.LocationListNextRequested when evt is LocationListNextEvent lne:
                    LocationListNextRequested?.Invoke(this, lne.Count);
                    break;
                case VimEventType.LocationListPrevRequested when evt is LocationListPrevEvent lpe:
                    LocationListPrevRequested?.Invoke(this, lpe.Count);
                    break;
                case VimEventType.LocationListGotoRequested when evt is LocationListGotoEvent lge:
                    LocationListGotoRequested?.Invoke(this, lge.Index);
                    break;
                case VimEventType.GrepRequested when evt is GrepRequestedEvent gre:
                    GrepRequested?.Invoke(this, new GrepRequestedEventArgs(gre.Pattern, gre.FileGlob, gre.IgnoreCase));
                    break;
                case VimEventType.ProjectReplaceRequested when evt is ProjectReplaceRequestedEvent pre:
                    ProjectReplaceRequested?.Invoke(this,
                        new ProjectReplaceRequestedEventArgs(pre.Pattern, pre.Replacement, pre.FileGlob, pre.IgnoreCase));
                    break;
                case VimEventType.QuickfixReplaceRequested when evt is QuickfixReplaceRequestedEvent qre:
                    QuickfixReplaceRequested?.Invoke(this, qre.Replacement);
                    break;
                case VimEventType.GitBlameRequested:
                    ToggleBlame();
                    break;
                case VimEventType.GitStatusRequested:
                    _ = ShowGitStatusAsync();
                    break;
                case VimEventType.GitDiffRequested:
                    _ = ShowGitDiffAsync();
                    break;
                case VimEventType.GitLogRequested:
                    _ = ShowGitLogAsync();
                    break;
                case VimEventType.GitPushRequested:
                    _ = RunGitPushAsync();
                    break;
                case VimEventType.GitPullRequested:
                    _ = RunGitPullAsync();
                    break;
                case VimEventType.GitCommitRequested:
                    ShowGitCommit();
                    break;
                case VimEventType.HunkNavigateRequested when evt is HunkNavigateRequestedEvent hnr:
                    NavigateHunk(hnr.Forward);
                    break;
                case VimEventType.HunkStageRequested when evt is HunkStageRequestedEvent hsr:
                    _ = StageCurrentHunkAsync(hsr.Stage);
                    break;
                case VimEventType.SymbolsRequested when evt is SymbolsRequestedEvent sre:
                    _ = HandleWorkspaceSymbolsAsync(sre.Query);
                    break;
                case VimEventType.SymbolsRequested:
                    _ = HandleDocumentSymbolsAsync();
                    break;
                case VimEventType.FoldsChanged:
                    needFullUpdate = true;
                    break;
                case VimEventType.OptionsChanged:
                    ApplyFoldMethod();
                    ApplyInlayHintsOption();
                    ApplySemanticTokensOption();
                    ApplyBreadcrumbOption();
                    needFullUpdate = true;
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
        var lines = GetCachedLines(buf);

        // Batch all canvas state updates so the visual layout is rebuilt once for this edit
        // instead of once per Set/Show call (SetLines, SetFolds, ShowLineNumbers each rebuild).
        Canvas.BeginBatch();
        try
        {
            var ext = buf.FilePath is { Length: > 0 } filePathForExt ? System.IO.Path.GetExtension(filePathForExt) : "";
            Canvas.MarkdownTableAlignEnabled = ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);

            Canvas.SetLines(lines);
            Canvas.DocumentDirectory = buf.FilePath is { Length: > 0 } fp
                ? System.IO.Path.GetDirectoryName(fp)
                : null;

            // Push fold state to canvas
            var folds = buf.Folds;
            var visMap = folds.BuildVisibleLineMap(buf.Text.LineCount);
            var closedStarts = folds.Folds.Where(f => f.IsClosed).Select(f => f.StartLine);
            var openStarts = folds.Folds.Where(f => !f.IsClosed).Select(f => f.StartLine);
            Canvas.SetFolds(visMap, closedStarts, openStarts);

            Canvas.SetCursor(_engine.Cursor);
            Canvas.SetMode(_engine.Mode);
            Canvas.ShowLineNumbers(!_minimalChrome && (_engine.Options.Number || _engine.Options.RelativeNumber));
            Canvas.ShowRelativeLineNumbers(_engine.Options.RelativeNumber);
            Canvas.SetScrollOff(_engine.Options.ScrollOff);
            Canvas.SetList(_engine.Options.List, _engine.Options.ListChars);
            Canvas.SetColorColumn(_engine.Options.ColorColumn);
            Canvas.SetIndentGuides(_engine.Options.IndentGuides, _engine.Options.TabStop);
            Canvas.SetScrollbar(!_minimalChrome && _engine.Options.Scrollbar);
            Canvas.SetMinimap(!_minimalChrome && _engine.Options.Minimap);
            Canvas.SetColorPreview(_engine.Options.ColorPreview);
            Canvas.SetSaveDiff(Editor.Core.Editing.SaveDiff.Compute(buf.Text.SavedLines, lines));
            Canvas.SetWhitespaceIssues(_engine.Options.HighlightWhitespace
                ? Editor.Core.Editing.WhitespaceIssueDetector.Detect(lines)
                : []);

            UpdateViewportDecorations();
            UpdateSearchHighlights(_engine.SearchPattern);
        }
        finally
        {
            Canvas.EndBatch();
        }

        SyncStatusBar();

        if (_engine.Options.Breadcrumb)
            RefreshBreadcrumbBar();
    }

    /// <summary>Recomputes the changed-since-save gutter against the buffer's saved baseline.
    /// Saving updates the baseline without firing a text-change, so call this after a save.</summary>
    private void RefreshSaveDiff()
    {
        var buf = _engine.CurrentBuffer;
        Canvas.SetSaveDiff(Editor.Core.Editing.SaveDiff.Compute(buf.Text.SavedLines, GetCachedLines(buf)));
    }

    private string[] GetCachedLines(VimBuffer buffer)
    {
        long version = buffer.Text.Version;
        if (ReferenceEquals(_cachedLinesBuffer, buffer) &&
            _cachedLinesVersion == version &&
            _cachedLines.Length > 0)
            return _cachedLines;

        _cachedLinesBuffer = buffer;
        _cachedLinesVersion = version;
        _cachedLines = buffer.Text.Snapshot();
        return _cachedLines;
    }

    private void UpdateViewportDecorations()
    {
        var buf = _engine.CurrentBuffer;
        var lines = GetCachedLines(buf);
        var (firstLine, lastLine) = Canvas.GetVisibleBufferLineRange();
        firstLine = Math.Clamp(firstLine, 0, Math.Max(0, lines.Length - 1));
        lastLine = Math.Clamp(lastLine, firstLine, Math.Max(0, lines.Length - 1));

        if (_engine.Options.Syntax)
        {
            var tokens = _engine.Syntax.TokenizeVisible(lines, firstLine, lastLine);
            Canvas.SetTokens(tokens);
        }
        else
        {
            Canvas.SetTokens([]);
        }

        if (_engine.Options.Spell && _engine.SpellChecker.IsLoaded)
        {
            var spellErrors = new Dictionary<int, IReadOnlyList<(int Start, int End)>>();
            for (int i = firstLine; i <= lastLine; i++)
            {
                var errs = _engine.GetSpellErrors(i);
                if (errs.Count > 0) spellErrors[i] = errs;
            }
            Canvas.SetSpellErrors(spellErrors);
        }
        else
        {
            Canvas.SetSpellErrors([]);
        }
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

    /// <summary>
    /// Scroll the canvas by <paramref name="lines"/> lines (positive = down, negative = up).
    /// The engine has already clamped the cursor if necessary; the CursorMoved event
    /// will update the cursor overlay if needed.
    /// </summary>
    private void ScrollLines(int lines)
    {
        if (Canvas.LineHeight <= 0) return;
        double newOffset = Canvas.VerticalOffset + lines * Canvas.LineHeight;
        Canvas.ScrollTo(newOffset);
    }

    public void ScrollToVerticalRatio(double ratio)
    {
        double maxOffset = Math.Max(0, Canvas.TotalContentHeight - Canvas.ViewportHeight);
        Canvas.ScrollTo(maxOffset * Math.Clamp(ratio, 0.0, 1.0), Canvas.HorizontalOffset);
    }

    /// <summary>
    /// Scrolls the viewport so the current cursor line sits at the top of the view,
    /// exactly like Vim's <c>zt</c>. A host pane (e.g. a code-outline / "go to symbol"
    /// jump) calls this right after <see cref="NavigateTo"/> to land the target line at
    /// the top instead of merely being scrolled into view. No-op until the canvas has a
    /// measured line height (guarded inside <c>AlignViewport</c>).
    /// </summary>
    public void ScrollCursorToTop() => AlignViewport(ViewportAlign.Top);

    private void AlignViewport(ViewportAlign align)
    {
        if (Canvas.LineHeight <= 0) return;

        var buf = _engine.CurrentBuffer;
        var folds = buf.Folds;
        var visMap = folds.BuildVisibleLineMap(buf.Text.LineCount);
        int cursorVisual = folds.BufferToVisualLine(_engine.Cursor.Line, visMap);
        if (cursorVisual < 0) cursorVisual = _engine.Cursor.Line;
        int totalVisible = visMap.Length > 0 ? visMap.Length : buf.Text.LineCount;

        var visible = Canvas.VisibleLines;
        var targetTopLine = align switch
        {
            ViewportAlign.Top => cursorVisual,
            ViewportAlign.Center => cursorVisual - (visible / 2),
            ViewportAlign.Bottom => cursorVisual - visible + 1,
            _ => cursorVisual
        };

        targetTopLine = Math.Clamp(targetTopLine, 0, Math.Max(0, totalVisible - 1));
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

    private static string? GetCtrlKey(Key key)
    {
        // Letters and digits sit on the same physical keys across keyboard
        // layouts, so map them directly.
        if (key >= Key.A && key <= Key.Z)
            return ((char)('a' + (key - Key.A))).ToString();
        if (key == Key.D6)
            return "6"; // Ctrl+6 — alternate-buffer switch

        // Punctuation differs by layout: e.g. ';' is VK_OEM_1 on US keyboards
        // but VK_OEM_PLUS on JIS keyboards (where VK_OEM_1 is ':'). The
        // Ctrl+key path never produces an OnTextInput event, so unlike the
        // Normal/Visual punctuation path it can't rely on the OS-resolved
        // character. Resolve the character the key actually prints on the
        // active layout instead of hard-coding US positions.
        return UnshiftedCharForKey(key) switch
        {
            '[' => "[",
            ';' => ";",
            _ => null
        };
    }

    private const uint MAPVK_VK_TO_CHAR = 2;

    /// <summary>
    /// Returns the character a key produces with no modifiers on the active
    /// keyboard layout, or null for non-printable / dead keys. Side-effect free
    /// (unlike ToUnicode it does not disturb pending dead-key state).
    /// </summary>
    private static char? UnshiftedCharForKey(Key key)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return null;
        uint mapped = MapVirtualKey((uint)vk, MAPVK_VK_TO_CHAR);
        if (mapped == 0) return null;
        // High bit flags a dead key; the character sits in the low word.
        char ch = (char)(mapped & 0x7FFF);
        return char.IsControl(ch) ? null : ch;
    }

    private static string UriToLocalPath(string uri)
    {
        try
        {
            var parsed = new Uri(uri);
            return parsed.IsFile ? parsed.LocalPath : uri;
        }
        catch { return uri; }
    }

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
