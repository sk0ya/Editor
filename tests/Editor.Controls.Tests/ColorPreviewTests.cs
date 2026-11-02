using System.Reflection;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

public sealed class ColorPreviewTests
{
    [Theory]
    [InlineData("color: #123;", 7)]
    [InlineData("#fff", 0)]
    [InlineData("background=#AABBCC", 11)]
    [InlineData("fill=\"#336699\"", 6)]
    [InlineData("<Color>#FF00FF00</Color>", 7)]
    [InlineData("[#abc, #def]", 7)]
    [InlineData("rgb(1, 2, 3)", 0)]
    [InlineData("c = rgba(1,2,3,0.5)", 4)]
    public void Finds_colors_where_colors_are_written(string line, int expectedStart)
    {
        var colors = OverlayRenderer.FindColors(line);
        Assert.Contains(colors, c => c.Start == expectedStart);
    }

    [Theory]
    [InlineData("[追加](#add)")]            // Markdown のリンク先（見出しアンカー）
    [InlineData("[追加](#add-feature)")]
    [InlineData("詳しくは [こちら](doc.md#bed) を参照")]
    [InlineData("<a href=\"#fed\">x</a>")]
    [InlineData("<a href='#cafe12'>x</a>")]
    [InlineData("issue#123")]
    [InlineData("see #add-feature")]         // アンカーの頭だけを色にしない
    [InlineData("torgb(1,2,3)")]
    public void Does_not_treat_links_and_word_fragments_as_colors(string line)
    {
        Assert.Empty(OverlayRenderer.FindColors(line));
    }

    [Fact]
    public void Text_after_a_color_is_shifted_to_make_room_for_the_swatch()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = new EditorCanvas();
            canvas.SetLines(["a: #123;", "a: x123;"]);

            double withColor = VisualX(canvas, 0, 8);    // 「;」の後ろ
            double withoutColor = VisualX(canvas, 1, 8);

            Assert.True(withColor > withoutColor + OverlayRenderer.ColorSwatchSize(10),
                $"見本ぶんズレていない: {withColor} vs {withoutColor}");

            // 見本を切れば元の位置に戻る
            canvas.SetColorPreview(false);
            Assert.Equal(withoutColor, VisualX(canvas, 0, 8), 3);
        });
    }

    private static double VisualX(EditorCanvas canvas, int line, int col)
    {
        var lines = (string[])typeof(EditorCanvas)
            .GetField("_lines", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;
        typeof(EditorCanvas).GetMethod("SetActiveLine", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(canvas, [line]);
        return (double)typeof(EditorCanvas)
            .GetMethod("GetVisualX", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(canvas, [lines[line], col])!;
    }
}
