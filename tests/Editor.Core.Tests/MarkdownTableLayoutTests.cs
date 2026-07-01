using Editor.Core.Editing;

namespace Editor.Core.Tests;

public class MarkdownTableLayoutTests
{
    [Theory]
    [InlineData("| a | b |", true)]
    [InlineData("a | b", true)]
    [InlineData("no pipes here", false)]
    [InlineData(@"escaped \| pipe only", false)]
    public void LooksLikeRow_DetectsUnescapedPipe(string line, bool expected)
        => Assert.Equal(expected, MarkdownTableLayout.LooksLikeRow(line));

    [Theory]
    [InlineData("| --- | --- |", true)]
    [InlineData("|:---|---:|", true)]
    [InlineData("| :--: |", true)]
    [InlineData("| abc | --- |", false)]
    [InlineData("| a | b |", false)]
    public void IsSeparatorRow_MatchesGfmDashRows(string line, bool expected)
        => Assert.Equal(expected, MarkdownTableLayout.IsSeparatorRow(line));

    [Fact]
    public void SplitCellSpans_DropsLeadingAndTrailingPipe()
    {
        var spans = MarkdownTableLayout.SplitCellSpans("| a | bb |");
        Assert.Equal(2, spans.Count);
        Assert.Equal("a", "| a | bb |"[spans[0].Start..spans[0].End].Trim());
        Assert.Equal("bb", "| a | bb |"[spans[1].Start..spans[1].End].Trim());
    }

    [Fact]
    public void SplitCellSpans_WorksWithoutOuterPipes()
    {
        var line = "a | b";
        var spans = MarkdownTableLayout.SplitCellSpans(line);
        Assert.Equal(2, spans.Count);
        Assert.Equal("a", line[spans[0].Start..spans[0].End].Trim());
        Assert.Equal("b", line[spans[1].Start..spans[1].End].Trim());
    }

    [Fact]
    public void SplitCellSpans_TreatsEscapedPipeAsLiteral()
    {
        var line = @"| a\|b | c |";
        var spans = MarkdownTableLayout.SplitCellSpans(line);
        Assert.Equal(2, spans.Count);
    }

    [Fact]
    public void FindBlocks_DetectsHeaderSeparatorAndBodyRows()
    {
        string[] lines =
        [
            "intro text",
            "| Name | Value |",
            "| --- | --- |",
            "| a | 1 |",
            "| bb | 2 |",
            "",
            "trailing text",
        ];
        var blocks = MarkdownTableLayout.FindBlocks(lines);
        Assert.Single(blocks);
        Assert.Equal(1, blocks[0].StartLine);
        Assert.Equal(4, blocks[0].EndLine);
    }

    [Fact]
    public void FindBlocks_RequiresMatchingColumnCountBetweenHeaderAndSeparator()
    {
        string[] lines =
        [
            "| a | b |",
            "| --- |",
        ];
        Assert.Empty(MarkdownTableLayout.FindBlocks(lines));
    }

    [Fact]
    public void FindBlocks_IgnoresNonTableContent()
    {
        string[] lines = ["just | a pipe", "no separator here"];
        Assert.Empty(MarkdownTableLayout.FindBlocks(lines));
    }
}
