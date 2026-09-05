using System.Windows;
using Editor.Controls.Rendering;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

/// <summary>本文ホバー（マウスを乗せると型と説明が出る）の当たり判定。</summary>
public class EditorCanvasTextHoverTests
{
    [Fact]
    public void HitTestWord_OverIdentifier_ReturnsWholeWord()
    {
        WithCanvas(canvas =>
        {
            // "var count = 1;" の "count"（5〜9 桁）の真ん中を指す。
            var point = PointAt(canvas, line: 0, column: 6);

            Assert.True(canvas.TryHitTestWord(point, out int line, out int start, out int end));
            Assert.Equal(0, line);
            Assert.Equal(4, start);
            Assert.Equal(8, end);
        });
    }

    /// <summary>行末より右の余白は「文字の上」ではない。<c>HitTest</c> は最終桁へ丸めるので、
    /// これを弾かないと空白に乗せただけで最後の語のツールチップが出る。</summary>
    [Fact]
    public void HitTestWord_PastEndOfLine_ReturnsFalse()
    {
        WithCanvas(canvas =>
        {
            var point = PointAt(canvas, line: 0, column: 40);

            Assert.False(canvas.TryHitTestWord(point, out _, out _, out _));
        });
    }

    [Fact]
    public void HitTestWord_BelowLastLine_ReturnsFalse()
    {
        WithCanvas(canvas =>
        {
            var point = PointAt(canvas, line: 20, column: 2);

            Assert.False(canvas.TryHitTestWord(point, out _, out _, out _));
        });
    }

    [Fact]
    public void HitTestWord_OverPunctuation_ReturnsFalse()
    {
        WithCanvas(canvas =>
        {
            // "var count = 1;" の "=" の位置。
            var point = PointAt(canvas, line: 0, column: 10);

            Assert.False(canvas.TryHitTestWord(point, out _, out _, out _));
        });
    }

    [Fact]
    public void HitTestWord_InGutter_ReturnsFalse()
    {
        WithCanvas(canvas =>
        {
            Assert.False(canvas.TryHitTestWord(new Point(2, 2), out _, out _, out _));
        });
    }

    [Fact]
    public void DiagnosticsAt_ReturnsOnlyTheOnesCoveringThePosition()
    {
        WithCanvas(canvas =>
        {
            var onCount = new LspDiagnostic(
                new LspRange(new LspPosition(0, 4), new LspPosition(0, 9)),
                "CS0219: 値が使われていません", DiagnosticSeverity.Warning, "csharp", "CS0219");
            var elsewhere = new LspDiagnostic(
                new LspRange(new LspPosition(1, 0), new LspPosition(1, 3)),
                "他の行", DiagnosticSeverity.Error);
            canvas.SetDiagnostics([onCount, elsewhere]);

            Assert.Equal([onCount], canvas.DiagnosticsAt(0, 6));
            Assert.Empty(canvas.DiagnosticsAt(0, 11));
            Assert.Equal([elsewhere], canvas.DiagnosticsAt(1, 1));
        });
    }

    /// <summary>複数行にまたがる診断では、終端行の桁を勝手に広げない——広げると最終行の先頭が
    /// 常に「診断の中」になり、無関係な語のホバーに他所のエラーが出る。</summary>
    [Fact]
    public void DiagnosticsAt_MultiLineRange_StopsAtTheEndColumn()
    {
        WithCanvas(canvas =>
        {
            var spanning = new LspDiagnostic(
                new LspRange(new LspPosition(0, 4), new LspPosition(1, 3)),
                "複数行にまたがる診断", DiagnosticSeverity.Error);
            canvas.SetDiagnostics([spanning]);

            Assert.Equal([spanning], canvas.DiagnosticsAt(0, 6));   // 開始行は開始桁から中
            Assert.Equal([spanning], canvas.DiagnosticsAt(1, 2));   // 終端行は終了桁の手前まで
            Assert.Empty(canvas.DiagnosticsAt(1, 3));               // 終了桁ちょうどは外
            Assert.Empty(canvas.DiagnosticsAt(1, 8));
        });
    }

    /// <summary>桁 <paramref name="column"/> の中心を指すキャンバス相対座標。</summary>
    private static Point PointAt(EditorCanvas canvas, int line, int column)
    {
        var origin = canvas.GetCursorPixelPosition();   // 桁 0 のカーソル位置＝本文左端
        return new Point(
            origin.X + (canvas.CharWidth * column) + (canvas.CharWidth / 2),
            (canvas.LineHeight * line) + (canvas.LineHeight / 2));
    }

    private static void WithCanvas(Action<EditorCanvas> test) => WpfTestHost.Run(() =>
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(["var count = 1;", "Run(count);", ""]);
        canvas.SetCursor(new Editor.Core.Models.CursorPosition(0, 0));
        var window = WpfTestHost.Load(canvas);
        try
        {
            canvas.UpdateLayout();
            test(canvas);
        }
        finally
        {
            window.Close();
        }
    });
}
