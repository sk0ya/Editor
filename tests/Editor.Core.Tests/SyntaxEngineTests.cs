using Editor.Core.Syntax;
using Editor.Core.Syntax.Languages;

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

    [Fact]
    public void Registry_ResolvesLuaAndRubyByExtension()
    {
        var engine = new SyntaxEngine();
        engine.DetectLanguage("script.lua");
        Assert.Equal("Lua", engine.LanguageName);

        engine.DetectLanguage("script.rb");
        Assert.Equal("Ruby", engine.LanguageName);
    }
}

public class LuaSyntaxTests
{
    [Fact]
    public void Tokenize_ClassifiesKeywordsStringsAndComments()
    {
        var lang = new LuaSyntax();
        var lines = new[] { "local x = \"hi\" -- comment", "function foo() end" };

        var tokens = lang.Tokenize(lines);

        Assert.Contains(tokens[0].Tokens, t => t.Kind == TokenKind.Keyword);
        Assert.Contains(tokens[0].Tokens, t => t.Kind == TokenKind.String);
        Assert.Contains(tokens[0].Tokens, t => t.Kind == TokenKind.Comment);
        Assert.Contains(tokens[1].Tokens, t => t.Kind == TokenKind.Keyword);
    }

    [Fact]
    public void Tokenize_LongStringSpansMultipleLines()
    {
        var lang = new LuaSyntax();
        var lines = new[] { "local s = [[", "multi", "line]] .. x" };

        var tokens = lang.Tokenize(lines);

        Assert.Contains(tokens[0].Tokens, t => t.Kind == TokenKind.String);
        Assert.Contains(tokens[1].Tokens, t => t.Kind == TokenKind.String);
        Assert.Contains(tokens[2].Tokens, t => t.Kind == TokenKind.String);
    }

    [Fact]
    public void Tokenize_LongCommentSpansMultipleLines()
    {
        var lang = new LuaSyntax();
        var lines = new[] { "--[[", "not code", "]]", "local x = 1" };

        var tokens = lang.Tokenize(lines);

        Assert.Contains(tokens[1].Tokens, t => t.Kind == TokenKind.Comment);
        Assert.Contains(tokens[3].Tokens, t => t.Kind == TokenKind.Keyword);
    }
}

public class RubySyntaxTests
{
    [Fact]
    public void Tokenize_ClassifiesKeywordsSymbolsAndComments()
    {
        var lang = new RubySyntax();
        var lines = new[] { "def foo(x) # comment", "  :sym" };

        var tokens = lang.Tokenize(lines);

        Assert.Contains(tokens[0].Tokens, t => t.Kind == TokenKind.Keyword);
        Assert.Contains(tokens[0].Tokens, t => t.Kind == TokenKind.Comment);
        Assert.Contains(tokens[1].Tokens, t => t.Kind == TokenKind.String);
    }

    [Fact]
    public void Tokenize_BeginEndBlockCommentIsAllComment()
    {
        var lang = new RubySyntax();
        var lines = new[] { "=begin", "not code", "=end" };

        var tokens = lang.Tokenize(lines);

        Assert.All(tokens, lt => Assert.Contains(lt.Tokens, t => t.Kind == TokenKind.Comment));
    }
}
