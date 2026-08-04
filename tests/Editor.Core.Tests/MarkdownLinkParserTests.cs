using Editor.Core.Text;

namespace Editor.Core.Tests;

/// <summary>
/// Inline-link destination parsing. Pins the shapes that break when a destination is cut at the
/// first <c>)</c>: balanced parens, titles, angled destinations, and nested link text.
/// </summary>
public class MarkdownLinkParserTests
{
    private static MarkdownInlineLink One(string text) => Assert.Single(MarkdownLinkParser.FindAll(text));

    [Theory]
    [InlineData("[aa](aa(bb)_cc.md)", "aa(bb)_cc.md")]
    [InlineData("[aa](a(b(c))d.md)", "a(b(c))d.md")]
    [InlineData("[aa](plain.md)", "plain.md")]
    [InlineData(@"[aa](a\(b.md)", "a(b.md")]
    [InlineData("[aa](<a (b .md>)", "a (b .md")]
    [InlineData("[aa](dir/f.md \"title\")", "dir/f.md")]
    [InlineData("[aa](a(b)_c.md 'title')", "a(b)_c.md")]
    public void Destination_RunsToTheBalancedClosingParen(string markdown, string expected)
    {
        var link = One(markdown);

        Assert.Equal(expected, link.Destination);
        Assert.Equal(0, link.Start);
        Assert.Equal(markdown.Length, link.Length);
    }

    [Fact]
    public void Span_CoversTheWholeLink()
    {
        const string src = "see [aa](aa(bb)_cc.md) here";

        var link = One(src);

        Assert.Equal("aa", link.Text);
        Assert.Equal("[aa](aa(bb)_cc.md)", src.Substring(link.Start, link.Length));
        Assert.Equal("aa(bb)_cc.md", src.Substring(link.DestinationStart, link.DestinationLength));
    }

    [Fact]
    public void Image_StartsAtTheBang()
    {
        var link = One("![alt](a(b).png)");

        Assert.True(link.IsImage);
        Assert.Equal(0, link.Start);
        Assert.Equal("alt", link.Text);
        Assert.Equal("a(b).png", link.Destination);
    }

    [Fact]
    public void LinkText_MayContainBalancedBrackets()
    {
        var link = One("[see [1] here](a(b).md)");

        Assert.Equal("see [1] here", link.Text);
        Assert.Equal("a(b).md", link.Destination);
    }

    [Fact]
    public void UnbalancedParens_AreNotADestination()
    {
        Assert.Empty(MarkdownLinkParser.FindAll("[aa](a(b.md"));
        Assert.Empty(MarkdownLinkParser.FindAll("[aa](a(b.md 'title'"));
    }

    [Fact]
    public void FindImages_ReachesImagesNestedInsideLinkText()
    {
        var images = MarkdownLinkParser.FindImages("[![alt](badge(1).svg)](https://example.com/a(b))");

        Assert.Single(images);
        Assert.Equal("badge(1).svg", images[0].Destination);
    }

    [Fact]
    public void FootnoteReference_IsNotALink()
    {
        Assert.Empty(MarkdownLinkParser.FindAll("body[^1] and more"));
    }

    [Theory]
    [InlineData("a(b)_c.md", "a(b)_c.md")]
    [InlineData("plain.md", "plain.md")]
    [InlineData("a (b).md", "<a (b).md>")]
    [InlineData("a(b.md", "<a(b.md>")]
    [InlineData("a<b>.md", @"<a\<b\>.md>")]
    public void EncodeDestination_WrapsOnlyWhatNeedsIt(string raw, string expected)
    {
        Assert.Equal(expected, MarkdownLinkParser.EncodeDestination(raw));
    }

    [Fact]
    public void EncodedDestination_ReadsBackUnchanged()
    {
        foreach (var raw in new[] { "a(b)_c.md", "a (b).md", "a(b.md", "a<b>.md", @"a\b.md" })
            Assert.Equal(raw, One($"[t]({MarkdownLinkParser.EncodeDestination(raw)})").Destination);
    }
}
