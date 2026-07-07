using Editor.Core.Editing;

namespace Editor.Core.Tests;

public class WhitespaceIssueDetectorTests
{
    private static Dictionary<int, List<WhitespaceIssue>> Detect(params string[] lines)
        => WhitespaceIssueDetector.Detect(lines);

    [Fact]
    public void CleanLines_ReturnsEmpty()
        => Assert.Empty(Detect("var x = 1;", "hello world"));

    [Fact]
    public void TrailingSpaces_Flagged()
    {
        var d = Detect("hello   ");
        var issue = Assert.Single(d[0]);
        Assert.Equal(WhitespaceIssueKind.TrailingWhitespace, issue.Kind);
        Assert.Equal(5, issue.Start);
        Assert.Equal(8, issue.End);
    }

    [Fact]
    public void TrailingTab_Flagged()
    {
        var d = Detect("hello\t");
        var issue = Assert.Single(d[0]);
        Assert.Equal(WhitespaceIssueKind.TrailingWhitespace, issue.Kind);
        Assert.Equal(5, issue.Start);
        Assert.Equal(6, issue.End);
    }

    [Fact]
    public void FullWidthSpace_MidLine_Flagged()
    {
        var d = Detect("hello　world");
        var issue = Assert.Single(d[0]);
        Assert.Equal(WhitespaceIssueKind.FullWidthSpace, issue.Kind);
        Assert.Equal(5, issue.Start);
        Assert.Equal(6, issue.End);
    }

    [Fact]
    public void FullWidthSpace_AtEndOfLine_CountsAsTrailingOnly()
    {
        var d = Detect("hello　");
        var issue = Assert.Single(d[0]);
        Assert.Equal(WhitespaceIssueKind.TrailingWhitespace, issue.Kind);
        Assert.Equal(5, issue.Start);
        Assert.Equal(6, issue.End);
    }

    [Fact]
    public void MultipleFullWidthSpaces_AllFlagged()
    {
        var d = Detect("a　b　c");
        Assert.Equal(2, d[0].Count);
        Assert.All(d[0], i => Assert.Equal(WhitespaceIssueKind.FullWidthSpace, i.Kind));
    }

    [Fact]
    public void FullWidthSpaceBeforeTrailingWhitespace_BothFlagged()
    {
        var d = Detect("a　b  ");
        Assert.Equal(2, d[0].Count);
        Assert.Contains(d[0], i => i.Kind == WhitespaceIssueKind.FullWidthSpace);
        Assert.Contains(d[0], i => i.Kind == WhitespaceIssueKind.TrailingWhitespace);
    }

    [Fact]
    public void EmptyLine_NotFlagged()
        => Assert.Empty(Detect(""));

    [Fact]
    public void MultipleLines_OnlyFlaggedLinesInResult()
    {
        var d = Detect("clean", "trailing ", "clean again");
        Assert.Single(d);
        Assert.True(d.ContainsKey(1));
    }
}
