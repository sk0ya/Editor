using System.Windows.Media;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// Caret-boundary measurement, including lines far longer than one <c>TextLine</c> can hold.
/// </summary>
public class TextBoundaryMeasurerTests
{
    private static double[] Measure(string text)
    {
        double[]? result = null;
        WpfTestHost.Run(() =>
            result = TextBoundaryMeasurer.Measure(text, new Typeface("Consolas"), 14, 1.0));
        return result!;
    }

    [Fact]
    public void Measures_every_boundary_of_an_ordinary_line()
    {
        var boundaries = Measure("hello world");

        Assert.Equal(12, boundaries.Length);
        Assert.Equal(0, boundaries[0]);
        // Monotonic and actually advancing.
        for (int i = 1; i < boundaries.Length; i++)
            Assert.True(boundaries[i] > boundaries[i - 1], $"boundary {i} did not advance");
    }

    [Fact]
    public void Measures_a_line_longer_than_one_formatted_line()
    {
        // A base64 data URL in a diagram/JSON file reaches this length easily. TextFormatter
        // stops at the paragraph width even with NoWrap, so a single FormatLine call covers
        // only the head of the text - measuring past it used to throw ArgumentOutOfRangeException
        // ("cpFirst") and, from the editor's load path, took the whole app down.
        var text = new string('W', 400_000);

        var boundaries = Measure(text);

        Assert.Equal(text.Length + 1, boundaries.Length);
        Assert.Equal(0, boundaries[0]);
        Assert.True(boundaries[^1] > boundaries[1000], "the tail of the line was never measured");
        for (int i = 1; i < boundaries.Length; i++)
            Assert.True(boundaries[i] >= boundaries[i - 1], $"boundary {i} went backwards");
    }
}
