using System.Windows;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Lsp;
using Editor.Core.Models;

namespace Editor.Controls.Rendering;

/// <summary>
/// Draws the floating LSP popups (signature help, completion, code actions), inline inlay hints,
/// and document-highlight backgrounds. Pure rendering — popup positioning uses the cursor pixel
/// position already computed by <see cref="EditorCanvas.GetCursorPixelPosition"/> and passed in.
/// </summary>
internal static class LspOverlayRenderer
{
    private static readonly SolidColorBrush s_inlayHintBg =
        Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0xAA)));
    private static readonly SolidColorBrush s_inlayHintFg =
        Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xAA, 0xAA, 0xCC)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public static void DrawInlayHints(
        DrawingContext dc, GlyphMetrics metrics,
        IReadOnlyDictionary<int, List<InlayHint>> inlayHintsByLine,
        int lineIndex, string lineText, double y, double textLeft, double scrollOffsetX)
    {
        if (inlayHintsByLine.Count == 0) return;
        if (!inlayHintsByLine.TryGetValue(lineIndex, out var hints)) return;

        foreach (var hint in hints)
        {
            int col = Math.Min(hint.Position.Character, lineText.Length);
            double xBase = textLeft + metrics.GetVisualX(lineText, col) - scrollOffsetX;

            var ft = metrics.FormatScaledText(hint.Label, s_inlayHintFg, 0.85);

            double hintY = y + (metrics.LineHeight - ft.Height) / 2;

            // Draw a subtle background pill
            var bgRect = new Rect(xBase - 1, hintY - 1, ft.Width + 2, ft.Height + 2);
            dc.DrawRectangle(s_inlayHintBg, null, bgRect);
            dc.DrawText(ft, new Point(xBase, hintY));
        }
    }

    public static void DrawDocumentHighlights(
        DrawingContext dc, EditorTheme theme, GlyphMetrics metrics,
        IReadOnlyList<DocumentHighlight>? documentHighlights,
        int line, double y, double textLeft, string lineText, double scrollOffsetX)
    {
        if (documentHighlights is null || documentHighlights.Count == 0) return;
        foreach (var hl in documentHighlights)
        {
            if (hl.Range.Start.Line != line && hl.Range.End.Line != line
                && !(hl.Range.Start.Line < line && hl.Range.End.Line > line)) continue;

            int startCol = hl.Range.Start.Line == line ? hl.Range.Start.Character : 0;
            int endCol   = hl.Range.End.Line   == line ? hl.Range.End.Character   : lineText.Length;
            startCol = Math.Clamp(startCol, 0, lineText.Length);
            endCol   = Math.Clamp(endCol,   0, lineText.Length);
            if (endCol <= startCol) continue;

            double hLeft  = textLeft + metrics.GetVisualX(lineText, startCol) - scrollOffsetX;
            double hWidth = metrics.GetVisualX(lineText, endCol) - metrics.GetVisualX(lineText, startCol);
            dc.DrawRectangle(theme.DocumentHighlightBackground, null, new Rect(hLeft, y, Math.Max(0, hWidth), metrics.LineHeight));
        }
    }

    public static void DrawSignatureHelp(
        DrawingContext dc, EditorTheme theme, GlyphMetrics metrics,
        LspSignatureHelp? signatureHelp, double textLeft, Size size, Point cursor)
    {
        if (signatureHelp == null || signatureHelp.Signatures.Count == 0) return;

        int sigIdx = Math.Clamp(signatureHelp.ActiveSignature, 0, signatureHelp.Signatures.Count - 1);
        var sig = signatureHelp.Signatures[sigIdx];
        if (string.IsNullOrEmpty(sig.Label)) return;

        int activeParam = signatureHelp.ActiveParameter;

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

        var ftBefore = metrics.FormatText(before, theme.Foreground);
        var ftHl     = string.IsNullOrEmpty(hl) ? null : metrics.FormatText(hl, theme.TokenKeyword);
        var ftAfter  = metrics.FormatText(after, theme.Foreground);

        double totalW = ftBefore.Width + (ftHl?.Width ?? 0) + ftAfter.Width + padX * 2;
        double totalH = metrics.LineHeight + padY * 2;

        double x = cursor.X;
        // Prefer above the cursor; fall back to below if no room
        double y = cursor.Y - totalH - 2;
        if (y < 0) y = cursor.Y + metrics.LineHeight + 2;
        if (x + totalW > size.Width) x = Math.Max(textLeft, size.Width - totalW - 2);

        dc.DrawRectangle(PopupChrome.Bg1, PopupChrome.Border, new Rect(x, y, totalW, totalH));

        double tx = x + padX;
        double ty = y + padY + (metrics.LineHeight - ftBefore.Height) / 2;

        dc.DrawText(ftBefore, new Point(tx, ty));
        tx += ftBefore.Width;
        if (ftHl != null)
        {
            dc.DrawText(ftHl, new Point(tx, ty));
            tx += ftHl.Width;
        }
        dc.DrawText(ftAfter, new Point(tx, ty));
    }

    public static void DrawCompletionPopup(
        DrawingContext dc, EditorTheme theme, GlyphMetrics metrics,
        IReadOnlyList<LspCompletionItem> completionItems, int completionSelection, int completionScrollOffset,
        double textLeft, Size size, Point cursor)
    {
        if (completionItems.Count == 0) return;

        const int maxVisible = 10;
        int scrollOffset = Math.Max(0, Math.Min(completionScrollOffset, completionItems.Count - 1));
        int count = Math.Min(maxVisible, completionItems.Count - scrollOffset);

        var texts = new FormattedText[count];
        double maxW = 0;
        for (int i = 0; i < count; i++)
        {
            var item = completionItems[scrollOffset + i];
            var label = item.Detail != null ? $"{item.Label}  {item.Detail}" : item.Label;
            var ft = metrics.FormatText(label, theme.Foreground);
            texts[i] = ft;
            maxW = Math.Max(maxW, ft.Width);
        }

        double rowH   = Math.Max(metrics.LineHeight, texts.Max(static t => t.Height));
        double padX   = 8;
        double padY   = 4;
        double popupW = maxW + padX * 2 + 24; // 24px for kind icon column
        double popupH = rowH * count + padY * 2;

        double x = cursor.X;
        double y = cursor.Y + metrics.LineHeight + 2;

        if (x + popupW > size.Width)  x = Math.Max(textLeft, size.Width - popupW - 2);
        if (y + popupH > size.Height) y = Math.Max(0, cursor.Y - popupH - 2);

        dc.DrawRectangle(PopupChrome.Bg2, PopupChrome.Border, new Rect(x, y, popupW, popupH));

        for (int i = 0; i < count; i++)
        {
            int itemIndex = scrollOffset + i;
            double rowY = y + padY + rowH * i;
            if (itemIndex == completionSelection)
                dc.DrawRectangle(theme.SelectionBg, null, new Rect(x + 1, rowY, popupW - 2, rowH));

            // Kind indicator dot
            var kindBrush = GetCompletionKindBrush(theme, completionItems[itemIndex].Kind);
            dc.DrawEllipse(kindBrush, null, new Point(x + padX + 4, rowY + rowH / 2), 4, 4);

            dc.DrawText(texts[i], new Point(x + padX + 16, rowY + (rowH - texts[i].Height) / 2));
        }

        // Documentation panel for selected item
        if (completionSelection >= 0 && completionSelection < completionItems.Count)
        {
            var selectedItem = completionItems[completionSelection];
            var docText = selectedItem.Documentation;
            if (!string.IsNullOrWhiteSpace(docText))
            {
                const double docPadX = 10;
                const double docPadY = 8;
                const double docMaxW = 320;
                const double docMaxH = 200;

                // Word-wrap documentation text
                var docLines = WrapDocText(metrics, theme, docText, docMaxW - docPadX * 2);
                double docLineH = metrics.LineHeight;
                double docW = docLines.Max(l => l.Width) + docPadX * 2;
                double docH = Math.Min(docMaxH, docLines.Count * docLineH + docPadY * 2);

                double docX = x + popupW + 2;
                double docY = y;

                // Flip to left if no room on right
                if (docX + docW > size.Width)
                    docX = x - docW - 2;

                dc.DrawRectangle(PopupChrome.DocBg, PopupChrome.Border, new Rect(docX, docY, docW, docH));

                // Clip doc text to panel
                dc.PushClip(new RectangleGeometry(new Rect(docX + 1, docY + 1, docW - 2, docH - 2)));
                int maxLines = (int)((docH - docPadY * 2) / docLineH);
                for (int i = 0; i < Math.Min(maxLines, docLines.Count); i++)
                    dc.DrawText(docLines[i], new Point(docX + docPadX, docY + docPadY + i * docLineH));
                dc.Pop();
            }
        }
    }

    private static List<FormattedText> WrapDocText(GlyphMetrics metrics, EditorTheme theme, string text, double maxWidth)
    {
        var result = new List<FormattedText>();
        var paragraphs = text.Replace("\r\n", "\n").Split('\n');
        foreach (var para in paragraphs)
        {
            if (string.IsNullOrEmpty(para)) { result.Add(metrics.FormatText("", theme.TokenComment)); continue; }
            var words = para.Split(' ');
            var line = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                var test = line.Length == 0 ? word : line + " " + word;
                var ft = metrics.FormatText(test, theme.TokenComment);
                if (ft.Width <= maxWidth || line.Length == 0)
                    line.Clear().Append(test);
                else
                {
                    result.Add(metrics.FormatText(line.ToString(), theme.TokenComment));
                    line.Clear().Append(word);
                }
            }
            if (line.Length > 0)
                result.Add(metrics.FormatText(line.ToString(), theme.TokenComment));
        }
        return result;
    }

    private static Brush GetCompletionKindBrush(EditorTheme theme, CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Class or CompletionItemKind.Interface => theme.TokenType,
        CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => theme.TokenKeyword,
        CompletionItemKind.Field or CompletionItemKind.Property or CompletionItemKind.Variable => theme.TokenAttribute,
        CompletionItemKind.Keyword => theme.TokenKeyword,
        CompletionItemKind.Snippet => theme.TokenString,
        _ => theme.TokenIdentifier
    };

    public static void DrawCodeActionPopup(
        DrawingContext dc, EditorTheme theme, GlyphMetrics metrics,
        IReadOnlyList<LspCodeAction> codeActionItems, int codeActionsSelection, int codeActionsScrollOffset,
        double textLeft, Size size, Point cursor)
    {
        if (codeActionItems.Count == 0) return;

        const int maxVisible = 10;
        int scrollOffset = Math.Max(0, Math.Min(codeActionsScrollOffset, codeActionItems.Count - 1));
        int count = Math.Min(maxVisible, codeActionItems.Count - scrollOffset);

        var texts = new FormattedText[count];
        double maxW = 0;
        for (int i = 0; i < count; i++)
        {
            var action = codeActionItems[scrollOffset + i];
            var label = action.Kind != null ? $"[{action.Kind}]  {action.Title}" : action.Title;
            var ft = metrics.FormatText(label, theme.Foreground);
            texts[i] = ft;
            maxW = Math.Max(maxW, ft.Width);
        }

        double rowH   = Math.Max(metrics.LineHeight, texts.Max(static t => t.Height));
        double padX   = 8;
        double padY   = 4;
        double popupW = maxW + padX * 2 + 16;
        double popupH = rowH * count + padY * 2;

        double x = cursor.X;
        double y = cursor.Y - popupH - 2;  // prefer above cursor
        if (y < 0) y = cursor.Y + metrics.LineHeight + 2;  // fall back to below

        if (x + popupW > size.Width) x = Math.Max(textLeft, size.Width - popupW - 2);

        dc.DrawRectangle(PopupChrome.Bg2, PopupChrome.Border, new Rect(x, y, popupW, popupH));

        for (int i = 0; i < count; i++)
        {
            int itemIndex = scrollOffset + i;
            double rowY = y + padY + rowH * i;
            if (itemIndex == codeActionsSelection)
                dc.DrawRectangle(theme.SelectionBg, null, new Rect(x + 1, rowY, popupW - 2, rowH));
            dc.DrawText(texts[i], new Point(x + padX, rowY + (rowH - texts[i].Height) / 2));
        }
    }
}
