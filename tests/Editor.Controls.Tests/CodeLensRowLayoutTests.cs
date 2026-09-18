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
