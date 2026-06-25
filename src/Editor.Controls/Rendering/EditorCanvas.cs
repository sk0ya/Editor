using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Syntax;
using Editor.Core.Text;

namespace Editor.Controls.Rendering;

public class EditorCanvas : FrameworkElement
{
    private readonly record struct VisualLineSegment(int BufferLine, int StartColumn, bool IsContinuation);

    private Typeface _typeface = new("Consolas");
    private Typeface _italicTypeface = new(new FontFamily("Consolas"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private double _fontSize = 14;
    private double _charWidth;
    private double _lineHeight;
    private readonly Dictionary<char, double> _charWidthCache = [];
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
    private Dictionary<int, string> _blameAnnotations = [];

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
    public event Action<string>? LinkClicked;          // (url) Ctrl+clicked on a detected URL
    public event Action<string>? FileLinkClicked;      // (absolutePath) Ctrl+clicked on a detected file path

    // Brushes/pens for popup chrome and spell underlines — created once (theme-independent)
    private static readonly SolidColorBrush s_popupBg1    = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x25, 0x26, 0x33)));
    private static readonly SolidColorBrush s_popupBg2    = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1F, 0x29)));
    private static readonly SolidColorBrush s_popupBg3    = Freeze(new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x1F, 0x29)));
    private static readonly SolidColorBrush s_popupDocBg  = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x27, 0x35)));
    private static readonly Pen             s_popupBorder = FreezePen(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x63, 0x65, 0x72))), 1));
    private static readonly SolidColorBrush s_spellBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0x9F, 0xFF)));
    private static readonly Pen             s_spellPen    = FreezePen(new Pen(s_spellBrush, 1.0));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    public EditorCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        StartCursorBlink();
        RebuildVisualLayout();
    }

    // ─────────────── Public scroll info ───────────────

    private int TotalVisualLines => Math.Max(1, _visualLines.Length);
    public double TotalContentHeight => TotalVisualLines * _lineHeight;
    public double TotalContentWidth => _contentWidth;
    public double ViewportHeight => RenderSize.Height;
    public double ViewportWidth => RenderSize.Width;
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
        _maxLineWidthCacheLines = null; // char widths changed — invalidate cached extent
        MeasureChar();
        RebuildVisualLayout();
        InvalidateVisual();
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
        _lineNumberWidth = Math.Max(3, _lines.Length.ToString().Length);
        RebuildVisualLayout();
        InvalidateVisual();
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

    public void SetCursor(CursorPosition cursor) { _cursor = cursor; EnsureCursorVisible(); InvalidateVisual(); }
    public void SetExtraCursors(IReadOnlyList<(int Line, int Col)> cursors) { _extraCursors = cursors; InvalidateVisual(); }
    public void SetSelection(Selection? sel) { _selection = sel; InvalidateVisual(); }
    public void SetMode(VimMode mode) { _mode = mode; InvalidateVisual(); }

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

    public void SetBlameAnnotations(Dictionary<int, string>? annotations)
    {
        _blameAnnotations = annotations ?? [];
        InvalidateVisual();
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
            var brush = SemanticTokenTypeToBrush(tok.TokenType);
            if (brush is null) continue;
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
        "function" or "method"               => Theme.TokenIdentifier,
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
        double maxOffsetY = Math.Max(0, TotalContentHeight - RenderSize.Height);
        double maxOffsetX = _wrapLines
            ? 0
            : Math.Max(0, TotalContentWidth - RenderSize.Width);
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
        var (_, _, gutterWidth) = GetGutterMetrics();
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

    private (int lineNumWidth, double foldColWidth, int gutterWidth) GetGutterMetrics()
    {
        int lineNumWidth = _showLineNumbers ? (int)((_lineNumberWidth + 1) * _charWidth) : 0;
        double foldColWidth = _showLineNumbers ? Math.Max(16.0, _charWidth + 4) : 0;
        int gutterWidth = (int)(lineNumWidth + foldColWidth);
        return (lineNumWidth, foldColWidth, gutterWidth);
    }

    private void ClampScrollOffsets(bool raiseScrollChanged)
    {
        double maxOffsetY = Math.Max(0, TotalContentHeight - RenderSize.Height);
        double maxOffsetX = _wrapLines
            ? 0
            : Math.Max(0, TotalContentWidth - RenderSize.Width);

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

        int lineNumWidth = _showLineNumbers ? (int)((_lineNumberWidth + 1) * _charWidth) : 0;
        double foldColWidth = _showLineNumbers ? Math.Max(16.0, _charWidth + 4) : 0;
        int gutterWidth = (int)(lineNumWidth + foldColWidth);

        if (_showLineNumbers && point.X < gutterWidth)
        {
            // Fold column click — check for fold indicator
            if (point.X >= lineNumWidth)
            {
                int bufferLine = HitTestGutterLine(point);
                if (bufferLine >= 0 && (_closedFoldStarts.Contains(bufferLine) || _openFoldStarts.Contains(bufferLine)))
                    FoldGutterClicked?.Invoke(bufferLine);
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
            Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }

        // Show pointer cursor over minimap
        if (_showMinimap && point.X >= RenderSize.Width - MinimapWidth)
        {
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
            Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }

        // Update fold indicator hover state
        int lineNumWidth2 = _showLineNumbers ? (int)((_lineNumberWidth + 1) * _charWidth) : 0;
        double foldColWidth2 = _showLineNumbers ? Math.Max(16.0, _charWidth + 4) : 0;
        int gutterWidth2 = (int)(lineNumWidth2 + foldColWidth2);
        if (_showLineNumbers && point.X >= lineNumWidth2 && point.X < gutterWidth2)
        {
            // Hovering in fold column
            int bufferLine = HitTestGutterLine(point);
            bool onFold = bufferLine >= 0 && (_closedFoldStarts.Contains(bufferLine) || _openFoldStarts.Contains(bufferLine));
            Cursor = onFold ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
            int prev = _hoveredFoldLine;
            _hoveredFoldLine = onFold ? bufferLine : -1;
            if (_hoveredFoldLine != prev) InvalidateVisual();
        }
        else if (_showLineNumbers && point.X < lineNumWidth2)
        {
            // Hovering in line number area
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }
            Cursor = System.Windows.Input.Cursors.Arrow;
        }
        else
        {
            if (_hoveredFoldLine >= 0) { _hoveredFoldLine = -1; InvalidateVisual(); }

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

        var (_, _, gutterWidth) = GetGutterMetrics();

        int visualLine = GetVisualLineIndexFromY(point.Y);
        var segment = GetVisualSegment(visualLine);
        int line = segment.BufferLine;
        line = Math.Clamp(line, 0, Math.Max(0, _lines.Length - 1));

        string hitLine = line < _lines.Length ? _lines[line] : string.Empty;
        double visualX = point.X - gutterWidth + (_wrapLines ? 0 : _scrollOffsetX);

        if (_wrapLines)
        {
            int segStart = Math.Min(segment.StartColumn, hitLine.Length);
            int segEnd = Math.Min(GetSegmentEndColumn(visualLine), hitLine.Length);
            int segLength = Math.Max(0, segEnd - segStart);
            string segmentText = segLength > 0 ? hitLine.Substring(segStart, segLength) : string.Empty;
            int segCol = VisualXToCol(segmentText, visualX);
            int wrappedMaxCol = Math.Max(0, hitLine.Length - 1);
            int colWrapped = Math.Clamp(segStart + segCol, 0, wrappedMaxCol);
            return (line, colWrapped);
        }

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
            var (_, _, gutterWidth) = GetGutterMetrics();
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
        double contentBottom = needHorizBar ? Math.Max(0, size.Height - ScrollbarSize) : size.Height;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, size.Width, contentBottom)));

        var (lineNumWidth, foldColWidth, gutterWidth) = GetGutterMetrics();
        double textLeft = gutterWidth;

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

            var lineText = _substitutePreviewLines.TryGetValue(l, out var previewLine)
                ? previewLine
                : l < _lines.Length ? _lines[l] : "";
            _scrollOffsetX = _wrapLines ? GetVisualX(lineText, segment.StartColumn) : baseOffsetX;
            bool drawNumberAndFold = !_wrapLines || !segment.IsContinuation;

            // Current line highlight
            if (l == _cursor.Line && Theme.CurrentLineBg != null && size.Width > textLeft)
                dc.DrawRectangle(Theme.CurrentLineBg, null, new Rect(textLeft, y, size.Width - textLeft, _lineHeight));

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
                    bool isCursorLine = l == _cursor.Line;
                    var lineNumberBrush = isCursorLine ? Theme.CurrentLineNumberFg : Theme.LineNumberFg;
                    string lineNumStr;
                    if (_relativeNumber && !isCursorLine)
                        lineNumStr = Math.Abs(l - _cursor.Line).ToString().PadLeft(_lineNumberWidth);
                    else
                        lineNumStr = (l + 1).ToString().PadLeft(_lineNumberWidth);
                    var numText = FormatText(lineNumStr, lineNumberBrush);
                    dc.DrawText(numText, new Point(2, y + (_lineHeight - numText.Height) / 2));

                    // Fold indicator in dedicated fold column (▶ closed, ▼ open)
                    bool isClosed = _closedFoldStarts.Contains(l);
                    bool isOpen = _openFoldStarts.Contains(l);
                    if (isClosed || isOpen)
                    {
                        bool hovered = _hoveredFoldLine == l;
                        var indicatorColor = hovered ? Theme.Foreground : Theme.LineNumberFg;
                        if (hovered)
                            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), null,
                                new Rect(lineNumWidth, y, foldColWidth, _lineHeight));
                        var marker = FormatText(isClosed ? "▶" : "▼", indicatorColor);
                        double mx = lineNumWidth + (foldColWidth - marker.Width) / 2;
                        dc.DrawText(marker, new Point(mx, y + (_lineHeight - marker.Height) / 2));
                    }

                    // Git diff bar (3px wide on left edge of gutter)
                    if (_gitDiff.TryGetValue(l, out var gitState) && gitState != GitLineState.None)
                    {
                        var gitBrush = gitState switch
                        {
                            GitLineState.Added    => Theme.GitAdded,
                            GitLineState.Modified => Theme.GitModified,
                            GitLineState.Deleted  => Theme.GitDeleted,
                            _                     => null
                        };
                        if (gitBrush != null)
                            dc.DrawRectangle(gitBrush, null, new Rect(0, y, 3, _lineHeight));
                    }
                }
            }

            dc.PushClip(new RectangleGeometry(new Rect(textLeft, y, Math.Max(0, size.Width - textLeft), _lineHeight)));

            // Selection highlight
            DrawSelection(dc, l, y, textLeft, lineText);

            // Search highlights
            DrawSearchHighlights(dc, l, y, textLeft, lineText);

            // Document highlights (LSP)
            DrawDocumentHighlights(dc, l, y, textLeft, lineText);

            // Matching bracket highlight
            DrawMatchingBrackets(dc, l, y, textLeft, lineText, bracketMatch);

            // Text with syntax coloring
            DrawLineText(dc, l, lineText, y, textLeft);

            // Inline color preview swatches
            if (_showColorPreview)
                DrawColorSwatches(dc, lineText, y, textLeft);

            // Invisible character markers (set list)
            DrawListChars(dc, lineText, y, textLeft);

            // Git blame annotation (virtual text at end of line)
            DrawBlameAnnotation(dc, l, lineText, y, textLeft);

            // LSP inlay hints (inline ghost text)
            DrawInlayHints(dc, l, lineText, y, textLeft);

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

            dc.Pop();
        }

        _scrollOffsetX = baseOffsetX;

        DrawImeCandidatePopup(dc, textLeft, size);
        DrawSignatureHelp(dc, textLeft, size);
        DrawCompletionPopup(dc, textLeft, size);
        DrawCodeActionPopup(dc, textLeft, size);

        // Color column guide line
        if (_colorColumn > 0 && _charWidth > 0)
        {
            double ccX = gutterWidth + (_colorColumn - 1) * _charWidth - _scrollOffsetX;
            if (ccX >= gutterWidth && ccX < size.Width)
            {
                var ccPen = new Pen(Theme.ColorColumnBrush, 1);
                dc.DrawLine(ccPen, new Point(ccX, 0), new Point(ccX, size.Height));
            }
        }

        // Indent guide lines
        if (_showIndentGuides && _charWidth > 0)
        {
            double indentWidth = _indentGuideTabStop * _charWidth;
            var guidePen = new Pen(Theme.IndentGuideBrush, 1);

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

            for (int level = 1; level <= maxIndentLevel; level++)
            {
                double x = gutterWidth + level * indentWidth - _scrollOffsetX;
                if (x < gutterWidth || x >= size.Width) continue;
                dc.DrawLine(guidePen, new Point(x, 0), new Point(x, size.Height));
            }
        }

        // Gutter border
        if (_showLineNumbers)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), 1);
            dc.DrawLine(pen, new Point(gutterWidth - 1, 0), new Point(gutterWidth - 1, size.Height));
        }

        // Minimap
        if (_showMinimap)
            DrawMinimap(dc, size);

        dc.Pop(); // end content clip (contentBottom)

        // Overlay scrollbars
        if (_showScrollbar)
            DrawScrollbars(dc, size);
    }

    private void DrawMinimap(DrawingContext dc, Size size)
    {
        if (_lineHeight <= 0 || _lines.Length == 0) return;

        double mmLeft = size.Width - MinimapWidth;

        // Background
        dc.DrawRectangle(Theme.MinimapBackground, null, new Rect(mmLeft, 0, MinimapWidth, size.Height));

        int totalLines = _lines.Length;
        // Each buffer line is represented as a tiny strip
        double lineStripH = Math.Max(1.0, Math.Min(2.0, size.Height / totalLines));
        double totalMapH  = totalLines * lineStripH;

        // Scroll the minimap so the viewport center stays centred when file is larger than minimap
        double viewportTopLine    = _scrollOffsetY / _lineHeight;
        double viewportBottomLine = viewportTopLine + _visibleLines;
        double viewportCentreLine = (viewportTopLine + viewportBottomLine) / 2.0;

        // Offset so the center of the visible region maps to the center of the minimap
        double mapOffsetY = 0;
        if (totalMapH > size.Height)
        {
            double idealCentre = viewportCentreLine * lineStripH;
            mapOffsetY = Math.Clamp(idealCentre - size.Height / 2.0, 0, totalMapH - size.Height);
        }

        // Foreground brush at low opacity for content strips
        var fgColor = (Theme.Foreground is SolidColorBrush sb) ? sb.Color : Color.FromRgb(0xCC, 0xCC, 0xCC);
        var contentBrush = new SolidColorBrush(Color.FromArgb(0x50, fgColor.R, fgColor.G, fgColor.B));

        // Draw each line as a colored block proportional to line length
        for (int l = 0; l < totalLines; l++)
        {
            double y = l * lineStripH - mapOffsetY;
            if (y + lineStripH < 0 || y > size.Height) continue;

            int lineLen = _lines[l].Length;
            if (lineLen == 0) continue;

            // Cap line width at minimap width, scale proportionally to a reasonable max (120 chars)
            double lineW = Math.Min(MinimapWidth - 4, lineLen / 120.0 * (MinimapWidth - 4));
            if (lineW < 1) lineW = 1;

            dc.DrawRectangle(contentBrush, null, new Rect(mmLeft + 2, y, lineW, Math.Max(1, lineStripH - 0.5)));
        }

        // Viewport highlight rectangle
        double vpTop = viewportTopLine * lineStripH - mapOffsetY;
        double vpH   = _visibleLines * lineStripH;
        vpTop = Math.Clamp(vpTop, 0, size.Height);
        vpH   = Math.Min(vpH, size.Height - vpTop);
        if (vpH > 0)
            dc.DrawRectangle(Theme.MinimapViewport, null, new Rect(mmLeft, vpTop, MinimapWidth, vpH));
    }

    private const double ScrollbarSize = 6.0;
    private const double ScrollbarThumbMinSize = 20.0;
    // The visible bars are thin (ScrollbarSize); the grab zone is wider so the
    // thumb is easy to hit and drag with the mouse.
    private const double ScrollbarHitSize = 16.0;

    // Geometry of the overlay scrollbars, shared by rendering and mouse hit-testing.
    private struct ScrollbarLayout
    {
        public bool NeedVert;
        public bool NeedHoriz;
        public double VertTrackH;
        public double VertThumbY;
        public double VertThumbH;
        public double VertMaxOff;
        public double HorizTrackW;
        public double HorizThumbX;
        public double HorizThumbW;
        public double HorizMaxOff;
    }

    private ScrollbarLayout ComputeScrollbarLayout(Size size)
    {
        var l = new ScrollbarLayout();
        double totalH = TotalContentHeight;
        double viewH  = size.Height;
        double totalW = TotalContentWidth;
        double viewW  = size.Width;

        l.NeedVert  = totalH > viewH + 1;
        l.NeedHoriz = !_wrapLines && totalW > viewW + 1;
        if (!l.NeedVert && !l.NeedHoriz) return l;

        // Reserve space so the two bars don't overlap at the corner
        l.VertTrackH  = l.NeedHoriz ? size.Height - ScrollbarSize : size.Height;
        l.HorizTrackW = l.NeedVert  ? size.Width  - ScrollbarSize : size.Width;

        if (l.NeedVert)
        {
            l.VertThumbH = Math.Min(l.VertTrackH, Math.Max(ScrollbarThumbMinSize, l.VertTrackH * viewH / totalH));
            l.VertMaxOff = totalH - viewH;
            l.VertThumbY = l.VertMaxOff > 0 ? (l.VertTrackH - l.VertThumbH) * (_scrollOffsetY / l.VertMaxOff) : 0;
        }
        if (l.NeedHoriz)
        {
            l.HorizThumbW = Math.Min(l.HorizTrackW, Math.Max(ScrollbarThumbMinSize, l.HorizTrackW * viewW / totalW));
            l.HorizMaxOff = totalW - viewW;
            l.HorizThumbX = l.HorizMaxOff > 0 ? (l.HorizTrackW - l.HorizThumbW) * (_scrollOffsetX / l.HorizMaxOff) : 0;
        }
        return l;
    }

    private void DrawScrollbars(DrawingContext dc, Size size)
    {
        var l = ComputeScrollbarLayout(size);
        if (!l.NeedVert && !l.NeedHoriz) return;

        if (l.NeedVert && l.VertTrackH > 0)
        {
            double trackX = size.Width - ScrollbarSize;
            dc.DrawRectangle(Theme.ScrollbarTrack, null, new Rect(trackX, 0, ScrollbarSize, l.VertTrackH));
            dc.DrawRectangle(Theme.ScrollbarThumb, null, new Rect(trackX + 1, l.VertThumbY + 1, ScrollbarSize - 2, Math.Max(0, l.VertThumbH - 2)));
        }

        if (l.NeedHoriz && l.HorizTrackW > 0)
        {
            double trackY = size.Height - ScrollbarSize;
            dc.DrawRectangle(Theme.ScrollbarTrack, null, new Rect(0, trackY, l.HorizTrackW, ScrollbarSize));
            dc.DrawRectangle(Theme.ScrollbarThumb, null, new Rect(l.HorizThumbX + 1, trackY + 1, Math.Max(0, l.HorizThumbW - 2), ScrollbarSize - 2));
        }
    }

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

    private void DrawBlameAnnotation(DrawingContext dc, int lineIndex, string lineText, double y, double textLeft)
    {
        if (_blameAnnotations.Count == 0) return;
        if (!_blameAnnotations.TryGetValue(lineIndex, out var blame)) return;

        double lineWidth = string.IsNullOrEmpty(lineText) ? 0 : GetVisualX(lineText, lineText.Length);
        var ft = FormatText(blame, Theme.LineNumberFg);
        dc.DrawText(ft, new Point(textLeft + lineWidth - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
    }

    private static readonly SolidColorBrush s_inlayHintBg =
        Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0xAA)));
    private static readonly SolidColorBrush s_inlayHintFg =
        Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xAA, 0xAA, 0xCC)));

    private void DrawInlayHints(DrawingContext dc, int lineIndex, string lineText, double y, double textLeft)
    {
        if (_inlayHintsByLine.Count == 0) return;
        if (!_inlayHintsByLine.TryGetValue(lineIndex, out var hints)) return;

        foreach (var hint in hints)
        {
            int col = Math.Min(hint.Position.Character, lineText.Length);
            double xBase = textLeft + GetVisualX(lineText, col) - _scrollOffsetX;

            var ft = new FormattedText(
                hint.Label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                _italicTypeface,
                _fontSize * 0.85,
                s_inlayHintFg,
                GetDpi());

            double hintY = y + (_lineHeight - ft.Height) / 2;

            // Draw a subtle background pill
            var bgRect = new Rect(xBase - 1, hintY - 1, ft.Width + 2, ft.Height + 2);
            dc.DrawRectangle(s_inlayHintBg, null, bgRect);
            dc.DrawText(ft, new Point(xBase, hintY));
        }
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

    private void DrawSignatureHelp(DrawingContext dc, double textLeft, Size size)
    {
        if (_signatureHelp == null || _signatureHelp.Signatures.Count == 0) return;

        int sigIdx = Math.Clamp(_signatureHelp.ActiveSignature, 0, _signatureHelp.Signatures.Count - 1);
        var sig = _signatureHelp.Signatures[sigIdx];
        if (string.IsNullOrEmpty(sig.Label)) return;

        int activeParam = _signatureHelp.ActiveParameter;

        // Find active parameter substring offsets within sig.Label
        int hlStart = -1, hlEnd = -1;
        if (activeParam >= 0 && activeParam < sig.Parameters.Count)
        {
            var paramLabel = sig.Parameters[activeParam].Label;
            if (!string.IsNullOrEmpty(paramLabel))
            {
                int idx = sig.Label.IndexOf(paramLabel, StringComparison.Ordinal);
                if (idx >= 0) { hlStart = idx; hlEnd = idx + paramLabel.Length; }
            }
        }

        const double padX = 8, padY = 3;

        // Build formatted text pieces
        var before = hlStart > 0 ? sig.Label[..hlStart] : (hlStart < 0 ? sig.Label : "");
        var hl     = hlStart >= 0 ? sig.Label[hlStart..hlEnd] : "";
        var after  = hlEnd > 0 && hlEnd < sig.Label.Length ? sig.Label[hlEnd..] : "";

        var ftBefore = FormatText(before, Theme.Foreground);
        var ftHl     = string.IsNullOrEmpty(hl) ? null : FormatText(hl, Theme.TokenKeyword);
        var ftAfter  = FormatText(after, Theme.Foreground);

        double totalW = ftBefore.Width + (ftHl?.Width ?? 0) + ftAfter.Width + padX * 2;
        double totalH = _lineHeight + padY * 2;

        var cursor = GetCursorPixelPosition();
        double x = cursor.X;
        // Prefer above the cursor; fall back to below if no room
        double y = cursor.Y - totalH - 2;
        if (y < 0) y = cursor.Y + _lineHeight + 2;
        if (x + totalW > size.Width) x = Math.Max(textLeft, size.Width - totalW - 2);

        dc.DrawRectangle(s_popupBg1, s_popupBorder, new Rect(x, y, totalW, totalH));

        double tx = x + padX;
        double ty = y + padY + (_lineHeight - ftBefore.Height) / 2;

        dc.DrawText(ftBefore, new Point(tx, ty));
        tx += ftBefore.Width;
        if (ftHl != null)
        {
            dc.DrawText(ftHl, new Point(tx, ty));
            tx += ftHl.Width;
        }
        dc.DrawText(ftAfter, new Point(tx, ty));
    }

    private void DrawCompletionPopup(DrawingContext dc, double textLeft, Size size)
    {
        if (_completionItems.Count == 0) return;

        const int maxVisible = 10;
        int scrollOffset = Math.Max(0, Math.Min(_completionScrollOffset, _completionItems.Count - 1));
        int count = Math.Min(maxVisible, _completionItems.Count - scrollOffset);

        var texts = new FormattedText[count];
        double maxW = 0;
        for (int i = 0; i < count; i++)
        {
            var item = _completionItems[scrollOffset + i];
            var label = item.Detail != null ? $"{item.Label}  {item.Detail}" : item.Label;
            var ft = FormatText(label, Theme.Foreground);
            texts[i] = ft;
            maxW = Math.Max(maxW, ft.Width);
        }

        double rowH   = Math.Max(_lineHeight, texts.Max(static t => t.Height));
        double padX   = 8;
        double padY   = 4;
        double popupW = maxW + padX * 2 + 24; // 24px for kind icon column
        double popupH = rowH * count + padY * 2;

        var cursor = GetCursorPixelPosition();
        double x = cursor.X;
        double y = cursor.Y + _lineHeight + 2;

        if (x + popupW > size.Width)  x = Math.Max(textLeft, size.Width - popupW - 2);
        if (y + popupH > size.Height) y = Math.Max(0, cursor.Y - popupH - 2);

        dc.DrawRectangle(s_popupBg2, s_popupBorder, new Rect(x, y, popupW, popupH));

        for (int i = 0; i < count; i++)
        {
            int itemIndex = scrollOffset + i;
            double rowY = y + padY + rowH * i;
            if (itemIndex == _completionSelection)
                dc.DrawRectangle(Theme.SelectionBg, null, new Rect(x + 1, rowY, popupW - 2, rowH));

            // Kind indicator dot
            var kindBrush = GetCompletionKindBrush(_completionItems[itemIndex].Kind);
            dc.DrawEllipse(kindBrush, null, new Point(x + padX + 4, rowY + rowH / 2), 4, 4);

            dc.DrawText(texts[i], new Point(x + padX + 16, rowY + (rowH - texts[i].Height) / 2));
        }

        // Documentation panel for selected item
        if (_completionSelection >= 0 && _completionSelection < _completionItems.Count)
        {
            var selectedItem = _completionItems[_completionSelection];
            var docText = selectedItem.Documentation;
            if (!string.IsNullOrWhiteSpace(docText))
            {
                const double docPadX = 10;
                const double docPadY = 8;
                const double docMaxW = 320;
                const double docMaxH = 200;

                // Word-wrap documentation text
                var docLines = WrapDocText(docText, docMaxW - docPadX * 2);
                double docLineH = _lineHeight;
                double docW = docLines.Max(l => l.Width) + docPadX * 2;
                double docH = Math.Min(docMaxH, docLines.Count * docLineH + docPadY * 2);

                double docX = x + popupW + 2;
                double docY = y;

                // Flip to left if no room on right
                if (docX + docW > size.Width)
                    docX = x - docW - 2;

                dc.DrawRectangle(s_popupDocBg, s_popupBorder, new Rect(docX, docY, docW, docH));

                // Clip doc text to panel
                dc.PushClip(new RectangleGeometry(new Rect(docX + 1, docY + 1, docW - 2, docH - 2)));
                int maxLines = (int)((docH - docPadY * 2) / docLineH);
                for (int i = 0; i < Math.Min(maxLines, docLines.Count); i++)
                    dc.DrawText(docLines[i], new Point(docX + docPadX, docY + docPadY + i * docLineH));
                dc.Pop();
            }
        }
    }

    private List<FormattedText> WrapDocText(string text, double maxWidth)
    {
        var result = new List<FormattedText>();
        var paragraphs = text.Replace("\r\n", "\n").Split('\n');
        foreach (var para in paragraphs)
        {
            if (string.IsNullOrEmpty(para)) { result.Add(FormatText("", Theme.TokenComment)); continue; }
            var words = para.Split(' ');
            var line = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                var test = line.Length == 0 ? word : line + " " + word;
                var ft = FormatText(test, Theme.TokenComment);
                if (ft.Width <= maxWidth || line.Length == 0)
                    line.Clear().Append(test);
                else
                {
                    result.Add(FormatText(line.ToString(), Theme.TokenComment));
                    line.Clear().Append(word);
                }
            }
            if (line.Length > 0)
                result.Add(FormatText(line.ToString(), Theme.TokenComment));
        }
        return result;
    }

    private Brush GetCompletionKindBrush(CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Class or CompletionItemKind.Interface => Theme.TokenType,
        CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => Theme.TokenKeyword,
        CompletionItemKind.Field or CompletionItemKind.Property or CompletionItemKind.Variable => Theme.TokenAttribute,
        CompletionItemKind.Keyword => Theme.TokenKeyword,
        CompletionItemKind.Snippet => Theme.TokenString,
        _ => Theme.TokenIdentifier
    };

    private void DrawCodeActionPopup(DrawingContext dc, double textLeft, Size size)
    {
        if (_codeActionItems.Count == 0) return;

        const int maxVisible = 10;
        int scrollOffset = Math.Max(0, Math.Min(_codeActionsScrollOffset, _codeActionItems.Count - 1));
        int count = Math.Min(maxVisible, _codeActionItems.Count - scrollOffset);

        var texts = new FormattedText[count];
        double maxW = 0;
        for (int i = 0; i < count; i++)
        {
            var action = _codeActionItems[scrollOffset + i];
            var label = action.Kind != null ? $"[{action.Kind}]  {action.Title}" : action.Title;
            var ft = FormatText(label, Theme.Foreground);
            texts[i] = ft;
            maxW = Math.Max(maxW, ft.Width);
        }

        double rowH   = Math.Max(_lineHeight, texts.Max(static t => t.Height));
        double padX   = 8;
        double padY   = 4;
        double popupW = maxW + padX * 2 + 16;
        double popupH = rowH * count + padY * 2;

        var cursor = GetCursorPixelPosition();
        double x = cursor.X;
        double y = cursor.Y - popupH - 2;  // prefer above cursor
        if (y < 0) y = cursor.Y + _lineHeight + 2;  // fall back to below

        if (x + popupW > size.Width) x = Math.Max(textLeft, size.Width - popupW - 2);

        dc.DrawRectangle(s_popupBg2, s_popupBorder, new Rect(x, y, popupW, popupH));

        for (int i = 0; i < count; i++)
        {
            int itemIndex = scrollOffset + i;
            double rowY = y + padY + rowH * i;
            if (itemIndex == _codeActionsSelection)
                dc.DrawRectangle(Theme.SelectionBg, null, new Rect(x + 1, rowY, popupW - 2, rowH));
            dc.DrawText(texts[i], new Point(x + padX, rowY + (rowH - texts[i].Height) / 2));
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
                double w = CharW(lineText[col]);
                dc.DrawRectangle(Theme.MatchingBracketBackground, null, new Rect(x, y, w, _lineHeight));
            }
        }

        // Highlight the matching bracket if it falls on this line
        if (bracketMatch == null || bracketMatch.Value.line != line) return;

        int matchCol = bracketMatch.Value.col;
        double mx = textLeft + GetVisualX(lineText, matchCol) - _scrollOffsetX;
        double mw = CharW(lineText[matchCol]);
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

    private void DrawDocumentHighlights(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (_documentHighlights is null || _documentHighlights.Count == 0) return;
        foreach (var hl in _documentHighlights)
        {
            if (hl.Range.Start.Line != line && hl.Range.End.Line != line
                && !(hl.Range.Start.Line < line && hl.Range.End.Line > line)) continue;

            int startCol = hl.Range.Start.Line == line ? hl.Range.Start.Character : 0;
            int endCol   = hl.Range.End.Line   == line ? hl.Range.End.Character   : lineText.Length;
            startCol = Math.Clamp(startCol, 0, lineText.Length);
            endCol   = Math.Clamp(endCol,   0, lineText.Length);
            if (endCol <= startCol) continue;

            double hLeft  = textLeft + GetVisualX(lineText, startCol) - _scrollOffsetX;
            double hWidth = GetVisualX(lineText, endCol) - GetVisualX(lineText, startCol);
            dc.DrawRectangle(Theme.DocumentHighlightBackground, null, new Rect(hLeft, y, Math.Max(0, hWidth), _lineHeight));
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

        // Semantic tokens take priority over regex-based syntax tokens when present for this line.
        // The list is pre-sorted by StartChar at SetSemanticTokens time.
        if (_semanticTokensByLine.TryGetValue(lineIndex, out var semTokens) && semTokens.Count > 0)
        {
            DrawLineTextWithSegments(dc, lineText, y, textLeft,
                semTokens.Select(t => (t.StartChar, t.Length, (Brush?)t.Brush)));
            return;
        }

        _tokensByLine.TryGetValue(lineIndex, out var tokens);

        if (tokens == null || tokens.Length == 0)
        {
            // No syntax — draw entire line in default color
            var ft = FormatText(lineText, Theme.Foreground);
            dc.DrawText(ft, new Point(textLeft - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            return;
        }

        // Draw segments with colors
        DrawLineTextWithSegments(dc, lineText, y, textLeft,
            tokens.Select(t => (t.StartColumn, t.Length, (Brush?)Theme.GetTokenBrush(t.Kind))));
    }

    /// <summary>
    /// Draws a line of text using a sequence of colored segments.
    /// Each segment is (startCol, length, brush?); gaps between segments use the default foreground.
    /// Null brush means "use default foreground".
    /// </summary>
    private void DrawLineTextWithSegments(DrawingContext dc, string lineText, double y, double textLeft,
        IEnumerable<(int StartCol, int Length, Brush? Brush)> segments)
    {
        int pos = 0;
        foreach (var (startCol, length, brush) in segments)
        {
            // Gap before segment in default color
            if (startCol > pos)
            {
                var gap = lineText[pos..Math.Min(startCol, lineText.Length)];
                if (gap.Length > 0)
                {
                    var ft = FormatText(gap, Theme.Foreground);
                    dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, pos) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
                }
            }
            // Segment text
            int end = Math.Min(startCol + length, lineText.Length);
            if (startCol < end)
            {
                var segText = lineText[startCol..end];
                var segBrush = brush ?? Theme.Foreground;
                var ft = FormatText(segText, segBrush);
                dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, startCol) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            }
            pos = Math.Max(pos, startCol + length);
        }
        // Remaining text
        if (pos < lineText.Length)
        {
            var rem = lineText[pos..];
            var ft = FormatText(rem, Theme.Foreground);
            dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, pos) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
        }
    }

    private static readonly Pen s_swatchBorderPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Colors.Black)), 0.75));

    private void DrawColorSwatches(DrawingContext dc, string lineText, double y, double textLeft)
    {
        if (string.IsNullOrEmpty(lineText)) return;

        double swatchSize = Math.Max(8, _lineHeight - 4);
        double swatchY    = y + (_lineHeight - swatchSize) / 2;

        for (int i = 0; i < lineText.Length; i++)
        {
            char c = lineText[i];
            if (c != '#' && c != 'r' && c != 'R') continue;

            if (!ColorParser.TryParseColor(lineText, i, out var color, out int matchLen))
                continue;

            // Position swatch right after the color text
            double textX = textLeft + GetVisualX(lineText, i + matchLen) - _scrollOffsetX;
            double swatchX = textX + 2; // small gap

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var swatchRect = new Rect(swatchX, swatchY, swatchSize, swatchSize);
            dc.DrawRectangle(brush, s_swatchBorderPen, swatchRect);

            // Advance past the matched token so we don't re-scan its interior
            i += matchLen - 1;
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
            double charW = CharW(c);

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

        if (_substitutePreviewLines.ContainsKey(lineIndex))
        {
            var ft = FormatText(merged, Theme.Foreground);
            dc.DrawText(ft, new Point(textLeft - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            return;
        }

        IEnumerable<(int StartCol, int Length, Brush? Brush)> segments = [];

        if (_semanticTokensByLine.TryGetValue(lineIndex, out var semTokens) && semTokens.Count > 0)
        {
            segments = semTokens.Select(t => (t.StartChar, t.Length, (Brush?)t.Brush));
        }
        else if (_tokensByLine.TryGetValue(lineIndex, out var tokens) && tokens.Length > 0)
        {
            segments = tokens.Select(t => (t.StartColumn, t.Length, (Brush?)Theme.GetTokenBrush(t.Kind)));
        }

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

        double cursorW = _cursor.Column < lineText.Length ? CharW(lineText[_cursor.Column]) : _charWidth;

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
                var ch = lineText[_cursor.Column].ToString();
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
            double cw = ecCol < lineText.Length ? CharW(lineText[ecCol]) : _charWidth;

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
                    var ft = FormatText(lineText[ecCol].ToString(), Theme.CursorForeground);
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

        dc.DrawRectangle(s_popupBg3, s_popupBorder, new Rect(x, y, popupW, popupH));

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
        var (_, _, gutterWidth) = GetGutterMetrics();
        string line = _cursor.Line < _lines.Length ? _lines[_cursor.Line] : string.Empty;
        int cursorCol = Math.Clamp(_cursor.Column, 0, line.Length);
        int cursorVisualLine = GetCursorVisualLine();
        var segment = GetVisualSegment(cursorVisualLine);
        double lineOffsetX = _wrapLines ? GetVisualX(line, segment.StartColumn) : _scrollOffsetX;
        double x = gutterWidth + GetVisualX(line, cursorCol) - lineOffsetX;
        double y = cursorVisualLine * _lineHeight - _scrollOffsetY;
        return new Point(Math.Max(gutterWidth, x), y);
    }

    // ─────────────── Character width helpers ───────────────

    private static bool UsesBaseMonospaceWidth(char c) =>
        c is '\t' || (c >= '\u0020' && c <= '\u007E');

    private double CharW(char c)
    {
        if (UsesBaseMonospaceWidth(c)) return _charWidth;
        if (_charWidthCache.TryGetValue(c, out double w)) return w;
        var ft = new FormattedText(c.ToString(), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White, GetDpi());
        w = ft.Width;
        _charWidthCache[c] = w;
        return w;
    }

    /// <summary>Visual X offset (in pixels) for character index <paramref name="col"/> in <paramref name="line"/>.</summary>
    private double GetVisualX(string line, int col)
    {
        double x = 0;
        int limit = Math.Min(col, line.Length);
        for (int i = 0; i < limit; i++)
            x += CharW(line[i]);
        return x;
    }

    /// <summary>Convert a visual X pixel offset to a character index in <paramref name="line"/>.</summary>
    private int VisualXToCol(string line, double visualX)
    {
        double x = 0;
        for (int i = 0; i < line.Length; i++)
        {
            double w = CharW(line[i]);
            if (x + w / 2 >= visualX) return i;
            x += w;
        }
        return line.Length;
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
        double viewHeight = RenderSize.Height;
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
            var (_, _, gutterWidth) = GetGutterMetrics();
            double viewportWidth = Math.Max(0, RenderSize.Width - gutterWidth);
            string line = _cursor.Line < _lines.Length ? _lines[_cursor.Line] : string.Empty;
            int cursorCol = Math.Clamp(_cursor.Column, 0, line.Length);
            double cursorX = GetVisualX(line, cursorCol);
            double cursorW = cursorCol < line.Length ? CharW(line[cursorCol]) : _charWidth;
            double marginX = 4 * _charWidth;

            if (viewportWidth > 0)
            {
                if (cursorX < _scrollOffsetX + marginX)
                    _scrollOffsetX = Math.Max(0, cursorX - marginX);
                else if (cursorX + cursorW > _scrollOffsetX + viewportWidth - marginX)
                    _scrollOffsetX = cursorX + cursorW + marginX - viewportWidth;
            }
        }

        double maxOffsetY = Math.Max(0, TotalContentHeight - RenderSize.Height);
        double maxOffsetX = _wrapLines
            ? 0
            : Math.Max(0, TotalContentWidth - RenderSize.Width);
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
