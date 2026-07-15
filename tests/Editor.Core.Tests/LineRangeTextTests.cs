using Editor.Core.Formatting;

namespace Editor.Core.Tests;

public class LineRangeTextTests
{
    private const string Doc = "one\ntwo\nthree\nfour";

    [Fact]
    public void Extract_ReturnsInclusiveRange()
    {
        Assert.Equal("two\nthree", LineRangeText.Extract(Doc, 1, 2));
    }

    [Fact]
    public void Extract_SingleLine_ReturnsThatLine()
    {
        Assert.Equal("three", LineRangeText.Extract(Doc, 2, 2));
    }

    [Fact]
    public void Replace_SplicesNewLinesInPlace()
    {
        Assert.Equal("one\nTWO\nTHREE\nfour", LineRangeText.Replace(Doc, 1, 2, "TWO\nTHREE"));
    }

    [Fact]
    public void Replace_WithFewerLines_ShrinksDocument()
    {
        Assert.Equal("one\nX\nfour", LineRangeText.Replace(Doc, 1, 2, "X"));
    }

    [Fact]
    public void Replace_WithMoreLines_GrowsDocument()
    {
        Assert.Equal("one\na\nb\nc\nfour", LineRangeText.Replace(Doc, 1, 2, "a\nb\nc"));
    }

    [Fact]
    public void ExtractThenReplace_Unchanged_RoundTripsCrlfDocument()
    {
        var crlf = "one\r\ntwo\r\nthree\r\nfour";
        var slice = LineRangeText.Extract(crlf, 1, 2);

        Assert.Equal(crlf, LineRangeText.Replace(crlf, 1, 2, slice));
    }

    [Fact]
    public void Clamp_OrdersAndBoundsRange()
    {
        Assert.Equal((1, 2), LineRangeText.Clamp(Doc, 2, 1));
        Assert.Equal((0, 3), LineRangeText.Clamp(Doc, -5, 99));
        Assert.Equal((3, 3), LineRangeText.Clamp(Doc, 3, 3));
    }

    [Fact]
    public void LineLength_IgnoresCarriageReturn()
    {
        Assert.Equal(5, LineRangeText.LineLength("one\r\nthree\r\n", 1));
    }

    [Fact]
    public void CommonIndent_ReturnsSharedPrefix()
    {
        var text = "        if (x)\n            y();\n        end";

        Assert.Equal("        ", LineRangeText.CommonIndent(text, 0, 2));
    }

    [Fact]
    public void CommonIndent_IgnoresBlankLines()
    {
        var text = "    a\n\n    b";

        Assert.Equal("    ", LineRangeText.CommonIndent(text, 0, 2));
    }

    [Fact]
    public void CommonIndent_UnindentedLine_ReturnsEmpty()
    {
        var text = "    a\nb";

        Assert.Equal("", LineRangeText.CommonIndent(text, 0, 1));
    }

    [Fact]
    public void CommonIndent_MixedTabsAndSpaces_StopsAtFirstDifference()
    {
        var text = "\t a\n\tb";

        Assert.Equal("\t", LineRangeText.CommonIndent(text, 0, 1));
    }

    [Fact]
    public void DedentThenIndent_RestoresOriginal()
    {
        var text = "    if (x)\n        y();";
        var indent = LineRangeText.CommonIndent(text, 0, 1);

        var dedented = LineRangeText.Dedent(text, indent);
        Assert.Equal("if (x)\n    y();", dedented);
        Assert.Equal(text, LineRangeText.Indent(dedented, indent));
    }

    [Fact]
    public void Indent_LeavesBlankLinesBare()
    {
        Assert.Equal("    a\n\n    b", LineRangeText.Indent("a\n\nb", "    "));
    }

    [Fact]
    public void Dedent_LineWithoutTheIndent_IsLeftAlone()
    {
        Assert.Equal("a\n  b", LineRangeText.Dedent("    a\n  b", "    "));
    }
}
