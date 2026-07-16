using System.Windows;
using System.Windows.Media;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Editor.Core.Editing;

namespace Editor.Controls.Rendering;

/// <summary>
/// Draws the line-number/fold column and the changed-lines gutter bars for a single visual row.
/// Pure rendering — hit-testing (fold click, blame click, column resize) stays on
/// <see cref="EditorCanvas"/>. All per-row/per-document state is passed in by the caller.
/// </summary>
internal static class GutterRenderer
{
    private static readonly SolidColorBrush s_foldHoverBg =
        Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush s_blameHoverBg =
        Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>Line number text, fold indicator (▶/▼), and the 3px git-diff bar for one row.
    /// Caller only invokes this when the row isn't a wrapped continuation of the previous one.</summary>
    public static void DrawLineNumberAndFold(
        DrawingContext dc, EditorTheme theme, GlyphMetrics metrics,
        int line, double y,
        double blameColWidth, int bpColWidth, int lineNumWidth, double foldColWidth,
        int cursorLine, bool relativeNumber, int lineNumberWidth,
        IReadOnlySet<int> closedFoldStarts, IReadOnlySet<int> openFoldStarts, int hoveredFoldLine,
        IReadOnlyDictionary<int, GitLineState> gitDiff)
    {
        bool isCursorLine = line == cursorLine;
        var lineNumberBrush = isCursorLine ? theme.CurrentLineNumberFg : theme.LineNumberFg;
        string lineNumStr = relativeNumber && !isCursorLine
            ? Math.Abs(line - cursorLine).ToString().PadLeft(lineNumberWidth)
            : (line + 1).ToString().PadLeft(lineNumberWidth);
        var numText = metrics.FormatText(lineNumStr, lineNumberBrush);
        dc.DrawText(numText, new Point(blameColWidth + bpColWidth + 2, y + (metrics.LineHeight - numText.Height) / 2));

        bool isClosed = closedFoldStarts.Contains(line);
        bool isOpen = openFoldStarts.Contains(line);
        if (isClosed || isOpen)
        {
            bool hovered = hoveredFoldLine == line;
            var indicatorColor = hovered ? theme.Foreground : theme.LineNumberFg;
            double foldX = blameColWidth + bpColWidth + lineNumWidth;
            if (hovered)
                dc.DrawRectangle(s_foldHoverBg, null,
                    new Rect(foldX, y, foldColWidth, metrics.LineHeight));
            DrawFoldChevron(dc, indicatorColor, foldX, y, foldColWidth, metrics.LineHeight, metrics.CharWidth, isClosed, hovered);
        }

        if (gitDiff.TryGetValue(line, out var gitState) && gitState != GitLineState.None)
        {
            var gitBrush = gitState switch
            {
                GitLineState.Added    => theme.GitAdded,
                GitLineState.Modified => theme.GitModified,
                GitLineState.Deleted  => theme.GitDeleted,
                _                     => null
            };
            if (gitBrush != null)
                dc.DrawRectangle(gitBrush, null, new Rect(blameColWidth, y, 3, metrics.LineHeight));
        }
    }

    /// <summary>
    /// Draws a crisp, font-independent chevron in the fold column: a right-pointing "›" when the
    /// fold is closed, a downward "⌄" when open. Vector strokes (rounded caps/joins) render sharper
    /// than the "▶"/"▼" glyphs at any DPI and animate the closed→open rotation implicitly.
    /// </summary>
    private static void DrawFoldChevron(
        DrawingContext dc, Brush color,
        double foldX, double y, double foldColWidth, double lineHeight, double charWidth,
        bool closed, bool hovered)
    {
        double cx = foldX + foldColWidth / 2;
        double cy = y + lineHeight / 2;
        // Arm reach: scaled to the glyph cell but clamped so it never dominates a tall line.
        double reach = Math.Min(Math.Min(foldColWidth, lineHeight) * 0.30, charWidth * 0.55);
        double thickness = Math.Max(1.0, charWidth * (hovered ? 0.16 : 0.13));

        var pen = new Pen(color, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (closed)
            {
                // ">" — nudged right a touch so the visual mass sits centered in the cell.
                double x = cx - reach * 0.35;
                ctx.BeginFigure(new Point(x - reach * 0.55, cy - reach), false, false);
                ctx.LineTo(new Point(x + reach * 0.65, cy), true, true);
                ctx.LineTo(new Point(x - reach * 0.55, cy + reach), true, true);
            }
            else
            {
                // "v" — nudged down slightly to balance the open state against the line-number baseline.
                double yc = cy - reach * 0.25;
                ctx.BeginFigure(new Point(cx - reach, yc - reach * 0.45), false, false);
                ctx.LineTo(new Point(cx, yc + reach * 0.65), true, true);
                ctx.LineTo(new Point(cx + reach, yc - reach * 0.45), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    /// <summary>The 2px changed-since-save bar flush against the right edge of the gutter.</summary>
    public static void DrawSaveDiffBar(
        DrawingContext dc, EditorTheme theme, double lineHeight,
        int line, double y, double gutterWidth,
        IReadOnlyDictionary<int, SaveLineState> saveDiff)
    {
        if (gutterWidth <= 0) return;
        if (!saveDiff.TryGetValue(line, out var saveState) || saveState == SaveLineState.None) return;

        var saveBrush = saveState switch
        {
            SaveLineState.Added    => theme.GitAdded,
            SaveLineState.Modified => theme.GitModified,
            SaveLineState.Deleted  => theme.GitDeleted,
            _                      => null
        };
        if (saveBrush == null) return;

        const double saveBarW = 2;
        double saveBarX = gutterWidth - saveBarW;
        // Deletions removed lines above this one (the line itself is unchanged), so mark it
        // with a short notch at the top rather than a full-height bar.
        double barH = saveState == SaveLineState.Deleted
            ? Math.Min(lineHeight, lineHeight / 3 + 1)
            : lineHeight;
        dc.DrawRectangle(saveBrush, null, new Rect(saveBarX, y, saveBarW, barH));
    }

    /// <summary>blame 左カラムの1行分を描く（背景は折り返し継続行にも、注釈テキストは先頭セグメントだけ）。
    /// ホバー中の行はハイライト＋前景色でクリックできることを示す。</summary>
    public static void DrawBlameMargin(
        DrawingContext dc, EditorTheme theme, GlyphMetrics metrics,
        IReadOnlyDictionary<int, EditorBlameLine> blameLines, int hoveredBlameLine,
        int lineIndex, double y, double x, double blameColWidth, bool drawText)
    {
        dc.DrawRectangle(theme.LineNumberBg, null, new Rect(x, y, blameColWidth, metrics.LineHeight));
        if (!drawText || !blameLines.TryGetValue(lineIndex, out var blame)) return;

        bool hovered = lineIndex == hoveredBlameLine;
        if (hovered)
            dc.DrawRectangle(s_blameHoverBg, null, new Rect(x, y, blameColWidth, metrics.LineHeight));
        var ft = metrics.FormatText(blame.Display, hovered ? theme.Foreground : theme.LineNumberFg);
        // ユーザーがカラムを狭めたとき、注釈がブレークポイント列以降へはみ出さないようクリップする
        dc.PushClip(new RectangleGeometry(new Rect(x, y, blameColWidth, metrics.LineHeight)));
        dc.DrawText(ft, new Point(x + 6, y + (metrics.LineHeight - ft.Height) / 2));
        dc.Pop();
    }
}
