using System.Windows;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Editing;

namespace Editor.Controls.Rendering;

/// <summary>
/// Draws the whole-viewport overlays and per-line markup that aren't gutter or LSP concerns:
/// minimap, overlay scrollbars, color column guide, indent guides, whitespace glyphs, and inline
/// color-preview swatches. Pure rendering; scrollbar geometry (<see cref="ComputeScrollbarLayout"/>)
/// is also reused by EditorCanvas's mouse hit-testing/dragging, which stays there.
/// </summary>
internal static class OverlayRenderer
{
    private static readonly Pen s_swatchBorderPen = FreezePen(new Pen(Freeze(new SolidColorBrush(Colors.Black)), 0.75));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    public static void DrawMinimap(
        DrawingContext dc, EditorTheme theme, string[] lines,
        double lineHeight, double scrollOffsetY, int visibleLines, double minimapWidth, Size size)
    {
        if (lineHeight <= 0 || lines.Length == 0) return;

        double mmLeft = size.Width - minimapWidth;

        dc.DrawRectangle(theme.MinimapBackground, null, new Rect(mmLeft, 0, minimapWidth, size.Height));

        int totalLines = lines.Length;
        // Each buffer line is represented as a tiny strip
        double lineStripH = Math.Max(1.0, Math.Min(2.0, size.Height / totalLines));
        double totalMapH  = totalLines * lineStripH;

        // Scroll the minimap so the viewport center stays centred when file is larger than minimap
        double viewportTopLine    = scrollOffsetY / lineHeight;
        double viewportBottomLine = viewportTopLine + visibleLines;
        double viewportCentreLine = (viewportTopLine + viewportBottomLine) / 2.0;

        // Offset so the center of the visible region maps to the center of the minimap
        double mapOffsetY = 0;
        if (totalMapH > size.Height)
        {
            double idealCentre = viewportCentreLine * lineStripH;
            mapOffsetY = Math.Clamp(idealCentre - size.Height / 2.0, 0, totalMapH - size.Height);
        }

        // Foreground brush at low opacity for content strips
        var fgColor = (theme.Foreground is SolidColorBrush sb) ? sb.Color : Color.FromRgb(0xCC, 0xCC, 0xCC);
        var contentBrush = new SolidColorBrush(Color.FromArgb(0x50, fgColor.R, fgColor.G, fgColor.B));

        // Draw each line as a colored block proportional to line length
        for (int l = 0; l < totalLines; l++)
        {
            double y = l * lineStripH - mapOffsetY;
            if (y + lineStripH < 0 || y > size.Height) continue;

            int lineLen = lines[l].Length;
            if (lineLen == 0) continue;

            // Cap line width at minimap width, scale proportionally to a reasonable max (120 chars)
            double lineW = Math.Min(minimapWidth - 4, lineLen / 120.0 * (minimapWidth - 4));
            if (lineW < 1) lineW = 1;

            dc.DrawRectangle(contentBrush, null, new Rect(mmLeft + 2, y, lineW, Math.Max(1, lineStripH - 0.5)));
        }

        // Viewport highlight rectangle
        double vpTop = viewportTopLine * lineStripH - mapOffsetY;
        double vpH   = visibleLines * lineStripH;
        vpTop = Math.Clamp(vpTop, 0, size.Height);
        vpH   = Math.Min(vpH, size.Height - vpTop);
        if (vpH > 0)
            dc.DrawRectangle(theme.MinimapViewport, null, new Rect(mmLeft, vpTop, minimapWidth, vpH));
    }

    public const double ScrollbarSize = 6.0;
    private const double ScrollbarThumbMinSize = 20.0;

    /// <summary>Geometry of the overlay scrollbars, shared by rendering and mouse hit-testing.</summary>
    public struct ScrollbarLayout
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

    public static ScrollbarLayout ComputeScrollbarLayout(
        Size size, bool wrapLines,
        double totalContentHeight, double totalContentWidth, double maxScrollOffsetY,
        double scrollOffsetY, double scrollOffsetX)
    {
        var l = new ScrollbarLayout();
        double totalH = totalContentHeight;
        double viewH  = size.Height;
        double totalW = totalContentWidth;
        double viewW  = size.Width;

        l.NeedVert  = totalH > viewH + 1;
        l.NeedHoriz = !wrapLines && totalW > viewW + 1;
        if (!l.NeedVert && !l.NeedHoriz) return l;

        // Reserve space so the two bars don't overlap at the corner
        l.VertTrackH  = l.NeedHoriz ? size.Height - ScrollbarSize : size.Height;
        l.HorizTrackW = l.NeedVert  ? size.Width  - ScrollbarSize : size.Width;

        if (l.NeedVert)
        {
            l.VertThumbH = Math.Min(l.VertTrackH, Math.Max(ScrollbarThumbMinSize, l.VertTrackH * viewH / totalH));
            l.VertMaxOff = maxScrollOffsetY;
            l.VertThumbY = l.VertMaxOff > 0 ? (l.VertTrackH - l.VertThumbH) * (scrollOffsetY / l.VertMaxOff) : 0;
        }
        if (l.NeedHoriz)
        {
            l.HorizThumbW = Math.Min(l.HorizTrackW, Math.Max(ScrollbarThumbMinSize, l.HorizTrackW * viewW / totalW));
            l.HorizMaxOff = totalW - viewW;
            l.HorizThumbX = l.HorizMaxOff > 0 ? (l.HorizTrackW - l.HorizThumbW) * (scrollOffsetX / l.HorizMaxOff) : 0;
        }
        return l;
    }

