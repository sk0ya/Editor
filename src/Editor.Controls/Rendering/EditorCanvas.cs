using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Controls.Rendering;

public class EditorCanvas : FrameworkElement
{
    private readonly record struct VisualLineSegment(int BufferLine, int StartColumn, bool IsContinuation);

    private Typeface _typeface = new("Consolas");
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
    private LineTokens[] _tokens = [];
    private List<CursorPosition> _searchMatches = [];
    private string _searchPattern = "";
    private bool _showLineNumbers = true;
    private bool _relativeNumber = false;
    private int _lineNumberWidth = 4; // digits
    private bool _cursorVisible = true;
    private bool _isActive = true;
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private bool _isDragging = false;
    private string _imeCompositionText = string.Empty;
    private string[] _imeCandidates = [];
    private int _imeCandidateSelection = -1;
    private VisualLineSegment[] _visualLines = [new VisualLineSegment(0, 0, false)];
    private bool _wrapLines;
    private double _contentWidth;
    // Folds
    private int[] _visibleLineMap = [];
    private HashSet<int> _closedFoldStarts = [];   // buffer lines with closed fold (▶)
    private HashSet<int> _openFoldStarts = [];     // buffer lines with open fold (▼)
    private int _hoveredFoldLine = -1;             // buffer line currently hovered in fold gutter

    // Scrollbar
    private bool _showScrollbar = true;

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

