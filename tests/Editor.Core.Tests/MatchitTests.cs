using Editor.Core.Config;
using Editor.Core.Engine;

namespace Editor.Core.Tests;

public class MatchitTests
{
    private VimEngine CreateEngine(string text = "", string? filePath = null, VimConfig? config = null)
    {
        var engine = new VimEngine(config ?? new VimConfig());
        if (!string.IsNullOrEmpty(text))
            engine.SetText(text);
        if (filePath != null)
            engine.CurrentBuffer.FilePath = filePath;
        return engine;
    }

    // ─── Regression: plain bracket matching must keep working unchanged ───

    [Fact]
    public void Percent_BracketMatching_StillWorks()
    {
        var engine = CreateEngine("int f() { return 0; }");
        engine.ProcessKey("f");
        engine.ProcessKey("{");
        Assert.Equal(8, engine.Cursor.Column);

        engine.ProcessKey("%");
        var line = engine.CurrentBuffer.Text.GetLine(0);
        Assert.Equal(line.LastIndexOf('}'), engine.Cursor.Column);
    }

    [Fact]
    public void Percent_BracketMatching_WorksWithMatchitLanguageAlsoConfigured()
    {
        // Even on a .lua file, brackets still take priority over keyword chains.
        var engine = CreateEngine("local t = {1, 2}", "test.lua");
        engine.ProcessKey("0");
        var line = engine.CurrentBuffer.Text.GetLine(0);
        int openCol = line.IndexOf('{');
        for (int i = 0; i < openCol; i++) engine.ProcessKey("l");

        engine.ProcessKey("%");
        Assert.Equal(line.LastIndexOf('}'), engine.Cursor.Column);
    }

    // ─── Lua keyword chains ───

