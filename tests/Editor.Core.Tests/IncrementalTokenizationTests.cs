using Editor.Core.Syntax;

namespace Editor.Core.Tests;

/// <summary>
/// <see cref="SyntaxEngine.Tokenize"/> は前回の結果を引き継いで変わった行から先だけを分け直す。
/// その結果は「毎回ファイル全体を分けた結果」と<b>寸分違わず</b>一致しなければならない——
/// 状態（ブロックコメント・複数行文字列・フェンス…）を開く／閉じる編集が混ざっても。
/// </summary>
public class IncrementalTokenizationTests
{
    /// <summary>どの言語でも状態を切り替えうる断片。言語ごとに意味を持つものも持たないものも混ぜる。</summary>
    private static readonly string[] Fragments =
    [
        "int x = 1;", "class Foo {", "}", "{", "", "   ", "// comment", "# comment", "-- comment",
        "/*", "*/", "/* inline */ code", "start /* open", "close */ end",
        "\"string\"", "@\"verbatim", "still verbatim\"", "'c'", "`template ${x}", "end`",
        "\"\"\"", "'''", "x = \"\"\"doc\"\"\"", "```", "~~~", "```csharp", "# Heading", "- item **bold**",
        "=begin", "=end", "<!--", "-->", "<tag attr=\"v\">", "</tag>",
        "@\"", "\"@", "@'", "'@", "<#", "#>", "cat <<EOF", "EOF", "<<-END", "END",
        "--[[", "]]", "--[==[", "]==]", "[[long", "string]]",
        "[section]", "key = \"\"\"", "key = '''", "a { color: red; }", "@media x {",
        "fn main() {", "func f() {", "def f():", "SELECT * FROM t", "echo hi", "{\"k\": 1}",
        "  - yaml: value", "@echo off", "set X=1", "日本語の行", "\t\tindented",
    ];

    /// <summary>組み込みの言語すべて（<c>Editor.Core.Syntax.Languages</c> の具象型）。</summary>
    private static ISyntaxLanguage[] BuiltInLanguages() =>
        typeof(SyntaxEngine).Assembly.GetTypes()
            .Where(t => t.Namespace == "Editor.Core.Syntax.Languages" && !t.IsAbstract
                && typeof(ISyntaxLanguage).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (ISyntaxLanguage)Activator.CreateInstance(t)!)
            .ToArray();

    private static ISyntaxLanguage Fresh(string name) =>
        BuiltInLanguages().Single(l => l.Name == name);

