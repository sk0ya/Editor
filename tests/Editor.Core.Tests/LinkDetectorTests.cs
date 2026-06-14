using Editor.Core.Text;

namespace Editor.Core.Tests;

public class LinkDetectorTests
{
    [Fact]
    public void FindLinks_DetectsHttpsUrl()
    {
        var links = LinkDetector.FindLinks("See https://example.com/path for details");

        Assert.Single(links);
        Assert.Equal(LinkKind.Url, links[0].Kind);
        Assert.Equal("https://example.com/path", links[0].Text);
        Assert.Equal(4, links[0].Start);
        Assert.Equal(4 + "https://example.com/path".Length, links[0].End);
    }

    [Fact]
    public void FindLinks_TrimsTrailingPunctuation()
    {
        var links = LinkDetector.FindLinks("Visit https://example.com/page, then https://other.com/path.");

        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/page", links[0].Text);
        Assert.Equal("https://other.com/path", links[1].Text);
    }

    [Fact]
    public void FindLinks_DetectsFtpAndHttp()
    {
        var links = LinkDetector.FindLinks("ftp://files.example.com and http://example.org");

        Assert.Equal(2, links.Count);
        Assert.Equal("ftp://files.example.com", links[0].Text);
        Assert.Equal("http://example.org", links[1].Text);
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
        Assert.Equal("https://example.com/path", link!.Value.Text);
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
        Assert.Equal("http://[::1]:8080/api", links[0].Text);
    }

    [Fact]
    public void FindLinks_IgnoresBareSchemePlaceholder()
    {
        var links = LinkDetector.FindLinks("e.g. https://...");

        Assert.DoesNotContain(links, l => l.Kind == LinkKind.Url);
    }

    [Fact]
    public void FindLinks_DetectsRelativePathWithSeparator()
    {
        var links = LinkDetector.FindLinks("see src/Editor.Core/Text/LinkDetector.cs for details");

        var path = Assert.Single(links, l => l.Kind == LinkKind.FilePath);
        Assert.Equal("src/Editor.Core/Text/LinkDetector.cs", path.Text);
    }

    [Fact]
    public void FindLinks_DetectsDotSlashAndDotDotPaths()
    {
        var links = LinkDetector.FindLinks("import ./util.ts and ../shared/lib.ts here");

        Assert.Contains(links, l => l.Kind == LinkKind.FilePath && l.Text == "./util.ts");
        Assert.Contains(links, l => l.Kind == LinkKind.FilePath && l.Text == "../shared/lib.ts");
    }

    [Fact]
    public void FindLinks_DetectsWindowsAbsolutePath()
    {
        var links = LinkDetector.FindLinks(@"open C:\Projects\Editor\README.md now");

        var path = Assert.Single(links, l => l.Kind == LinkKind.FilePath);
        Assert.Equal(@"C:\Projects\Editor\README.md", path.Text);
    }

    [Fact]
    public void FindLinks_DetectsPosixAbsoluteAndHomePaths()
    {
        var links = LinkDetector.FindLinks("see /etc/hosts and ~/.vimrc files");

        Assert.Contains(links, l => l.Kind == LinkKind.FilePath && l.Text == "/etc/hosts");
        Assert.Contains(links, l => l.Kind == LinkKind.FilePath && l.Text == "~/.vimrc");
    }

    [Fact]
    public void FindLinks_DetectsBareFilenameWithExtension()
    {
        var links = LinkDetector.FindLinks("edit README.md please");

        var path = Assert.Single(links, l => l.Kind == LinkKind.FilePath);
        Assert.Equal("README.md", path.Text);
    }

    [Fact]
    public void FindLinks_IgnoresVersionLikeNumbers()
    {
        var links = LinkDetector.FindLinks("version 1.5 and pi 3.14 are not paths");

        Assert.Empty(links);
    }

    [Fact]
    public void FindLinks_DoesNotDetectPathInsideUrl()
    {
        var links = LinkDetector.FindLinks("visit https://example.com/path/to/file.cs now");

        var single = Assert.Single(links);
        Assert.Equal(LinkKind.Url, single.Kind);
    }
}
