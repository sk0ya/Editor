using Editor.Core.Editing;

namespace Editor.Core.Tests;

public class BracketGuidesTests
{
    private static string[] Lines(params string[] lines) => lines;

    // ── どこに線を引くか ──

    [Fact]
    public void Build_AllmanBrace_AnchorsAtBraceColumn()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "class A",
            "{",
            "    int x;",
            "}"));

        var g = Assert.Single(guides);
        Assert.Equal(1, g.OpenLine);
        Assert.Equal(3, g.CloseLine);
        Assert.Equal(3, g.AnchorLine);
        Assert.Equal(0, g.AnchorColumn);
        Assert.Equal(0, g.Depth);
    }

    [Fact]
    public void Build_KandRBrace_AnchorsAtClosingBraceIndent()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "    if (x) {",
            "        y();",
            "    }"));

        // 閉じ括弧が行頭なので、ブロックのインデント桁（4）に縦線が立つ
        var g = Assert.Single(guides);
        Assert.Equal(2, g.AnchorLine);
        Assert.Equal(4, g.AnchorColumn);
    }

    [Fact]
    public void Build_ClosingBracketNotFirstOnLine_AnchorsAtOpeningColumn()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "foo(a,",
            "    b);"));

        var g = Assert.Single(guides);
        Assert.Equal(0, g.AnchorLine);
        Assert.Equal(3, g.AnchorColumn);
    }

    [Fact]
    public void Build_SingleLinePair_NoGuide()
        => Assert.Empty(BracketGuideScanner.Build(Lines("var x = new[] { 1, 2 };")));

    [Fact]
    public void Build_NestedPairs_CarryDepth()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    {",
            "        x;",
            "    }",
            "}"));

        Assert.Equal(2, guides.Count);
        // 内側が先に閉じる
        Assert.Equal(1, guides[0].Depth);
        Assert.Equal(1, guides[0].OpenLine);
        Assert.Equal(0, guides[1].Depth);
        Assert.Equal(0, guides[1].OpenLine);
    }

    // ── 数えてはいけない括弧 ──

    [Fact]
    public void Build_BraceInString_Ignored()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    var s = \"}\";",
            "}"));

        var g = Assert.Single(guides);
        Assert.Equal(0, g.OpenLine);
        Assert.Equal(2, g.CloseLine);
    }

    [Fact]
    public void Build_BraceInLineComment_Ignored()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    // }",
            "}"));

        Assert.Equal(2, Assert.Single(guides).CloseLine);
    }

    [Fact]
    public void Build_BraceInBlockComment_Ignored()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    /* } ",
            "       } */",
            "}"));

        Assert.Equal(3, Assert.Single(guides).CloseLine);
    }

    [Fact]
    public void Build_EscapedQuoteInString_DoesNotLeakOut()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    var s = \"\\\" }\";",
            "}"));

        Assert.Equal(2, Assert.Single(guides).CloseLine);
    }

    [Fact]
    public void Build_HashCommentSyntax_IgnoresBraceInComment()
    {
        var syntax = new BracketGuideSyntax(LineComment: "#", BlockCommentStart: null, BlockCommentEnd: null);
        var guides = BracketGuideScanner.Build(Lines(
            "def f():",
            "    d = {",
            "        # }",
            "    }"), syntax);

        Assert.Equal(3, Assert.Single(guides).CloseLine);
    }

    [Fact]
    public void Build_PlainSyntax_CountsEverything()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    // }",
            "}"), BracketGuideSyntax.Plain);

        // コメントを解さない設定では 2 行目の } が閉じ括弧になる
        Assert.Equal(1, Assert.Single(guides).CloseLine);
    }

    // ── 壊れたコード ──

    [Fact]
    public void Build_UnclosedBracket_ProducesNoGuideButKeepsGoing()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    foo(",
            "}"));

        // ( は閉じないまま — } は外側の { に対応づく
        var g = Assert.Single(guides);
        Assert.Equal(0, g.OpenLine);
        Assert.Equal(2, g.CloseLine);
    }

    [Fact]
    public void Build_StrayClosingBracket_Ignored()
        => Assert.Empty(BracketGuideScanner.Build(Lines("}", "   }")));

    [Fact]
    public void Build_EmptyBuffer_NoGuides()
        => Assert.Empty(BracketGuideScanner.Build([]));

    // ── いまカーソルがいるブロック ──

    [Fact]
    public void ActiveIndex_PicksInnermostEnclosingPair()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "{",
            "    {",
            "        x;",
            "    }",
            "}"));

        int active = BracketGuideScanner.ActiveIndex(guides, 2, 8);
        Assert.Equal(1, guides[active].OpenLine);
    }

    [Fact]
    public void ActiveIndex_OnTheBracketItself_Counts()
    {
        var guides = BracketGuideScanner.Build(Lines("{", "    x;", "}"));

        Assert.Equal(0, BracketGuideScanner.ActiveIndex(guides, 0, 0));
        Assert.Equal(0, BracketGuideScanner.ActiveIndex(guides, 2, 0));
    }

    [Fact]
    public void ActiveIndex_OutsideEveryPair_ReturnsMinusOne()
    {
        var guides = BracketGuideScanner.Build(Lines("{", "    x;", "}", "after;"));

        Assert.Equal(-1, BracketGuideScanner.ActiveIndex(guides, 3, 0));
    }

    [Fact]
    public void ActiveIndex_BeforeOpeningColumnOnOpenLine_IsOutside()
    {
        var guides = BracketGuideScanner.Build(Lines(
            "if (x) {",
            "    y;",
            "}"));

        Assert.Equal(-1, BracketGuideScanner.ActiveIndex(guides, 0, 0));
        Assert.Equal(0, BracketGuideScanner.ActiveIndex(guides, 0, 7));
    }
}
