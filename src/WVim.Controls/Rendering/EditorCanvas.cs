using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WVim.Controls.Themes;
using WVim.Core.Engine;
using WVim.Core.Models;
using WVim.Core.Syntax;

namespace WVim.Controls.Rendering;

public class EditorCanvas : FrameworkElement
{
    private Typeface _typeface = new("Consolas");
    private double _fontSize = 14;
    private double _charWidth;
    private double _lineHeight;
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

    public EditorTheme Theme { get; set; } = EditorTheme.Dracula;

    public event Action<double, double>? ScrollChanged;
    public event Action<int>? VisibleLinesChanged;

    public EditorCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        StartCursorBlink();
    }

    public void UpdateFont(string family, double size)
    {
        _typeface = new Typeface(family);
        _fontSize = size;
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

    public double CharWidth => _charWidth;
    public double LineHeight => _lineHeight;
    public int VisibleLines => Math.Max(1, _visibleLines);
    public int FirstVisibleLine => (int)(_scrollOffsetY / _lineHeight);
    public int LastVisibleLine => Math.Min(_lines.Length - 1, FirstVisibleLine + _visibleLines);

    public void ScrollTo(double offsetY, double offsetX = 0)
    {
        _scrollOffsetY = offsetY;
        _scrollOffsetX = offsetX;
        InvalidateVisual();
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
            DrawSelection(dc, l, y, textLeft, lineText.Length);

            // Search highlights
            DrawSearchHighlights(dc, l, y, textLeft, lineText.Length);

            // Text with syntax coloring
            DrawLineText(dc, l, lineText, y, textLeft);

            // Cursor
            DrawCursor(dc, l, y, textLeft, lineText);
        }

        // Gutter border
        if (_showLineNumbers)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), 1);
            dc.DrawLine(pen, new Point(lineNumGutter - 1, 0), new Point(lineNumGutter - 1, size.Height));
        }
    }

    private void DrawSelection(DrawingContext dc, int line, double y, double textLeft, int lineLen)
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
            int endCol = line == end.Line ? end.Column + 1 : lineLen;
            selLeft = textLeft + startCol * _charWidth - _scrollOffsetX;
            selWidth = (endCol - startCol) * _charWidth;
        }

        dc.DrawRectangle(Theme.SelectionBg, null, new Rect(selLeft, y, Math.Max(0, selWidth), _lineHeight));
    }

    private void DrawSearchHighlights(DrawingContext dc, int line, double y, double textLeft, int lineLen)
    {
        if (string.IsNullOrEmpty(_searchPattern)) return;
        foreach (var match in _searchMatches)
        {
            if (match.Line != line) continue;
            double hLeft = textLeft + match.Column * _charWidth - _scrollOffsetX;
            double hWidth = _searchPattern.Length * _charWidth;
            dc.DrawRectangle(Theme.SearchHighlightBg, null, new Rect(hLeft, y, hWidth, _lineHeight));
        }
    }

    private void DrawLineText(DrawingContext dc, int lineIndex, string lineText, double y, double textLeft)
    {
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
                    dc.DrawText(ft, new Point(textLeft + pos * _charWidth - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
                }
            }
            // Token text
            int end = Math.Min(tok.StartColumn + tok.Length, lineText.Length);
            if (tok.StartColumn < end)
            {
                var tokText = lineText[tok.StartColumn..end];
                var brush = Theme.GetTokenBrush(tok.Kind);
                var ft = FormatText(tokText, brush);
                dc.DrawText(ft, new Point(textLeft + tok.StartColumn * _charWidth - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
            }
            pos = Math.Max(pos, tok.StartColumn + tok.Length);
        }
        // Remaining text
        if (pos < lineText.Length)
        {
            var rem = lineText[pos..];
            var ft = FormatText(rem, Theme.Foreground);
            dc.DrawText(ft, new Point(textLeft + pos * _charWidth - _scrollOffsetX, y + (_lineHeight - ft.Height) / 2));
        }
    }

    private void DrawCursor(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (line != _cursor.Line || !_cursorVisible) return;

        double cursorX = textLeft + _cursor.Column * _charWidth - _scrollOffsetX;
        double cursorY = y;

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
            dc.DrawLine(pen, new Point(cursorX, cursorY + _lineHeight - 2), new Point(cursorX + _charWidth, cursorY + _lineHeight - 2));
        }
        else
        {
            // Block cursor
            dc.DrawRectangle(Theme.CursorBackground, null, new Rect(cursorX, cursorY, _charWidth, _lineHeight));
            if (_cursor.Column < lineText.Length)
            {
                var ch = lineText[_cursor.Column].ToString();
                var ft = FormatText(ch, Theme.CursorForeground);
                dc.DrawText(ft, new Point(cursorX, cursorY + (_lineHeight - ft.Height) / 2));
            }
        }
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
