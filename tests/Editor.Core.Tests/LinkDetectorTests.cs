using Editor.Core.Text;

namespace Editor.Core.Tests;

public class LinkDetectorTests
{
    [Fact]
    public void FindLinks_DetectsHttpsUrl()
    {
        var links = LinkDetector.FindLinks("See https://example.com/path for details");

        Assert.Single(links);
        Assert.Equal("https://example.com/path", links[0].Url);
        Assert.Equal(4, links[0].Start);
        Assert.Equal(4 + "https://example.com/path".Length, links[0].End);
    }

    [Fact]
    public void FindLinks_TrimsTrailingPunctuation()
    {
        var links = LinkDetector.FindLinks("Visit https://example.com/page, then https://other.com/path.");

        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/page", links[0].Url);
        Assert.Equal("https://other.com/path", links[1].Url);
    }

    [Fact]
    public void FindLinks_DetectsFtpAndHttp()
    {
        var links = LinkDetector.FindLinks("ftp://files.example.com and http://example.org");

        Assert.Equal(2, links.Count);
        Assert.Equal("ftp://files.example.com", links[0].Url);
        Assert.Equal("http://example.org", links[1].Url);
    }

    [Fact]
    public void FindLinks_ReturnsEmpty_WhenNoUrl()
    {
        var links = LinkDetector.FindLinks("no links here");

        Assert.Empty(links);
    }

    [Fact]
    public void FindLinkAt_ReturnsLinkWhenColumnInsideSpan()
    {
        const string line = "See https://example.com/path for details";
        var link = LinkDetector.FindLinkAt(line, 10);

        Assert.NotNull(link);
        Assert.Equal("https://example.com/path", link!.Value.Url);
    }

    [Fact]
    public void FindLinkAt_ReturnsNullWhenColumnOutsideSpan()
    {
        const string line = "See https://example.com/path for details";
        Assert.Null(LinkDetector.FindLinkAt(line, 0));
        Assert.Null(LinkDetector.FindLinkAt(line, line.Length - 1));
    }

    [Fact]
    public void FindLinks_DetectsIPv6Host()
    {
        var links = LinkDetector.FindLinks("Server is at http://[::1]:8080/api, use it");

        Assert.Single(links);
        Assert.Equal("http://[::1]:8080/api", links[0].Url);
    }

    [Fact]
    public void FindLinks_IgnoresBareSchemePlaceholder()
    {
        var links = LinkDetector.FindLinks("e.g. https://...");

        Assert.Empty(links);
    }
}