    public static TheoryData<string> LineStateLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in BuiltInLanguages())
            if (language is ILineStateSyntaxLanguage)
                data.Add(language.Name);
        return data;
    }

    [Fact]
    public void AllBuiltInLanguages_TokenizeLineByLine()
    {
        var languages = BuiltInLanguages();
        Assert.True(languages.Length >= 19, $"found only {languages.Length} languages");
        Assert.Empty(languages.Where(l => l is not ILineStateSyntaxLanguage).Select(l => l.Name));
    }

    [Theory]
    [MemberData(nameof(LineStateLanguages))]
    public void RandomEdits_MatchFullTokenization(string languageName)
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var random = new Random(seed * 7919 + languageName.Length);
            var engine = new SyntaxEngine();
            engine.SetLanguage(languageName);
            var lines = Enumerable.Range(0, random.Next(0, 60)).Select(_ => Pick(random)).ToList();

            for (int step = 0; step < 60; step++)
            {
                Mutate(lines, random);
                var snapshot = lines.ToArray();
                var actual = engine.Tokenize(snapshot);
                var expected = Fresh(languageName).Tokenize(snapshot);
                AssertSameTokens(expected, actual, $"{languageName} seed={seed} step={step}");
            }
        }
    }

    [Fact]
    public void EditInMiddle_ReusesTokensOfUnaffectedLines()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = Enumerable.Range(0, 2000).Select(i => $"    var v{i} = Call({i}); // note").ToArray();
        var before = engine.Tokenize(lines);

        var edited = (string[])lines.Clone();
        edited[1000] = "    var changed = 1;";
        var after = engine.Tokenize(edited);

        Assert.Same(before[10].Tokens, after[10].Tokens);
        Assert.Same(before[1999].Tokens, after[1999].Tokens);
        Assert.NotSame(before[1000].Tokens, after[1000].Tokens);
        AssertSameTokens(new Editor.Core.Syntax.Languages.CSharpSyntax().Tokenize(edited), after, "middle edit");
    }

    [Fact]
    public void Invalidate_BetweenEdits_KeepsIncrementalBaseline()
    {
        // エンジンは本文が変わるたびに Invalidate() を呼ぶ。それで差分の基準まで捨てると毎打鍵ファイル全体を
        // 分け直すことになる（実際にそうなっていた）。
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = Enumerable.Range(0, 2000).Select(i => $"int a{i} = {i};").ToArray();
        var before = engine.Tokenize(lines);

        engine.Invalidate();
        var edited = (string[])lines.Clone();
        edited[500] = "int changed = 0;";
        var after = engine.Tokenize(edited);

        Assert.Same(before[1500].Tokens, after[1500].Tokens);
        AssertSameTokens(new Editor.Core.Syntax.Languages.CSharpSyntax().Tokenize(edited), after, "invalidate");
    }

    [Fact]
    public void Invalidate_WithSameArrayMutatedInPlace_Retokenizes()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = new[] { "int a;", "int b;", "int c;" };
        engine.Tokenize(lines);

        lines[1] = "/* open";
        engine.Invalidate();
        var actual = engine.Tokenize(lines);

        AssertSameTokens(new Editor.Core.Syntax.Languages.CSharpSyntax().Tokenize(lines), actual, "in place");
    }

    [Fact]
    public void InsertingLines_ShiftsReusedTokensToNewLineNumbers()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = Enumerable.Range(0, 500).Select(i => $"int a{i} = {i};").ToList();
        var before = engine.Tokenize(lines.ToArray());

        lines.InsertRange(100, ["// one", "// two", "// three"]);
        var after = engine.Tokenize(lines.ToArray());

        Assert.Equal(Enumerable.Range(0, lines.Count), after.Select(t => t.Line));
        Assert.Same(before[400].Tokens, after[403].Tokens);
        AssertSameTokens(new Editor.Core.Syntax.Languages.CSharpSyntax().Tokenize(lines.ToArray()), after, "insert");
    }

    [Fact]
    public void OpeningBlockComment_RetokenizesUntilStateConvergesAgain()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = Enumerable.Range(0, 300).Select(i => $"int a{i} = {i};").ToArray();
        engine.Tokenize(lines);

        var opened = (string[])lines.Clone();
        opened[10] = "/* open";
        var commented = engine.Tokenize(opened);
        Assert.All(commented.Skip(11), line => Assert.All(line.Tokens, t => Assert.Equal(TokenKind.Comment, t.Kind)));

        var closed = (string[])opened.Clone();
        closed[20] = "*/";
        var reopened = engine.Tokenize(closed);
        Assert.Contains(reopened[21].Tokens, t => t.Kind == TokenKind.Keyword);
        AssertSameTokens(new Editor.Core.Syntax.Languages.CSharpSyntax().Tokenize(closed), reopened, "close");
    }

    [Fact]
    public void CallerMutatingPreviousArray_DoesNotCorruptNextDiff()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = Enumerable.Range(0, 50).Select(i => $"int a{i};").ToArray();
        engine.Tokenize(lines);

        // 呼び出し側が渡した配列を使い回して書き換えた。前回の行として覚えている内容は変わってはいけない。
        var next = (string[])lines.Clone();
        lines[5] = "/* stale";
        next[30] = "int changed;";
        var actual = engine.Tokenize(next);

        AssertSameTokens(new Editor.Core.Syntax.Languages.CSharpSyntax().Tokenize(next), actual, "mutated");
    }

    [Fact]
    public void LanguageSwitch_DoesNotReuseOtherLanguagesState()
    {
        var engine = new SyntaxEngine();
        engine.SetLanguage("C#");
        var lines = new[] { "/* a", "b */", "# c", "d" };
        engine.Tokenize(lines);

        engine.SetLanguage("Python");
        var python = engine.Tokenize((string[])lines.Clone());

        AssertSameTokens(new Editor.Core.Syntax.Languages.PythonSyntax().Tokenize(lines), python, "switch");
    }

    private static string Pick(Random random) => Fragments[random.Next(Fragments.Length)];

    private static void Mutate(List<string> lines, Random random)
    {
        switch (random.Next(6))
        {
            case 0:
                lines.Insert(random.Next(lines.Count + 1), Pick(random));
                break;
            case 1:
                if (lines.Count > 0) lines.RemoveAt(random.Next(lines.Count));
                break;
            case 2 when lines.Count > 0:
            {
                // 1行の中への打鍵（断片を行の途中へ差し込む）
                int i = random.Next(lines.Count);
                int at = random.Next(lines[i].Length + 1);
                lines[i] = lines[i][..at] + Pick(random) + lines[i][at..];
                break;
            }
            case 3 when lines.Count > 0:
            {
                // 1文字消す
                int i = random.Next(lines.Count);
                if (lines[i].Length > 0)
                {
                    int at = random.Next(lines[i].Length);
                    lines[i] = lines[i].Remove(at, 1);
                }
                break;
            }
            case 4:
            {
                // 複数行の貼り付け
                int at = random.Next(lines.Count + 1);
                lines.InsertRange(at, Enumerable.Range(0, random.Next(1, 6)).Select(_ => Pick(random)));
                break;
            }
            default:
                if (lines.Count > 0) lines[random.Next(lines.Count)] = Pick(random);
                break;
        }
    }

    private static void AssertSameTokens(LineTokens[] expected, LineTokens[] actual, string context)
    {
        Assert.True(expected.Length == actual.Length, $"{context}: line count {expected.Length} != {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Line == actual[i].Line, $"{context}: line index at {i}");
            Assert.True(expected[i].Tokens.SequenceEqual(actual[i].Tokens),
                $"{context}: tokens differ at line {i}: [{string.Join(", ", expected[i].Tokens)}] vs [{string.Join(", ", actual[i].Tokens)}]");
        }
    }
}
