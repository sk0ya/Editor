using Editor.Controls.Rendering;
using Editor.Core.Lsp;
using Editor.Core.Models;

namespace Editor.Controls.Tests;

/// <summary>
/// CodeLensは宣言行の「直上の注釈行」に出す。行末へ重ねていた頃と違い表示行が1行増えるので、
/// バッファ行との対応（＝キャレット位置とスクロール目標）がずれないことを確かめる。
/// </summary>
public sealed class CodeLensRowLayoutTests
{
    private static EditorCanvas CanvasWithLens(int lensLine, out EditorCanvas canvas)
    {
        canvas = new EditorCanvas();
        canvas.SetLines(["class C", "{", "    void M() { }", "}"]);
        canvas.SetCodeLenses([
            new LspCodeLens(
                new LspRange(new LspPosition(lensLine, 4), new LspPosition(lensLine, 4)),
                new LspCodeActionCommand("editor.action.showReferences", "2 個の参照"))
        ]);
        return canvas;
    }

    [Fact]
    public void Lens_line_gets_its_own_row_above_the_declaration()
    {
        WpfTestHost.Run(() =>
        {
            CanvasWithLens(2, out var canvas);

            // 注釈行のぶんだけ表示行が1行増え、宣言行はその1行下へ送られる。
            Assert.Equal(5, canvas.VisualLineCount);
            Assert.Equal(3, canvas.GetTextVisualLine(2));
            Assert.Equal(1, canvas.GetTextVisualLine(1));
            Assert.Equal(4, canvas.GetTextVisualLine(3));
        });
    }

    [Fact]
    public void Cursor_stays_on_the_declaration_row_not_the_annotation_row()
    {
        WpfTestHost.Run(() =>
        {
            CanvasWithLens(2, out var canvas);

            canvas.SetCursor(new CursorPosition(2, 9));

            // キャレットのY（= ビジュアル行 3）は注釈行ではなく本文行を指す。
            Assert.Equal(canvas.GetTextVisualLine(2) * canvas.LineHeight,
                canvas.GetCursorPixelPosition().Y + canvas.VerticalOffset);
        });
    }

    /// <summary>未解決のレンズでも行は確保され、解決済みが届いても<b>行レイアウトは動かない</b>。
    /// これが「開いた直後に本文がずれる」を消している当のふるまい。</summary>
    [Fact]
    public void Unresolved_lens_reserves_the_row_and_resolving_moves_nothing()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = new EditorCanvas();
            canvas.SetLines(["class C", "{", "    void M() { }", "}"]);
            var range = new LspRange(new LspPosition(2, 4), new LspPosition(2, 4));

            canvas.SetCodeLenses([new LspCodeLens(range, RawJson: "{}")]);
            Assert.Equal(5, canvas.VisualLineCount);
            Assert.Equal(3, canvas.GetTextVisualLine(2));

            canvas.SetCodeLenses([
                new LspCodeLens(range, new LspCodeActionCommand("editor.action.showReferences", "2 個の参照"))
            ]);
            Assert.Equal(5, canvas.VisualLineCount);
            Assert.Equal(3, canvas.GetTextVisualLine(2));
        });
    }

    /// <summary>画面より<b>上</b>に注釈行が入っても、見えている行はその場に留まる。スクロール位置は
    /// 画素なので、留めないとタブのスクロール位置を復元して開いた瞬間に読んでいる場所ごと流れる。</summary>
    [Fact]
    public void Lens_rows_above_the_viewport_do_not_move_the_visible_line()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = new EditorCanvas();
            canvas.SetLines(Enumerable.Range(0, 60).Select(i => $"line {i}").ToArray());
            canvas.ScrollTo(30 * canvas.LineHeight);
            Assert.Equal(30, canvas.GetTextVisualLine(30));

            // 画面の上（0〜20行目）に3つ、注釈行が挿さる。
            canvas.SetCodeLenses([
                Lens(3), Lens(10), Lens(20),
            ]);

            // 行番号としては3行下がるが、見えている行は同じ高さのまま。
            Assert.Equal(33, canvas.GetTextVisualLine(30));
            Assert.Equal(33 * canvas.LineHeight, canvas.VerticalOffset);

            static LspCodeLens Lens(int line) => new(
                new LspRange(new LspPosition(line, 0), new LspPosition(line, 0)),
                new LspCodeActionCommand("editor.action.showReferences", "2 個の参照"));
        });
    }

    [Fact]
    public void Removing_the_lenses_restores_the_original_row_count()
    {
        WpfTestHost.Run(() =>
        {
            CanvasWithLens(2, out var canvas);
            Assert.Equal(5, canvas.VisualLineCount);

            canvas.SetCodeLenses([]);

            Assert.Equal(4, canvas.VisualLineCount);
            Assert.Equal(2, canvas.GetTextVisualLine(2));
        });
    }
}
