using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// 折り返さないときの横の広がり（<see cref="EditorCanvas.TotalContentWidth"/>）は、全行を WPF で整形せず
/// 文字ごとの幅の和で見積もる（日本語の大きな文書を開くと UI スレッドが秒単位で止まったため）。
/// 見積もりが<b>小さく</b>出ると行末までスクロールできなくなるので、正確な計測を下回らないことと、
/// 大きく外れないことを確かめる。
/// </summary>
public sealed class EditorCanvasNoWrapExtentTests
{
    private const string Family = "Consolas";
    private const double Size = 14;

    [Theory]
    [InlineData("public static void Main(string[] args) { Console.WriteLine(\"hello\"); }")]
    [InlineData("差分ビューで日本語の多い Markdown を開くと、全行の整形で UI が止まっていた。")]
    [InlineData("| 列 | 説明 |  mixed ｶﾅ and 全角ＡＢＣ")]
    public void Extent_covers_the_measured_width_of_the_longest_line(string longest)
    {
        WpfTestHost.Run(() =>
        {
            double extent = LineExtent(longest);
            double exact = TextBoundaryMeasurer.Measure(longest, new System.Windows.Media.Typeface(Family), Size, 1.0)[^1];

            Assert.True(extent >= exact - 0.5, $"見積もり {extent} が実測 {exact} を下回った");
            Assert.True(extent <= exact * 1.1 + 1, $"見積もり {extent} が実測 {exact} から外れすぎ");
        });
    }

    [Fact]
    public void Leading_tabs_are_not_underestimated()
    {
        WpfTestHost.Run(() =>
        {
            const string line = "\t\t\tvar x = 1;";
            double extent = LineExtent(line);
            double exact = TextBoundaryMeasurer.Measure(line, new System.Windows.Media.Typeface(Family), Size, 1.0)[^1];

            Assert.True(extent >= exact - 0.5, $"見積もり {extent} が実測 {exact} を下回った");
        });
    }

    /// <summary>行の広がりだけを取り出す：同じ行数の空文書との差（行番号の列の幅を打ち消す）。</summary>
    private static double LineExtent(string longest)
    {
        var withLine = Canvas(["short", longest, "x"]);
        var empty = Canvas(["", "", ""]);
        return withLine.TotalContentWidth - empty.TotalContentWidth;
    }

    private static EditorCanvas Canvas(string[] lines)
    {
        var canvas = new EditorCanvas { WrapLines = false };
        canvas.UpdateFont(Family, Size);
        canvas.SetLines(lines);
        return canvas;
    }
}
