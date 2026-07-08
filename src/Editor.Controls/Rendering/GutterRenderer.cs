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
            if (hovered)
                dc.DrawRectangle(s_foldHoverBg, null,
                    new Rect(blameColWidth + bpColWidth + lineNumWidth, y, foldColWidth, metrics.LineHeight));
            var marker = metrics.FormatText(isClosed ? "▶" : "▼", indicatorColor);
            double mx = blameColWidth + bpColWidth + lineNumWidth + (foldColWidth - marker.Width) / 2;
            dc.DrawText(marker, new Point(mx, y + (metrics.LineHeight - marker.Height) / 2));
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
