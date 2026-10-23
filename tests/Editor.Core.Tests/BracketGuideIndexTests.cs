using Editor.Core.Editing;

namespace Editor.Core.Tests;

/// <summary>
/// <see cref="BracketGuideIndex"/> は編集のたびに変わった行から先だけを走査する。結果は変更前の全体走査
/// （<see cref="ReferenceBracketGuideScanner"/>）と寸分違わず一致しなければならない——括弧の対応が
/// 遠くまで組み変わる編集（開き括弧を足す・コメントを開く）や行の挿入・削除が混ざっても。
/// </summary>
public class BracketGuideIndexTests
{
    private static readonly string[] Fragments =
    [
        "{", "}", "(", ")", "[", "]", "{ }", "if (x) {", "} else {", "foo(a, [1, 2]);", "    {", "    }",
        "// { comment", "/* {", "} */", "/* inline { } */ {", "\"{\"", "'}'", "\"unterminated {", "'(",
        "x = \"a\\\"{\";", "", "   ", "text", "{{{{{{{{{{{{{{{{", "}}}}}}}}", "]])", "<!-- {", "--> }", "# {", "-- (",
        "\t{", "日本語 { 括弧 }",
    ];

    private static readonly BracketGuideSyntax[] Syntaxes =
    [
        BracketGuideSyntax.CFamily,
        BracketGuideSyntax.Plain,
        new("#", null, null, true, false),
        new(null, "<!--", "-->", true, false),
        new("--", "/*", "*/", false, true),
    ];

    [Fact]
    public void Build_MatchesPreviousImplementation()
    {
        for (int seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var lines = Enumerable.Range(0, random.Next(0, 80)).Select(_ => Pick(random)).ToArray();
            var syntax = Syntaxes[seed % Syntaxes.Length];
            AssertSame(ReferenceBracketGuideScanner.Build(lines, syntax), BracketGuideScanner.Build(lines, syntax), $"seed={seed}");
        }
    }

    [Fact]
    public void RandomEdits_MatchFullScan()
    {
        for (int seed = 0; seed < 120; seed++)
        {
            var random = new Random(seed * 31 + 7);
            var syntax = Syntaxes[seed % Syntaxes.Length];
            var index = new BracketGuideIndex();
            var lines = Enumerable.Range(0, random.Next(0, 80)).Select(_ => Pick(random)).ToList();
            for (int step = 0; step < 80; step++)
            {
                Mutate(lines, random);
                var snapshot = lines.ToArray();
                AssertSame(ReferenceBracketGuideScanner.Build(snapshot, syntax), index.Update(snapshot, syntax),
                    $"seed={seed} step={step}");
            }
        }
    }

    [Fact]
    public void DeepNesting_BeyondMaxDepth_MatchesFullScan()
    {
        var random = new Random(5);
        var index = new BracketGuideIndex();
        var lines = new List<string>();
        lines.AddRange(Enumerable.Repeat("{{{{{{{{", 12));     // 96 段＝上限 64 を超える
        lines.AddRange(Enumerable.Range(0, 40).Select(i => $"x{i};"));
        lines.AddRange(Enumerable.Repeat("}}}}}}}}", 12));
        for (int step = 0; step < 150; step++)
        {
            Mutate(lines, random);
            var snapshot = lines.ToArray();
            AssertSame(ReferenceBracketGuideScanner.Build(snapshot), index.Update(snapshot), $"step={step}");
        }
    }