    // Git
    private Dictionary<int, GitLineState> _gitDiff = [];
    private Dictionary<int, string> _blameAnnotations = [];

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
        _fontSize = size;
        _charWidth = 0;
        _charWidthCache.Clear();
        MeasureChar();
        RebuildVisualLayout();
        InvalidateVisual();
    }

    public void SetLines(string[] lines)
    {
        _lines = lines.Length > 0 ? lines : [""];
        _lineNumberWidth = Math.Max(3, _lines.Length.ToString().Length);
        RebuildVisualLayout();
        InvalidateVisual();
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; InvalidateVisual(); }
    }

    public void SetCursor(CursorPosition cursor) { _cursor = cursor; EnsureCursorVisible(); InvalidateVisual(); }
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

    public void SetBlameAnnotations(Dictionary<int, string>? annotations)
    {
        _blameAnnotations = annotations ?? [];
        InvalidateVisual();
    }

    public void SetTokens(LineTokens[] tokens) { _tokens = tokens; InvalidateVisual(); }
    public void SetSearchMatches(List<CursorPosition> matches, string pattern) { _searchMatches = matches; _searchPattern = pattern; InvalidateVisual(); }
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

    public void SetImeCompositionText(string text)
    {
        text ??= string.Empty;
        if (_imeCompositionText == text) return;
        _imeCompositionText = text;
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
        MeasureChar();
        var (_, _, gutterWidth) = GetGutterMetrics();
        double availableTextWidth = Math.Max(1, RenderSize.Width - gutterWidth);

        var visibleLines = _visibleLineMap.Length > 0
            ? _visibleLineMap
            : Enumerable.Range(0, Math.Max(1, _lines.Length)).ToArray();

        var visualLines = new List<VisualLineSegment>(visibleLines.Length);
        double maxLineWidth = 0;

        foreach (int lineIndex in visibleLines)
        {
            int safeLine = Math.Clamp(lineIndex, 0, Math.Max(0, _lines.Length - 1));
            string lineText = safeLine < _lines.Length ? _lines[safeLine] : string.Empty;
            maxLineWidth = Math.Max(maxLineWidth, GetVisualX(lineText, lineText.Length));

            if (!_wrapLines || lineText.Length == 0 || availableTextWidth <= 1)
            {
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
        CaptureMouse();
        _isDragging = false;

        var point = e.GetPosition(this);
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
        MouseClicked?.Invoke(line, col);
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && IsMouseCaptured)
        {
            _isDragging = true;
            var (line, col) = HitTest(point);
            MouseDragging?.Invoke(line, col);
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
            Cursor = System.Windows.Input.Cursors.IBeam;
        }
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
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
            double textAreaWidth = Math.Max(1, finalSize.Width - gutterWidth);
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

        var (lineNumWidth, foldColWidth, gutterWidth) = GetGutterMetrics();
        double textLeft = gutterWidth;

        int firstLine = (int)(_scrollOffsetY / _lineHeight);
        int lastLine = Math.Min(TotalVisualLines - 1, firstLine + _visibleLines + 1);
        double baseOffsetX = _scrollOffsetX;

        // Compute matching bracket once — reused for every visible line
        var bracketMatch = FindMatchingBracket(_cursor.Line, _cursor.Column);

        // Draw each visible line (vi = visual index, l = buffer line)
        for (int vi = firstLine; vi <= lastLine; vi++)
        {
            var segment = GetVisualSegment(vi);
            int l = segment.BufferLine;
            double y = vi * _lineHeight - _scrollOffsetY;
            if (y + _lineHeight < 0 || y > size.Height) continue;

            var lineText = l < _lines.Length ? _lines[l] : "";
            _scrollOffsetX = _wrapLines ? GetVisualX(lineText, segment.StartColumn) : baseOffsetX;
            bool drawNumberAndFold = !_wrapLines || !segment.IsContinuation;

            // Current line highlight
            if (l == _cursor.Line && Theme.CurrentLineBg != null)
                dc.DrawRectangle(Theme.CurrentLineBg, null, new Rect(textLeft, y, size.Width - textLeft, _lineHeight));

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

            // LSP diagnostics (wavy underlines)
            DrawDiagnostics(dc, l, y, textLeft, lineText);

            // Spell check errors (blue wavy underlines)
            DrawSpellErrors(dc, l, y, textLeft, lineText);

            // Cursor
            DrawCursor(dc, l, y, textLeft, lineText);

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

        // Overlay scrollbars
        if (_showScrollbar)
            DrawScrollbars(dc, size);
    }

    private const double ScrollbarSize = 6.0;
    private const double ScrollbarThumbMinSize = 20.0;

    private void DrawScrollbars(DrawingContext dc, Size size)
    {
        double totalH = TotalContentHeight;
        double viewH  = size.Height;
        double totalW = TotalContentWidth;
        double viewW  = size.Width;

        bool needVert  = totalH > viewH + 1;
        bool needHoriz = !_wrapLines && totalW > viewW + 1;

        if (!needVert && !needHoriz) return;

        // Reserve space so the two bars don't overlap at the corner
        double vertTrackH  = needHoriz ? size.Height - ScrollbarSize : size.Height;
        double horizTrackW = needVert  ? size.Width  - ScrollbarSize : size.Width;

        if (needVert)
        {
            double trackX = size.Width - ScrollbarSize;
            dc.DrawRectangle(Theme.ScrollbarTrack, null, new Rect(trackX, 0, ScrollbarSize, vertTrackH));
            double thumbH = Math.Min(vertTrackH, Math.Max(ScrollbarThumbMinSize, vertTrackH * viewH / totalH));
            double maxOff = totalH - viewH;
            double thumbY = maxOff > 0 ? (vertTrackH - thumbH) * (_scrollOffsetY / maxOff) : 0;
            dc.DrawRectangle(Theme.ScrollbarThumb, null, new Rect(trackX + 1, thumbY + 1, ScrollbarSize - 2, Math.Max(0, thumbH - 2)));
        }

        if (needHoriz)
        {
            double trackY = size.Height - ScrollbarSize;
            dc.DrawRectangle(Theme.ScrollbarTrack, null, new Rect(0, trackY, horizTrackW, ScrollbarSize));
            double thumbW = Math.Min(horizTrackW, Math.Max(ScrollbarThumbMinSize, horizTrackW * viewW / totalW));
            double maxOff = totalW - viewW;
            double thumbX = maxOff > 0 ? (horizTrackW - thumbW) * (_scrollOffsetX / maxOff) : 0;
            dc.DrawRectangle(Theme.ScrollbarThumb, null, new Rect(thumbX + 1, trackY + 1, Math.Max(0, thumbW - 2), ScrollbarSize - 2));
        }
    }

    private void DrawBlameAnnotation(DrawingContext dc, int lineIndex, string lineText, double y, double textLeft)
    {
        if (_blameAnnotations.Count == 0) return;
        if (!_blameAnnotations.TryGetValue(lineIndex, out var blame)) return;

        double lineWidth = string.IsNullOrEmpty(lineText) ? 0 : GetVisualX(lineText, lineText.Length);
        var ft = FormatText(blame, Theme.LineNumberFg);
        dc.DrawText(ft, new Point(textLeft + lineWidth - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
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
        foreach (var match in _searchMatches)
        {
            if (match.Line != line) continue;
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
            DrawLineTextWithImeComposition(dc, lineText, y, textLeft);
            return;
        }

        if (string.IsNullOrEmpty(lineText)) return;

        var tokens = _tokens.FirstOrDefault(t => t.Line == lineIndex).Tokens;

        if (tokens == null || tokens.Length == 0)
        {
            // No syntax — draw entire line in default color
            var ft = FormatText(lineText, Theme.Foreground);
            dc.DrawText(ft, new Point(textLeft - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            return;
        }

        // Draw segments with colors
        int pos = 0;
        foreach (var tok in tokens.OrderBy(t => t.StartColumn))
        {
            // Gap before token in default color
            if (tok.StartColumn > pos)
            {
                var gap = lineText[pos..Math.Min(tok.StartColumn, lineText.Length)];
                if (gap.Length > 0)
                {
                    var ft = FormatText(gap, Theme.Foreground);
                    dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, pos) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
                }
            }
            // Token text
            int end = Math.Min(tok.StartColumn + tok.Length, lineText.Length);
            if (tok.StartColumn < end)
            {
                var tokText = lineText[tok.StartColumn..end];
                var brush = Theme.GetTokenBrush(tok.Kind);
                var ft = FormatText(tokText, brush);
                dc.DrawText(ft, new Point(textLeft + GetVisualX(lineText, tok.StartColumn) - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            }
            pos = Math.Max(pos, tok.StartColumn + tok.Length);
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

    private void DrawLineTextWithImeComposition(DrawingContext dc, string lineText, double y, double textLeft)
    {
        int cursorCol = Math.Clamp(_cursor.Column, 0, lineText.Length);
        string merged = lineText.Insert(cursorCol, _imeCompositionText);
        if (string.IsNullOrEmpty(merged)) return;

        var ft = FormatText(merged, Theme.Foreground);
        dc.DrawText(ft, new Point(textLeft - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
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

        if (_mode == VimMode.Insert || _mode == VimMode.Command ||
            _mode == VimMode.SearchForward || _mode == VimMode.SearchBackward)
        {
            // Thin line cursor
            var pen = new Pen(Theme.InsertCursor, 2);
            dc.DrawLine(pen, new Point(cursorX, cursorY), new Point(cursorX, cursorY + _lineHeight));
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

    private void DrawImeCompositionUnderline(DrawingContext dc, double cursorX, double cursorY)
    {
        if (string.IsNullOrEmpty(_imeCompositionText)) return;

        double width = Math.Max(1, FormatText(_imeCompositionText, Theme.Foreground).Width);
        var pen = new Pen(Theme.InsertCursor, 1);
        dc.DrawLine(
            pen,
            new Point(cursorX, cursorY + _lineHeight - 1),
            new Point(cursorX + width, cursorY + _lineHeight - 1));
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

    // ─────────────── Full-width character helpers ───────────────

    private static bool IsFullWidth(char c) =>
        (c >= '\u1100' && c <= '\u115F') ||
        (c >= '\u2E80' && c <= '\u303E') ||
        (c >= '\u3041' && c <= '\u33BF') ||
        (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u4E00' && c <= '\uA4CF') ||
        (c >= '\uA960' && c <= '\uA97F') ||
        (c >= '\uAC00' && c <= '\uD7FF') ||
        (c >= '\uF900' && c <= '\uFAFF') ||
        (c >= '\uFE10' && c <= '\uFE1F') ||
        (c >= '\uFE30' && c <= '\uFE6F') ||
        (c >= '\uFF01' && c <= '\uFF60') ||
        (c >= '\uFFE0' && c <= '\uFFE6');

    private double CharW(char c)
    {
        if (!IsFullWidth(c)) return _charWidth;
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
