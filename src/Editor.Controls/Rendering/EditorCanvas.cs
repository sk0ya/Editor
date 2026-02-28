using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Controls.Rendering;

public class EditorCanvas : FrameworkElement
{
    private Typeface _typeface = new("Consolas");
    private double _fontSize = 14;
    private double _charWidth;
    private double _lineHeight;
    private readonly Dictionary<char, double> _charWidthCache = [];
    private double _scrollOffsetY;
    private double _scrollOffsetX;
    private int _visibleLines;
    private int _visibleColumns;

    private string[] _lines = [""];
    private CursorPosition _cursor;
    private Selection? _selection;
    private VimMode _mode = VimMode.Normal;
    private LineTokens[] _tokens = [];
    private List<CursorPosition> _searchMatches = [];
    private string _searchPattern = "";
    private bool _showLineNumbers = true;
    private int _lineNumberWidth = 4; // digits
    private bool _cursorVisible = true;
    private System.Windows.Threading.DispatcherTimer? _cursorTimer;
    private bool _isDragging = false;
    private string _imeCompositionText = string.Empty;
    private string[] _imeCandidates = [];
    private int _imeCandidateSelection = -1;
    // LSP
    private IReadOnlyList<LspDiagnostic> _diagnostics = [];
    private IReadOnlyList<LspCompletionItem> _completionItems = [];
    private int _completionSelection = -1;

    public EditorTheme Theme { get; set; } = EditorTheme.Dracula;

    public event Action<double, double>? ScrollChanged;
    public event Action<int>? VisibleLinesChanged;
    public event Action<int, int>? MouseClicked;    // (line, col)
    public event Action<int, int>? MouseDragging;   // (line, col) during drag
    public event Action? MouseDragEnded;

