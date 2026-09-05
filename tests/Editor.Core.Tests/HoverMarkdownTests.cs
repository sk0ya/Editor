using Editor.Core.Text;

namespace Editor.Core.Tests;

public class HoverMarkdownTests
{
    [Fact]
    public void Parse_CodeFence_BecomesCodeBlockWithLanguage()
    {
        var blocks = HoverMarkdown.Parse("```csharp\nvoid Run(int count)\n```");

        var block = Assert.Single(blocks);
        Assert.Equal(HoverBlockKind.Code, block.Kind);
        Assert.Equal("csharp", block.Language);
        Assert.Equal("void Run(int count)", block.Code);
    }

    [Fact]
    public void Parse_FenceThenSummary_KeepsBothInOrder()
    {
        var blocks = HoverMarkdown.Parse("```csharp\nint Count\n```\n\nThe number of items.");

        Assert.Equal(2, blocks.Count);
        Assert.Equal(HoverBlockKind.Code, blocks[0].Kind);
        Assert.Equal(HoverBlockKind.Text, blocks[1].Kind);
        Assert.Equal("The number of items.", Text(blocks[1]));
    }

    /// <summary>サーバーは識別子の <c>_</c> を <c>\_</c> と書いて返す。外さないとコードに無い
    /// バックスラッシュがそのまま見える。</summary>
    [Fact]
    public void Parse_EscapedUnderscore_IsUnescaped()
    {
        var blocks = HoverMarkdown.Parse(@"'\_value' は null ではありません");

        Assert.Equal("'_value' は null ではありません", Text(Assert.Single(blocks)));
    }

    /// <summary>アンダースコアは斜体の記号として扱わない——<c>MAX_LEN</c> のような識別子が
    /// 記号ごと消えてしまうため。</summary>
    [Fact]
    public void Parse_UnderscoresInIdentifier_AreNotItalic()
    {
        var blocks = HoverMarkdown.Parse("MAX_LEN と _value は定数です");

        var block = Assert.Single(blocks);
        Assert.Equal("MAX_LEN と _value は定数です", Text(block));
        Assert.All(block.Spans, s => Assert.NotEqual(HoverSpanStyle.Italic, s.Style));
    }

    /// <summary>掛け算の <c>*</c> を斜体と読むと、記号ごと本文から消える。</summary>
    [Fact]
    public void Parse_AsterisksAroundSpaces_AreNotItalic()
    {
        var block = Assert.Single(HoverMarkdown.Parse("Returns width * height * depth"));

        Assert.Equal("Returns width * height * depth", Text(block));
        Assert.All(block.Spans, s => Assert.NotEqual(HoverSpanStyle.Italic, s.Style));
    }

    [Fact]
    public void Parse_InlineMarkup_BecomesStyledSpans()
    {
        var block = Assert.Single(HoverMarkdown.Parse("**Summary** of `Count` and *maybe* more"));

        Assert.Contains(block.Spans, s => s.Style == HoverSpanStyle.Bold && s.Text == "Summary");
        Assert.Contains(block.Spans, s => s.Style == HoverSpanStyle.Code && s.Text == "Count");
        Assert.Contains(block.Spans, s => s.Style == HoverSpanStyle.Italic && s.Text == "maybe");
        Assert.Equal("Summary of Count and maybe more", Text(block));
    }

    [Fact]
    public void Parse_HorizontalRule_SeparatesSignatureFromSummary()
    {
        var blocks = HoverMarkdown.Parse("```ts\nconst x: number\n```\n---\nA counter.");

        Assert.Collection(blocks,
            b => Assert.Equal(HoverBlockKind.Code, b.Kind),
            b => Assert.Equal(HoverBlockKind.Rule, b.Kind),
            b => Assert.Equal(HoverBlockKind.Text, b.Kind));
    }

    /// <summary>先頭・末尾の区切りは間に何も無いので出さない（線だけのポップアップにしない）。</summary>
    [Fact]
    public void Parse_LeadingAndTrailingRules_AreDropped()
    {
        var blocks = HoverMarkdown.Parse("---\nA counter.\n\n---\n");

        Assert.Equal(HoverBlockKind.Text, Assert.Single(blocks).Kind);
    }

    [Fact]
    public void Parse_PlainText_PassesThroughWithLineBreaks()
    {
        var block = Assert.Single(HoverMarkdown.Parse("int Count { get; }\n要素の数。"));

        Assert.Equal("int Count { get; }\n要素の数。", Text(block));
    }

    [Fact]
    public void Parse_Heading_BecomesBoldLine()
    {
        var block = Assert.Single(HoverMarkdown.Parse("### Remarks"));

        var span = Assert.Single(block.Spans);
        Assert.Equal(HoverSpanStyle.Bold, span.Style);
        Assert.Equal("Remarks", span.Text);
    }

    [Fact]
    public void Parse_Link_KeepsLabelDropsUrl()
    {
        var block = Assert.Single(HoverMarkdown.Parse("See [the docs](https://example.com/x) for details"));

        Assert.Equal("See the docs for details", Text(block));
    }

    [Fact]
    public void Parse_BlankOrWhitespace_IsEmpty()
    {
        Assert.Empty(HoverMarkdown.Parse(null));
        Assert.Empty(HoverMarkdown.Parse("   \n\n"));
    }

    [Fact]
    public void PlainText_FlattensBlocks()
    {
        var text = HoverMarkdown.PlainText("```csharp\nint Count\n```\n\n**要素**の数。");

        Assert.Equal("int Count\n要素の数。", text);
    }

    private static string Text(HoverBlock block) =>
        string.Concat(block.Spans.Select(s => s.Style == HoverSpanStyle.LineBreak ? "\n" : s.Text));
}
