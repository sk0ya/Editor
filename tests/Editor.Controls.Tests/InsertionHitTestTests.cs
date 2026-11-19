using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// ファイルのドロップ等で「落とした所」に文字を差し込むための当たり（<see cref="EditorCanvas.TryHitTestInsertion"/>）。
/// カーソルの当たりと違って行末の後ろを返すこと、ガターの上・キャンバスの外は false にすることを固定する。
/// </summary>
public class InsertionHitTestTests
{
    private static EditorCanvas NewArrangedCanvas()
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(["abc", "hello world", ""]);
        canvas.Measure(new Size(600, 400));
        canvas.Arrange(new Rect(0, 0, 600, 400));
        return canvas;
    }

    private static double LineHeight(EditorCanvas canvas) =>
        (double)typeof(EditorCanvas).GetField("_lineHeight", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;

    private static double CharWidth(EditorCanvas canvas) =>
        (double)typeof(EditorCanvas).GetField("_charWidth", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;

    private static int GutterWidth(EditorCanvas canvas)
    {
        var method = typeof(EditorCanvas).GetMethod("GetGutterMetrics", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)((ITuple)method.Invoke(canvas, null)!)[5]!;
    }

    private static Point At(EditorCanvas canvas, int line, double column)
        => new(GutterWidth(canvas) + CharWidth(canvas) * column + 1, LineHeight(canvas) * (line + 0.5));

    [Fact]
    public void InsideText_ReturnsLineAndColumn()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewArrangedCanvas();

            Assert.True(canvas.TryHitTestInsertion(At(canvas, 1, 2), out var line, out var column));
            Assert.Equal(1, line);
            Assert.Equal(2, column);
        });
    }

    [Fact]
    public void PastEndOfLine_ReturnsLineLength_UnlikeTheCursor()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewArrangedCanvas();

            Assert.True(canvas.TryHitTestInsertion(At(canvas, 0, 20), out var line, out var column));
            Assert.Equal(0, line);
            Assert.Equal(3, column);   // "abc" の後ろ（カーソルなら 2 で止まる）

            Assert.True(canvas.TryHitTestInsertion(At(canvas, 2, 5), out line, out column));
            Assert.Equal(2, line);
            Assert.Equal(0, column);   // 空行
        });
    }

    [Fact]
    public void OverGutterOrOutside_ReturnsFalse()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewArrangedCanvas();

            Assert.False(canvas.TryHitTestInsertion(new Point(1, LineHeight(canvas) * 0.5), out _, out _));
            Assert.False(canvas.TryHitTestInsertion(new Point(300, -5), out _, out _));
            Assert.False(canvas.TryHitTestInsertion(new Point(700, 10), out _, out _));
        });
    }
}