    public static void DrawScrollbars(DrawingContext dc, EditorTheme theme, Size size, ScrollbarLayout l)
    {
        if (!l.NeedVert && !l.NeedHoriz) return;

        if (l.NeedVert && l.VertTrackH > 0)
        {
            double trackX = size.Width - ScrollbarSize;
            dc.DrawRectangle(theme.ScrollbarTrack, null, new Rect(trackX, 0, ScrollbarSize, l.VertTrackH));
            dc.DrawRectangle(theme.ScrollbarThumb, null, new Rect(trackX + 1, l.VertThumbY + 1, ScrollbarSize - 2, Math.Max(0, l.VertThumbH - 2)));
        }

        if (l.NeedHoriz && l.HorizTrackW > 0)
        {
            double trackY = size.Height - ScrollbarSize;
            dc.DrawRectangle(theme.ScrollbarTrack, null, new Rect(0, trackY, l.HorizTrackW, ScrollbarSize));
            dc.DrawRectangle(theme.ScrollbarThumb, null, new Rect(l.HorizThumbX + 1, trackY + 1, Math.Max(0, l.HorizThumbW - 2), ScrollbarSize - 2));
        }
    }

    public static void DrawColorColumn(
        DrawingContext dc, EditorTheme theme, int colorColumn, double charWidth,
        double gutterWidth, double scrollOffsetX, Size size)
    {
        if (colorColumn <= 0 || charWidth <= 0) return;
        double ccX = gutterWidth + (colorColumn - 1) * charWidth - scrollOffsetX;
        if (ccX < gutterWidth || ccX >= size.Width) return;
        var ccPen = new Pen(theme.ColorColumnBrush, 1);
        dc.DrawLine(ccPen, new Point(ccX, 0), new Point(ccX, size.Height));
    }

    /// <summary>Vertical guide lines at each indent level up to <paramref name="maxIndentLevel"/>
    /// (computed by the caller by scanning visible lines).</summary>
    public static void DrawIndentGuides(
        DrawingContext dc, EditorTheme theme, double gutterWidth, double indentWidth,
        int maxIndentLevel, double scrollOffsetX, Size size)
    {
        var guidePen = new Pen(theme.IndentGuideBrush, 1);
        for (int level = 1; level <= maxIndentLevel; level++)
        {
            double x = gutterWidth + level * indentWidth - scrollOffsetX;
            if (x < gutterWidth || x >= size.Width) continue;
            dc.DrawLine(guidePen, new Point(x, 0), new Point(x, size.Height));
        }
    }

    public static void DrawWhitespaceIssues(
        DrawingContext dc, EditorTheme theme, GlyphMetrics metrics,
        int line, double y, double textLeft, string lineText, double scrollOffsetX,
        IReadOnlyDictionary<int, List<WhitespaceIssue>> whitespaceIssues)
    {
        if (!whitespaceIssues.TryGetValue(line, out var issues)) return;
        foreach (var issue in issues)
        {
            var brush = issue.Kind == WhitespaceIssueKind.FullWidthSpace
                ? theme.FullWidthSpaceBackground
                : theme.TrailingWhitespaceBackground;
            double hLeft  = textLeft + metrics.GetVisualX(lineText, issue.Start) - scrollOffsetX;
            double hWidth = metrics.GetVisualX(lineText, issue.End) - metrics.GetVisualX(lineText, issue.Start);
            dc.DrawRectangle(brush, null, new Rect(hLeft, y, Math.Max(0, hWidth), metrics.LineHeight));
        }
    }

    public static void DrawColorSwatches(
        DrawingContext dc, GlyphMetrics metrics, string lineText, double y, double textLeft, double scrollOffsetX)
    {
        if (string.IsNullOrEmpty(lineText)) return;

        double swatchSize = Math.Max(8, metrics.LineHeight - 4);
        double swatchY    = y + (metrics.LineHeight - swatchSize) / 2;

        for (int i = 0; i < lineText.Length; i++)
        {
            char c = lineText[i];
            if (c != '#' && c != 'r' && c != 'R') continue;

            if (!ColorParser.TryParseColor(lineText, i, out var color, out int matchLen))
                continue;

            // Position swatch right after the color text
            double textX = textLeft + metrics.GetVisualX(lineText, i + matchLen) - scrollOffsetX;
            double swatchX = textX + 2; // small gap

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var swatchRect = new Rect(swatchX, swatchY, swatchSize, swatchSize);
            dc.DrawRectangle(brush, s_swatchBorderPen, swatchRect);

            // Advance past the matched token so we don't re-scan its interior
            i += matchLen - 1;
        }
    }
}
