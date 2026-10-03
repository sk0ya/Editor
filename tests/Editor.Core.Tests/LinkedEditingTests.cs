using Editor.Core.Config;
using Editor.Core.Editing;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class LinkedEditingTests
{
    // ── フォールバック: タグ対応の探索 ──

    private static LinkedEditingRanges? Find(string text, int line, int col, MarkupDialect dialect = MarkupDialect.Xml)
    {
        var lines = text.Split('\n');
        return MarkupTagPairFinder.Find(l => lines[l], lines.Length, line, col, dialect);
    }

    [Fact]
    public void Find_OpenTag_PairsWithClose_AcrossLines()
    {
        var r = Find("<Grid>\n  <Button/>\n</Grid>", 0, 2);
        Assert.NotNull(r);
        Assert.Equal([new LinkedRange(0, 1, 5), new LinkedRange(2, 2, 6)], r!.Ranges);
    }

    [Fact]
    public void Find_CloseTag_PairsWithOpen()
    {
        var r = Find("<a><b></b></a>", 0, 12);
        Assert.Equal([new LinkedRange(0, 12, 13), new LinkedRange(0, 1, 2)], r!.Ranges);
    }

    [Fact]
    public void Find_CaretRightAfterName_Counts()
    {
        var r = Find("<div>x</div>", 0, 4);
        Assert.NotNull(r);
    }

    [Fact]
    public void Find_NestedSameName_PairsInnermostCorrectly()
    {
        var r = Find("<g>\n<g>\n</g>\n</g>", 0, 1);
        Assert.Equal(new LinkedRange(3, 2, 3), r!.Ranges[1]);
    }

    [Fact]
    public void Find_SelfClosingTag_HasNoPair()
        => Assert.Null(Find("<a>\n<br/>\n</a>", 1, 2));

    [Fact]
    public void Find_HtmlVoidElement_HasNoPair()
        => Assert.Null(Find("<p><br></p>", 0, 5, MarkupDialect.Html));

    [Fact]
    public void Find_IgnoresTagsInsideMultiLineComment()
    {
        // コメント内の </a> を閉じタグと読むと、外側の <a> が誤ったタグと対になる
        var r = Find("<a>\n<!-- </a>\n<a> -->\n</a>", 0, 1);
        Assert.Equal(new LinkedRange(3, 2, 3), r!.Ranges[1]);
    }

    [Fact]
    public void Find_CaretInsideComment_IsNotATag()
        => Assert.Null(Find("<!-- <a></a> -->", 0, 6));

    [Fact]
    public void Find_IgnoresCdata()
    {
        var r = Find("<s>\n<![CDATA[ </s> <s> ]]>\n</s>", 0, 1);
        Assert.Equal(new LinkedRange(2, 2, 3), r!.Ranges[1]);
    }

    [Fact]
    public void Find_LessThanInAttributeValue_IsNotATag()
    {
        var r = Find("<Run Text=\"a <b> c\" X='1 > 0'>\n</Run>", 0, 2);
        Assert.Equal(new LinkedRange(1, 2, 5), r!.Ranges[1]);
    }

    [Fact]
    public void Find_MultiLineAttributes()
    {
        var r = Find("<Button\n  Content=\"OK\"\n  Width=\"10\">\n</Button>", 0, 3);
        Assert.Equal(new LinkedRange(3, 2, 8), r!.Ranges[1]);
    }

    [Fact]
    public void Find_HtmlScriptContent_IsRawText()
    {
        var r = Find("<div>\n<script>if (a<b) x();</script>\n</div>", 0, 2, MarkupDialect.Html);
        Assert.Equal(new LinkedRange(2, 2, 5), r!.Ranges[1]);
    }

    [Fact]
    public void Find_UnfinishedTag_DoesNotSwallowNextTag()
    {
        // 書きかけの "<di" の後ろの "<span>" はちゃんとタグとして読む
        var r = Find("<di\n<span></span>", 1, 2);
        Assert.Equal(new LinkedRange(1, 8, 12), r!.Ranges[1]);
    }

    [Fact]
    public void Find_NamespacedXamlNames()
    {
        var r = Find("<local:View.Resources>\n</local:View.Resources>", 0, 5);
        Assert.Equal(new LinkedRange(1, 2, 22), r!.Ranges[1]);
    }

    [Fact]
    public void Find_TooLarge_ReturnsNull()
    {
        var big = "<a>" + new string('x', MarkupTagPairFinder.MaxCharacters) + "</a>";
        Assert.Null(Find(big, 0, 1));
    }

    [Theory]
    [InlineData("App.xaml", MarkupDialect.Xml)]
    [InlineData("a.csproj", MarkupDialect.Xml)]
    [InlineData("index.HTML", MarkupDialect.Html)]
    public void DialectFor_Markup(string path, MarkupDialect expected)
        => Assert.Equal(expected, MarkupTagPairFinder.DialectFor(path));

    [Theory]
    [InlineData("App.tsx")]
    [InlineData("App.jsx")]
    [InlineData("Program.cs")]
    [InlineData(null)]
    public void DialectFor_NonMarkup_IsNull(string? path)
        => Assert.Null(MarkupTagPairFinder.DialectFor(path));

    // ── セッション: 差分から写す ──

    [Fact]
    public void Session_Insert_MirrorsToOtherLine()
    {
        var lines = new[] { "<div>", "</div>" };
        var s = LinkedEditingSession.TryStart(new([new(0, 1, 4), new(1, 2, 5)]), l => lines[l])!;
        var u = s.Apply(0, "<div>", "<divx>", 4, l => lines[l]);
        Assert.Equal("</divx>", u!.Lines[1]);
        Assert.Equal(0, u.CaretShift);
    }

    [Fact]
    public void Session_EditCloseTagOnSameLine_ShiftsCaret()
    {
        // 閉じタグを編集すると、同じ行で前にある開きタグが伸びるのでカーソル列もずれる
        const string old = "<a>x</a>";
        var s = LinkedEditingSession.TryStart(new([new(0, 1, 2), new(0, 6, 7)]), _ => old)!;
        var u = s.Apply(0, old, "<a>x</ab>", 7, _ => old);
        Assert.Equal("<ab>x</ab>", u!.Lines[0]);
        Assert.Equal(1, u.CaretShift);
    }

    [Fact]
    public void Session_RepeatedChars_ResolvedTowardCaret()
    {
        var lines = new[] { "<dd>", "</dd>" };
        var s = LinkedEditingSession.TryStart(new([new(0, 1, 3), new(1, 2, 4)]), l => lines[l])!;
        var u = s.Apply(0, "<dd>", "<d>", 2, l => lines[l]); // Backspace at col 2
        Assert.Equal("</d>", u!.Lines[1]);
    }

    [Fact]
    public void Session_SpaceEndsLinking()
    {
        var lines = new[] { "<div>", "</div>" };
        var s = LinkedEditingSession.TryStart(new([new(0, 1, 4), new(1, 2, 5)]), l => lines[l])!;
        Assert.Null(s.Apply(0, "<div>", "<div >", 4, l => lines[l]));
    }

    [Fact]
    public void Session_EditOutsideRange_Ends()
    {
        var lines = new[] { "<div>x", "</div>" };
        var s = LinkedEditingSession.TryStart(new([new(0, 1, 4), new(1, 2, 5)]), l => lines[l])!;
        Assert.Null(s.Apply(0, "<div>x", "<div>xy", 6, l => lines[l]));
    }

    [Fact]
    public void Session_MismatchedRanges_DoNotStart()
    {
        var lines = new[] { "<div>", "</span>" };
        Assert.Null(LinkedEditingSession.TryStart(new([new(0, 1, 4), new(1, 2, 6)]), l => lines[l]));
    }

    [Fact]
    public void Session_WordPattern_FromServerIsHonoured()
    {
        var lines = new[] { "<div>", "</div>" };
        var s = LinkedEditingSession.TryStart(new([new(0, 1, 4), new(1, 2, 5)], "[a-z]+"), l => lines[l])!;
        Assert.Null(s.Apply(0, "<div>", "<div1>", 4, l => lines[l]));
    }

    // ── エンジン統合 ──

    private static VimEngine CreateEngine(string text, string path = "C:\\t\\View.xaml")
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(text);
        engine.CurrentBuffer.FilePath = path;
        return engine;
    }

    private static void Type(VimEngine engine, string keys)
    {
        foreach (var c in keys) engine.ProcessKey(c.ToString());
    }

    [Fact]
    public void Engine_InsertInOpenTag_RenamesCloseTag()
    {
        var engine = CreateEngine("<Grid>\n  <Button/>\n</Grid>");
        engine.SetCursorPosition(new CursorPosition(0, 4));
        engine.ProcessKey("i");
        Type(engine, "X");
        Assert.Equal("<GriXd>\n  <Button/>\n</GriXd>", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(2, engine.LinkedEditingRanges.Count);
    }

    [Fact]
    public void Engine_EditCloseTag_RenamesOpenTag_AndKeepsCaret()
    {
        var engine = CreateEngine("<a>x</a>");
        engine.SetCursorPosition(new CursorPosition(0, 6));
        engine.ProcessKey("a"); // append after 'a' → caret at col 7
        Type(engine, "bc");
        Assert.Equal("<abc>x</abc>", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(new CursorPosition(0, 11), engine.Cursor);
    }

    [Fact]
    public void Engine_ChangeInnerWord_ThenType_RenamesBoth_UndoOnce()
    {
        var engine = CreateEngine("<Grid>\n</Grid>");
        engine.SetCursorPosition(new CursorPosition(0, 2));
        Type(engine, "ciw");
        Assert.Equal("<>\n</>", engine.CurrentBuffer.Text.GetText());
        Type(engine, "Panel");
        engine.ProcessKey("Escape");
        Assert.Equal("<Panel>\n</Panel>", engine.CurrentBuffer.Text.GetText());

        engine.ProcessKey("u");
        Assert.Equal("<Grid>\n</Grid>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_Backspace_Mirrors()
    {
        var engine = CreateEngine("<abc></abc>");
        engine.SetCursorPosition(new CursorPosition(0, 3));
        engine.ProcessKey("a"); // caret col 4
        engine.ProcessKey("Back");
        Assert.Equal("<ab></ab>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_LeavingRange_EndsLinking()
    {
        var engine = CreateEngine("<a>xy</a>");
        engine.SetCursorPosition(new CursorPosition(0, 1));
        engine.ProcessKey("a"); // col 2
        Type(engine, "b");
        Assert.Equal("<ab>xy</ab>", engine.CurrentBuffer.Text.GetText());
        engine.ProcessKey("Right"); // カーソルが名前の外へ
        Assert.Empty(engine.LinkedEditingRanges);
        Type(engine, "z");
        Assert.Equal("<ab>zxy</ab>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_Disabled_ByOption()
    {
        var engine = CreateEngine("<a></a>");
        engine.ExecuteExCommand("set nolinkedediting");
        engine.SetCursorPosition(new CursorPosition(0, 1));
        engine.ProcessKey("a");
        Type(engine, "b");
        Assert.Equal("<ab></a>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_NonMarkupFile_WithoutProvider_DoesNothing()
    {
        var engine = CreateEngine("<a></a>", "C:\\t\\App.tsx");
        engine.SetCursorPosition(new CursorPosition(0, 1));
        engine.ProcessKey("a");
        Type(engine, "b");
        Assert.Equal("<ab></a>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_ProviderRanges_UsedForTsx()
    {
        var engine = CreateEngine("<a></a>", "C:\\t\\App.tsx");
        long? askedVersion = null;
        engine.LinkedEditingRangeProvider = (line, col, version) =>
        {
            askedVersion = version;
            return new LinkedEditingRanges([new(0, 1, 2), new(0, 5, 6)]);
        };
        engine.SetCursorPosition(new CursorPosition(0, 1));
        engine.ProcessKey("a");
        var versionBeforeTyping = engine.CurrentBuffer.Text.Version;
        Type(engine, "b");
        Assert.Equal("<ab></ab>", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(versionBeforeTyping, askedVersion);
    }

    [Fact]
    public void Engine_ProviderNone_SuppressesFallback()
    {
        var engine = CreateEngine("<a></a>");
        engine.LinkedEditingRangeProvider = (_, _, _) => LinkedEditingRanges.None;
        engine.SetCursorPosition(new CursorPosition(0, 1));
        engine.ProcessKey("a");
        Type(engine, "b");
        Assert.Equal("<ab></a>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_Suspended_DoesNothing()
    {
        var engine = CreateEngine("<a></a>");
        engine.LinkedEditingSuspended = true;
        engine.SetCursorPosition(new CursorPosition(0, 1));
        engine.ProcessKey("a");
        Type(engine, "b");
        Assert.Equal("<ab></a>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_NormalModeEdit_DoesNotLink()
    {
        var engine = CreateEngine("<ab></ab>");
        engine.SetCursorPosition(new CursorPosition(0, 2));
        engine.ProcessKey("x");
        Assert.Equal("<a></ab>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_ExternalTextChange_EndsSession()
    {
        var engine = CreateEngine("<a></a>");
        engine.SetCursorPosition(new CursorPosition(0, 1));
        engine.ProcessKey("a");
        Type(engine, "b");
        engine.SetText("<ab>\n<q></q>\n</ab>");
        Assert.Empty(engine.LinkedEditingRanges);
    }

    [Fact]
    public void Engine_PlainMode_LinksAndUndoesTogether()
    {
        var engine = CreateEngine("<a></a>");
        engine.SetVimEnabled(false);
        engine.SetCursorPosition(new CursorPosition(0, 2));
        Type(engine, "bc");
        Assert.Equal("<abc></abc>", engine.CurrentBuffer.Text.GetText());
        engine.ProcessKey("z", ctrl: true);
        Assert.Equal("<a></a>", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_UnpairedNewTag_TypingDoesNotLink()
    {
        var engine = CreateEngine("<root>\n\n</root>");
        engine.SetCursorPosition(new CursorPosition(1, 0));
        engine.ProcessKey("i");
        Type(engine, "<item");
        Assert.Equal("<root>\n<item\n</root>", engine.CurrentBuffer.Text.GetText());
        Assert.Empty(engine.LinkedEditingRanges);
    }
}

public class LspLinkedEditingRangesParserTests
{
    private static LinkedEditingRanges Parse(string json)
        => Editor.Core.Lsp.LspLinkedEditingRangesParser.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Parse_RangesAndWordPattern()
    {
        var r = Parse("""{"ranges":[{"start":{"line":0,"character":1},"end":{"line":0,"character":4}},{"start":{"line":2,"character":2},"end":{"line":2,"character":5}}],"wordPattern":"[a-z]+"}""");
        Assert.Equal([new LinkedRange(0, 1, 4), new LinkedRange(2, 2, 5)], r.Ranges);
        Assert.Equal("[a-z]+", r.WordPattern);
    }

    [Fact]
    public void Parse_Null_IsNone()
        => Assert.Same(LinkedEditingRanges.None, Parse("null"));

    [Fact]
    public void Parse_MultiLineRange_IsNone()
        => Assert.Same(LinkedEditingRanges.None, Parse("""{"ranges":[{"start":{"line":0,"character":1},"end":{"line":1,"character":0}},{"start":{"line":2,"character":2},"end":{"line":2,"character":5}}]}"""));
}