    [Fact]
    public void Percent_Lua_IfJumpsToEnd_SkippingMiddles()
    {
        var text = "if a then\n  print(a)\nelseif b then\n  print(b)\nelse\n  print(c)\nend";
        var engine = CreateEngine(text, "test.lua");
        // Cursor starts at (0,0) on "if"
        engine.ProcessKey("%");
        Assert.Equal(6, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Percent_Lua_EndJumpsBackToIf()
    {
        var text = "if a then\n  print(a)\nelseif b then\n  print(b)\nelse\n  print(c)\nend";
        var engine = CreateEngine(text, "test.lua");
        engine.ProcessKey("G"); // last line, "end"
        Assert.Equal(6, engine.Cursor.Line);

        engine.ProcessKey("%");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Percent_Ruby_MiddleKeyword_JumpsToNextSiblingNotEnd()
    {
        // Ruby's if-chain has no "then" slot, so this cleanly exercises "next sibling"
        // without an incidental same-line keyword (Lua's "elseif ... then" would land on
        // its own "then" first, which is also correct matchit behavior but less clear here).
        var text = "if a\n  x\nelsif b\n  y\nelse\n  z\nend";
        var engine = CreateEngine(text, "test.rb");
        engine.ProcessKey("j"); engine.ProcessKey("j"); // line 2: "elsif b"
        Assert.Equal(2, engine.Cursor.Line);

        engine.ProcessKey("%");
        // "elsif" jumps to next sibling "else" (line 4), not straight to "end" (line 6)
        Assert.Equal(4, engine.Cursor.Line);
    }

    [Fact]
    public void Percent_Lua_NestedIf_OuterIfJumpsToOuterEnd_NotInner()
    {
        var text = string.Join('\n',
            "if outer then",       // 0
            "  if inner then",     // 1
            "    x()",             // 2
            "  end",               // 3  (inner end)
            "end");                // 4  (outer end)
        var engine = CreateEngine(text, "test.lua");
        // cursor at (0,0) on outer "if"
        engine.ProcessKey("%");
        Assert.Equal(4, engine.Cursor.Line); // outer end, not line 3
    }

    // ─── C preprocessor ───

    [Fact]
    public void Percent_CPreprocessor_IfJumpsToEndif_SkippingMiddles()
    {
        var text = "#if FOO\nint a;\n#elif BAR\nint b;\n#else\nint c;\n#endif";
        var engine = CreateEngine(text, "test.c");
        engine.ProcessKey("%");
        Assert.Equal(6, engine.Cursor.Line);
    }

    [Fact]
    public void Percent_CPreprocessor_EndifJumpsBackToIf()
    {
        var text = "#if FOO\nint a;\n#elif BAR\nint b;\n#else\nint c;\n#endif";
        var engine = CreateEngine(text, "test.c");
        engine.ProcessKey("G");
        Assert.Equal(6, engine.Cursor.Line);

        engine.ProcessKey("%");
        Assert.Equal(0, engine.Cursor.Line);
    }

    [Fact]
    public void Percent_CPreprocessor_MiddleKeyword_JumpsToNextSibling()
    {
        var text = "#if FOO\nint a;\n#elif BAR\nint b;\n#else\nint c;\n#endif";
        var engine = CreateEngine(text, "test.c");
        engine.ProcessKey("j"); engine.ProcessKey("j"); // line 2: "#elif BAR"
        Assert.Equal(2, engine.Cursor.Line);

        engine.ProcessKey("%");
        Assert.Equal(4, engine.Cursor.Line); // "#else", not straight to "#endif"
    }

    [Fact]
    public void Percent_CPreprocessor_NestedIfdef_OuterJumpsToOuterEndif()
    {
        var text = string.Join('\n',
            "#ifdef FOO",   // 0
            "#ifdef BAR",   // 1
            "int x;",       // 2
            "#endif",       // 3 inner
            "#endif");      // 4 outer
        var engine = CreateEngine(text, "test.c");
        engine.ProcessKey("%");
        Assert.Equal(4, engine.Cursor.Line);
    }

    // ─── HTML/XML tag matching ───

    [Fact]
    public void Percent_Html_OpenTagJumpsToCloseTag()
    {
        var text = "<div>\n  text\n</div>";
        var engine = CreateEngine(text, "test.html");
        // cursor at (0,1) inside "<div>"
        engine.ProcessKey("l");
        engine.ProcessKey("%");
        Assert.Equal(2, engine.Cursor.Line);
    }

    [Fact]
    public void Percent_Html_CloseTagJumpsBackToOpenTag()
    {
        var text = "<div>\n  text\n</div>";
        var engine = CreateEngine(text, "test.html");
        engine.ProcessKey("G");
        Assert.Equal(2, engine.Cursor.Line);

        engine.ProcessKey("%");
        Assert.Equal(0, engine.Cursor.Line);
    }

    [Fact]
    public void Percent_Html_NestedSameNameTags_MatchesCorrectly()
    {
        var text = string.Join('\n',
            "<div>",       // 0 outer
            "  <div>",     // 1 inner
            "    x",       // 2
            "  </div>",    // 3 inner close
            "</div>");     // 4 outer close
        var engine = CreateEngine(text, "test.html");
        engine.ProcessKey("%"); // cursor starts at outer <div> on line 0
        Assert.Equal(4, engine.Cursor.Line);
    }

    [Fact]
    public void Percent_Html_SelfClosingTag_IsNotAMatchTarget()
    {
        var text = "<br/>\n<div>x</div>";
        var engine = CreateEngine(text, "test.html");
        // cursor on the self-closing tag; % must not move the cursor (no match)
        engine.ProcessKey("%");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Percent_Html_NestedTagAfterSameLineComment_MatchesInnerNotOuter()
    {
        // The comment on line 1 shares a line with a real nested <div> — only the
        // commented portion should be ignored, not the whole line's tag events.
        var text = string.Join('\n',
            "<div>",             // 0 outer open
            "<!-- x --><div>",   // 1 comment + real nested open
            "</div>",            // 2 inner close
            "</div>");           // 3 outer close
        var engine = CreateEngine(text, "test.html");
        engine.ProcessKey("%"); // cursor starts at outer <div> on line 0
        Assert.Equal(3, engine.Cursor.Line);
    }

    [Fact]
    public void Percent_Html_TagWithGreaterThanInQuotedAttribute_MatchesCorrectly()
    {
        var text = "<div title=\"a > b\">\n  x\n</div>";
        var engine = CreateEngine(text, "test.html");
        engine.ProcessKey("%"); // cursor starts on the opening <div ...>
        Assert.Equal(2, engine.Cursor.Line);
    }

    // ─── No language / non-keyword text: no-op, same as today ───

    [Fact]
    public void Percent_NoMatchitLanguageRegistered_NoMotion()
    {
        var engine = CreateEngine("if a then end", "test.txt");
        var before = engine.Cursor;
        engine.ProcessKey("%");
        Assert.Equal(before, engine.Cursor);
    }

    [Fact]
    public void Percent_OnPlainWord_NoMatchitLanguage_NoMotion()
    {
        var engine = CreateEngine("hello world");
        var before = engine.Cursor;
        engine.ProcessKey("%");
        Assert.Equal(before, engine.Cursor);
    }

    [Fact]
    public void Percent_LuaFile_OnNonKeywordText_NoMotion()
    {
        var engine = CreateEngine("local x = 1", "test.lua");
        var before = engine.Cursor; // cursor on "local"
        engine.ProcessKey("%");
        Assert.Equal(before, engine.Cursor);
    }
}
