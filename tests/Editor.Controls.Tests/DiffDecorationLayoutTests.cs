using Editor.Controls.Rendering;
using Editor.Core.Editing;
using Editor.Core.Models;

namespace Editor.Controls.Tests;

/// <summary>
/// 左右並びの差分表示の空き行（<see cref="DiffDecorations.SpacersBefore"/>）。本文を持たない表示だけの行で、
/// 反対側の行と高さを揃えるためにある。バッファ行と表示行の対応（キャレット・スクロール目標）が
/// 空き行のぶんだけ正しくずれ、空き行そのものにはキャレットが乗らないことを確かめる。
/// </summary>
public sealed class DiffDecorationLayoutTests
{
    private static DiffDecorations Spacers(params (int Line, int Count)[] spacers)
        => new(new Dictionary<int, DiffDecorationKind>(), spacers.ToDictionary(s => s.Line, s => s.Count));

    private static EditorCanvas Canvas()
    {
        var canvas = new EditorCanvas();
        canvas.SetLines(["a", "b", "c", "d"]);
        return canvas;
    }

    [Fact]
    public void Spacers_push_the_following_line_down()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas();

            canvas.SetDiffDecorations(Spacers((2, 3)));

            Assert.Equal(7, canvas.VisualLineCount);
            Assert.Equal(1, canvas.GetTextVisualLine(1));
            Assert.Equal(5, canvas.GetTextVisualLine(2));
            Assert.Equal(6, canvas.GetTextVisualLine(3));
        });
    }

    [Fact]
    public void Spacers_at_the_line_count_go_after_the_last_line()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas();

            canvas.SetDiffDecorations(Spacers((4, 2)));

            Assert.Equal(6, canvas.VisualLineCount);
            Assert.Equal(3, canvas.GetTextVisualLine(3));
        });
    }

    /// <summary>編集で行が減り、装飾が行数より先の番号を指したままでも、空き行は末尾に残る
    /// （ホストが差分を取り直すまでの間に、行の高さの合計が揺れないように）。</summary>
    [Fact]
    public void Spacers_beyond_the_line_count_are_kept_at_the_end()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas();

            canvas.SetDiffDecorations(Spacers((9, 2)));

            Assert.Equal(6, canvas.VisualLineCount);
        });
    }

    [Fact]
    public void Cursor_is_drawn_on_the_text_row_not_on_a_spacer()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas();
            canvas.SetDiffDecorations(Spacers((2, 3)));

            canvas.SetCursor(new CursorPosition(2, 0));

            Assert.Equal(canvas.GetTextVisualLine(2) * canvas.LineHeight,
                canvas.GetCursorPixelPosition().Y + canvas.VerticalOffset);
        });
    }

    [Fact]
    public void Clearing_the_decorations_restores_the_plain_layout()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas();
            canvas.SetDiffDecorations(Spacers((0, 2), (4, 1)));
            Assert.Equal(7, canvas.VisualLineCount);

            canvas.SetDiffDecorations(null);

            Assert.Equal(4, canvas.VisualLineCount);
            Assert.Equal(0, canvas.GetTextVisualLine(0));
        });
    }

    [Fact]
    public void Line_kinds_alone_do_not_change_the_layout()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas();

            canvas.SetDiffDecorations(new DiffDecorations(
                new Dictionary<int, DiffDecorationKind> { [1] = DiffDecorationKind.Added },
                new Dictionary<int, int>()));

            Assert.Equal(4, canvas.VisualLineCount);
            Assert.Equal(DiffDecorationKind.Added, canvas.DiffDecorations.Lines[1]);
        });
    }

    [Fact]
    public void Spacers_and_code_lens_rows_stack_above_the_line()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = Canvas();
            canvas.SetCodeLenses([
                new Editor.Core.Lsp.LspCodeLens(
                    new Editor.Core.Lsp.LspRange(new Editor.Core.Lsp.LspPosition(2, 0), new Editor.Core.Lsp.LspPosition(2, 0)),
                    RawJson: "{}")
            ]);

            canvas.SetDiffDecorations(Spacers((2, 1)));

            // 空き行 → 注釈行 → 本文の順（行2は表示行 1+1+1 = 3 の次）。
            Assert.Equal(6, canvas.VisualLineCount);
            Assert.Equal(4, canvas.GetTextVisualLine(2));
        });
    }
}
