using Editor.Core.Syntax;

namespace Editor.Core.Tests;

public class SyntaxEngineTests
{
    [Fact]
    public void TokenizeVisible_SmallFile_UsesFullDocumentTokens()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = new[] { "class C", "{", "int x;", "}" };

        var tokens = engine.TokenizeVisible(lines, 2, 2);

        Assert.Equal([0, 1, 2, 3], tokens.Select(t => t.Line).ToArray());
    }

    [Fact]
    public void TokenizeVisible_LargeFile_ReturnsOnlyRequestedOriginalLines()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("JSON");
        var lines = Enumerable.Range(0, SyntaxEngine.LargeFileLineThreshold + 20)
            .Select(i => $"{{ \"value\": {i} }}")
            .ToArray();

        var tokens = engine.TokenizeVisible(lines, 5010, 5015);

        Assert.Equal([5010, 5011, 5012, 5013, 5014, 5015], tokens.Select(t => t.Line).ToArray());
        Assert.All(tokens, line => Assert.NotEmpty(line.Tokens));
    }

    [Fact]
    public void TokenizeVisible_LargeFile_CachesSameRangeForSameLineArray()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("JSON");
        var lines = Enumerable.Range(0, SyntaxEngine.LargeFileLineThreshold + 1)
            .Select(i => $"{{ \"value\": {i} }}")
            .ToArray();

        var first = engine.TokenizeVisible(lines, 4000, 4010);
        var second = engine.TokenizeVisible(lines, 4000, 4010);

        Assert.Same(first, second);
    }

    [Fact]
    public void TokenizeVisible_LargeContextSensitiveFile_PreservesFullDocumentState()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = Enumerable.Range(0, SyntaxEngine.LargeFileLineThreshold + 20)
            .Select(i => i == 0 ? "/*" : $"comment line {i}")
            .ToArray();

        var tokens = engine.TokenizeVisible(lines, 5010, 5010);
        var targetLine = Assert.Single(tokens, t => t.Line == 5010);

        Assert.Contains(targetLine.Tokens, t => t.Kind == TokenKind.Comment);
    }
}
