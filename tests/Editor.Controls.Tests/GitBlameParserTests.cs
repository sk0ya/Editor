using Editor.Controls.Git;

namespace Editor.Controls.Tests;

public sealed class GitBlameParserTests
{
    private const string Hash1 = "1111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222";
    private const string ZeroHash = "0000000000000000000000000000000000000000";

    // author-time 1750000000 = 2025-06-15 (ローカルタイムゾーン依存のため日付そのものは検証しない)
    private static string PorcelainSample() =>
        $"{Hash1} 1 1 2\n" +
        "author koya\n" +
        "author-mail <koya@example.com>\n" +
        "author-time 1750000000\n" +
        "author-tz +0900\n" +
        "summary 最初のコミット\n" +
        "filename a.txt\n" +
        "\tline one\n" +
        $"{Hash1} 2 2\n" +          // 同一コミット2行目：メタ行は porcelain では省略される
        "\tline two\n" +
        $"{Hash2} 1 3 1\n" +
        "author yamada\n" +
        "author-time 1750000000\n" +
        "summary 2つ目のコミット\n" +
        "filename a.txt\n" +
        "\tline three\n";

    [Fact]
    public void ParsePorcelainBlame_AnnotatesEveryLine_EvenWhenCommitMetaIsOmitted()
    {
        var result = GitDiffProvider.ParsePorcelainBlame(PorcelainSample());

        // 同一コミットの2行目（メタ省略行）にも注釈が付く＝全行表示
        Assert.Equal(3, result.Count);
        Assert.Equal("1111111", result[0].CommitHash);
        Assert.Equal("1111111", result[1].CommitHash);
        Assert.Equal("2222222", result[2].CommitHash);
        Assert.Equal("koya", result[1].Author);
        Assert.Equal(result[0].Display, result[1].Display);
    }

    [Fact]
    public void ParsePorcelainBlame_CapturesOriginalLineForDiffJump()
    {
        var result = GitDiffProvider.ParsePorcelainBlame(PorcelainSample());

        // ヘッダの orig-lineno（そのコミット時点の行番号）が各行に入る
        Assert.Equal(1, result[0].OriginalLine);
        Assert.Equal(2, result[1].OriginalLine);
        Assert.Equal(1, result[2].OriginalLine);
    }

    [Fact]
    public void ParsePorcelainBlame_CapturesSummaryForTooltip()
    {
        var result = GitDiffProvider.ParsePorcelainBlame(PorcelainSample());

        Assert.Equal("最初のコミット", result[0].Summary);
        Assert.Equal("最初のコミット", result[1].Summary);
        Assert.Equal("2つ目のコミット", result[2].Summary);
        Assert.Contains("2つ目のコミット", result[2].Tooltip);
    }

    [Fact]
    public void ParsePorcelainBlame_SkipsUncommittedLines()
    {
        var output =
            $"{ZeroHash} 1 1 1\n" +
            "author Not Committed Yet\n" +
            "author-time 1750000000\n" +
            "summary Version of a.txt from a.txt\n" +
            "filename a.txt\n" +
            "\tuncommitted line\n";

        Assert.Empty(GitDiffProvider.ParsePorcelainBlame(output));
    }
}