    public EditorCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        StartCursorBlink();
    }

    // ─────────────── Public scroll info ───────────────

    public double TotalContentHeight => _lines.Length * _lineHeight;
    public double ViewportHeight => RenderSize.Height;

    public void UpdateFont(string family, double size)
    {
        _typeface = new Typeface(family);
        _fontSize = size;
        _charWidth = 0;
        _charWidthCache.Clear();
        MeasureChar();
        InvalidateVisual();
    }

    public void SetLines(string[] lines)
    {
        _lines = lines;
        _lineNumberWidth = Math.Max(3, lines.Length.ToString().Length);
        InvalidateVisual();
    }

    public void SetCursor(CursorPosition cursor) { _cursor = cursor; EnsureCursorVisible(); InvalidateVisual(); }
    public void SetSelection(Selection? sel) { _selection = sel; InvalidateVisual(); }
    public void SetMode(VimMode mode) { _mode = mode; InvalidateVisual(); }
    public void SetTokens(LineTokens[] tokens) { _tokens = tokens; InvalidateVisual(); }
    public void SetSearchMatches(List<CursorPosition> matches, string pattern) { _searchMatches = matches; _searchPattern = pattern; InvalidateVisual(); }
    public void ShowLineNumbers(bool show) { _showLineNumbers = show; InvalidateVisual(); }
    public void SetDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
        InvalidateVisual();
    }

    public void SetCompletionItems(IReadOnlyList<LspCompletionItem> items, int selection)
    {
        _completionItems = items;
        _completionSelection = selection;
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
    public int FirstVisibleLine => (int)(_scrollOffsetY / _lineHeight);
    public int LastVisibleLine => Math.Min(_lines.Length - 1, FirstVisibleLine + _visibleLines);

    public void ScrollTo(double offsetY, double offsetX = 0)
    {
        double maxOffsetY = Math.Max(0, TotalContentHeight - RenderSize.Height);
        _scrollOffsetY = Math.Clamp(offsetY, 0, maxOffsetY);
        _scrollOffsetX = Math.Max(0, offsetX);
        ScrollChanged?.Invoke(_scrollOffsetY, _scrollOffsetX);
        InvalidateVisual();
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
        var (line, col) = HitTest(e.GetPosition(this));
        _isDragging = false;
        MouseClicked?.Invoke(line, col);
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && IsMouseCaptured)
        {
            _isDragging = true;
            var (line, col) = HitTest(e.GetPosition(this));
            MouseDragging?.Invoke(line, col);
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

    private (int line, int col) HitTest(System.Windows.Point point)
    {
        if (_lineHeight <= 0 || _charWidth <= 0) return (0, 0);

        int lineNumGutter = _showLineNumbers ? (int)((_lineNumberWidth + 1) * _charWidth) : 0;

        int line = (int)((point.Y + _scrollOffsetY) / _lineHeight);
        line = Math.Clamp(line, 0, Math.Max(0, _lines.Length - 1));

        string hitLine = line < _lines.Length ? _lines[line] : "";
        double visualX = point.X - lineNumGutter + _scrollOffsetX;
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
            _visibleColumns = (int)(finalSize.Width / _charWidth) + 2;
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        MeasureChar();

        // Background
        dc.DrawRectangle(Theme.Background, null, new Rect(size));

        int lineNumGutter = _showLineNumbers ? (int)((_lineNumberWidth + 1) * _charWidth) : 0;
        double textLeft = lineNumGutter;

        int firstLine = (int)(_scrollOffsetY / _lineHeight);
        int lastLine = Math.Min(_lines.Length - 1, firstLine + _visibleLines + 1);

        // Draw each visible line
        for (int l = firstLine; l <= lastLine; l++)
        {
            double y = l * _lineHeight - _scrollOffsetY;
            if (y + _lineHeight < 0 || y > size.Height) continue;

            var lineText = l < _lines.Length ? _lines[l] : "";

            // Current line highlight
            if (l == _cursor.Line && Theme.CurrentLineBg != null)
                dc.DrawRectangle(Theme.CurrentLineBg, null, new Rect(textLeft, y, size.Width - textLeft, _lineHeight));

            // Line number gutter
            if (_showLineNumbers)
            {
                dc.DrawRectangle(Theme.LineNumberBg, null, new Rect(0, y, lineNumGutter, _lineHeight));
                var numText = FormatText((l + 1).ToString().PadLeft(_lineNumberWidth), Theme.LineNumberFg);
                dc.DrawText(numText, new Point(2, y + (_lineHeight - numText.Height) / 2));
            }

            // Selection highlight
            DrawSelection(dc, l, y, textLeft, lineText);

            // Search highlights
            DrawSearchHighlights(dc, l, y, textLeft, lineText);

            // Text with syntax coloring
            DrawLineText(dc, l, lineText, y, textLeft);

            // LSP diagnostics (wavy underlines)
            DrawDiagnostics(dc, l, y, textLeft, lineText);

            // Cursor
            DrawCursor(dc, l, y, textLeft, lineText);
        }

        DrawImeCandidatePopup(dc, textLeft, size);
        DrawCompletionPopup(dc, textLeft, size);

        // Gutter border
        if (_showLineNumbers)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), 1);
            dc.DrawLine(pen, new Point(lineNumGutter - 1, 0), new Point(lineNumGutter - 1, size.Height));
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

    private void DrawCompletionPopup(DrawingContext dc, double textLeft, Size size)
    {
        if (_completionItems.Count == 0) return;

        const int maxVisible = 10;
        int count = Math.Min(maxVisible, _completionItems.Count);

        var texts = new FormattedText[count];
        double maxW = 0;
        for (int i = 0; i < count; i++)
        {
            var item = _completionItems[i];
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

        var bg     = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1F, 0x29));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(0x63, 0x65, 0x72)), 1);
        dc.DrawRectangle(bg, border, new Rect(x, y, popupW, popupH));

        for (int i = 0; i < count; i++)
        {
            double rowY = y + padY + rowH * i;
            if (i == _completionSelection)
                dc.DrawRectangle(Theme.SelectionBg, null, new Rect(x + 1, rowY, popupW - 2, rowH));

            // Kind indicator dot
            var kindBrush = GetCompletionKindBrush(_completionItems[i].Kind);
            dc.DrawEllipse(kindBrush, null, new Point(x + padX + 4, rowY + rowH / 2), 4, 4);

            dc.DrawText(texts[i], new Point(x + padX + 16, rowY + (rowH - texts[i].Height) / 2));
        }
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

        var bg = new SolidColorBrush(Color.FromArgb(0xEE, 0x1E, 0x1F, 0x29));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(0x63, 0x65, 0x72)), 1);
        dc.DrawRectangle(bg, border, new Rect(x, y, popupW, popupH));

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
        int lineNumGutter = _showLineNumbers ? (int)((_lineNumberWidth + 1) * _charWidth) : 0;
        string line = _cursor.Line < _lines.Length ? _lines[_cursor.Line] : "";
        double x = lineNumGutter + GetVisualX(line, _cursor.Column) - _scrollOffsetX;
        double y = _cursor.Line * _lineHeight - _scrollOffsetY;
        return new Point(Math.Max(lineNumGutter, x), y);
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

        double cursorY = _cursor.Line * _lineHeight;
        double viewHeight = RenderSize.Height;
        double margin = 5 * _lineHeight;

        if (cursorY < _scrollOffsetY + margin)
            _scrollOffsetY = Math.Max(0, cursorY - margin);
        else if (cursorY + _lineHeight > _scrollOffsetY + viewHeight - margin)
            _scrollOffsetY = cursorY + _lineHeight + margin - viewHeight;

        _scrollOffsetY = Math.Max(0, _scrollOffsetY);
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