    [Fact]
    public void GuideLimit_EditsAroundTheCap_MatchFullScan()
    {
        // 上限ちょうど手前から上限超えまでを行き来する。上限で切れた結果を引き継いではいけない。
        var index = new BracketGuideIndex();
        var lines = new List<string>();
        for (int i = 0; i < BracketGuideScanner.MaxGuides - 3; i++) { lines.Add("{"); lines.Add("}"); }
        var random = new Random(11);
        for (int step = 0; step < 30; step++)
        {
            int at = random.Next(2) == 0 ? random.Next(Math.Min(50, lines.Count)) : lines.Count - random.Next(Math.Min(50, lines.Count));
            switch (step % 3)
            {
                case 0: lines.InsertRange(Math.Clamp(at, 0, lines.Count), ["{", "}", "{", "}"]); break;
                case 1: lines.InsertRange(Math.Clamp(at, 0, lines.Count), ["(", ")"]); break;
                default: if (lines.Count > 4) lines.RemoveRange(Math.Clamp(at, 0, lines.Count - 4), 4); break;
            }
            var snapshot = lines.ToArray();
            AssertSame(ReferenceBracketGuideScanner.Build(snapshot), index.Update(snapshot), $"step={step}");
        }
    }

    [Fact]
    public void CharacterLimit_DropsGuidesAndRecovers()
    {
        var index = new BracketGuideIndex();
        var small = new[] { "{", "x", "}" };
        Assert.Single(index.Update(small));

        var huge = new[] { "{", new string('x', BracketGuideScanner.MaxCharacters), "}" };
        Assert.Empty(index.Update(huge));

        AssertSame(ReferenceBracketGuideScanner.Build(small), index.Update((string[])small.Clone()), "recover");
    }

    [Fact]
    public void SyntaxChange_DoesNotReusePreviousScan()
    {
        var index = new BracketGuideIndex();
        var lines = new[] { "{", "# }", "}" };
        index.Update(lines, BracketGuideSyntax.CFamily);
        var hash = new BracketGuideSyntax("#", null, null, true, false);
        AssertSame(ReferenceBracketGuideScanner.Build(lines, hash), index.Update((string[])lines.Clone(), hash), "syntax");
    }

    [Fact]
    public void EditInsideLine_ReusesUnaffectedLines()
    {
        var index = new BracketGuideIndex();
        var lines = new List<string>();
        for (int i = 0; i < 500; i++) { lines.Add("void M() {"); lines.Add("    Call(1);"); lines.Add("}"); }
        var before = index.Update(lines.ToArray());
        lines[700] = "    Call(1, 2);";
        var after = index.Update(lines.ToArray());
        AssertSame(ReferenceBracketGuideScanner.Build(lines.ToArray()), after, "inside line");
        Assert.Equal(before.Count, after.Count);
    }

    private static string Pick(Random random) => Fragments[random.Next(Fragments.Length)];

    private static void Mutate(List<string> lines, Random random)
    {
        switch (random.Next(6))
        {
            case 0: lines.Insert(random.Next(lines.Count + 1), Pick(random)); break;
            case 1: if (lines.Count > 0) lines.RemoveAt(random.Next(lines.Count)); break;
            case 2 when lines.Count > 0:
            {
                int i = random.Next(lines.Count);
                int at = random.Next(lines[i].Length + 1);
                lines[i] = lines[i][..at] + Pick(random) + lines[i][at..];
                break;
            }
            case 3 when lines.Count > 0:
            {
                int i = random.Next(lines.Count);
                if (lines[i].Length > 0) lines[i] = lines[i].Remove(random.Next(lines[i].Length), 1);
                break;
            }
            case 4:
                lines.InsertRange(random.Next(lines.Count + 1), Enumerable.Range(0, random.Next(1, 6)).Select(_ => Pick(random)));
                break;
            default:
                if (lines.Count > 0) lines[random.Next(lines.Count)] = Pick(random);
                break;
        }
    }

    private static void AssertSame(IReadOnlyList<BracketGuide> expected, IReadOnlyList<BracketGuide> actual, string context)
    {
        Assert.True(expected.Count == actual.Count, $"{context}: {expected.Count} guides expected, got {actual.Count}");
        for (int i = 0; i < expected.Count; i++)
            Assert.True(expected[i] == actual[i], $"{context}: guide {i}: {expected[i]} vs {actual[i]}");
    }
}
