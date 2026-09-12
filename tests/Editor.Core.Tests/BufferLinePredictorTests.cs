using Editor.Core.Completion;

namespace Editor.Core.Tests;

/// <summary>
/// キャレットの先に薄く出す提案の中身。外れた提案が出続けるのは、何も出ないより悪い
/// ——「出さない」条件の方を厚く固定する。
/// </summary>
public class BufferLinePredictorTests
{
    private static string? Predict(string[] lines, int line)
        => BufferLinePredictor.Predict(lines, line, lines[line].Length)?.Text;

    [Fact]
    public void Continues_a_line_that_repeats_an_earlier_one()
    {
        string[] lines =
        [
            "Assert.Equal(1, actual.Count);",
            "Assert.Equ",
        ];

        Assert.Equal("al(1, actual.Count);", Predict(lines, 1));
    }

    [Fact]
    public void Indentation_does_not_have_to_match()
    {
        string[] lines =
        [
            "        private readonly ILogger _logger;",
            "    private readonly IL",
        ];

        Assert.Equal("ogger _logger;", Predict(lines, 1));
    }

    /// <summary>近い行を採る。遠くの似た行より、いま書いている流れの方が意図に近い。</summary>
    [Fact]
    public void The_nearest_matching_line_wins()
    {
        string[] lines =
        [
            "case Kind.Far: return \"far\";",
            "",
            "case Kind.Near: return \"near\";",
            "case Kind.",
        ];

        Assert.Equal("Near: return \"near\";", Predict(lines, 3));
    }

    /// <summary>同じ距離なら上を採る。書き終えた行が手本で、下はまだ直しかけのことがある。</summary>
    [Fact]
    public void At_equal_distance_the_line_above_wins()
    {
        string[] lines =
        [
            "value.Write(above);",
            "value.Wr",
            "value.Write(below);",
        ];

        Assert.Equal("ite(above);", Predict(lines, 1));
    }

    [Fact]
    public void Nothing_is_suggested_when_the_caret_is_not_at_the_end_of_the_line()
    {
        string[] lines =
        [
            "Assert.Equal(1, actual.Count);",
            "Assert.Equal(1, x);",
        ];

        Assert.Null(BufferLinePredictor.Predict(lines, 1, "Assert.Equ".Length));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("if")]
    [InlineData("  x")]
    public void Nothing_is_suggested_from_too_short_a_clue(string current)
    {
        string[] lines = ["if (x) { return; }", current];
        Assert.Null(Predict(lines, 1));
    }

    [Fact]
    public void Nothing_is_suggested_when_the_line_is_already_complete()
    {
        string[] lines =
        [
            "return value;",
            "return value;",
        ];

        Assert.Null(Predict(lines, 1));
    }

    [Fact]
    public void Nothing_is_suggested_when_no_earlier_line_starts_the_same_way()
    {
        string[] lines =
        [
            "using System;",
            "namespace Foo;",
            "public sealed class Ba",
        ];

        Assert.Null(Predict(lines, 2));
    }

    [Fact]
    public void Matching_is_case_sensitive()
    {
        string[] lines =
        [
            "Console.WriteLine(x);",
            "console.Wri",
        ];

        Assert.Null(Predict(lines, 1));
    }

    /// <summary>遠すぎる行は採らない。画面の外どころかファイルの反対側にある行の続きを
    /// 出されても、手本として読めない。</summary>
    [Fact]
    public void Lines_beyond_the_search_radius_are_not_used()
    {
        var lines = new List<string> { "Assert.Equal(1, actual.Count);" };
        for (int i = 0; i < BufferLinePredictor.SearchRadius + 5; i++) lines.Add("");
        lines.Add("Assert.Equ");

        Assert.Null(Predict([.. lines], lines.Count - 1));
    }

    [Fact]
    public void An_out_of_range_line_is_not_a_crash()
    {
        Assert.Null(BufferLinePredictor.Predict(["a"], 5, 0));
        Assert.Null(BufferLinePredictor.Predict([], 0, 0));
    }

    [Theory]
    [InlineData("Name { get; set; }", "Name")]
    [InlineData(".Name", ".Name")]
    [InlineData("(x, y);", "(x")]
    [InlineData("   trailing", "   trailing")]
    public void Word_wise_acceptance_takes_one_word_including_its_leading_punctuation(
        string suggestion, string expected)
    {
        var item = new InlineSuggestion(suggestion, InlineSuggestionSource.BufferLine);
        Assert.Equal(expected, suggestion[..item.NextWordLength()]);
    }

    [Fact]
    public void Only_the_first_line_is_shown_for_a_multi_line_suggestion()
    {
        var item = new InlineSuggestion("first\r\nsecond", InlineSuggestionSource.External);
        Assert.Equal("first", item.FirstLine);
        Assert.Equal("first", new InlineSuggestion("first", InlineSuggestionSource.External).FirstLine);
    }
}
