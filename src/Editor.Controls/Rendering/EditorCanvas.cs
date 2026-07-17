using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Editor.Core.Editing;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Syntax;
using Editor.Core.Text;

namespace Editor.Controls.Rendering;

public partial class EditorCanvas : FrameworkElement
{
    private readonly record struct VisualLineSegment(int BufferLine, int StartColumn, bool IsContinuation);

    private Typeface _typeface = new("Consolas");
    private Typeface _italicTypeface = new(new FontFamily("Consolas"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private double _fontSize = 14;
    private double _charWidth;
    private double _lineHeight;
    private readonly Dictionary<char, double> _charWidthCache = [];
    // Character-boundary X positions measured by WPF's text formatter. Unchanged line strings
    // retain their entry across edits; changed/deleted strings are pruned in SetLines.
    private readonly Dictionary<string, double[]> _textBoundaryCache = [];
    private double _scrollOffsetY;
    private double _scrollOffsetX;
    private int _visibleLines;
    private int _visibleColumns;
    private int _scrollOff;

    private string[] _lines = [""];
    private CursorPosition _cursor;
    private Selection? _selection;
    private VimMode _mode = VimMode.Normal;
    private Dictionary<int, SyntaxToken[]> _tokensByLine = [];
    private Dictionary<int, List<CursorPosition>> _searchMatchesByLine = [];
    private string _searchPattern = "";
    private IReadOnlyDictionary<int, string> _substitutePreviewLines = new Dictionary<int, string>();
    private bool _showLineNumbers = true;
    private bool _relativeNumber = false;
    private int _lineNumberWidth = 4; // digits
    private bool _cursorVisible = true;
    private bool _isActive = true;
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private bool _isDragging = false;
    private Point? _mouseDownPoint;
    private bool _draggingVScrollbar = false;
    private bool _draggingHScrollbar = false;
    private double _scrollbarGrabOffset = 0;
    private string _imeCompositionText = string.Empty;
    // Caret position (in characters) inside the composition string, as reported by
    // the IME (GCS_CURSORPOS). -1 means "place the caret at the end" — used as the
    // fallback when no position is known.
    private int _imeCompositionCursor = -1;
    // Target clause (注目文節) range within the composition string, in characters
    // [start, end). Highlighted while converting so arrow-key clause navigation is
    // visible. start < 0 means "no target clause".
    private int _imeTargetClauseStart = -1;
    private int _imeTargetClauseEnd = -1;
    // Clause start offsets (incl. 0) splitting the composition into 文節 segments;
    // empty when the composition isn't segmented.
    private int[] _imeClauseStarts = [];
    private string[] _imeCandidates = [];
    private int _imeCandidateSelection = -1;
    private VisualLineSegment[] _visualLines = [new VisualLineSegment(0, 0, false)];
    private bool _wrapLines;

    // Layout batching: while batching, RebuildVisualLayout() is deferred so a burst of
    // Canvas.SetXxx() calls (e.g. one keystroke's UpdateAll) rebuilds the layout once
    // instead of 3+ times. See BeginBatch/EndBatch.
    private bool _batchingLayout;
    private bool _layoutDirtyDuringBatch;

    // Cached max line width for the horizontal content extent. Only meaningful when not
    // wrapping; recomputed only when the line array itself changes (keyed by reference).
    private string[]? _maxLineWidthCacheLines;
    private double _cachedMaxLineWidth;
    private readonly Dictionary<int, (string Text, IReadOnlyList<Editor.Core.Text.DetectedLink> Links)> _linkCache = [];
    // File-path link verification (resolved absolute path → exists on disk). Null while a
    // background check is in flight. Populated off the UI thread; reads/writes happen on the
    // UI thread except the one assignment inside the Dispatcher callback in EnsurePathVerified.
    private readonly Dictionary<string, bool> _pathExists = [];
    private readonly HashSet<string> _pathChecking = [];
    private string? _documentDirectory;
    private double _contentWidth;

    // Markdown table column alignment (rendering-only — never mutates buffer text). Maps
    // buffer line -> (buffer column -> pixel width override), stretching the gap before each
    // '|' so columns line up even when cell content has mixed-width (e.g. Japanese) characters.
    private bool _markdownTableAlignEnabled;
    private Dictionary<int, Dictionary<int, double>> _tableColumnOverrides = [];
    private Dictionary<int, double>? _activeLineOverrides;
    // The detected table blocks for the current text (kept so the cursor's enclosing table can be
    // found without re-scanning on every cursor/mode change).
    private IReadOnlyList<Editor.Core.Editing.MarkdownTableLayout.TableBlock> _tableBlocks = [];
    // While editing (Insert/Replace mode, which also covers the Vim-disabled resting state), the
    // whole table the cursor sits in is shown raw/unaligned. This is that block's line range, or
    // [-1,-1] when no table is being edited. See SetActiveLine for why.
    private int _suppressedTableStart = -1;
    private int _suppressedTableEnd = -1;

    // Folds
    private int[] _visibleLineMap = [];
    private HashSet<int> _closedFoldStarts = [];   // buffer lines with closed fold (▶)
    private HashSet<int> _openFoldStarts = [];     // buffer lines with open fold (▼)
    private int _hoveredFoldLine = -1;             // buffer line currently hovered in fold gutter

    // Scrollbar
    private bool _showScrollbar = true;

    // Minimap
    private bool _showMinimap = false;
    private const double MinimapWidth = 80.0;

    // Color column
    private int _colorColumn = 0;

    // Indent guides
    private bool _showIndentGuides = false;
    private int _indentGuideTabStop = 4;

    // Color preview swatches
    private bool _showColorPreview = true;

    // List chars
    private bool _showList = false;
    private string _listCharsRaw = "";
    private string _listTab   = "→ ";
    private string _listTrail = "·";
    private string _listEol   = "¶";
    private string _listSpace = "";   // empty = don't show spaces

    // Multi-cursor extra cursors (line, col) pairs — primary cursor is drawn by DrawCursor
    private IReadOnlyList<(int Line, int Col)> _extraCursors = [];

    // Git
    private Dictionary<int, GitLineState> _gitDiff = [];
    private Dictionary<int, EditorBlameLine> _blameLines = [];
    private double _blameColWidth;     // 0 = blame margin hidden (no annotations)
    private int _hoveredBlameLine = -1;
    // blame ホバー中のコミット情報ツールチップ（手動開閉）
    private System.Windows.Controls.ToolTip? _blameToolTip;
    // blame カラムのユーザー調整幅（プロセス内共有。0 = 内容に合わせた自動幅）。カラム右端のドラッグで
    // 変更し、他のエディタ／次回の blame 表示でも同じ幅を使う（アプリ再起動で自動幅に戻る）。
    private static double s_blameColUserWidth;
    private bool _blameColResizing;
    private const double BlameColMinWidth = 40;
    private const double BlameColEdgeGrip = 4;  // 右端リサイズハンドルの当たり判定（±px）

    // Changed-since-last-save gutter (independent of git; tracks the on-disk baseline)
    private Dictionary<int, Editor.Core.Editing.SaveLineState> _saveDiff = [];

    // Inlay hints — raw list plus a line-keyed lookup rebuilt on SetInlayHints
    private IReadOnlyList<InlayHint> _inlayHints = [];
    private Dictionary<int, List<InlayHint>> _inlayHintsByLine = [];

    // Semantic tokens — line-keyed lookup: line → list of (startChar, length, brush)
    private Dictionary<int, List<(int StartChar, int Length, Brush Brush)>> _semanticTokensByLine = [];

    // Document highlights
    private IReadOnlyList<DocumentHighlight>? _documentHighlights;

    // LSP
    private IReadOnlyList<LspDiagnostic> _diagnostics = [];
    private IReadOnlyList<LspCompletionItem> _completionItems = [];
    private int _completionSelection = -1;
    private int _completionScrollOffset = 0;
    private LspSignatureHelp? _signatureHelp;
    private IReadOnlyList<LspCodeAction> _codeActionItems = [];
    private int _codeActionsSelection = 0;
    private int _codeActionsScrollOffset = 0;

    public EditorTheme Theme { get; set; } = EditorTheme.Dracula;

    public event Action<double, double>? ScrollChanged;
    public event Action<int>? VisibleLinesChanged;
    public event Action? ScrollMetricsChanged;
    public event Action<int, int>? MouseClicked;       // (line, col)
    public event Action<int, int>? MouseRightClicked;  // (line, col)
    public event Action<int, int>? MouseDragging;      // (line, col) during drag
    public event Action? MouseDragEnded;
    public event Action<int>? FoldGutterClicked;       // (bufferLine) fold indicator clicked
    public event Action<int, EditorBlameLine>? BlameClicked;  // (bufferLine, info) blame margin clicked
    public event Action<string>? LinkClicked;          // (url) Ctrl+clicked on a detected URL
    public event Action<string>? FileLinkClicked;      // (absolutePath) Ctrl+clicked on a detected file path

    // Brushes/pens for spell underlines — created once (theme-independent). Popup chrome brushes
    // shared with LspOverlayRenderer live in PopupChrome.
    private static readonly SolidColorBrush s_spellBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0x9F, 0xFF)));
    private static readonly Pen             s_spellPen    = FreezePen(new Pen(s_spellBrush, 1.0));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    private readonly GutterHitTester _gutterHitTester;

    public EditorCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        _gutterHitTester = new GutterHitTester(HitTestGutterLine);
        StartCursorBlink();
        RebuildVisualLayout();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EditorCanvasAutomationPeer(this);

    // ─────────────── Public scroll info ───────────────

    private int TotalVisualLines => Math.Max(1, _visualLines.Length);
    public double TotalContentHeight => TotalVisualLines * _lineHeight;
    public double TotalContentWidth => _contentWidth;
    public double ViewportHeight => RenderSize.Height;
    public double ViewportWidth => RenderSize.Width;

    // The overlay scrollbars sit at the bottom/right edges. When a bar is shown it
    // occupies ScrollbarSize px that text must not scroll under, otherwise the last
    // line / rightmost column ends up hidden behind the bar. These give the height/
    // width actually usable for text, so scroll clamping keeps content clear of the bars.
    private bool HorizontalBarPresent => _showScrollbar && !_wrapLines && TotalContentWidth > RenderSize.Width + 1;
    private bool VerticalBarPresent => _showScrollbar && TotalContentHeight > RenderSize.Height + 1;
    private double UsableViewportHeight => Math.Max(0, RenderSize.Height - (HorizontalBarPresent ? OverlayRenderer.ScrollbarSize : 0));
    private double UsableViewportWidth => Math.Max(0, RenderSize.Width - (VerticalBarPresent ? OverlayRenderer.ScrollbarSize : 0));

    // Allows scrolling past the last line (like Vim's `~` lines) so the last line can
    // still be moved all the way to the top of the viewport. Without this, `zz` on the
    // last line can't center it once the file end is already at the viewport bottom.
    private double MaxScrollOffsetY
    {
        get
        {
            double natural = TotalContentHeight - UsableViewportHeight;
            return natural <= 0 ? 0 : TotalContentHeight - _lineHeight;
        }
    }

    public double VerticalOffset => _scrollOffsetY;
    public double HorizontalOffset => _scrollOffsetX;
    public bool WrapLines
    {
        get => _wrapLines;
        set
        {
            if (_wrapLines == value) return;
            _wrapLines = value;
            RebuildVisualLayout();
            InvalidateVisual();
        }
    }

    public void UpdateFont(string family, double size)
    {
        _typeface = new Typeface(family);
        _italicTypeface = new Typeface(new FontFamily(family), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _fontSize = size;
        _charWidth = 0;
        _charWidthCache.Clear();
        _textBoundaryCache.Clear();
        _maxLineWidthCacheLines = null; // char widths changed — invalidate cached extent
        MeasureChar();
        RecomputeTableOverrides();
        RebuildVisualLayout();
        InvalidateVisual();
    }

    /// <summary>
    /// When true (set by the host for .md/.markdown files), GFM table rows are visually
    /// realigned on render — the gap before each '|' is stretched so columns line up even when
    /// cells mix half-width and full-width (e.g. Japanese) characters. The buffer text itself is
    /// never modified.
    /// </summary>
    public bool MarkdownTableAlignEnabled
    {
        get => _markdownTableAlignEnabled;
        set
        {
            if (_markdownTableAlignEnabled == value) return;
            _markdownTableAlignEnabled = value;
            RecomputeTableOverrides();
            RebuildVisualLayout();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Begin a batch of state updates. While batching, <see cref="RebuildVisualLayout"/> calls
    /// from the various Set/Show methods are coalesced and deferred until <see cref="EndBatch"/>,
    /// so a single edit rebuilds the visual layout once rather than several times.
    /// </summary>
    public void BeginBatch() => _batchingLayout = true;

    /// <summary>Ends a batch started by <see cref="BeginBatch"/>, applying a single layout rebuild if needed.</summary>
    public void EndBatch()
    {
        _batchingLayout = false;
        if (_layoutDirtyDuringBatch)
        {
            _layoutDirtyDuringBatch = false;
            RebuildVisualLayout();
            // Any SetCursor() during the batch computed scroll against the stale layout;
            // recompute now that the visual lines are rebuilt.
            EnsureCursorVisible();
        }
        InvalidateVisual();
    }

    public void SetLines(string[] lines)
    {
        if (ReferenceEquals(_lines, lines))
            return;

        _lines = lines.Length > 0 ? lines : [""];
        // A TextBuffer snapshot is a new array after every edit, but almost all of its line
        // strings are unchanged. Keep those measurements and discard only stale line text.
        // This also bounds the cache to the current document instead of clearing it globally.
        if (_textBoundaryCache.Count > 0)
        {
            var currentLines = _lines.ToHashSet(StringComparer.Ordinal);
            foreach (string cachedLine in _textBoundaryCache.Keys.ToArray())
                if (!currentLines.Contains(cachedLine))
                    _textBoundaryCache.Remove(cachedLine);
        }
        _lineNumberWidth = Math.Max(3, _lines.Length.ToString().Length);
        RecomputeTableOverrides();
        RebuildVisualLayout();
        InvalidateVisual();
        EditorCanvasAutomationPeer.NotifyTextChanged(this);
    }

    /// <summary>
    /// Directory used to resolve relative file-path links to absolute paths (the directory of
    /// the document being edited). Setting it to a new value discards cached path-existence
    /// results so links are re-verified against the new base directory.
    /// </summary>
    public string? DocumentDirectory
    {
        get => _documentDirectory;
        set
        {
            if (string.Equals(_documentDirectory, value, StringComparison.OrdinalIgnoreCase))
                return;
            _documentDirectory = value;
            _pathExists.Clear();
            _pathChecking.Clear();
            InvalidateVisual();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; InvalidateVisual(); }
    }

    public void SetCursor(CursorPosition cursor) { if (_cursor == cursor) return; _cursor = cursor; UpdateSuppressedTableRange(); EnsureCursorVisible(); InvalidateVisual(); EditorCanvasAutomationPeer.NotifySelectionChanged(this); }
    public void SetExtraCursors(IReadOnlyList<(int Line, int Col)> cursors) { _extraCursors = cursors; InvalidateVisual(); }
    public void SetSelection(Selection? sel) { if (_selection == sel) return; _selection = sel; InvalidateVisual(); EditorCanvasAutomationPeer.NotifySelectionChanged(this); }
    public void SetMode(VimMode mode) { _mode = mode; UpdateSuppressedTableRange(); InvalidateVisual(); }

    public void SetFolds(int[] visibleLineMap, IEnumerable<int> closedFoldStarts, IEnumerable<int> openFoldStarts)
    {
        _visibleLineMap = visibleLineMap;
        _closedFoldStarts = [.. closedFoldStarts];
        _openFoldStarts = [.. openFoldStarts];
        RebuildVisualLayout();
        InvalidateVisual();
    }
    public void SetGitDiff(Dictionary<int, GitLineState> diff)
    {
        _gitDiff = diff;
        InvalidateVisual();
    }

    public Dictionary<int, GitLineState>? GetGitDiff() => _gitDiff;

    /// <summary>Sets the per-line changed-since-save state for the change bar drawn at the
    /// right edge of the gutter (flush against the text).</summary>
    public void SetSaveDiff(Dictionary<int, Editor.Core.Editing.SaveLineState> diff)
    {
        _saveDiff = diff;
        InvalidateVisual();
    }

    public void SetInlayHints(IReadOnlyList<InlayHint> hints)
    {
        _inlayHints = hints;
        _inlayHintsByLine = [];
        foreach (var h in hints)
        {
            if (!_inlayHintsByLine.TryGetValue(h.Position.Line, out var list))
                _inlayHintsByLine[h.Position.Line] = list = [];
            list.Add(h);
        }
        InvalidateVisual();
    }

    public void SetBlameLines(Dictionary<int, EditorBlameLine>? blame)
    {
        _blameLines = blame ?? [];
        _hoveredBlameLine = -1;
        CloseBlameToolTip();
        _blameColWidth = ComputeBlameColumnWidth();
        // カラムの分だけ本文幅が変わる（折り返し幅・可視桁数に影響）ので、再描画に加えて再レイアウトも要求する。
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>blame 左カラムの幅（px）。最長の注釈テキスト幅＋左右パディング。注釈なしなら 0（カラム非表示）。</summary>
    private double ComputeBlameColumnWidth()
    {
        if (_blameLines.Count == 0) return 0;
        if (s_blameColUserWidth > 0) return s_blameColUserWidth;  // ドラッグで調整済みならその幅
        MeasureChar();
        double max = 0;
        foreach (var info in _blameLines.Values)
        {
            double w = 0;
            foreach (var ch in info.Display)
                w += CharW(ch);
            if (w > max) max = w;
        }
        return max + 12; // 左右 6px ずつの余白
    }

    public void SetDocumentHighlights(IReadOnlyList<DocumentHighlight>? highlights)
    {
        _documentHighlights = highlights ?? [];
        InvalidateVisual();
    }

    public void SetSemanticTokens(SemanticToken[] tokens)
    {
        _semanticTokensByLine = [];
        foreach (var tok in tokens)
        {
            // null brush = the server classified this token (variable/parameter/property) but we
            // render it in the default foreground. Store it as an explicit Foreground override so
            // that, when merged with regex tokens, it wins over heuristic guesses (e.g. PascalCase→Type).
            var brush = SemanticTokenTypeToBrush(tok.TokenType) ?? Theme.Foreground;
            if (!_semanticTokensByLine.TryGetValue(tok.Line, out var list))
                _semanticTokensByLine[tok.Line] = list = [];
            list.Add((tok.StartChar, tok.Length, brush));
        }
        // Pre-sort each line's tokens by start column so the render loop needs no LINQ allocation per frame.
        foreach (var list in _semanticTokensByLine.Values)
            list.Sort((a, b) => a.StartChar.CompareTo(b.StartChar));
        InvalidateVisual();
    }

    private Brush? SemanticTokenTypeToBrush(string tokenType) => tokenType switch
    {
        "namespace"                          => Theme.TokenType,
        "class" or "struct" or "enum"
            or "interface" or "type"
            or "typeParameter"               => Theme.TokenType,
        "function" or "method"               => Theme.TokenFunction,
        "keyword" or "modifier"              => Theme.TokenKeyword,
        "string" or "regexp"                 => Theme.TokenString,
        "number"                             => Theme.TokenNumber,
        "comment"                            => Theme.TokenComment,
        "decorator" or "attribute"           => Theme.TokenAttribute,
        "macro" or "operator"               => Theme.TokenKeyword,
        "variable" or "parameter"
            or "property" or "enumMember"
            or "event"                       => null,  // use default foreground (no override)
        _                                    => null
    };

    public void SetTokens(LineTokens[] tokens)
    {
        _tokensByLine = new Dictionary<int, SyntaxToken[]>(tokens.Length);
        foreach (var lineTokens in tokens)
        {
            _tokensByLine[lineTokens.Line] = lineTokens.Tokens.Length > 1
                ? lineTokens.Tokens.OrderBy(t => t.StartColumn).ToArray()
                : lineTokens.Tokens;
        }
        InvalidateVisual();
    }
    public void SetSearchMatches(List<CursorPosition> matches, string pattern)
    {
        _searchPattern = pattern;
        _searchMatchesByLine = [];
        foreach (var match in matches)
        {
            if (!_searchMatchesByLine.TryGetValue(match.Line, out var lineMatches))
                _searchMatchesByLine[match.Line] = lineMatches = [];
            lineMatches.Add(match);
        }
        InvalidateVisual();
    }
    public void SetSubstitutePreview(IReadOnlyDictionary<int, string>? previewLines)
    {
        _substitutePreviewLines = previewLines ?? new Dictionary<int, string>();
        InvalidateVisual();
    }
    public void ShowLineNumbers(bool show)
    {
        _showLineNumbers = show;
        RebuildVisualLayout();
        InvalidateVisual();
    }
    public void ShowRelativeLineNumbers(bool relative)
    {
        _relativeNumber = relative;
        InvalidateVisual();
    }

    public void SetScrollOff(int scrollOff) { _scrollOff = Math.Max(0, scrollOff); }

    public void SetScrollbar(bool show)
    {
        if (_showScrollbar == show) return;
        _showScrollbar = show;
        InvalidateVisual();
    }

    public void SetMinimap(bool show)
    {
        if (_showMinimap == show) return;
        _showMinimap = show;
        RebuildVisualLayout();
        InvalidateVisual();
    }

    public void SetColorColumn(int col)
    {
        if (_colorColumn == col) return;
        _colorColumn = col;
        InvalidateVisual();
    }

    public void SetIndentGuides(bool show, int tabStop)
    {
        if (_showIndentGuides == show && _indentGuideTabStop == tabStop) return;
        _showIndentGuides = show;
        _indentGuideTabStop = Math.Max(1, tabStop);
        InvalidateVisual();
    }

    public void SetColorPreview(bool show)
    {
        if (_showColorPreview == show) return;
        _showColorPreview = show;
        InvalidateVisual();
    }

    public void SetList(bool show, string listchars)
    {
        if (_showList == show && _listCharsRaw == listchars) return;
        _showList = show;
        _listCharsRaw = listchars;
        // Parse "tab:→ ,trail:·,eol:¶,space:·"
        _listTab = "→ "; _listTrail = "·"; _listEol = "¶"; _listSpace = "";
        foreach (var part in listchars.Split(','))
        {
            int colon = part.IndexOf(':');
            if (colon < 0) continue;
            var key = part[..colon].Trim();
            var val = part[(colon + 1)..];
            switch (key)
            {
                case "tab":   _listTab   = val; break;
                case "trail": _listTrail = val; break;
                case "eol":   _listEol   = val; break;
                case "space": _listSpace = val; break;
            }
        }
        InvalidateVisual();
    }

    public void SetDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
        InvalidateVisual();
    }

    // Spell check errors: line → list of (startCol, endCol) spans
    private Dictionary<int, IReadOnlyList<(int Start, int End)>> _spellErrors = [];

    public void SetSpellErrors(Dictionary<int, IReadOnlyList<(int Start, int End)>> errors)
    {
        _spellErrors = errors;
        InvalidateVisual();
    }

    // Full-width space / trailing whitespace markers: line → list of issues (see 'highlightwhitespace')
    private Dictionary<int, List<WhitespaceIssue>> _whitespaceIssues = [];

    public void SetWhitespaceIssues(Dictionary<int, List<WhitespaceIssue>> issues)
    {
        _whitespaceIssues = issues;
        InvalidateVisual();
    }

    public void SetCompletionItems(IReadOnlyList<LspCompletionItem> items, int selection, int scrollOffset = 0)
    {
        _completionItems = items;
        _completionSelection = selection;
        _completionScrollOffset = scrollOffset;
        InvalidateVisual();
    }

    public void SetSignatureHelp(LspSignatureHelp? help)
    {
        _signatureHelp = help;
        InvalidateVisual();
    }

    public void SetCodeActions(IReadOnlyList<LspCodeAction> items, int selection, int scrollOffset = 0)
    {
        if (_codeActionItems.Count == 0 && items.Count == 0) return;
        _codeActionItems = items;
        _codeActionsSelection = selection;
        _codeActionsScrollOffset = scrollOffset;
        InvalidateVisual();
    }

    public void SetImeCompositionText(string text, int cursor = -1)
    {
        text ??= string.Empty;
        if (_imeCompositionText == text && _imeCompositionCursor == cursor) return;
        _imeCompositionText = text;
        _imeCompositionCursor = cursor;
        InvalidateVisual();
    }

    /// <summary>
    /// Sets the IME clause segmentation for the active composition. <paramref name="starts"/>
    /// holds the character offset of each clause start (including 0); the focused clause
    /// is [targetStart, targetEnd). Pass an empty array / -1 to clear.
    /// </summary>
    public void SetImeClauses(int[]? starts, int targetStart, int targetEnd)
    {
        starts ??= [];
        if (_imeClauseStarts.AsSpan().SequenceEqual(starts)
            && _imeTargetClauseStart == targetStart && _imeTargetClauseEnd == targetEnd) return;
        _imeClauseStarts = starts;
        _imeTargetClauseStart = targetStart;
        _imeTargetClauseEnd = targetEnd;
        InvalidateVisual();
    }
    public void SetImeCandidates(IReadOnlyList<string>? candidates, int selectedIndex)
    {
        var next = candidates?
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .ToArray() ?? [];

        int nextSelection = next.Length == 0 ? -1 : Math.Clamp(selectedIndex, 0, next.Length - 1);

        if (_imeCandidateSelection == nextSelection && _imeCandidates.SequenceEqual(next))
            return;

        _imeCandidates = next;
        _imeCandidateSelection = nextSelection;
        InvalidateVisual();
    }

    public double CharWidth => _charWidth;

    /// <summary>
    /// Maps a sticky source column to the closest character cell on another line using the
    /// actual rendered X coordinate. Markdown table alignment overrides are included for both
    /// lines, so j/k follows the pipes and cells as they appear rather than their buffer columns.
    /// </summary>
    public int ResolveVerticalColumn(int sourceLine, int sourceColumn, int targetLine, int maxColumn)
    {
        if (_lines.Length == 0) return 0;

        sourceLine = Math.Clamp(sourceLine, 0, _lines.Length - 1);
        targetLine = Math.Clamp(targetLine, 0, _lines.Length - 1);
        string sourceText = _lines[sourceLine];
        string targetText = _lines[targetLine];

        SetActiveLine(sourceLine);
        double goalX = GetVisualX(sourceText, Math.Clamp(sourceColumn, 0, sourceText.Length));

        SetActiveLine(targetLine);
        int column = VisualXToCol(targetText, goalX);
        return Math.Clamp(column, 0, maxColumn);
    }
    public double LineHeight => _lineHeight;
    public int VisibleLines => Math.Max(1, _visibleLines);
    public int FirstVisibleLine => _lineHeight <= 0 ? 0 : (int)(_scrollOffsetY / _lineHeight);
    public int LastVisibleLine => Math.Min(TotalVisualLines - 1, FirstVisibleLine + _visibleLines);
    public (int FirstLine, int LastLine) GetVisibleBufferLineRange()
    {
        int firstVisualLine = FirstVisibleLine;
        int lastVisualLine = LastVisibleLine;
        int firstBufferLine = int.MaxValue;
        int lastBufferLine = 0;

        for (int i = firstVisualLine; i <= lastVisualLine; i++)
        {
            var segment = GetVisualSegment(i);
            firstBufferLine = Math.Min(firstBufferLine, segment.BufferLine);
            lastBufferLine = Math.Max(lastBufferLine, segment.BufferLine);
        }

        return firstBufferLine == int.MaxValue
            ? (0, 0)
            : (firstBufferLine, lastBufferLine);
    }

    public void ScrollTo(double offsetY, double offsetX = 0)
    {
        double maxOffsetY = MaxScrollOffsetY;
        double maxOffsetX = _wrapLines
            ? 0
            : Math.Max(0, TotalContentWidth - UsableViewportWidth);
        _scrollOffsetY = Math.Clamp(offsetY, 0, maxOffsetY);
        _scrollOffsetX = _wrapLines ? 0 : Math.Clamp(offsetX, 0, maxOffsetX);
        ScrollChanged?.Invoke(_scrollOffsetY, _scrollOffsetX);
        InvalidateVisual();
    }

    private void RebuildVisualLayout()
    {
        // Defer while batching — EndBatch() performs a single rebuild.
        if (_batchingLayout) { _layoutDirtyDuringBatch = true; return; }

        MeasureChar();
        var (_, _, _, gutterWidth) = GetGutterMetrics();
        double minimapReserve = _showMinimap ? MinimapWidth : 0;
        double availableTextWidth = Math.Max(1, RenderSize.Width - gutterWidth - minimapReserve);

        var visibleLines = _visibleLineMap.Length > 0
            ? _visibleLineMap
            : Enumerable.Range(0, Math.Max(1, _lines.Length)).ToArray();

        var visualLines = new List<VisualLineSegment>(visibleLines.Length);

        // The horizontal extent (maxLineWidth) only matters when not wrapping, and only changes
        // when the line array changes. Reuse the cached value to skip the O(total chars) scan on
        // edits that don't touch text (cursor/mode/fold/line-number toggles).
        bool needMaxWidth = !_wrapLines;
        bool widthCached = needMaxWidth && ReferenceEquals(_maxLineWidthCacheLines, _lines);
        double maxLineWidth = widthCached ? _cachedMaxLineWidth : 0;

        foreach (int lineIndex in visibleLines)
        {
            int safeLine = Math.Clamp(lineIndex, 0, Math.Max(0, _lines.Length - 1));
            string lineText = safeLine < _lines.Length ? _lines[safeLine] : string.Empty;
            SetActiveLine(safeLine);

            if (!_wrapLines || lineText.Length == 0 || availableTextWidth <= 1)
            {
                if (needMaxWidth && !widthCached)
                    maxLineWidth = Math.Max(maxLineWidth, GetVisualX(lineText, lineText.Length));
                visualLines.Add(new VisualLineSegment(safeLine, 0, false));
                continue;
            }

            int startCol = 0;
            bool isContinuation = false;
            while (startCol < lineText.Length)
            {
                visualLines.Add(new VisualLineSegment(safeLine, startCol, isContinuation));
                int segLen = GetWrapSegmentLength(lineText, startCol, availableTextWidth);
                startCol += Math.Max(1, segLen);
                isContinuation = true;
            }
        }

        if (visualLines.Count == 0)
            visualLines.Add(new VisualLineSegment(0, 0, false));

        if (needMaxWidth && !widthCached)
        {
            _cachedMaxLineWidth = maxLineWidth;
            _maxLineWidthCacheLines = _lines;
        }

        _visualLines = visualLines.ToArray();
        _contentWidth = _wrapLines ? RenderSize.Width : gutterWidth + maxLineWidth;

        ClampScrollOffsets(raiseScrollChanged: true);
        ScrollMetricsChanged?.Invoke();
    }

    private int GetWrapSegmentLength(string lineText, int startCol, double maxWidth)
    {
        if (startCol >= lineText.Length) return 0;
        if (maxWidth <= 0) return 1;

        double width = 0;
        int length = 0;

        for (int i = startCol; i < lineText.Length; i++)
        {
            double chWidth = CharW(lineText[i]);
            if (length > 0 && width + chWidth > maxWidth)
                break;

            width += chWidth;
            length++;

            if (width >= maxWidth)
                break;
        }

        return Math.Max(1, length);
    }

    private (int bpColWidth, int lineNumWidth, double foldColWidth, int gutterWidth) GetGutterMetrics()
    {
        // ブレークポイント列はガター最左に確保する。デバッグ無効時(_breakpointsEnabled=false)は幅0で
        // 従来のガター（行番号＋フォールド列）と完全に一致する＝既存利用に影響しない。
        int bpColWidth = _breakpointsEnabled ? (int)Math.Max(14.0, _charWidth + 2) : 0;
        int lineNumWidth = _showLineNumbers ? (int)((_lineNumberWidth + 1) * _charWidth) : 0;
        double foldColWidth = _showLineNumbers ? Math.Max(16.0, _charWidth + 4) : 0;
        // blame 左カラム（:Gblame 有効時のみ幅 > 0）はガターの最左＝ブレークポイント列よりさらに左に確保する。
        // 各列の並びは blame | ブレークポイント | 行番号 | フォールド | 本文（x 位置は _blameColWidth 起点）。
        int gutterWidth = (int)(_blameColWidth + bpColWidth + lineNumWidth + foldColWidth);
        return (bpColWidth, lineNumWidth, foldColWidth, gutterWidth);
    }

    private void ClampScrollOffsets(bool raiseScrollChanged)
    {
        double maxOffsetY = MaxScrollOffsetY;
        double maxOffsetX = _wrapLines
            ? 0
            : Math.Max(0, TotalContentWidth - UsableViewportWidth);

        double newOffsetY = Math.Clamp(_scrollOffsetY, 0, maxOffsetY);
        double newOffsetX = _wrapLines ? 0 : Math.Clamp(_scrollOffsetX, 0, maxOffsetX);

        bool changed = newOffsetY != _scrollOffsetY || newOffsetX != _scrollOffsetX;
        _scrollOffsetY = newOffsetY;
        _scrollOffsetX = newOffsetX;

        if (raiseScrollChanged && changed)
            ScrollChanged?.Invoke(_scrollOffsetY, _scrollOffsetX);
    }

    private VisualLineSegment GetVisualSegment(int visualLine)
    {
        if (_visualLines.Length == 0)
            return new VisualLineSegment(0, 0, false);

        int clamped = Math.Clamp(visualLine, 0, _visualLines.Length - 1);
        return _visualLines[clamped];
    }

    private int GetSegmentEndColumn(int visualLine)
    {
        var segment = GetVisualSegment(visualLine);
        int next = visualLine + 1;
        if (next < _visualLines.Length && _visualLines[next].BufferLine == segment.BufferLine)
            return _visualLines[next].StartColumn;

        string lineText = segment.BufferLine < _lines.Length ? _lines[segment.BufferLine] : string.Empty;
        return lineText.Length;
    }

    private int GetVisualLineIndexFromY(double y)
    {
        if (_lineHeight <= 0) return 0;
        int visualLine = (int)((y + _scrollOffsetY) / _lineHeight);
        return Math.Clamp(visualLine, 0, Math.Max(0, TotalVisualLines - 1));
    }

    // ─────────────── Mouse handling ───────────────

    protected override void OnMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // 3 lines per notch (each notch = 120 units)
        double delta = -(e.Delta / 120.0) * 3 * _lineHeight;
        ScrollTo(_scrollOffsetY + delta, _scrollOffsetX);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _isDragging = false;
        _mouseDownPoint = e.GetPosition(this);

        var point = _mouseDownPoint.Value;

        // Scrollbar drag — the bars overlay everything, so test them first
        if (_showScrollbar && TryBeginScrollbarDrag(point))
        {
            e.Handled = true;
            return;
        }

        CaptureMouse();

        // Minimap click — scroll to the clicked position
        if (_showMinimap && point.X >= RenderSize.Width - MinimapWidth)
        {
            int totalLines = Math.Max(1, _lines.Length);
            double lineStripH = Math.Max(1.0, Math.Min(2.0, RenderSize.Height / totalLines));
            double totalMapH  = totalLines * lineStripH;

            double viewportTopLine    = _scrollOffsetY / _lineHeight;
            double viewportCentreLine = viewportTopLine + _visibleLines / 2.0;
            double mapOffsetY = 0;
            if (totalMapH > RenderSize.Height)
            {
                double idealCentre = viewportCentreLine * lineStripH;
                mapOffsetY = Math.Clamp(idealCentre - RenderSize.Height / 2.0, 0, totalMapH - RenderSize.Height);
            }

            double clickedLine = (point.Y + mapOffsetY) / lineStripH;
            double newScrollY = Math.Clamp((clickedLine - _visibleLines / 2.0) * _lineHeight, 0,
                Math.Max(0, TotalContentHeight - RenderSize.Height));
            _scrollOffsetY = newScrollY;
            ClampScrollOffsets(raiseScrollChanged: true);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var (bpColWidth, lineNumWidth, _, gutterWidth) = GetGutterMetrics();
        var boundaries = new GutterHitTester.Boundaries(_blameColWidth, bpColWidth, lineNumWidth, gutterWidth);

        // blame カラム右端のリサイズハンドル — ドラッグで幅の変更を開始する（CaptureMouse は取得済み）。
        if (IsOnBlameColEdge(point))
        {
            _blameColResizing = true;
            CloseBlameToolTip();
            e.Handled = true;
            return;
        }

        // Blame margin click (far left) — report the line's commit info to the host.
        if (_gutterHitTester.TryHitBlameGutter(point, boundaries, out int blameClickLine))
        {
            if (blameClickLine >= 0 && _blameLines.TryGetValue(blameClickLine, out var blame))
                BlameClicked?.Invoke(blameClickLine, blame);
            e.Handled = true;
            return;
        }

        // Breakpoint column click — toggle a breakpoint on that line.
        if (_gutterHitTester.TryHitBreakpointGutter(point, boundaries, out int bpClickLine))
        {
            if (bpClickLine >= 0) BreakpointToggled?.Invoke(bpClickLine);
            e.Handled = true;
            return;
        }

        if (_showLineNumbers && point.X < gutterWidth)
        {
            // Fold column click — check for fold indicator
            if (_gutterHitTester.TryHitFoldGutter(point, boundaries, out int foldClickLine))
            {
                if (foldClickLine >= 0 && (_closedFoldStarts.Contains(foldClickLine) || _openFoldStarts.Contains(foldClickLine)))
                    FoldGutterClicked?.Invoke(foldClickLine);
            }
            e.Handled = true;
            return;
        }

        var (line, col) = HitTest(point);

        // Ctrl+Click on a detected URL or file path opens it instead of moving the cursor
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            var link = GetLinkAt(line, col);
            if (link != null)
            {
                if (link.Value.Kind == Editor.Core.Text.LinkKind.Url)
                {
                    LinkClicked?.Invoke(link.Value.Text);
                }
                else if (ResolveLinkPath(link.Value.Text) is { } resolved)
                {
                    FileLinkClicked?.Invoke(resolved);
                }
                e.Handled = true;
                return;
            }
        }

        MouseClicked?.Invoke(line, col);
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        if ((_draggingVScrollbar || _draggingHScrollbar) &&
            e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            UpdateScrollbarDrag(point);
            return;
        }

        // blame カラム幅のリサイズドラッグ中（テキスト選択ドラッグより先に処理する）
        if (_blameColResizing && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            ResizeBlameColumnTo(point.X);
            return;
        }

        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && IsMouseCaptured)
        {
            if (!_isDragging && _mouseDownPoint is { } downPoint)
            {
                double dx = Math.Abs(point.X - downPoint.X);
                double dy = Math.Abs(point.Y - downPoint.Y);
                if (dx < SystemParameters.MinimumHorizontalDragDistance &&
                    dy < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }
            }

            _isDragging = true;
            ClearDataTipHover();
            // Don't fire MouseDragging when dragging in minimap area
            if (!(_showMinimap && point.X >= RenderSize.Width - MinimapWidth))
            {
                var (line, col) = HitTest(point);
                MouseDragging?.Invoke(line, col);
            }
            return;
        }

        // Arrow cursor over the overlay scrollbars
        if (_showScrollbar && IsOverScrollbar(point))
        {
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
            ClearDataTipHover();
            Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }

        // Show pointer cursor over minimap
        if (_showMinimap && point.X >= RenderSize.Width - MinimapWidth)
        {
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
            ClearDataTipHover();
            Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }

        // Update fold/breakpoint/blame hover state
        var (bpColW, lineNumWidth2, _, gutterWidth2) = GetGutterMetrics();
        var boundaries2 = new GutterHitTester.Boundaries(_blameColWidth, bpColW, lineNumWidth2, gutterWidth2);

        // blame カラム右端のリサイズハンドル上 — 左右カーソルを出し、他のホバー状態は解除する。
        if (IsOnBlameColEdge(point))
        {
            if (_hoveredBlameLine >= 0) { _hoveredBlameLine = -1; CloseBlameToolTip(); InvalidateVisual(); }
            SetHoveredBreakpointLine(-1);
            ClearDataTipHover();
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
            Cursor = System.Windows.Input.Cursors.SizeWE;
            return;
        }

        // blame 左カラム（最左）のホバー行（注釈のある行だけ）。領域外へ出たら解除して再描画し、
        // コミット情報ツールチップも開閉する。
        bool inBlameGutter = _gutterHitTester.TryHitBlameGutter(point, boundaries2, out int blameLine);
        int blameHover = inBlameGutter && _blameLines.ContainsKey(blameLine) ? blameLine : -1;
        if (blameHover != _hoveredBlameLine)
        {
            _hoveredBlameLine = blameHover;
            UpdateBlameToolTip(blameHover);
            InvalidateVisual();
        }

        if (_gutterHitTester.TryHitBreakpointGutter(point, boundaries2, out int bpHoverLine))
        {
            // Hovering in breakpoint column — show a faint placeholder dot and a hand cursor.
            Cursor = bpHoverLine >= 0 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
            SetHoveredBreakpointLine(bpHoverLine);
            ClearDataTipHover();
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
        }
        else if (inBlameGutter)
        {
            // Hovering in blame margin — hand cursor over an annotated (clickable) line.
            SetHoveredBreakpointLine(-1);
            ClearDataTipHover();
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
            Cursor = blameHover >= 0
                ? System.Windows.Input.Cursors.Hand
                : System.Windows.Input.Cursors.Arrow;
        }
        else if (_showLineNumbers && _gutterHitTester.TryHitFoldGutter(point, boundaries2, out int foldHoverLine))
        {
            // Hovering in fold column
            SetHoveredBreakpointLine(-1);
            ClearDataTipHover();
            bool onFold = foldHoverLine >= 0 && (_closedFoldStarts.Contains(foldHoverLine) || _openFoldStarts.Contains(foldHoverLine));
            Cursor = onFold ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
            int prev = _hoveredFoldLine;
            _hoveredFoldLine = onFold ? foldHoverLine : -1;
            if (_hoveredFoldLine != prev) InvalidateVisual();
        }
        else if (_showLineNumbers && _gutterHitTester.TryHitLineNumberGutter(point, boundaries2, out _))
        {
            // Hovering in line number area
            SetHoveredBreakpointLine(-1);
            ClearDataTipHover();
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
            Cursor = System.Windows.Input.Cursors.Arrow;
        }
        else
        {
            SetHoveredBreakpointLine(-1);
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }

            // Debug DataTip: while stopped, hovering an identifier asks the host to evaluate it.
            UpdateDataTipHover(point);

            // Ctrl+hover over a link shows a hand cursor as a clickability cue
            bool ctrlDown = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
            bool onLink = false;
            if (ctrlDown)
            {
                var (hLine, hCol) = HitTest(point);
                onLink = GetLinkAt(hLine, hCol) != null;
            }
            Cursor = onLink ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.IBeam;
        }
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        _mouseDownPoint = null;
        _blameColResizing = false;
        if (_draggingVScrollbar || _draggingHScrollbar)
        {
            _draggingVScrollbar = false;
            _draggingHScrollbar = false;
        }
        if (_isDragging)
        {
            _isDragging = false;
            MouseDragEnded?.Invoke();
        }
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
        if (_hoveredBlameLine >= 0) { _hoveredBlameLine = -1; InvalidateVisual(); }
        CloseBlameToolTip();
        ClearDataTipHover();
        Cursor = System.Windows.Input.Cursors.IBeam;
    }

    protected override void OnMouseRightButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var (line, col) = HitTest(e.GetPosition(this));
        MouseRightClicked?.Invoke(line, col);
        // Do not mark as Handled — WPF needs the event to bubble to trigger ContextMenu on MouseRightButtonUp
    }

    // Y座標からバッファ行インデックスを返す（fold-aware）
    private int BufferLineFromY(double y)
    {
        int visualLine = GetVisualLineIndexFromY(y);
        return GetVisualSegment(visualLine).BufferLine;
    }

    // Y座標からバッファ行インデックスを返す（ガタークリック用）
    private int HitTestGutterLine(System.Windows.Point point)
    {
        if (_lineHeight <= 0) return -1;
        int visualLine = GetVisualLineIndexFromY(point.Y);
        var segment = GetVisualSegment(visualLine);
        if (_wrapLines && segment.IsContinuation)
            return -1;
        return segment.BufferLine;
    }

    // カーソルのビジュアル行インデックスを返す（fold-aware）
    private int GetCursorVisualLine()
    {
        if (_visualLines.Length == 0) return 0;

        string lineText = _cursor.Line < _lines.Length ? _lines[_cursor.Line] : string.Empty;
        int cursorCol = Math.Clamp(_cursor.Column, 0, lineText.Length);
        int firstForLine = -1;

        for (int i = 0; i < _visualLines.Length; i++)
        {
            var segment = _visualLines[i];
            if (segment.BufferLine != _cursor.Line)
                continue;

            if (firstForLine < 0)
                firstForLine = i;

            int segEnd = GetSegmentEndColumn(i);
            bool isLastForLine = i == _visualLines.Length - 1
                || _visualLines[i + 1].BufferLine != segment.BufferLine;

            if (lineText.Length == 0)
                return i;

            if (cursorCol >= segment.StartColumn &&
                (cursorCol < segEnd || (isLastForLine && cursorCol <= segEnd)))
                return i;
        }

        if (firstForLine >= 0)
            return firstForLine;

        int fallback = Array.FindIndex(_visualLines, s => s.BufferLine >= _cursor.Line);
        return fallback >= 0 ? fallback : _visualLines.Length - 1;
    }

    private (int line, int col) HitTest(System.Windows.Point point)
    {
        if (_lineHeight <= 0 || _charWidth <= 0) return (0, 0);

        var (_, _, _, gutterWidth) = GetGutterMetrics();

        int visualLine = GetVisualLineIndexFromY(point.Y);
        var segment = GetVisualSegment(visualLine);
        int line = segment.BufferLine;
        line = Math.Clamp(line, 0, Math.Max(0, _lines.Length - 1));

        string hitLine = line < _lines.Length ? _lines[line] : string.Empty;
        double visualX = point.X - gutterWidth + (_wrapLines ? 0 : _scrollOffsetX);

        if (_wrapLines)
        {
            // segmentText is a substring, so its indices don't match the absolute buffer columns
            // that table overrides are keyed by — skip overrides here rather than mis-apply them.
            _activeLineOverrides = null;
            int segStart = Math.Min(segment.StartColumn, hitLine.Length);
            int segEnd = Math.Min(GetSegmentEndColumn(visualLine), hitLine.Length);
            int segLength = Math.Max(0, segEnd - segStart);
            string segmentText = segLength > 0 ? hitLine.Substring(segStart, segLength) : string.Empty;
            int segCol = VisualXToCol(segmentText, visualX);
            int wrappedMaxCol = Math.Max(0, hitLine.Length - 1);
            int colWrapped = Math.Clamp(segStart + segCol, 0, wrappedMaxCol);
            return (line, colWrapped);
        }

        SetActiveLine(line);
        int col = VisualXToCol(hitLine, visualX);
        int maxCol = Math.Max(0, hitLine.Length - 1);
        col = Math.Clamp(col, 0, maxCol);

        return (line, col);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureChar();
        // FrameworkElement must not return Infinity — clamp to finite values
        double w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_lineHeight > 0)
        {
            _visibleLines = (int)(finalSize.Height / _lineHeight) + 2;
            VisibleLinesChanged?.Invoke(_visibleLines);
        }
        if (_charWidth > 0)
        {
            var (_, _, _, gutterWidth) = GetGutterMetrics();
            double minimapReserve = _showMinimap ? MinimapWidth : 0;
            double textAreaWidth = Math.Max(1, finalSize.Width - gutterWidth - minimapReserve);
            _visibleColumns = (int)(textAreaWidth / _charWidth) + 2;
        }

        RebuildVisualLayout();
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        MeasureChar();

        // Background
        dc.DrawRectangle(Theme.Background, null, new Rect(size));

        // Reserve space at the bottom for the horizontal scrollbar so it never
        // overlaps the last line of text. Content (text/gutter/minimap) is clipped
        // to contentBottom; the scrollbars themselves are drawn afterwards.
        bool needHorizBar = _showScrollbar && !_wrapLines && TotalContentWidth > size.Width + 1;
        double contentBottom = needHorizBar ? Math.Max(0, size.Height - OverlayRenderer.ScrollbarSize) : size.Height;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, size.Width, contentBottom)));

        var (bpColWidth, lineNumWidth, foldColWidth, gutterWidth) = GetGutterMetrics();
        double textLeft = gutterWidth;
        var metrics = BuildGlyphMetrics();

        int firstLine = (int)(_scrollOffsetY / _lineHeight);
        int lastLine = Math.Min(TotalVisualLines - 1, firstLine + _visibleLines + 1);
        double baseOffsetX = _scrollOffsetX;

        // Compute matching bracket once — reused for every visible line
        var bracketMatch = FindMatchingBracket(_cursor.Line, _cursor.Column);

        // Conflict marker region tracking — scan from line 0 up to firstLine to determine state
        bool inOursRegion = false, inTheirsRegion = false;
        for (int si = 0; si < firstLine && si < TotalVisualLines; si++)
        {
            var seg = GetVisualSegment(si);
            if (seg.BufferLine < _lines.Length)
            {
                var sl = _lines[seg.BufferLine];
                if (sl.StartsWith("<<<<<<<", StringComparison.Ordinal))      { inOursRegion = true;  inTheirsRegion = false; }
                else if (sl.StartsWith("=======", StringComparison.Ordinal)) { inOursRegion = false; inTheirsRegion = true;  }
                else if (sl.StartsWith(">>>>>>>", StringComparison.Ordinal)) { inOursRegion = false; inTheirsRegion = false; }
            }
        }

        // Draw each visible line (vi = visual index, l = buffer line)
        for (int vi = firstLine; vi <= lastLine; vi++)
        {
            var segment = GetVisualSegment(vi);
            int l = segment.BufferLine;
            double y = vi * _lineHeight - _scrollOffsetY;
            if (y + _lineHeight < 0 || y > contentBottom) continue;

            bool isPreviewLine = _substitutePreviewLines.TryGetValue(l, out var previewLine);
            string lineText = isPreviewLine ? previewLine! : l < _lines.Length ? _lines[l] : "";
            // Preview text (from :s) doesn't share column positions with the real buffer line,
            // so table overrides (keyed by real-buffer columns) must not be applied to it.
            SetActiveLine(isPreviewLine ? -1 : l);
            _scrollOffsetX = _wrapLines ? GetVisualX(lineText, segment.StartColumn) : baseOffsetX;
            bool drawNumberAndFold = !_wrapLines || !segment.IsContinuation;

            // Current line highlight
            if (l == _cursor.Line && Theme.CurrentLineBg != null && size.Width > textLeft)
                dc.DrawRectangle(Theme.CurrentLineBg, null, new Rect(textLeft, y, size.Width - textLeft, _lineHeight));

            // Debug execution line highlight (the line the debugger is currently stopped at)
            if (_breakpointsEnabled && l == _executionLine && size.Width > textLeft)
                dc.DrawRectangle(ExecutionLineBrush, null, new Rect(textLeft, y, size.Width - textLeft, _lineHeight));

            if (_substitutePreviewLines.ContainsKey(l) && size.Width > textLeft)
                dc.DrawRectangle(Theme.DocumentHighlightBackground, null, new Rect(textLeft, y, size.Width - textLeft, _lineHeight));

            // Conflict marker highlighting
            if (lineText.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                DrawConflictBackground(dc, y, textLeft, Theme.ConflictOursHeader);
                inOursRegion = true; inTheirsRegion = false;
            }
            else if (lineText.StartsWith("=======", StringComparison.Ordinal))
            {
                DrawConflictBackground(dc, y, textLeft, Theme.ConflictSeparator);
                inOursRegion = false; inTheirsRegion = true;
            }
            else if (lineText.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                DrawConflictBackground(dc, y, textLeft, Theme.ConflictTheirsHeader);
                inOursRegion = false; inTheirsRegion = false;
            }
            else if (inOursRegion)
                DrawConflictBackground(dc, y, textLeft, Theme.ConflictOurs);
            else if (inTheirsRegion)
                DrawConflictBackground(dc, y, textLeft, Theme.ConflictTheirs);

            // Line number gutter
            if (_showLineNumbers)
            {
                dc.DrawRectangle(Theme.LineNumberBg, null, new Rect(0, y, gutterWidth, _lineHeight));
                if (drawNumberAndFold)
                {
                    GutterRenderer.DrawLineNumberAndFold(dc, Theme, metrics, l, y,
                        _blameColWidth, bpColWidth, lineNumWidth, foldColWidth,
                        _cursor.Line, _relativeNumber, _lineNumberWidth,
                        _closedFoldStarts, _openFoldStarts, _hoveredFoldLine, _gitDiff);
                }
            }

            // Changed-since-save change bar — flush against the text at the right edge of the
            // gutter, so it reads as a property of the code line (distinct from the git bar,
            // which sits at the far-left edge). Shown even when line numbers are hidden.
            if (drawNumberAndFold)
                GutterRenderer.DrawSaveDiffBar(dc, Theme, _lineHeight, l, y, gutterWidth, _saveDiff);

            // Breakpoint glyph / execution-line arrow in the dedicated breakpoint column
            // (right of the blame margin when blame is active).
            if (_breakpointsEnabled && drawNumberAndFold && bpColWidth > 0)
                DrawBreakpointGlyph(dc, l, y, _blameColWidth, bpColWidth);

            // Git blame margin (far-left column of the gutter)
            if (_blameColWidth > 0)
                GutterRenderer.DrawBlameMargin(dc, Theme, metrics, _blameLines, _hoveredBlameLine, l, y, 0, _blameColWidth, drawNumberAndFold);

            dc.PushClip(new RectangleGeometry(new Rect(textLeft, y, Math.Max(0, size.Width - textLeft), _lineHeight)));
            // A bug in any single overlay must not tear down the whole render pass (which would
            // crash the host app). Catch per-line, and always Pop the clip so it stays balanced.
            try
            {
                // Selection highlight
                DrawSelection(dc, l, y, textLeft, lineText);

                // Search highlights
                DrawSearchHighlights(dc, l, y, textLeft, lineText);

                // Document highlights (LSP)
                LspOverlayRenderer.DrawDocumentHighlights(dc, Theme, metrics, _documentHighlights, l, y, textLeft, lineText, _scrollOffsetX);

                // Full-width space / trailing whitespace markers
                OverlayRenderer.DrawWhitespaceIssues(dc, Theme, metrics, l, y, textLeft, lineText, _scrollOffsetX, _whitespaceIssues);

                // Matching bracket highlight
                DrawMatchingBrackets(dc, l, y, textLeft, lineText, bracketMatch);

                // Text with syntax coloring
                DrawLineText(dc, l, lineText, y, textLeft);

                // Inline color preview swatches
                if (_showColorPreview)
                    OverlayRenderer.DrawColorSwatches(dc, metrics, lineText, y, textLeft, _scrollOffsetX);

                // Invisible character markers (set list)
                DrawListChars(dc, lineText, y, textLeft);

                // LSP inlay hints (inline ghost text)
                LspOverlayRenderer.DrawInlayHints(dc, metrics, _inlayHintsByLine, l, lineText, y, textLeft, _scrollOffsetX);

                // LSP diagnostics (wavy underlines)
                DrawDiagnostics(dc, l, y, textLeft, lineText);

                // Spell check errors (blue wavy underlines)
                DrawSpellErrors(dc, l, y, textLeft, lineText);

                // Detected link underlines
                DrawLinkUnderline(dc, l, y, textLeft, lineText, segment.StartColumn, GetSegmentEndColumn(vi));

                // Cursor
                DrawCursor(dc, l, y, textLeft, lineText);

                // Extra cursors (multi-cursor mode)
                DrawExtraCursors(dc, l, y, textLeft, lineText);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EditorCanvas: failed rendering line {l}: {ex}");
            }
            finally
            {
                dc.Pop();
            }
        }

        _scrollOffsetX = baseOffsetX;

        // Post-loop overlays. Guard them as a group so a failure can't escape OnRender (crashing
        // the app); the content clip is popped in the finally either way so it stays balanced.
        try
        {
            DrawImeCandidatePopup(dc, textLeft, size);
            LspOverlayRenderer.DrawSignatureHelp(dc, Theme, metrics, _signatureHelp, textLeft, size, GetCursorPixelPosition());
            LspOverlayRenderer.DrawCompletionPopup(dc, Theme, metrics, _completionItems, _completionSelection, _completionScrollOffset, textLeft, size, GetCursorPixelPosition());
            LspOverlayRenderer.DrawCodeActionPopup(dc, Theme, metrics, _codeActionItems, _codeActionsSelection, _codeActionsScrollOffset, textLeft, size, GetCursorPixelPosition());

            // Color column guide line
            OverlayRenderer.DrawColorColumn(dc, Theme, _colorColumn, _charWidth, gutterWidth, _scrollOffsetX, size);

            // Indent guide lines
            if (_showIndentGuides && _charWidth > 0)
            {
                double indentWidth = _indentGuideTabStop * _charWidth;

                // Find the deepest indent level among all visible non-blank lines
                int maxIndentLevel = 0;
                for (int vi = firstLine; vi <= lastLine; vi++)
                {
                    var seg = GetVisualSegment(vi);
                    int l = seg.BufferLine;
                    if (l >= _lines.Length) continue;
                    var line = _lines[l];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int spaces = 0;
                    foreach (char ch in line)
                    {
                        if (ch == ' ') spaces++;
                        else if (ch == '\t') spaces += _indentGuideTabStop;
                        else break;
                    }
                    int level = spaces / _indentGuideTabStop;
                    if (level > maxIndentLevel) maxIndentLevel = level;
                }

                OverlayRenderer.DrawIndentGuides(dc, Theme, gutterWidth, indentWidth, maxIndentLevel, _scrollOffsetX, size);
            }

            // Gutter border
            if (_showLineNumbers)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), 1);
                dc.DrawLine(pen, new Point(gutterWidth - 1, 0), new Point(gutterWidth - 1, size.Height));
            }

            // Minimap
            if (_showMinimap)
                OverlayRenderer.DrawMinimap(dc, Theme, _lines, _lineHeight, _scrollOffsetY, _visibleLines, MinimapWidth, size);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EditorCanvas: overlay render failed: {ex}");
        }
        finally
        {
            dc.Pop(); // end content clip (contentBottom)
        }

        // Overlay scrollbars
        try
        {
            if (_showScrollbar)
                OverlayRenderer.DrawScrollbars(dc, Theme, size, ComputeScrollbarLayout(size));
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EditorCanvas: scrollbar render failed: {ex}");
        }
    }

    // The visible bars are thin (OverlayRenderer.ScrollbarSize); the grab zone is wider so the
    // thumb is easy to hit and drag with the mouse.
    private const double ScrollbarHitSize = 16.0;

    private OverlayRenderer.ScrollbarLayout ComputeScrollbarLayout(Size size) =>
        OverlayRenderer.ComputeScrollbarLayout(size, _wrapLines, TotalContentHeight, TotalContentWidth, MaxScrollOffsetY, _scrollOffsetY, _scrollOffsetX);

    private bool IsOverScrollbar(System.Windows.Point point)
    {
        var size = RenderSize;
        var l = ComputeScrollbarLayout(size);
        if (l.NeedVert && l.VertTrackH > 0 &&
            point.X >= size.Width - ScrollbarHitSize && point.Y <= l.VertTrackH)
            return true;
        if (l.NeedHoriz && l.HorizTrackW > 0 &&
            point.Y >= size.Height - ScrollbarHitSize && point.X <= l.HorizTrackW)
            return true;
        return false;
    }

    // Returns true and starts a drag if the click landed on a scrollbar.
    private bool TryBeginScrollbarDrag(System.Windows.Point point)
    {
        var size = RenderSize;
        var l = ComputeScrollbarLayout(size);

        // Vertical scrollbar — rightmost strip
        if (l.NeedVert && l.VertTrackH > 0 &&
            point.X >= size.Width - ScrollbarHitSize && point.Y <= l.VertTrackH)
        {
            double thumbRange = l.VertTrackH - l.VertThumbH;
            if (point.Y >= l.VertThumbY && point.Y <= l.VertThumbY + l.VertThumbH)
            {
                _scrollbarGrabOffset = point.Y - l.VertThumbY;
            }
            else
            {
                // Click on the track jumps the thumb so it centres on the cursor
                double newThumbY = Math.Clamp(point.Y - l.VertThumbH / 2, 0, Math.Max(0, thumbRange));
                _scrollbarGrabOffset = point.Y - newThumbY;
                if (thumbRange > 0)
                    ScrollTo(newThumbY / thumbRange * l.VertMaxOff, _scrollOffsetX);
            }
            _draggingVScrollbar = true;
            CaptureMouse();
            return true;
        }

        // Horizontal scrollbar — bottom strip
        if (l.NeedHoriz && l.HorizTrackW > 0 &&
            point.Y >= size.Height - ScrollbarHitSize && point.X <= l.HorizTrackW)
        {
            double thumbRange = l.HorizTrackW - l.HorizThumbW;
            if (point.X >= l.HorizThumbX && point.X <= l.HorizThumbX + l.HorizThumbW)
            {
                _scrollbarGrabOffset = point.X - l.HorizThumbX;
            }
            else
            {
                double newThumbX = Math.Clamp(point.X - l.HorizThumbW / 2, 0, Math.Max(0, thumbRange));
                _scrollbarGrabOffset = point.X - newThumbX;
                if (thumbRange > 0)
                    ScrollTo(_scrollOffsetY, newThumbX / thumbRange * l.HorizMaxOff);
            }
            _draggingHScrollbar = true;
            CaptureMouse();
            return true;
        }

        return false;
    }

    private void UpdateScrollbarDrag(System.Windows.Point point)
    {
        var size = RenderSize;
        var l = ComputeScrollbarLayout(size);

        if (_draggingVScrollbar && l.NeedVert)
        {
            double thumbRange = l.VertTrackH - l.VertThumbH;
            if (thumbRange > 0)
            {
                double newThumbY = Math.Clamp(point.Y - _scrollbarGrabOffset, 0, thumbRange);
                ScrollTo(newThumbY / thumbRange * l.VertMaxOff, _scrollOffsetX);
            }
        }
        else if (_draggingHScrollbar && l.NeedHoriz)
        {
            double thumbRange = l.HorizTrackW - l.HorizThumbW;
            if (thumbRange > 0)
            {
                double newThumbX = Math.Clamp(point.X - _scrollbarGrabOffset, 0, thumbRange);
                ScrollTo(_scrollOffsetY, newThumbX / thumbRange * l.HorizMaxOff);
            }
        }
    }

    private void DrawConflictBackground(DrawingContext dc, double y, double textLeft, SolidColorBrush brush)
    {
        double width = Math.Max(0, RenderSize.Width - textLeft);
        if (width > 0)
            dc.DrawRectangle(brush, null, new Rect(textLeft, y, width, _lineHeight));
    }

    /// <summary>blame カラム右端のリサイズハンドル（±<see cref="BlameColEdgeGrip"/>px）に載っているか。</summary>
    private bool IsOnBlameColEdge(Point p) =>
        !_isDragging && _blameColWidth > 0 && Math.Abs(p.X - _blameColWidth) <= BlameColEdgeGrip;

    /// <summary>blame カラム幅をドラッグ位置へ変更し、プロセス内共有の記憶値（<see cref="s_blameColUserWidth"/>）
    /// も更新する。以降に blame を表示する他のエディタも同じ幅になる。</summary>
    private void ResizeBlameColumnTo(double x)
    {
        double max = Math.Max(BlameColMinWidth, RenderSize.Width - 120);  // 本文の可視幅を最低限残す
        double w = Math.Clamp(x, BlameColMinWidth, max);
        if (Math.Abs(w - _blameColWidth) < 0.5) return;
        s_blameColUserWidth = w;
        _blameColWidth = w;
        InvalidateArrange();  // カラム幅で本文の折り返し幅が変わる
        InvalidateVisual();
    }

    /// <summary>blame カラムのホバー行に合わせてコミット情報ツールチップ（ハッシュ・著者・日付＋コミット
    /// メッセージ要約）を開閉する。行が変わったら位置を取り直すため一度閉じて開き直す。</summary>
    private void UpdateBlameToolTip(int hoveredLine)
    {
        if (hoveredLine < 0 || !_blameLines.TryGetValue(hoveredLine, out var blame))
        {
            CloseBlameToolTip();
            return;
        }

        _blameToolTip ??= new System.Windows.Controls.ToolTip
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
        };
        _blameToolTip.IsOpen = false;  // 行移動時にマウス位置へ出し直す
        _blameToolTip.Content = blame.Tooltip;
        _blameToolTip.IsOpen = true;
    }

    private void CloseBlameToolTip()
    {
        if (_blameToolTip is { } tip) tip.IsOpen = false;
    }

    private void DrawDiagnostics(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        foreach (var diag in _diagnostics)
        {
            if (diag.Range.Start.Line > line || diag.Range.End.Line < line) continue;

            var brush = diag.Severity switch
            {
                DiagnosticSeverity.Error       => Theme.DiagnosticError,
                DiagnosticSeverity.Warning     => Theme.DiagnosticWarning,
                DiagnosticSeverity.Information => Theme.DiagnosticInfo,
                _                              => Theme.DiagnosticHint
            };

            int startCol = line == diag.Range.Start.Line ? diag.Range.Start.Character : 0;
            int endCol   = line == diag.Range.End.Line   ? diag.Range.End.Character   : lineText.Length;
            endCol = Math.Max(startCol + 1, Math.Min(endCol, lineText.Length));

            double xStart = textLeft + GetVisualX(lineText, startCol) - _scrollOffsetX;
            double xEnd   = textLeft + GetVisualX(lineText, endCol)   - _scrollOffsetX;
            double yBase  = y + _lineHeight - 2;

            DrawWavyLine(dc, new Pen(brush, 1.0), xStart, xEnd, yBase);
        }
    }

    /// <summary>Detected links for <paramref name="lineText"/>, cached until the line's text changes.</summary>
    private IReadOnlyList<Editor.Core.Text.DetectedLink> GetLinks(int line, string lineText)
    {
        if (_linkCache.TryGetValue(line, out var cached) && cached.Text == lineText)
            return cached.Links;

        var links = LinkDetector.FindLinks(lineText);
        _linkCache[line] = (lineText, links);
        return links;
    }

    /// <summary>
    /// Whether a detected link should be treated as live (underlined / clickable). URLs always
    /// are; a file-path candidate is live only once a background check confirms it exists on
    /// disk. Unverified candidates kick off that check and report not-live for now.
    /// </summary>
    private bool IsLinkLive(Editor.Core.Text.DetectedLink link)
    {
        if (link.Kind == Editor.Core.Text.LinkKind.Url) return true;

        var resolved = ResolveLinkPath(link.Text);
        if (resolved == null) return false;
        if (_pathExists.TryGetValue(resolved, out bool exists)) return exists;

        EnsurePathVerified(resolved);
        return false;
    }

    /// <summary>Resolves a file-path link to an absolute path, or null if it can't be resolved.</summary>
    private string? ResolveLinkPath(string text)
    {
        try
        {
            string path = text;
            if (path.Length >= 1 && path[0] == '~')
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = home + path[1..];
            }

            if (!System.IO.Path.IsPathRooted(path))
            {
                if (string.IsNullOrEmpty(_documentDirectory)) return null;
                path = System.IO.Path.Combine(_documentDirectory, path);
            }
            return System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return null; // invalid path characters, etc.
        }
    }

    /// <summary>Checks <paramref name="resolved"/> existence off the UI thread, then redraws.</summary>
    private void EnsurePathVerified(string resolved)
    {
        if (!_pathChecking.Add(resolved)) return; // already in flight

        Task.Run(() =>
        {
            bool exists;
            try { exists = System.IO.File.Exists(resolved) || System.IO.Directory.Exists(resolved); }
            catch { exists = false; }

            Dispatcher.BeginInvoke(() =>
            {
                _pathChecking.Remove(resolved);
                _pathExists[resolved] = exists;
                if (exists) InvalidateVisual(); // reveal newly-confirmed link underline
            });
        });
    }

    /// <summary>Returns the live link at buffer (line, col), or null if none — shared by Ctrl+Click and Ctrl+hover.</summary>
    private Editor.Core.Text.DetectedLink? GetLinkAt(int line, int col)
    {
        string lineText = line < _lines.Length ? _lines[line] : string.Empty;
        foreach (var link in GetLinks(line, lineText))
        {
            if (col >= link.Start && col < link.End && IsLinkLive(link))
                return link;
        }
        return null;
    }

    private void DrawLinkUnderline(DrawingContext dc, int line, double y, double textLeft, string lineText, int segStart, int segEnd)
    {
        var links = GetLinks(line, lineText);
        if (links.Count == 0) return;

        double yBase = y + _lineHeight - 2;
        var pen = new Pen(Theme.LinkColor, 1.0);
        foreach (var link in links)
        {
            if (!IsLinkLive(link)) continue;

            // Clip the link span to this visual segment so a link that crosses a
            // word-wrap boundary doesn't draw an underline into adjacent rows.
            int start = Math.Max(link.Start, segStart);
            int end = Math.Min(link.End, segEnd);
            if (end <= start) continue;

            double xStart = textLeft + GetVisualX(lineText, start) - _scrollOffsetX;
            double xEnd   = textLeft + GetVisualX(lineText, end)   - _scrollOffsetX;
            dc.DrawLine(pen, new Point(xStart, yBase), new Point(xEnd, yBase));
        }
    }

    private void DrawSpellErrors(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (!_spellErrors.TryGetValue(line, out var errors)) return;
        double yBase = y + _lineHeight - 2;
        foreach (var (start, end) in errors)
        {
            double xStart = textLeft + GetVisualX(lineText, start) - _scrollOffsetX;
            double xEnd   = textLeft + GetVisualX(lineText, end + 1) - _scrollOffsetX;
            DrawWavyLine(dc, s_spellPen, xStart, xEnd, yBase);
        }
    }

    private static void DrawWavyLine(DrawingContext dc, Pen pen, double x1, double x2, double yBase)
    {
        const double step = 4.0;
        const double amp  = 1.5;
        for (double x = x1; x < x2 - step; x += step)
        {
            dc.DrawLine(pen, new Point(x,            yBase + amp),
                             new Point(x + step / 2, yBase - amp));
            dc.DrawLine(pen, new Point(x + step / 2, yBase - amp),
                             new Point(x + step,     yBase + amp));
        }
    }

    // Returns the (line, col) of the bracket that matches the one at (cursorLine, cursorCol),
    // or null if the cursor is not on a bracket or no match is found.
    private (int line, int col)? FindMatchingBracket(int cursorLine, int cursorCol)
    {
        if (cursorLine < 0 || cursorLine >= _lines.Length) return null;
        string line = _lines[cursorLine];
        if (cursorCol < 0 || cursorCol >= line.Length) return null;

        char ch = line[cursorCol];
        bool searchForward = ch is '(' or '[' or '{';
        if (!searchForward && ch is not (')' or ']' or '}')) return null;

        char open  = ch is '(' or ')' ? '(' : ch is '[' or ']' ? '[' : '{';
        char close = ch is '(' or ')' ? ')' : ch is '[' or ']' ? ']' : '}';

        int depth = 0;

        if (searchForward)
        {
            for (int l = cursorLine; l < _lines.Length; l++)
            {
                string ln = _lines[l];
                int startCol = l == cursorLine ? cursorCol : 0;
                for (int c = startCol; c < ln.Length; c++)
                {
                    if (ln[c] == open)  depth++;
                    else if (ln[c] == close) { depth--; if (depth == 0) return (l, c); }
                }
            }
        }
        else
        {
            for (int l = cursorLine; l >= 0; l--)
            {
                string ln = _lines[l];
                int startCol = l == cursorLine ? cursorCol : ln.Length - 1;
                for (int c = startCol; c >= 0; c--)
                {
                    if (ln[c] == close) depth++;
                    else if (ln[c] == open) { depth--; if (depth == 0) return (l, c); }
                }
            }
        }

        return null;
    }

    private void DrawMatchingBrackets(DrawingContext dc, int line, double y, double textLeft, string lineText,
        (int line, int col)? bracketMatch)
    {
        // Highlight the bracket under the cursor (on the cursor's line only)
        if (line == _cursor.Line)
        {
            int col = _cursor.Column;
            if (col < lineText.Length && lineText[col] is '(' or ')' or '[' or ']' or '{' or '}')
            {
                double x = textLeft + GetVisualX(lineText, col) - _scrollOffsetX;
                double w = CharW(lineText[col], col);
                dc.DrawRectangle(Theme.MatchingBracketBackground, null, new Rect(x, y, w, _lineHeight));
            }
        }

        // Highlight the matching bracket if it falls on this line
        if (bracketMatch == null || bracketMatch.Value.line != line) return;

        int matchCol = bracketMatch.Value.col;
        double mx = textLeft + GetVisualX(lineText, matchCol) - _scrollOffsetX;
        double mw = CharW(lineText[matchCol], matchCol);
        dc.DrawRectangle(Theme.MatchingBracketBackground, null, new Rect(mx, y, mw, _lineHeight));
    }

    private void DrawSelection(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (_selection == null || !_selection.Value.ContainsLine(line)) return;
        var sel = _selection.Value;
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        double selLeft = textLeft;
        double selWidth = RenderSize.Width - textLeft;

        if (sel.Type == SelectionType.Line)
        {
            // Entire line highlighted
        }
        else if (sel.Type == SelectionType.Block)
        {
            // Rectangular selection: the same column span on every line in range.
            int leftCol = Math.Min(sel.Start.Column, sel.End.Column);
            int rightCol = Math.Max(sel.Start.Column, sel.End.Column);
            int startCol = Math.Min(leftCol, lineText.Length);
            int endCol = Math.Min(rightCol + 1, lineText.Length);
            selLeft = textLeft + GetVisualX(lineText, startCol) - _scrollOffsetX;
            selWidth = GetVisualX(lineText, endCol) - GetVisualX(lineText, startCol);
        }
        else
        {
            int startCol = line == start.Line ? start.Column : 0;
            int endCol = line == end.Line ? end.Column + 1 : lineText.Length;
            selLeft = textLeft + GetVisualX(lineText, startCol) - _scrollOffsetX;
            selWidth = GetVisualX(lineText, endCol) - GetVisualX(lineText, startCol);
        }

        dc.DrawRectangle(Theme.SelectionBg, null, new Rect(selLeft, y, Math.Max(0, selWidth), _lineHeight));
    }

    private void DrawSearchHighlights(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (string.IsNullOrEmpty(_searchPattern)) return;
        if (!_searchMatchesByLine.TryGetValue(line, out var lineMatches)) return;
        foreach (var match in lineMatches)
        {
            double hLeft = textLeft + GetVisualX(lineText, match.Column) - _scrollOffsetX;
            int matchEnd = Math.Min(match.Column + _searchPattern.Length, lineText.Length);
            double hWidth = GetVisualX(lineText, matchEnd) - GetVisualX(lineText, match.Column);
            dc.DrawRectangle(Theme.SearchHighlightBg, null, new Rect(hLeft, y, hWidth, _lineHeight));
        }
    }


    private void DrawLineText(DrawingContext dc, int lineIndex, string lineText, double y, double textLeft)
    {
        if (lineIndex == _cursor.Line &&
            _mode is VimMode.Insert or VimMode.Replace &&
            !string.IsNullOrEmpty(_imeCompositionText))
        {
            DrawLineTextWithImeComposition(dc, lineIndex, lineText, y, textLeft);
            return;
        }

        if (string.IsNullOrEmpty(lineText)) return;

        if (_substitutePreviewLines.ContainsKey(lineIndex))
        {
            var ft = FormatText(lineText, Theme.Foreground);
            dc.DrawText(ft, new Point(textLeft - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            return;
        }

        var segments = BuildColorSegments(lineIndex, lineText.Length);
        if (segments == null)
        {
            // No syntax — draw entire line in default color
            DrawTextRun(dc, lineText, 0, lineText.Length, textLeft, y, Theme.Foreground);
            return;
        }

        DrawLineTextWithSegments(dc, lineText, y, textLeft, segments);
    }

    /// <summary>
    /// Builds the colored segments for a line by layering LSP semantic tokens on top of the
    /// regex-based syntax tokens. Regex tokens form the base layer (so keywords/strings/comments
    /// keep their color even when the server only emits semantic tokens for identifiers/types);
    /// semantic tokens override wherever they exist. Returns null if the line has no coloring.
    /// </summary>
    private List<(int StartCol, int Length, Brush? Brush)>? BuildColorSegments(int lineIndex, int lineLength)
    {
        _semanticTokensByLine.TryGetValue(lineIndex, out var semTokens);
        _tokensByLine.TryGetValue(lineIndex, out var regexTokens);
        bool hasSem = semTokens is { Count: > 0 };
        bool hasRegex = regexTokens is { Length: > 0 };

        if (!hasSem && !hasRegex) return null;

        if (hasSem && !hasRegex)
            return semTokens!.Select(t => (t.StartChar, t.Length, (Brush?)t.Brush)).ToList();

        if (!hasSem && hasRegex)
            return regexTokens!.Select(t => (t.StartColumn, t.Length, (Brush?)Theme.GetTokenBrush(t.Kind))).ToList();

        // Both present: merge per-character (regex base, semantic overlay), then coalesce runs.
        if (lineLength <= 0) return null;
        var brushes = new Brush?[lineLength];
        var has = new bool[lineLength];
        foreach (var t in regexTokens!)
        {
            var b = Theme.GetTokenBrush(t.Kind);
            for (int c = t.StartColumn; c < t.StartColumn + t.Length && c < lineLength; c++)
            { if (c >= 0) { brushes[c] = b; has[c] = true; } }
        }
        foreach (var (sc, ln, br) in semTokens!)
        {
            for (int c = sc; c < sc + ln && c < lineLength; c++)
            { if (c >= 0) { brushes[c] = br; has[c] = true; } }
        }

        var segs = new List<(int StartCol, int Length, Brush? Brush)>();
        int i = 0;
        while (i < lineLength)
        {
            if (!has[i]) { i++; continue; }
            int start = i;
            var brush = brushes[i];
            while (i < lineLength && has[i] && ReferenceEquals(brushes[i], brush)) i++;
            segs.Add((start, i - start, brush));
        }
        return segs;
    }

    /// <summary>
    /// Draws a line of text using a sequence of colored segments.
    /// Each segment is (startCol, length, brush?); gaps between segments use the default foreground.
    /// Null brush means "use default foreground".
    /// </summary>
    private void DrawLineTextWithSegments(DrawingContext dc, string lineText, double y, double textLeft,
        IEnumerable<(int StartCol, int Length, Brush? Brush)> segments)
    {
        // Keep shaping identical to the layout used by GetTextBoundaries. Drawing each syntax
        // token as a separate FormattedText run changes kerning/ligatures and can move glyphs
        // away from the cursor geometry. A single formatted line can carry per-range brushes.
        if (_activeLineOverrides == null || _activeLineOverrides.Count == 0)
        {
            var ft = FormatText(lineText, Theme.Foreground);
            foreach (var (startCol, length, brush) in segments)
            {
                int start = Math.Clamp(startCol, 0, lineText.Length);
                int count = Math.Clamp(length, 0, lineText.Length - start);
                if (count > 0 && brush != null) ft.SetForegroundBrush(brush, start, count);
            }
            dc.DrawText(ft, new Point(textLeft - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            return;
        }

        int pos = 0;
        foreach (var (startCol, length, brush) in segments)
        {
            // Gap before segment in default color
            if (startCol > pos)
                DrawTextRun(dc, lineText, pos, Math.Min(startCol, lineText.Length), textLeft, y, Theme.Foreground);

            // Segment text
            int end = Math.Min(startCol + length, lineText.Length);
            if (startCol < end)
                DrawTextRun(dc, lineText, startCol, end, textLeft, y, brush ?? Theme.Foreground);

            pos = Math.Max(pos, startCol + length);
        }
        // Remaining text
        if (pos < lineText.Length)
            DrawTextRun(dc, lineText, pos, lineText.Length, textLeft, y, Theme.Foreground);
    }

    /// <summary>
    /// Draws lineText[startCol..endCol) in a single color. A run of plain text is normally drawn
    /// as one FormattedText call, which lays out its own characters using the font's natural
    /// advances — a per-character width override (see <see cref="_activeLineOverrides"/>, used for
    /// markdown table column alignment) has no visible effect unless the run is split there, since
    /// otherwise the override only feeds coordinate math that this draw call never consults. So
    /// when overrides are active, split into sub-runs at each overridden column: the overridden
    /// character is drawn alone (at its natural size) and the next run starts at the stretched
    /// GetVisualX position, producing the visible gap.
    /// </summary>
    private void DrawTextRun(DrawingContext dc, string lineText, int startCol, int endCol, double textLeft, double y, Brush brush)
    {
        if (startCol >= endCol) return;

        if (_activeLineOverrides == null || _activeLineOverrides.Count == 0)
        {
            var ft = FormatText(lineText[startCol..endCol], brush);
            dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, startCol) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            return;
        }

        int runStart = startCol;
        for (int col = startCol; col < endCol; col++)
        {
            if (!_activeLineOverrides.ContainsKey(col)) continue;
            int runEnd = col + 1;
            if (runEnd > runStart)
            {
                var ft = FormatText(lineText[runStart..runEnd], brush);
                dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, runStart) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            }
            runStart = runEnd;
        }
        if (runStart < endCol)
        {
            var ft = FormatText(lineText[runStart..endCol], brush);
            dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, runStart) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
        }
    }

    private void DrawListChars(DrawingContext dc, string lineText, double y, double textLeft)
    {
        if (!_showList) return;
        var brush = Theme.ListCharBrush;

        // Pre-format each glyph once per line (not per character)
        FormattedText? tabFt     = _listTab.Length   > 0 ? FormatText(_listTab[0].ToString(),   brush) : null;
        FormattedText? tabFillFt = _listTab.Length   > 1 ? FormatText(_listTab[1].ToString(),   brush) : null;
        FormattedText? trailFt   = _listTrail.Length > 0 ? FormatText(_listTrail[0].ToString(), brush) : null;
        FormattedText? spaceFt   = _listSpace.Length > 0 ? FormatText(_listSpace[0].ToString(), brush) : null;
        FormattedText? eolFt     = _listEol.Length   > 0 ? FormatText(_listEol[0].ToString(),   brush) : null;

        // Find index where trailing whitespace starts
        int trailStart = lineText.Length;
        while (trailStart > 0 && (lineText[trailStart - 1] == ' ' || lineText[trailStart - 1] == '\t'))
            trailStart--;

        double x = 0;
        for (int i = 0; i < lineText.Length; i++)
        {
            char c = lineText[i];
            double px = textLeft + x - _scrollOffsetX;
            double charW = CharW(c, i);

            if (c == '\t' && tabFt != null)
            {
                dc.DrawText(tabFt, new Point(px, y + (_lineHeight - tabFt.Height) / 2));
                if (tabFillFt != null && charW > tabFt.Width)
                {
                    for (double fx = px + tabFt.Width; fx + tabFillFt.Width / 2 < px + charW; fx += tabFillFt.Width)
                        dc.DrawText(tabFillFt, new Point(fx, y + (_lineHeight - tabFillFt.Height) / 2));
                }
            }
            else if (c == ' ')
            {
                var ft = i >= trailStart ? trailFt : spaceFt;
                if (ft != null)
                    dc.DrawText(ft, new Point(px, y + (_lineHeight - ft.Height) / 2));
            }

            x += charW;
        }

        // EOL marker
        if (eolFt != null)
        {
            double eolX = textLeft + x - _scrollOffsetX;
            dc.DrawText(eolFt, new Point(eolX, y + (_lineHeight - eolFt.Height) / 2));
        }
    }

    private void DrawLineTextWithImeComposition(DrawingContext dc, int lineIndex, string lineText, double y, double textLeft)
    {
        int cursorCol = Math.Clamp(_cursor.Column, 0, lineText.Length);
        string merged = lineText.Insert(cursorCol, _imeCompositionText);
        if (string.IsNullOrEmpty(merged)) return;

        // `merged` splices composition text into the line, shifting every column after the
        // cursor — table overrides are keyed by real-buffer columns, so they'd land on the
        // wrong characters here. Skip them while composing; realignment resumes on commit.
        _activeLineOverrides = null;

        if (_substitutePreviewLines.ContainsKey(lineIndex))
        {
            var ft = FormatText(merged, Theme.Foreground);
            dc.DrawText(ft, new Point(textLeft - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            return;
        }

        IEnumerable<(int StartCol, int Length, Brush? Brush)> segments =
            BuildColorSegments(lineIndex, lineText.Length) ?? [];

        DrawLineTextWithSegments(dc, merged, y, textLeft,
            ShiftSegmentsForImeComposition(segments, lineText.Length, cursorCol, _imeCompositionText.Length));
    }

    private static IEnumerable<(int StartCol, int Length, Brush? Brush)> ShiftSegmentsForImeComposition(
        IEnumerable<(int StartCol, int Length, Brush? Brush)> segments,
        int originalLength,
        int cursorCol,
        int compositionLength)
    {
        foreach (var (startCol, length, brush) in segments)
        {
            if (length <= 0) continue;

            int start = Math.Clamp(startCol, 0, originalLength);
            int end = Math.Clamp(startCol + length, 0, originalLength);
            if (end <= start) continue;

            if (start < cursorCol)
            {
                int beforeEnd = Math.Min(end, cursorCol);
                if (beforeEnd > start)
                    yield return (start, beforeEnd - start, brush);
            }

            if (end > cursorCol)
            {
                int afterStart = Math.Max(start, cursorCol);
                yield return (afterStart + compositionLength, end - afterStart, brush);
            }
        }
    }

    private void DrawCursor(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (!_isActive) return;
        if (line != _cursor.Line) return;

        double cursorX = textLeft + GetVisualX(lineText, _cursor.Column) - _scrollOffsetX;
        double cursorY = y;

        double cursorW = _cursor.Column < lineText.Length ? CursorGlyphWidth(lineText, _cursor.Column) : _charWidth;

        if (_mode is VimMode.Insert or VimMode.Replace)
            DrawImeCompositionUnderline(dc, cursorX, cursorY);

        if (!_cursorVisible) return;

        // While an IME composition is active, the composition text is rendered
        // inserted at the cursor column. Advance the caret to the IME's own caret
        // position within that composition (GCS_CURSORPOS), so arrow keys that move
        // the IME caret are reflected. -1 falls back to the end of the composition.
        double caretX = cursorX;
        if (_mode is VimMode.Insert or VimMode.Replace && !string.IsNullOrEmpty(_imeCompositionText))
        {
            int caretChars = _imeCompositionCursor < 0
                ? _imeCompositionText.Length
                : Math.Clamp(_imeCompositionCursor, 0, _imeCompositionText.Length);
            if (caretChars > 0)
                caretX += FormatText(_imeCompositionText[..caretChars], Theme.Foreground).Width;
        }

        if (_mode == VimMode.Insert || _mode == VimMode.Command ||
            _mode == VimMode.SearchForward || _mode == VimMode.SearchBackward)
        {
            // Thin line cursor
            var pen = new Pen(Theme.InsertCursor, 2);
            dc.DrawLine(pen, new Point(caretX, cursorY), new Point(caretX, cursorY + _lineHeight));
        }
        else if (_mode == VimMode.Replace)
        {
            // Underline cursor
            var pen = new Pen(Theme.InsertCursor, 2);
            dc.DrawLine(pen, new Point(cursorX, cursorY + _lineHeight - 2), new Point(cursorX + cursorW, cursorY + _lineHeight - 2));
        }
        else
        {
            // Block cursor
            dc.DrawRectangle(Theme.CursorBackground, null, new Rect(cursorX, cursorY, cursorW, _lineHeight));
            if (_cursor.Column < lineText.Length)
            {
                var ch = lineText.Substring(_cursor.Column, GraphemeLength(lineText, _cursor.Column));
                var ft = FormatText(ch, Theme.CursorForeground);
                dc.DrawText(ft, new Point(cursorX, cursorY + (_lineHeight - ft.Height) / 2));
            }
        }
    }

    private void DrawExtraCursors(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (!_isActive || _extraCursors.Count == 0) return;
        if (!_cursorVisible) return;

        foreach (var (ecLine, ecCol) in _extraCursors)
        {
            if (ecLine != line) continue;
            double cx = textLeft + GetVisualX(lineText, ecCol) - _scrollOffsetX;
            double cw = ecCol < lineText.Length ? CursorGlyphWidth(lineText, ecCol) : _charWidth;

            if (_mode == VimMode.Insert || _mode == VimMode.Command ||
                _mode == VimMode.SearchForward || _mode == VimMode.SearchBackward)
            {
                // Thin line cursor in insert mode
                var pen = new Pen(Theme.InsertCursor, 2);
                dc.DrawLine(pen, new Point(cx, y), new Point(cx, y + _lineHeight));
            }
            else
            {
                // Block cursor in normal mode — use a slightly transparent version of the cursor color
                var extraBrush = new SolidColorBrush(Color.FromArgb(180,
                    Theme.CursorBackground is SolidColorBrush scb ? scb.Color.R : (byte)0x50,
                    Theme.CursorBackground is SolidColorBrush scb2 ? scb2.Color.G : (byte)0xFA,
                    Theme.CursorBackground is SolidColorBrush scb3 ? scb3.Color.B : (byte)0x78));
                dc.DrawRectangle(extraBrush, null, new Rect(cx, y, cw, _lineHeight));
                if (ecCol < lineText.Length)
                {
                    var ft = FormatText(lineText.Substring(ecCol, GraphemeLength(lineText, ecCol)), Theme.CursorForeground);
                    dc.DrawText(ft, new Point(cx, y + (_lineHeight - ft.Height) / 2));
                }
            }
        }
    }

    private void DrawImeCompositionUnderline(DrawingContext dc, double cursorX, double cursorY)
    {
        if (string.IsNullOrEmpty(_imeCompositionText)) return;

        int len = _imeCompositionText.Length;
        double underlineY = cursorY + _lineHeight - 1;
        double CharX(int col) => cursorX + (col > 0 ? FormatText(_imeCompositionText[..Math.Clamp(col, 0, len)], Theme.Foreground).Width : 0);

        // Build clause segments from the clause-start offsets. Fall back to a single
        // segment covering the whole composition when no segmentation is available.
        var bounds = new List<int> { 0 };
        foreach (int s in _imeClauseStarts)
            if (s > 0 && s < len && !bounds.Contains(s)) bounds.Add(s);
        bounds.Sort();
        bounds.Add(len);

        var thinPen = new Pen(Theme.InsertCursor, 1);
        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            int cs = bounds[i], ce = bounds[i + 1];
            if (ce <= cs) continue;
            // Leave a 2px gap before each clause boundary so segments read as separate.
            double x0 = CharX(cs);
            double x1 = CharX(ce) - (ce < len ? 2 : 0);
            if (x1 <= x0) x1 = x0 + 1;

            bool isTarget = _imeTargetClauseStart >= 0
                && cs >= _imeTargetClauseStart && ce <= _imeTargetClauseEnd;
            if (isTarget)
            {
                // Focused clause: translucent background + thick underline.
                var hl = new SolidColorBrush(Theme.InsertCursor is SolidColorBrush sb
                    ? Color.FromArgb(48, sb.Color.R, sb.Color.G, sb.Color.B)
                    : Color.FromArgb(48, 0x80, 0x80, 0xFF));
                dc.DrawRectangle(hl, null, new Rect(x0, cursorY, Math.Max(1, x1 - x0), _lineHeight));
                dc.DrawLine(new Pen(Theme.InsertCursor, 2), new Point(x0, underlineY), new Point(x1, underlineY));
            }
            else
            {
                dc.DrawLine(thinPen, new Point(x0, underlineY), new Point(x1, underlineY));
            }
        }
    }

    private void DrawImeCandidatePopup(DrawingContext dc, double textLeft, Size size)
    {
        if (_imeCandidates.Length == 0) return;

        const int maxVisible = 8;
        int count = Math.Min(maxVisible, _imeCandidates.Length);
        var items = _imeCandidates.Take(count)
            .Select((c, i) => $"{i + 1}. {c}")
            .ToArray();

        var texts = new FormattedText[count];
        double maxTextWidth = 0;
        for (int i = 0; i < count; i++)
        {
            var ft = FormatText(items[i], Theme.Foreground);
            texts[i] = ft;
            maxTextWidth = Math.Max(maxTextWidth, ft.Width);
        }

        double rowH = Math.Max(_lineHeight, texts.Max(static t => t.Height));
        double padX = 8;
        double padY = 4;
        double popupW = maxTextWidth + (padX * 2);
        double popupH = (rowH * count) + (padY * 2);

        var cursor = GetCursorPixelPosition();
        double x = cursor.X;
        double y = cursor.Y + _lineHeight + 2;

        if (x + popupW > size.Width)
            x = Math.Max(textLeft, size.Width - popupW - 2);

        if (y + popupH > size.Height)
            y = Math.Max(0, cursor.Y - popupH - 2);

        dc.DrawRectangle(PopupChrome.Bg3, PopupChrome.Border, new Rect(x, y, popupW, popupH));

        int selected = _imeCandidateSelection;
        for (int i = 0; i < count; i++)
        {
            double rowY = y + padY + (rowH * i);
            if (i == selected)
            {
                dc.DrawRectangle(Theme.SelectionBg, null, new Rect(x + 1, rowY, popupW - 2, rowH));
            }

            var ft = texts[i];
            dc.DrawText(ft, new Point(x + padX, rowY + (rowH - ft.Height) / 2));
        }
    }

    /// <summary>
    /// Returns the canvas-local pixel position of the top-left corner of the cursor cell.
    /// Used to position the IME composition window.
    /// </summary>
    public Point GetCursorPixelPosition()
    {
        MeasureChar();
        var (_, _, _, gutterWidth) = GetGutterMetrics();
        string line = _cursor.Line < _lines.Length ? _lines[_cursor.Line] : string.Empty;
        int cursorCol = Math.Clamp(_cursor.Column, 0, line.Length);
        int cursorVisualLine = GetCursorVisualLine();
        var segment = GetVisualSegment(cursorVisualLine);
        SetActiveLine(_cursor.Line);
        double lineOffsetX = _wrapLines ? GetVisualX(line, segment.StartColumn) : _scrollOffsetX;
        double x = gutterWidth + GetVisualX(line, cursorCol) - lineOffsetX;
        double y = cursorVisualLine * _lineHeight - _scrollOffsetY;
        return new Point(Math.Max(gutterWidth, x), y);
    }

    /// <summary>
    /// Caret pixel position including the in-flight IME composition offset, so the native
    /// candidate window anchors under the converting clause (the caret the user sees) rather
    /// than the composition's start column. Falls back to <see cref="GetCursorPixelPosition"/>
    /// when no composition is active.
    /// </summary>
    public Point GetImeCaretPixelPosition()
    {
        var p = GetCursorPixelPosition();
        if (string.IsNullOrEmpty(_imeCompositionText)) return p;

        int caretChars = _imeCompositionCursor < 0
            ? _imeCompositionText.Length
            : Math.Clamp(_imeCompositionCursor, 0, _imeCompositionText.Length);
        if (caretChars <= 0) return p;

        double advance = FormatText(_imeCompositionText[..caretChars], Theme.Foreground).Width;
        return new Point(p.X + advance, p.Y);
    }

    // ─────────────── Character width helpers ───────────────

    /// <summary>
    /// Number of UTF-16 <c>char</c> units the glyph at <paramref name="col"/> occupies — more than 1
    /// for surrogate-pair emoji, variation selectors, ZWJ sequences, and flag pairs. Used so the
    /// cursor box spans the whole glyph instead of measuring/drawing just one code unit of it.
    /// </summary>
    private static int GraphemeLength(string line, int col) => GraphemeCluster.LengthAt(line, col);

    /// <summary>Visual width of the glyph at <paramref name="col"/>, grapheme-cluster aware.</summary>
    private double CursorGlyphWidth(string line, int col) =>
        GetVisualX(line, col + GraphemeLength(line, col)) - GetVisualX(line, col);

    private double CharW(char c)
    {
        if (c == '\t') return _charWidth;
        if (_charWidthCache.TryGetValue(c, out double w)) return w;
        var ft = new FormattedText(c.ToString(), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White, GetDpi());
        w = ft.WidthIncludingTrailingWhitespace;
        _charWidthCache[c] = w;
        return w;
    }

    /// <summary>
    /// Width of the character at <paramref name="col"/> in the line currently set via
    /// <see cref="SetActiveLine"/> — honors the markdown table column-alignment override for
    /// that column when one is active, otherwise falls back to the plain measured width.
    /// </summary>
    private double CharW(char c, int col)
    {
        if (_activeLineOverrides != null && _activeLineOverrides.TryGetValue(col, out double w)) return w;
        return CharW(c);
    }

    /// <summary>Must be called before any GetVisualX/VisualXToCol/CharW(char,int) use for a given buffer line.</summary>
    private void SetActiveLine(int lineIndex)
    {
        // While editing inside a table, the whole table is shown raw (unaligned). Table alignment
        // is recomputed on every edit, and while an IME is composing the edited line's overrides
        // are dropped entirely (the composition text is spliced in and shifts columns) — so an
        // aligned table would flip between raw-while-composing and stretched-on-commit on every
        // keystroke, which reads as the alignment "running or not running". Suppressing the entire
        // enclosing table (not just the caret row) keeps every column stable while you edit any
        // cell, and re-aligns in one step when insert mode is left. The suppressed range is only
        // set in Insert/Replace mode (which also covers the Vim-disabled resting state); Normal-mode
        // viewing/navigation keeps the table aligned.
        if (_suppressedTableStart >= 0 && lineIndex >= _suppressedTableStart && lineIndex <= _suppressedTableEnd)
        {
            _activeLineOverrides = null;
            return;
        }
        _activeLineOverrides = _tableColumnOverrides.Count > 0 && _tableColumnOverrides.TryGetValue(lineIndex, out var ov)
            ? ov
            : null;
    }

    /// <summary>
    /// Recomputes <see cref="_suppressedTableStart"/>/<see cref="_suppressedTableEnd"/>: the line
    /// range of the table the cursor is currently editing, or [-1,-1] when the cursor is not inside
    /// a table in an editing mode. Cheap — scans the already-detected <see cref="_tableBlocks"/>.
    /// </summary>
    private void UpdateSuppressedTableRange()
    {
        _suppressedTableStart = _suppressedTableEnd = -1;
        if (_mode is not (VimMode.Insert or VimMode.Replace)) return;
        foreach (var block in _tableBlocks)
        {
            if (_cursor.Line >= block.StartLine && _cursor.Line <= block.EndLine)
            {
                _suppressedTableStart = block.StartLine;
                _suppressedTableEnd = block.EndLine;
                return;
            }
        }
    }

    /// <summary>Visual X offset (in pixels) for character index <paramref name="col"/> in <paramref name="line"/>.</summary>
    private double GetVisualX(string line, int col)
    {
        int limit = Math.Clamp(col, 0, line.Length);
        if (_activeLineOverrides == null || _activeLineOverrides.Count == 0)
            return GetTextBoundaries(line)[limit];

        // Markdown table alignment intentionally stretches selected character cells beyond
        // their natural layout width, so apply only those deltas on top of the real boundary.
        double x = GetTextBoundaries(line)[limit];
        foreach (var (column, width) in _activeLineOverrides)
        {
            if (column >= limit || column >= line.Length) continue;
            double naturalWidth = GetTextBoundaries(line)[column + 1] - GetTextBoundaries(line)[column];
            x += width - naturalWidth;
        }
        return x;
    }

    /// <summary>Convert a visual X pixel offset to a character index in <paramref name="line"/>.</summary>
    private int VisualXToCol(string line, double visualX)
    {
        int i = 0;
        while (i < line.Length)
        {
            int next = GraphemeCluster.NextBoundary(line, i, 1);
            double left = GetVisualX(line, i);
            double right = GetVisualX(line, next);
            if ((left + right) / 2 >= visualX) return i;
            i = next;
        }
        return line.Length;
    }

    private double[] GetTextBoundaries(string text)
    {
        if (_textBoundaryCache.TryGetValue(text, out var cached)) return cached;
        var boundaries = TextBoundaryMeasurer.Measure(text, _typeface, _fontSize, GetDpi());
        _textBoundaryCache[text] = boundaries;
        return boundaries;
    }

    /// <summary>
    /// Recomputes the markdown table column-alignment overrides for the current <see cref="_lines"/>.
    /// Cheap no-op when the feature is off or there's no table in the document.
    /// </summary>
    private void RecomputeTableOverrides()
    {
        if (!_markdownTableAlignEnabled)
        {
            _tableColumnOverrides = [];
            _tableBlocks = [];
            UpdateSuppressedTableRange();
            return;
        }
        MeasureChar();
        _tableColumnOverrides = ComputeTableColumnOverrides(_lines);
        UpdateSuppressedTableRange();
    }

    /// <summary>
    /// For every detected GFM table block, measures each column's widest already-authored span
    /// (real font metrics, so it adapts to full-width characters) and records, per row, how much
    /// extra width the gap before that row's closing '|' needs so every row's pipes land at the
    /// same X. The target is the widest existing span rather than content+fixed-padding, because
    /// formatters like prettier pad cells by character *count* — a full-width cell can already
    /// render wider than a same-char-count half-width cell, and only ever needs stretching, never
    /// shrinking, to match. A row whose span is already the widest in its column gets no override.
    /// </summary>
    private Dictionary<int, Dictionary<int, double>> ComputeTableColumnOverrides(string[] lines)
    {
        var result = new Dictionary<int, Dictionary<int, double>>();
        if (lines.Length == 0) { _tableBlocks = []; return result; }

        var blocks = Editor.Core.Editing.MarkdownTableLayout.FindBlocks(lines);
        _tableBlocks = blocks;
        if (blocks.Count == 0) return result;

        foreach (var block in blocks)
        {
            int rowCount = block.EndLine - block.StartLine + 1;
            var rowSpans = new List<Editor.Core.Editing.MarkdownTableLayout.CellSpan>[rowCount];
            int colCount = 0;

            for (int r = 0; r < rowCount; r++)
            {
                rowSpans[r] = Editor.Core.Editing.MarkdownTableLayout.SplitCellSpans(lines[block.StartLine + r]);
                colCount = Math.Max(colCount, rowSpans[r].Count);
            }
            if (colCount == 0) continue;

            var colWidth = new double[colCount];
            for (int r = 0; r < rowCount; r++)
            {
                string line = lines[block.StartLine + r];
                var spans = rowSpans[r];
                for (int c = 0; c < spans.Count; c++)
                {
                    double spanWidth = 0;
                    for (int i = spans[c].Start; i < spans[c].End; i++) spanWidth += CharW(line[i]);
                    if (spanWidth > colWidth[c]) colWidth[c] = spanWidth;
                }
            }

            for (int r = 0; r < rowCount; r++)
            {
                int lineIndex = block.StartLine + r;
                string line = lines[lineIndex];
                var spans = rowSpans[r];
                for (int c = 0; c < spans.Count; c++)
                {
                    var span = spans[c];
                    if (span.End >= line.Length || line[span.End] != '|') continue; // no closing pipe to align

                    double spanWidth = 0;
                    for (int i = span.Start; i < span.End; i++) spanWidth += CharW(line[i]);

                    double extra = colWidth[c] - spanWidth;
                    if (extra <= 0.5) continue;

                    int widenCol = span.End > span.Start ? span.End - 1 : span.End;
                    double baseWidth = CharW(line[widenCol]);

                    if (!result.TryGetValue(lineIndex, out var perLine))
                        result[lineIndex] = perLine = [];
                    perLine[widenCol] = baseWidth + extra;
                }
            }
        }

        return result;
    }

    private double GetDpi()
    {
        try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return 1.0; }
    }

    private FormattedText FormatText(string text, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            brush,
            GetDpi());
    }

    /// <summary>Formats text in the italic typeface at <paramref name="sizeScale"/> times the normal
    /// font size (used for inlay hints).</summary>
    private FormattedText FormatScaledText(string text, Brush brush, double sizeScale)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _italicTypeface,
            _fontSize * sizeScale,
            brush,
            GetDpi());
    }

    private GlyphMetrics BuildGlyphMetrics() => new(FormatText, FormatScaledText, GetVisualX, _lineHeight, _charWidth);

    private void MeasureChar()
    {
        if (_charWidth > 0) return;
        var ft = new FormattedText(
            "M",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            Brushes.White,
            GetDpi());
        _charWidth = ft.Width;
        _lineHeight = ft.Height + 2;
    }

    private void EnsureCursorVisible()
    {
        MeasureChar();
        if (_lineHeight == 0) return;

        double cursorY = GetCursorVisualLine() * _lineHeight;
        double viewHeight = UsableViewportHeight;
        double margin = _scrollOff * _lineHeight;

        if (cursorY < _scrollOffsetY + margin)
            _scrollOffsetY = Math.Max(0, cursorY - margin);
        else if (cursorY + _lineHeight > _scrollOffsetY + viewHeight - margin)
            _scrollOffsetY = cursorY + _lineHeight + margin - viewHeight;

        if (_wrapLines)
        {
            _scrollOffsetX = 0;
        }
        else
        {
            var (_, _, _, gutterWidth) = GetGutterMetrics();
            double viewportWidth = Math.Max(0, UsableViewportWidth - gutterWidth);
            string line = _cursor.Line < _lines.Length ? _lines[_cursor.Line] : string.Empty;
            int cursorCol = Math.Clamp(_cursor.Column, 0, line.Length);
            SetActiveLine(_cursor.Line);
            double cursorX = GetVisualX(line, cursorCol);
            double cursorW = cursorCol < line.Length ? CursorGlyphWidth(line, cursorCol) : _charWidth;
            double marginX = 4 * _charWidth;

            if (viewportWidth > 0)
            {
                if (cursorX < _scrollOffsetX + marginX)
                    _scrollOffsetX = Math.Max(0, cursorX - marginX);
                else if (cursorX + cursorW > _scrollOffsetX + viewportWidth - marginX)
                    _scrollOffsetX = cursorX + cursorW + marginX - viewportWidth;
            }
        }

        double maxOffsetY = MaxScrollOffsetY;
        double maxOffsetX = _wrapLines
            ? 0
            : Math.Max(0, TotalContentWidth - UsableViewportWidth);
        _scrollOffsetY = Math.Clamp(_scrollOffsetY, 0, maxOffsetY);
        _scrollOffsetX = _wrapLines ? 0 : Math.Clamp(_scrollOffsetX, 0, maxOffsetX);
        ScrollChanged?.Invoke(_scrollOffsetY, _scrollOffsetX);
    }

    private void StartCursorBlink()
    {
        _cursorTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530)
        };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorVisible = !_cursorVisible;
            InvalidateVisual();
        };
        _cursorTimer.Start();
    }

    public void ResetCursorBlink()
    {
        _cursorVisible = true;
        _cursorTimer?.Stop();
        _cursorTimer?.Start();
        InvalidateVisual();
    }
}
