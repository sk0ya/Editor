using Editor.Core.Syntax;
using Editor.Core.Syntax.Languages;

namespace Editor.Core.Tests;

/// <summary>
/// Markdown highlighting for inline links. The destination token has to reach the balanced closing
/// paren; stopping at the first <c>)</c> left the tail of the path painted as body text.
/// </summary>
public class MarkdownSyntaxTests
{
    private static SyntaxToken[] Tokenize(string line) =>
        new MarkdownSyntax().Tokenize([line])[0].Tokens.ToArray();

    [Fact]
    public void Link_DestinationTokenCoversBalancedParens()
    {
        const string line = "[aa](aa(bb)_cc.md)";

        var tokens = Tokenize(line);

        var text = Assert.Single(tokens, t => t.Kind == TokenKind.Identifier);
        Assert.Equal("[aa]", line.Substring(text.StartColumn, text.Length));

        var dest = Assert.Single(tokens, t => t.Kind == TokenKind.Comment);
        Assert.Equal("(aa(bb)_cc.md)", line.Substring(dest.StartColumn, dest.Length));
    }

    [Fact]
    public void Image_TokenStartsAtTheBang()
    {
        const string line = "![alt](img(1).png)";

        var tokens = Tokenize(line);

        var text = Assert.Single(tokens, t => t.Kind == TokenKind.Identifier);
        Assert.Equal("![alt]", line.Substring(text.StartColumn, text.Length));

        var dest = Assert.Single(tokens, t => t.Kind == TokenKind.Comment);
        Assert.Equal("(img(1).png)", line.Substring(dest.StartColumn, dest.Length));
    }

    [Fact]
    public void Link_TailAfterTheDestinationIsNotSwallowed()
    {
        const string line = "[aa](a(b).md) と **太字**";

        var tokens = Tokenize(line);

        Assert.Contains(tokens, t => t.Kind == TokenKind.Operator
            && line.Substring(t.StartColumn, t.Length) == "**太字**");
    }

    [Fact]
    public void PlainBrackets_AreNotHighlightedAsALink()
    {
        var tokens = Tokenize("[not a link] and (not a dest)");

        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Comment);
    }
}
