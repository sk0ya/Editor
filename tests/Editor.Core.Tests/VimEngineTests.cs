using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class VimEngineTests
{
    private VimEngine CreateEngine(string text = "", VimConfig? config = null)
    {
        var engine = new VimEngine(config ?? new VimConfig());
        if (!string.IsNullOrEmpty(text))
            engine.SetText(text);
        return engine;
    }

    [Fact]
    public void InitialMode_IsNormal()
    {
        var engine = CreateEngine();
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void PressI_EntersInsertMode()
    {
        var engine = CreateEngine("hello");
        var events = engine.ProcessKey("i");
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Contains(events, e => e.Type == VimEventType.ModeChanged);
    }

    [Fact]
    public void PressEscape_InInsert_ReturnsToNormal()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("i");
        engine.ProcessKey("Escape");
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void TypeInInsert_AddsText()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("h");
        engine.ProcessKey("i");
        engine.ProcessKey("Escape");
        Assert.Equal("hi", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void PressH_MovesLeft()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("l"); // move right first
        var col = engine.Cursor.Column;
        engine.ProcessKey("h");
        Assert.Equal(col - 1, engine.Cursor.Column);
    }

    [Fact]
    public void PressL_MovesRight()
    {
        var engine = CreateEngine("hello");
        var col = engine.Cursor.Column;
        engine.ProcessKey("l");
        Assert.Equal(col + 1, engine.Cursor.Column);
    }

    [Fact]
    public void PressJ_MovesDown()
    {
        var engine = CreateEngine("line1\nline2");
        var line = engine.Cursor.Line;
        engine.ProcessKey("j");
        Assert.Equal(line + 1, engine.Cursor.Line);
    }

    [Fact]
    public void PressK_MovesUp()
    {
        var engine = CreateEngine("line1\nline2");
        engine.ProcessKey("j"); // go to line 2
        var line = engine.Cursor.Line;
        engine.ProcessKey("k");
        Assert.Equal(line - 1, engine.Cursor.Line);
    }

    [Fact]
    public void DD_DeletesLine()
    {
        var engine = CreateEngine("line1\nline2\nline3");
        engine.ProcessKey("d");
        engine.ProcessKey("d");
        var text = engine.CurrentBuffer.Text.GetText();
        Assert.DoesNotContain("line1", text);
        Assert.Contains("line2", text);
    }

    [Fact]
    public void YY_YanksLine_ThenP_Pastes()
    {
        var engine = CreateEngine("hello\nworld");
        engine.ProcessKey("y");
        engine.ProcessKey("y");
        engine.ProcessKey("p");
        Assert.Equal(3, engine.CurrentBuffer.Text.LineCount);
    }

    [Fact]
    public void Undo_RestoresText()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("i");
        engine.ProcessKey("x");
        engine.ProcessKey("Escape");
        var modified = engine.CurrentBuffer.Text.GetText();
        engine.ProcessKey("u");
        var restored = engine.CurrentBuffer.Text.GetText();
        Assert.NotEqual(modified, restored);
        Assert.Equal("hello", restored);
    }

    [Fact]
    public void PressV_EntersVisualMode()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("v");
        Assert.Equal(VimMode.Visual, engine.Mode);
    }

    [Fact]
    public void VisualMode_Gg_MovesToFirstLine()
    {
        var engine = CreateEngine("line1\nline2\nline3");
        engine.ProcessKey("j");
        engine.ProcessKey("v");

        engine.ProcessKey("g");
        engine.ProcessKey("g");

        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.Equal(0, engine.Cursor.Line);
    }

    [Fact]
    public void VisualMode_Ge_MovesToEndOfPreviousWord()
    {
        var engine = CreateEngine("one two");
        engine.ProcessKey("w");
        engine.ProcessKey("v");

        engine.ProcessKey("g");
        engine.ProcessKey("e");

        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void VisualMode_R_ReplacesSelectionWithGivenChar()
    {
        var engine = CreateEngine("abcdef");
        engine.ProcessKey("v");
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        engine.ProcessKey("r");
        engine.ProcessKey("X");

        Assert.Equal("XXXdef", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void VisualMode_Colon_EntersCommandModeWithRange()
    {
        var engine = CreateEngine("a\nb\nc");
        engine.ProcessKey("V");
        engine.ProcessKey("j");

        engine.ProcessKey(":");

        Assert.Equal(VimMode.Command, engine.Mode);
        Assert.Equal("1,2", engine.CommandLine);
        Assert.Null(engine.Selection);
    }

    [Fact]
    public void VisualMode_ColonSort_AppliesToSelectedLines()
    {
        var engine = CreateEngine("c\na\nb\nd");
        engine.ProcessKey("V");
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey(":");
        foreach (var ch in "sort")
            engine.ProcessKey(ch.ToString());
        engine.ProcessKey("Return");

        Assert.Equal("a\nb\nc\nd", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void PressColon_EntersCommandMode()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey(":");
        Assert.Equal(VimMode.Command, engine.Mode);
    }

    [Fact]
    public void PressW_MovesForwardWord()
    {
        var engine = CreateEngine("hello world");
        engine.ProcessKey("w");
        Assert.Equal(6, engine.Cursor.Column); // "world" starts at col 6
    }

    [Fact]
    public void PressZero_GoesToLineStart()
    {
        var engine = CreateEngine("   hello");
        engine.ProcessKey("$"); // go to end
        engine.ProcessKey("0"); // go to start
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void PressGG_GoesToFirstLine()
    {
        var engine = CreateEngine("line1\nline2\nline3");
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("g");
        engine.ProcessKey("g");
        Assert.Equal(0, engine.Cursor.Line);
    }

    [Fact]
    public void PressG_GoesToLastLine()
    {
        var engine = CreateEngine("line1\nline2\nline3");
        engine.ProcessKey("G");
        Assert.Equal(2, engine.Cursor.Line);
    }

    [Fact]
    public void Count_Prefix_RepeatMotion()
    {
        var engine = CreateEngine("hello world foo bar");
        engine.ProcessKey("3");
        engine.ProcessKey("l");
        Assert.Equal(3, engine.Cursor.Column);
    }

    [Fact]
    public void X_DeleteCharUnderCursor()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("x");
        Assert.Equal("ello", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void O_OpensLineBelow()
    {
        var engine = CreateEngine("line1\nline3");
        engine.ProcessKey("o");
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Equal(3, engine.CurrentBuffer.Text.LineCount);
        Assert.Equal(1, engine.Cursor.Line);
    }

    [Fact]
    public void Ge_MovesToEndOfPreviousWord()
    {
        var engine = CreateEngine("one two");
        engine.ProcessKey("w");
        engine.ProcessKey("g");
        engine.ProcessKey("e");
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void GjAndGk_MoveDownAndUp()
    {
        var engine = CreateEngine("line1\nline2\nline3");
        engine.ProcessKey("g");
        engine.ProcessKey("j");
        Assert.Equal(1, engine.Cursor.Line);

        engine.ProcessKey("g");
        engine.ProcessKey("k");
        Assert.Equal(0, engine.Cursor.Line);
    }

    [Fact]
    public void PlusMinusUnderscoreAndPipe_Motions_Work()
    {
        var engine = CreateEngine("  a\n    b\nc");

        engine.ProcessKey("+");
        Assert.Equal(new CursorPosition(1, 4), engine.Cursor);

        engine.ProcessKey("-");
        Assert.Equal(new CursorPosition(0, 2), engine.Cursor);

        engine.ProcessKey("2");
        engine.ProcessKey("_");
        Assert.Equal(new CursorPosition(1, 4), engine.Cursor);

        engine.ProcessKey("3");
        engine.ProcessKey("|");
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void GtAndGT_EmitTabNavigationEvents()
    {
        var engine = CreateEngine("x");

        engine.ProcessKey("g");
        var next = engine.ProcessKey("t");
        Assert.Contains(next, e => e is NextTabRequestedEvent);

        engine.ProcessKey("g");
        var prev = engine.ProcessKey("T");
        Assert.Contains(prev, e => e is PrevTabRequestedEvent);
    }

    [Fact]
    public void U_AlsoUndoesLastChange()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("i");
        engine.ProcessKey("x");
        engine.ProcessKey("Escape");

        engine.ProcessKey("U");

        Assert.Equal("hello", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlRToken_RedoesAfterUndo()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("i");
        engine.ProcessKey("x");
        engine.ProcessKey("Escape");
        engine.ProcessKey("u");

        engine.ProcessKey("\x12");

        Assert.Equal("xhello", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Yiw_YanksInnerWord_AndCanPasteAtEnd()
    {
        var engine = CreateEngine("foo bar");

        engine.ProcessKey("y");
        engine.ProcessKey("i");
        engine.ProcessKey("w");
        engine.ProcessKey("$");
        engine.ProcessKey("p");

        Assert.Equal("foo barfoo", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Ciw_ChangesInnerWord()
    {
        var engine = CreateEngine("foo bar");

        engine.ProcessKey("c");
        engine.ProcessKey("i");
        engine.ProcessKey("w");
        engine.ProcessKey("X");
        engine.ProcessKey("Escape");

        Assert.Equal("X bar", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Dot_RepeatsSimpleChange()
    {
        var engine = CreateEngine("abc");

        engine.ProcessKey("x");
        engine.ProcessKey(".");

        Assert.Equal("c", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Dot_RepeatsCiwInsertedText()
    {
        var engine = CreateEngine("foo bar");

        engine.ProcessKey("c");
        engine.ProcessKey("i");
        engine.ProcessKey("w");
        engine.ProcessKey("X");
        engine.ProcessKey("Escape");
        engine.ProcessKey("w");
        engine.ProcessKey(".");

        Assert.Equal("X X", engine.CurrentBuffer.Text.GetText());
    }

    // ──── Text Object Extensions ────

    [Fact]
    public void Di_Paren_DeletesInsideBrackets()
    {
        var engine = CreateEngine("foo(bar)baz");
        // cursor on 'b' inside parens (col 4)
        engine.ProcessKey("f"); engine.ProcessKey("b");
        engine.ProcessKey("d"); engine.ProcessKey("i"); engine.ProcessKey("(");
        Assert.Equal("foo()baz", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Da_Paren_DeletesAroundBrackets()
    {
        var engine = CreateEngine("foo(bar)baz");
        engine.ProcessKey("f"); engine.ProcessKey("b");
        engine.ProcessKey("d"); engine.ProcessKey("a"); engine.ProcessKey("(");
        Assert.Equal("foobaz", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Di_CurlyBrace_DeletesInsideBraces()
    {
        var engine = CreateEngine("x{hello}y");
        engine.ProcessKey("f"); engine.ProcessKey("h");
        engine.ProcessKey("d"); engine.ProcessKey("i"); engine.ProcessKey("{");
        Assert.Equal("x{}y", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Di_SquareBracket_DeletesInsideBrackets()
    {
        var engine = CreateEngine("a[bc]d");
        engine.ProcessKey("f"); engine.ProcessKey("b");
        engine.ProcessKey("d"); engine.ProcessKey("i"); engine.ProcessKey("[");
        Assert.Equal("a[]d", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Di_DoubleQuote_DeletesInsideQuotes()
    {
        var engine = CreateEngine("say \"hello\" there");
        engine.ProcessKey("f"); engine.ProcessKey("h");
        engine.ProcessKey("d"); engine.ProcessKey("i"); engine.ProcessKey("\"");
        Assert.Equal("say \"\" there", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Da_DoubleQuote_DeletesAroundQuotes()
    {
        var engine = CreateEngine("say \"hello\" there");
        engine.ProcessKey("f"); engine.ProcessKey("h");
        engine.ProcessKey("d"); engine.ProcessKey("a"); engine.ProcessKey("\"");
        Assert.Equal("say  there", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Di_SingleQuote_DeletesInsideQuotes()
    {
        var engine = CreateEngine("say 'hello' ok");
        // position cursor inside 'hello'
        engine.ProcessKey("f"); engine.ProcessKey("h");
        engine.ProcessKey("d"); engine.ProcessKey("i"); engine.ProcessKey("'");
        Assert.Equal("say '' ok", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Yip_YanksInnerParagraph()
    {
        var engine = CreateEngine("line1\nline2\n\nline4");
        // cursor on line1
        engine.ProcessKey("y"); engine.ProcessKey("i"); engine.ProcessKey("p");
        // should yank "line1\nline2"
        Assert.Equal(VimMode.Normal, engine.Mode);
        // Paste to verify content was yanked
        engine.ProcessKey("G");
        engine.ProcessKey("p");
        var text = engine.CurrentBuffer.Text.GetText();
        Assert.Contains("line1", text);
        Assert.Contains("line2", text);
    }

    [Fact]
    public void Visual_IW_SelectsInnerWord()
    {
        var engine = CreateEngine("foo bar");
        engine.ProcessKey("v");
        engine.ProcessKey("i"); engine.ProcessKey("w");
        Assert.Equal(VimMode.Visual, engine.Mode);
        // Cursor is at end of "foo" (col 2), visual start at col 0
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void FindMotions_FFTT_WorkAsStandaloneMotions()
    {
        var engine = CreateEngine("abcabc");

        engine.ProcessKey("f");
        engine.ProcessKey("c");
        Assert.Equal(2, engine.Cursor.Column);

        engine.ProcessKey("t");
        engine.ProcessKey("a");
        Assert.Equal(2, engine.Cursor.Column);

        engine.ProcessKey("$");
        engine.ProcessKey("F");
        engine.ProcessKey("a");
        Assert.Equal(3, engine.Cursor.Column);

        engine.ProcessKey("$");
        engine.ProcessKey("T");
        engine.ProcessKey("a");
        Assert.Equal(4, engine.Cursor.Column);
    }

    [Fact]
    public void CtrlNormalBindings_WorksForHJMAndBracket()
    {
        var engine = CreateEngine("one two\nnext line");

        // Use 'w' (non-ctrl) to move to word-forward position (col 4 = start of "two")
        engine.ProcessKey("w");
        Assert.Equal(4, engine.Cursor.Column);

        engine.ProcessKey("h", ctrl: true);
        Assert.Equal(3, engine.Cursor.Column);

        engine.ProcessKey("j", ctrl: true);
        Assert.Equal(1, engine.Cursor.Line);

        engine.ProcessKey("m", ctrl: true);
        Assert.Equal(0, engine.Cursor.Column);

        engine.ProcessKey("d");
        var before = engine.CurrentBuffer.Text.GetText();
        engine.ProcessKey("[", ctrl: true);
        engine.ProcessKey("d");
        var after = engine.CurrentBuffer.Text.GetText();
        Assert.Equal(before, after);
    }

    [Fact]
    public void CtrlV_BlockDelete_DeletesRectangularSelection()
    {
        var engine = CreateEngine("abcd\nabcd\nabcd");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("l");
        engine.ProcessKey("d");

        Assert.Equal("cd\ncd\ncd", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_BlockInsertI_InsertsTextOnEachSelectedLine()
    {
        var engine = CreateEngine("abc\nabc\nabc");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("I");
        engine.ProcessKey("X");
        engine.ProcessKey("Escape");

        Assert.Equal("Xabc\nXabc\nXabc", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_BlockChange_C_ChangesRectangularSelectionOnEachLine()
    {
        var engine = CreateEngine("abcd\nabcd");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("l");
        engine.ProcessKey("c");
        engine.ProcessKey("X");
        engine.ProcessKey("Escape");

        Assert.Equal("Xcd\nXcd", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_BlockFindMotionF_WorksThenDelete()
    {
        var engine = CreateEngine("abcd\nabcd\nabcd");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("f");
        engine.ProcessKey("d");
        engine.ProcessKey("d");

        Assert.Equal("\n\n", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_BlockMode_O_SwapsSelectionAnchor()
    {
        var engine = CreateEngine("abcd\nabcd");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("l");
        engine.ProcessKey("o");

        Assert.Equal(VimMode.VisualBlock, engine.Mode);
        Assert.Equal(new CursorPosition(0, 0), engine.Cursor);
        Assert.NotNull(engine.Selection);
        Assert.Equal(new CursorPosition(1, 1), engine.Selection!.Value.Start);
        Assert.Equal(new CursorPosition(0, 0), engine.Selection!.Value.End);
    }

    [Fact]
    public void CtrlV_BlockMode_R_ReplacesBlockWithGivenChar()
    {
        var engine = CreateEngine("abcd\nabcd");
        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("l");
        engine.ProcessKey("r");
        engine.ProcessKey("Z");

        Assert.Equal("ZZcd\nZZcd", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void ZCommands_EmitViewportAlignEvents()
    {
        var engine = CreateEngine("line1\nline2\nline3");

        engine.ProcessKey("z");
        var zz = engine.ProcessKey("z");
        var zzEvt = Assert.IsType<ViewportAlignRequestedEvent>(Assert.Single(zz, e => e is ViewportAlignRequestedEvent));
        Assert.Equal(ViewportAlign.Center, zzEvt.Align);

        engine.ProcessKey("z");
        var zt = engine.ProcessKey("t");
        var ztEvt = Assert.IsType<ViewportAlignRequestedEvent>(Assert.Single(zt, e => e is ViewportAlignRequestedEvent));
        Assert.Equal(ViewportAlign.Top, ztEvt.Align);

        engine.ProcessKey("z");
        var zb = engine.ProcessKey("b");
        var zbEvt = Assert.IsType<ViewportAlignRequestedEvent>(Assert.Single(zb, e => e is ViewportAlignRequestedEvent));
        Assert.Equal(ViewportAlign.Bottom, zbEvt.Align);
    }

    [Fact]
    public void NormalMap_FromConfig_RemapKey()
    {
        var config = new VimConfig();
        config.NormalMaps["j"] = "l";
        var engine = CreateEngine("abc", config);

        engine.ProcessKey("j");

        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void NormalMap_FromConfig_SupportsSpecialLhsNotation()
    {
        var config = new VimConfig();
        config.NormalMaps["<Space>"] = "l";
        var engine = CreateEngine("abc", config);

        engine.ProcessKey(" ");

        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void NormalMap_FromConfig_SupportsModifierNotation()
    {
        var config = new VimConfig();
        config.NormalMaps["<C-j>"] = "l";
        var engine = CreateEngine("abc", config);

        engine.ProcessKey("j", ctrl: true);

        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void NormalMap_RhsMultiKeySequence_IsExecuted()
    {
        var config = new VimConfig();
        config.NormalMaps["x"] = "dd";
        var engine = CreateEngine("line1\nline2", config);

        engine.ProcessKey("x");

        Assert.Equal(1, engine.CurrentBuffer.Text.LineCount);
        Assert.Equal("line2", engine.CurrentBuffer.Text.GetLine(0));
    }

    [Fact]
    public void K_WithoutMapping_EmitsLspHoverRequested()
    {
        // K in Normal mode with no vimrc mapping should emit LspHoverRequested
        // so the WPF layer can call ShowLspHoverAsync via the event system (like gd).
        var engine = CreateEngine("hello world");

        var events = engine.ProcessKey("K");

        Assert.Contains(events, e => e.Type == VimEventType.LspHoverRequested);
    }

    [Fact]
    public void NormalMap_K_WhenMapped_ExecutesMappedCommand()
    {
        // When K is mapped in vimrc the mapping fires instead of LspHoverRequested.
        var config = new VimConfig();
        config.NormalMaps["K"] = "dd";
        var engine = CreateEngine("line1\nline2", config);

        var events = engine.ProcessKey("K");

        Assert.Equal(1, engine.CurrentBuffer.Text.LineCount);
        Assert.Equal("line2", engine.CurrentBuffer.Text.GetLine(0));
        Assert.DoesNotContain(events, e => e.Type == VimEventType.LspHoverRequested);
    }

    [Fact]
    public void VimConfig_ParseLines_Nnoremap_K_RegistersMapping()
    {
        // Verifies vimrc "nnoremap K ..." is parsed and stored in NormalMaps.
        var cfg = new VimConfig();
        cfg.ParseLines(["nnoremap K dd"]);
        Assert.True(cfg.NormalMaps.ContainsKey("K"), "K should be in NormalMaps after nnoremap K dd");
        Assert.Equal("dd", cfg.NormalMaps["K"]);
    }

    [Fact]
    public void InsertMap_FromConfig_CanLeaveInsertMode()
    {
        var config = new VimConfig();
        config.InsertMaps["x"] = "<Esc>";
        var engine = CreateEngine("abc", config);

        engine.ProcessKey("i");
        engine.ProcessKey("x");

        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Equal("abc", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void ExNnoremap_UpdatesNormalMapImmediately()
    {
        var engine = CreateEngine("abc");

        engine.ProcessKey(":");
        foreach (var ch in "nnoremap j l")
            engine.ProcessKey(ch.ToString());
        engine.ProcessKey("Return");
        engine.ProcessKey("j");

        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void InsertMap_WithMultiKeyLhs_TriggersOnSecondKey()
    {
        var config = new VimConfig();
        config.InsertMaps["jj"] = "<Esc>";
        var engine = CreateEngine("abc", config);

        engine.ProcessKey("i");
        engine.ProcessKey("j");
        Assert.Equal(VimMode.Insert, engine.Mode);

        engine.ProcessKey("j");

        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Equal("abc", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void InsertMap_WithMultiKeyLhs_FallsBackWhenNoMatch()
    {
        var config = new VimConfig();
        config.InsertMaps["jj"] = "<Esc>";
        var engine = CreateEngine("", config);

        engine.ProcessKey("i");
        engine.ProcessKey("j");
        engine.ProcessKey("k");

        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Equal("jk", engine.CurrentBuffer.Text.GetText());
    }

    // ── VimConfig / vimrc parsing tests ──────────────────────────────────

    [Fact]
    public void VimConfig_ParseLeader_SpaceLeader()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["let mapleader=\"\\<Space>\""]);
        Assert.Equal(" ", cfg.Leader);
    }

    [Fact]
    public void VimConfig_ParseMap_ExpandsLeader()
    {
        var cfg = new VimConfig();
        cfg.ParseLines([
            "let mapleader=\"\\<Space>\"",
            "nnoremap <Leader>w :w<CR>",
        ]);
        Assert.True(cfg.NormalMaps.ContainsKey(" w"), "Leader+w map should be registered with space");
        Assert.Equal(":w<CR>", cfg.NormalMaps[" w"]);
    }

    [Fact]
    public void VimConfig_ParseMap_StripsSilentFlag()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["nnoremap <silent>J 15j"]);
        Assert.True(cfg.NormalMaps.ContainsKey("J"), "J should be mapped after stripping <silent>");
        Assert.Equal("15j", cfg.NormalMaps["J"]);
    }

    [Fact]
    public void VimConfig_ParseMap_CmdReplacement()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["nnoremap t, <Cmd>bp<CR>"]);
        Assert.True(cfg.NormalMaps.ContainsKey("t,"));
        Assert.Equal(":bp<CR>", cfg.NormalMaps["t,"]);
    }

    [Fact]
    public void VimConfig_xnoremap_MapsToVisual()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["xnoremap Q :qa<CR>"]);
        Assert.True(cfg.VisualMaps.ContainsKey("Q"));
    }

    [Fact]
    public void VimOptions_WrapScan_ParsedFromVimrc()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["set nowrapscan"]);
        Assert.False(cfg.Options.WrapScan);
    }

    [Fact]
    public void VimOptions_NoopOptions_ParsedSilently()
    {
        var cfg = new VimConfig();
        // Should not throw and should silently accept all these
        cfg.ParseLines([
            "set mouse=a",
            "set noswapfile",
            "set nobackup",
            "set autoread",
            "set wildmenu",
            "set showmatch",
            "set breakindent",
            "set laststatus=2",
            "set history=10000",
            "set visualbell t_vb=",
        ]);
        Assert.Equal(10000, cfg.Options.History);
    }

    [Fact]
    public void G_Motion_MovesToLastNonBlank()
    {
        var engine = CreateEngine("  hello  ");
        engine.ProcessKey("g");
        engine.ProcessKey("_");
        Assert.Equal(6, engine.Cursor.Column); // 'o' in "hello"
    }

    [Fact]
    public void WrapScan_False_SearchStopsAtEnd()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["set nowrapscan"]);
        var engine = CreateEngine("abc\nabc", cfg);
        // Go to second line where "abc" is
        engine.ProcessKey("j");
        // Start searching forward — should not wrap back to line 0
        engine.ProcessKey("/");
        foreach (var ch in "abc") engine.ProcessKey(ch.ToString());
        engine.ProcessKey("Return");
        // We're on line 1. Now press n — should NOT move to line 0
        engine.ProcessKey("n");
        Assert.Equal(1, engine.Cursor.Line);
    }

    // ── :set option tests ──────────────────────────────────────────────────

    private static IReadOnlyList<VimEvent> ExCmd(VimEngine engine, string cmd)
    {
        engine.ProcessKey(":");
        foreach (var ch in cmd)
            engine.ProcessKey(ch.ToString());
        return engine.ProcessKey("Return");
    }

    [Fact]
    public void SetRelativeNumber_SetsFlag()
    {
        var engine = CreateEngine("a\nb\nc");
        ExCmd(engine, "set relativenumber");
        Assert.True(engine.Options.RelativeNumber);
    }

    [Fact]
    public void SetNoRelativeNumber_ClearsFlag()
    {
        var engine = CreateEngine("a\nb\nc");
        ExCmd(engine, "set relativenumber");
        ExCmd(engine, "set norelativenumber");
        Assert.False(engine.Options.RelativeNumber);
    }

    [Fact]
    public void SetRnu_ShortAlias_SetsFlag()
    {
        var engine = CreateEngine("a\nb\nc");
        ExCmd(engine, "set rnu");
        Assert.True(engine.Options.RelativeNumber);
    }

    [Fact]
    public void SetRelativeNumber_EmitsOptionsChangedEvent()
    {
        var engine = CreateEngine("a\nb\nc");
        var events = ExCmd(engine, "set relativenumber");
        Assert.Contains(events, e => e.Type == VimEventType.OptionsChanged);
    }

    [Fact]
    public void SetNoRelativeNumber_EmitsOptionsChangedEvent()
    {
        var engine = CreateEngine("a\nb\nc");
        ExCmd(engine, "set relativenumber");
        var events = ExCmd(engine, "set norelativenumber");
        Assert.Contains(events, e => e.Type == VimEventType.OptionsChanged);
    }

    [Fact]
    public void SetNumber_EmitsOptionsChangedEvent()
    {
        var engine = CreateEngine("a\nb\nc");
        var events = ExCmd(engine, "set number");
        Assert.Contains(events, e => e.Type == VimEventType.OptionsChanged);
    }

    [Fact]
    public void SetNoNumber_ClearsFlagAndEmitsOptionsChanged()
    {
        var engine = CreateEngine("a\nb\nc");
        ExCmd(engine, "set number");
        var events = ExCmd(engine, "set nonumber");
        Assert.False(engine.Options.Number);
        Assert.Contains(events, e => e.Type == VimEventType.OptionsChanged);
    }

    // ── Ctrl+W window prefix tests ──────────────────────────────────────────

    [Fact]
    public void CtrlW_w_EmitsWindowNavNext()
    {
        var engine = CreateEngine("hello");
        var e1 = engine.ProcessKey("w", ctrl: true);
        Assert.DoesNotContain(e1, e => e.Type == VimEventType.WindowNavRequested);
        var e2 = engine.ProcessKey("w");
        Assert.Contains(e2, e => e is WindowNavRequestedEvent { Dir: WindowNavDir.Next });
    }

    [Fact]
    public void CtrlW_CtrlW_EmitsWindowNavNext()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("w", ctrl: true);
        var events = engine.ProcessKey("w", ctrl: true);
        Assert.Contains(events, e => e is WindowNavRequestedEvent { Dir: WindowNavDir.Next });
    }

    [Fact]
    public void CtrlW_W_EmitsWindowNavPrev()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("w", ctrl: true);
        var events = engine.ProcessKey("W");
        Assert.Contains(events, e => e is WindowNavRequestedEvent { Dir: WindowNavDir.Prev });
    }

    [Fact]
    public void CtrlW_q_EmitsWindowCloseRequested()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("w", ctrl: true);
        var events = engine.ProcessKey("q");
        Assert.Contains(events, e => e is WindowCloseRequestedEvent { Force: false });
    }

    [Fact]
    public void CtrlW_c_EmitsWindowCloseRequested()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("w", ctrl: true);
        var events = engine.ProcessKey("c");
        Assert.Contains(events, e => e.Type == VimEventType.WindowCloseRequested);
    }

    [Fact]
    public void CtrlW_v_EmitsSplitRequestedVertical()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("w", ctrl: true);
        var events = engine.ProcessKey("v");
        Assert.Contains(events, e => e is SplitRequestedEvent { Vertical: true });
    }

    [Fact]
    public void CtrlW_s_EmitsSplitRequestedHorizontal()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("w", ctrl: true);
        var events = engine.ProcessKey("s");
        Assert.Contains(events, e => e is SplitRequestedEvent { Vertical: false });
    }

    [Fact]
    public void CtrlW_Escape_CancelsPending()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("w", ctrl: true);
        var events = engine.ProcessKey("Escape");
        Assert.DoesNotContain(events, e =>
            e.Type is VimEventType.WindowNavRequested or
                      VimEventType.WindowCloseRequested or
                      VimEventType.SplitRequested);
    }

    [Fact]
    public void CtrlW_Escape_ThenJ_MovesCursor()
    {
        var engine = CreateEngine("line1\nline2");
        engine.ProcessKey("w", ctrl: true);
        engine.ProcessKey("Escape");
        var startLine = engine.Cursor.Line;
        engine.ProcessKey("j");
        Assert.Equal(startLine + 1, engine.Cursor.Line);
    }

    [Fact]
    public void ColonQ_EmitsWindowCloseRequested()
    {
        var engine = CreateEngine("hello");
        var events = ExCmd(engine, "q");
        Assert.Contains(events, e => e.Type == VimEventType.WindowCloseRequested);
        Assert.DoesNotContain(events, e => e.Type == VimEventType.QuitRequested);
    }

    [Fact]
    public void ColonSplit_EmitsSplitRequestedHorizontal()
    {
        var engine = CreateEngine("hello");
        var events = ExCmd(engine, "split");
        Assert.Contains(events, e => e is SplitRequestedEvent { Vertical: false, FilePath: null });
    }

    [Fact]
    public void ColonVsplit_WithFilename_EmitsSplitWithPath()
    {
        var engine = CreateEngine("hello");
        var events = ExCmd(engine, "vsplit foo.txt");
        Assert.Contains(events, e => e is SplitRequestedEvent { Vertical: true, FilePath: "foo.txt" });
    }

    [Fact]
    public void ColonSort_CanUndoAndRedo()
    {
        var engine = CreateEngine("c\na\nb");

        ExCmd(engine, "%sort");
        Assert.Equal("a\nb\nc", engine.CurrentBuffer.Text.GetText());

        engine.ProcessKey("u");
        Assert.Equal("c\na\nb", engine.CurrentBuffer.Text.GetText());

        engine.ProcessKey("r", ctrl: true);
        Assert.Equal("a\nb\nc", engine.CurrentBuffer.Text.GetText());
    }

    // ─── Ctrl+A / Ctrl+X ───

    [Fact]
    public void CtrlA_IncrementDecimal()
    {
        var engine = CreateEngine("foo 42 bar");
        // Move cursor onto '4'
        engine.ProcessKey("4"); engine.ProcessKey("l");
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("foo 43 bar", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlX_DecrementDecimal()
    {
        var engine = CreateEngine("foo 42 bar");
        engine.ProcessKey("4"); engine.ProcessKey("l");
        engine.ProcessKey("x", ctrl: true);
        Assert.Equal("foo 41 bar", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_WithCount_IncrementsByCount()
    {
        var engine = CreateEngine("value=10");
        // cursor at col 0, scan finds '10'
        engine.ProcessKey("5");
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("value=15", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_NegativeNumber_Increments()
    {
        var engine = CreateEngine("x=-5 y");
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("x=-4 y", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlX_DecrementToNegative()
    {
        var engine = CreateEngine("n=0");
        engine.ProcessKey("x", ctrl: true);
        Assert.Equal("n=-1", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_HexNumber_Increments()
    {
        var engine = CreateEngine("color=0xfe");
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("color=0xff", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_HexOverflow_GrowsDigits()
    {
        var engine = CreateEngine("0xff");
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("0x100", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_CursorLandsOnLastDigit()
    {
        var engine = CreateEngine("99");
        engine.ProcessKey("a", ctrl: true);
        // "99" → "100": cursor should be at col 2 (last '0')
        Assert.Equal(2, engine.Cursor.Column);
        Assert.Equal("100", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_NoNumber_DoesNothing()
    {
        var engine = CreateEngine("hello world");
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("hello world", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_FindsNumberAfterCursor()
    {
        var engine = CreateEngine("abc 7 def");
        // cursor at col 0, no digit at col 0–2; scans forward and finds '7' at col 4
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("abc 8 def", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlA_CursorInsideNumber_UsesFullNumber()
    {
        // Cursor on '9' of "19" → should increment whole 19→20, not just 9→10 giving "110"
        var engine = CreateEngine("19");
        engine.ProcessKey("l"); // move to col 1 ('9')
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal("20", engine.CurrentBuffer.Text.GetText());
    }

    // ─────────────── AUTO-PAIRS ───────────────

    [Fact]
    public void AutoPairs_OpenParen_InsertsClosingParen()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("(");
        Assert.Equal("()", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(1, engine.Cursor.Column); // cursor between the pair
    }

    [Fact]
    public void AutoPairs_OpenBracket_InsertsClosingBracket()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("[");
        Assert.Equal("[]", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_OpenBrace_InsertsClosingBrace()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("{");
        Assert.Equal("{}", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_DoubleQuote_InsertsPair()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("\"");
        Assert.Equal("\"\"", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_SingleQuote_InsertsPair()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("'");
        Assert.Equal("''", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_Backtick_InsertsPair()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("`");
        Assert.Equal("``", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_CloseParen_SkipsOver()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("("); // inserts "()" cursor at col 1
        engine.ProcessKey(")"); // should skip over, not insert
        Assert.Equal("()", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_CloseBracket_SkipsOver()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("[");
        engine.ProcessKey("]");
        Assert.Equal("[]", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_CloseBrace_SkipsOver()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("{");
        engine.ProcessKey("}");
        Assert.Equal("{}", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_Quote_DoesNotSkip_InsertsNewPair()
    {
        // Symmetric pairs always insert rather than skip
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("\""); // inserts ""
        engine.ProcessKey("\""); // should insert another "" pair, not skip
        Assert.Equal("\"\"\"\"", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void AutoPairs_Backspace_DeletesBothChars()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("("); // inserts "()", cursor at 1
        engine.ProcessKey("Back"); // should delete both '(' and ')'
        Assert.Equal("", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_TypeInsidePair()
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey("(");   // "()" cursor=1
        engine.ProcessKey("x");   // "(x)" cursor=2
        engine.ProcessKey(")");   // skip over → "(x)" cursor=3
        Assert.Equal("(x)", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(3, engine.Cursor.Column);
    }

    [Fact]
    public void AutoPairs_Disabled_NoPairInsertion()
    {
        var config = new VimConfig();
        config.Options.Apply("nopairs");
        var engine = CreateEngine(config: config);
        engine.ProcessKey("i");
        engine.ProcessKey("(");
        Assert.Equal("(", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void AutoPairs_Disabled_NoSkipOver()
    {
        var config = new VimConfig();
        config.Options.Apply("nopairs");
        var engine = CreateEngine("()", config: config);
        engine.ProcessKey("i"); // enter insert at col 0
        engine.ProcessKey(")"); // no skip — inserts ")"
        Assert.Equal(")()", engine.CurrentBuffer.Text.GetText());
    }

    // ─── Case conversion operators ───────────────────────────────────────────

    [Fact]
    public void Guu_LowercasesCurrentLine()
    {
        var engine = CreateEngine("Hello World");
        engine.ProcessKey("g");
        engine.ProcessKey("u");
        engine.ProcessKey("u");
        Assert.Equal("hello world", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void GUU_UppercasesCurrentLine()
    {
        var engine = CreateEngine("hello world");
        engine.ProcessKey("g");
        engine.ProcessKey("U");
        engine.ProcessKey("U");
        Assert.Equal("HELLO WORLD", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void GTilde_TogglesCurrentLine()
    {
        var engine = CreateEngine("Hello World");
        engine.ProcessKey("g");
        engine.ProcessKey("~");
        engine.ProcessKey("~");
        Assert.Equal("hELLO wORLD", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void GuIw_LowercasesInnerWord()
    {
        var engine = CreateEngine("HELLO world");
        engine.ProcessKey("g");
        engine.ProcessKey("u");
        engine.ProcessKey("i");
        engine.ProcessKey("w");
        Assert.Equal("hello world", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void GUIw_UppercasesInnerWord()
    {
        var engine = CreateEngine("hello world");
        engine.ProcessKey("g");
        engine.ProcessKey("U");
        engine.ProcessKey("i");
        engine.ProcessKey("w");
        Assert.Equal("HELLO world", engine.CurrentBuffer.Text.GetText());
    }

    // ─────────────── :normal tests ───────────────

    private void ExCommand(VimEngine engine, string cmd)
    {
        engine.ProcessKey(":");
        foreach (var ch in cmd) engine.ProcessKey(ch.ToString());
        engine.ProcessKey("Return");
    }

    [Fact]
    public void Normal_AppendSemicolon_AllLines()
    {
        var engine = CreateEngine("foo\nbar\nbaz");
        ExCommand(engine, "%normal A;");
        Assert.Equal("foo;\nbar;\nbaz;", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void Normal_OnCurrentLine_NoRange()
    {
        var engine = CreateEngine("hello world");
        ExCommand(engine, "normal x");
        Assert.Equal("ello world", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void Normal_DeleteWord()
    {
        var engine = CreateEngine("hello world");
        engine.ProcessKey("d");
        engine.ProcessKey("w");
        var textAfterDirectDw = engine.CurrentBuffer.Text.GetText();
        // Reset and try via :normal
        engine.SetText("hello world");
        ExCommand(engine, "normal dw");
        Assert.Equal(textAfterDirectDw, engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Normal_WithLineRange_OnlyAffectsRange()
    {
        var engine = CreateEngine("aaa\nbbb\nccc");
        ExCommand(engine, "1,2normal A!");
        Assert.Equal("aaa!\nbbb!\nccc", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Normal_Bang_IgnoresMappings()
    {
        // :normal! executes the sequence while ignoring user mappings
        var engine = CreateEngine("hello");
        ExCommand(engine, "normal! x");
        Assert.Equal("ello", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void Normal_SpecialKey_EscapeInSequence()
    {
        var engine = CreateEngine("hello");
        // Enter insert mode, type text, then Escape back — all via :normal
        ExCommand(engine, "normal A<Esc>");
        // Should append nothing (A then immediately Esc)
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void Normal_IsUndoable()
    {
        var engine = CreateEngine("foo\nbar");
        ExCommand(engine, "%normal A;");
        Assert.Equal("foo;\nbar;", engine.CurrentBuffer.Text.GetText());
        // Undo should revert
        engine.ProcessKey("u");
        Assert.Equal("foo\nbar", engine.CurrentBuffer.Text.GetText());
    }
}
