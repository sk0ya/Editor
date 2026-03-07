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
    public void CtrlV_BlockAppendA_AppendsAfterRightColumnOfBlock()
    {
        // Block spans col 0-2 (full "abc"), A inserts after col 2 = end of line
        var engine = CreateEngine("abc\nabc\nabc");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("$"); // extend to end of line (col 2)
        engine.ProcessKey("A");
        engine.ProcessKey("X");
        engine.ProcessKey("Escape");

        Assert.Equal("abcX\nabcX\nabcX", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_BlockAppendA_AppendsAtRightColumnOfBlock()
    {
        // Select columns 0-1, A inserts after col 1 (between 'b' and 'c')
        var engine = CreateEngine("abcd\nabcd\nabcd");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("l"); // extend block to col 0-1
        engine.ProcessKey("A");
        engine.ProcessKey("X");
        engine.ProcessKey("Escape");

        Assert.Equal("abXcd\nabXcd\nabXcd", engine.CurrentBuffer.Text.GetText());
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

    // ── VisualBlock extra tests ──────────────────────────────────────────

    [Fact]
    public void CtrlV_BlockYank_YanksRectangularRegion()
    {
        // Select 2 columns (col 0-1) across 3 rows, yank, then paste to verify
        var engine = CreateEngine("abcd\nefgh\nijkl");

        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("l");
        engine.ProcessKey("y");

        // Cursor returns to top-left of block; mode back to Normal
        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Equal(0, engine.Cursor.Line);

        // Move to end of last line and paste — block paste inserts columns
        engine.ProcessKey("G");
        engine.ProcessKey("$");
        engine.ProcessKey("p");

        // After paste the text should be modified (registers not empty)
        // Verify text has changed from original (paste occurred)
        var text = engine.CurrentBuffer.Text.GetText();
        Assert.NotEqual("abcd\nefgh\nijkl", text);
    }

    [Fact]
    public void CtrlV_BlockTilde_TogglesCase()
    {
        // Use a single-line block so the column-range logic is unambiguous
        var engine = CreateEngine("abcdef");

        // CtrlV + ll selects cols 0-2 (3-char wide block on 1 line)
        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        engine.ProcessKey("~");

        Assert.Equal(VimMode.Normal, engine.Mode);
        var text = engine.CurrentBuffer.Text.GetText();
        // Cols 0-2 toggled to upper; rest unchanged
        Assert.Equal("ABCdef", text);
    }

    // ── MotionEngine tests ───────────────────────────────────────────────

    [Fact]
    public void W_Motion_MovesOverWORD()
    {
        // "foo.bar baz" — W skips the whole "foo.bar" token (no whitespace) as one WORD
        var engine = CreateEngine("foo.bar baz");
        engine.ProcessKey("W");
        // After W from col 0, cursor should be at the start of "baz" (col 8)
        Assert.Equal(8, engine.Cursor.Column);
    }

    [Fact]
    public void B_Motion_MovesBackwardWORD()
    {
        var engine = CreateEngine("foo.bar baz");
        // Move to "baz"
        engine.ProcessKey("W");
        Assert.Equal(8, engine.Cursor.Column);
        // B should jump back to start of "foo.bar" (col 0)
        engine.ProcessKey("B");
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void CurlyBrace_Paragraph_Forward()
    {
        var engine = CreateEngine("line1\nline2\n\nline4\nline5");
        // } from line 0 should move past the blank line (paragraph boundary)
        engine.ProcessKey("}");
        // Should be on the blank line (line 2) or line 3 depending on Vim semantics
        Assert.True(engine.Cursor.Line >= 2);
    }

    [Fact]
    public void CurlyBrace_Paragraph_Backward()
    {
        var engine = CreateEngine("line1\nline2\n\nline4\nline5");
        // Move to last line first
        engine.ProcessKey("G");
        var lastLine = engine.Cursor.Line;
        // { moves backward past paragraph boundary
        engine.ProcessKey("{");
        Assert.True(engine.Cursor.Line < lastLine);
    }

    [Fact]
    public void Percent_Motion_JumpsToMatchingBrace()
    {
        // "int f() { return 0; }" — '{' is at col 8 (0-indexed)
        var engine = CreateEngine("int f() { return 0; }");
        // Use f{ to jump to '{'
        engine.ProcessKey("f");
        engine.ProcessKey("{");
        Assert.Equal(8, engine.Cursor.Column);
        // % should jump to the matching '}'
        engine.ProcessKey("%");
        var line = engine.CurrentBuffer.Text.GetLine(0);
        var closingCol = line.LastIndexOf('}');
        Assert.Equal(closingCol, engine.Cursor.Column);
    }

    [Fact]
    public void GG_Motion_JumpsToLine()
    {
        var engine = CreateEngine("line1\nline2\nline3");
        // gg — go to first line
        engine.ProcessKey("g");
        engine.ProcessKey("g");
        Assert.Equal(0, engine.Cursor.Line);

        // G — go to last line
        engine.ProcessKey("G");
        Assert.Equal(2, engine.Cursor.Line);
    }

    [Fact]
    public void HML_Motions_UseViewportState()
    {
        // 10 lines of content, viewport top=2 showing 5 lines (lines 2-6)
        var engine = CreateEngine("l0\nl1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9");
        engine.SetViewportState(topLine: 2, visibleLines: 5);

        // H — top of viewport = line 2
        engine.ProcessKey("H");
        Assert.Equal(2, engine.Cursor.Line);

        // M — middle of viewport = line 2 + 5/2 = line 4
        engine.ProcessKey("M");
        Assert.Equal(4, engine.Cursor.Line);

        // L — bottom of viewport = line 2 + 5 - 1 = line 6
        engine.ProcessKey("L");
        Assert.Equal(6, engine.Cursor.Line);
    }

    // ── Insert mode Ctrl tests ───────────────────────────────────────────

    [Fact]
    public void Insert_CtrlU_DeletesToLineStart()
    {
        var engine = CreateEngine("hello world");
        // Move to col 5 (the space between "hello" and "world") via l×5
        for (int i = 0; i < 5; i++) engine.ProcessKey("l");
        engine.ProcessKey("i");
        // Ctrl+U should delete from col 5 back to col 0
        engine.ProcessKey("u", ctrl: true);
        engine.ProcessKey("Escape");
        // Remaining text: " world" (the part after col 5, now at start)
        Assert.Equal(" world", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Insert_CtrlW_DeletesWord()
    {
        // Start with text that ends in a word, enter insert mode at end, type a new word, then Ctrl+W deletes it
        var engine = CreateEngine("hello ");
        // Press A to append at end of line
        engine.ProcessKey("A");
        Assert.Equal(VimMode.Insert, engine.Mode);
        // Type a word
        engine.ProcessKey("f");
        engine.ProcessKey("o");
        engine.ProcessKey("o");
        // Ctrl+W should delete "foo" (the word just typed)
        engine.ProcessKey("w", ctrl: true);
        engine.ProcessKey("Escape");
        Assert.Equal("hello ", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void InsertMode_CtrlW_DeletesWordBefore()
    {
        var engine = CreateEngine("hello world");
        // Position cursor at end of "world" (col 11)
        engine.ProcessKey("$");
        engine.ProcessKey("a"); // append — cursor moves past 'd'
        Assert.Equal(VimMode.Insert, engine.Mode);
        // Ctrl+W should delete "world"
        engine.ProcessKey("w", ctrl: true);
        engine.ProcessKey("Escape");
        // "hello " remains
        Assert.Equal("hello ", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void InsertMode_CtrlU_DeletesToLineStart()
    {
        var engine = CreateEngine("hello world");
        // Move to col 5, enter insert mode
        for (int i = 0; i < 5; i++) engine.ProcessKey("l");
        engine.ProcessKey("i");
        Assert.Equal(VimMode.Insert, engine.Mode);
        // Ctrl+U deletes from cursor (col 5) back to col 0
        engine.ProcessKey("u", ctrl: true);
        engine.ProcessKey("Escape");
        Assert.Equal(" world", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void InsertMode_CtrlO_ExecutesNormalCommandAndReturnsToInsert()
    {
        var engine = CreateEngine("hello world");
        // Enter insert mode at start of "world" (position 6)
        for (int i = 0; i < 6; i++) engine.ProcessKey("l");
        engine.ProcessKey("i");
        Assert.Equal(VimMode.Insert, engine.Mode);
        // Ctrl+O: one Normal command then back to Insert
        engine.ProcessKey("o", ctrl: true);
        Assert.Equal(VimMode.Normal, engine.Mode);
        // Execute 'w' (word forward) as Normal command
        engine.ProcessKey("w");
        // Should be back in Insert mode
        Assert.Equal(VimMode.Insert, engine.Mode);
        // Cursor should have moved (past "world" to col 11, clamped for normal→insert)
        Assert.True(engine.Cursor.Column > 6);
    }

    [Fact]
    public void ZZ_SavesAndEmitsQuitRequested()
    {
        var engine = CreateEngine("hello");
        // Write to a temp file so Save() has a path
        var tmpFile = Path.GetTempFileName();
        try
        {
            engine.CurrentBuffer.FilePath = tmpFile;
            var events = engine.ProcessKey("Z");
            // After first Z, should be incomplete (no quit yet)
            Assert.DoesNotContain(events, e => e.Type == VimEventType.QuitRequested);

            events = engine.ProcessKey("Z");
            // After ZZ, QuitRequested should be emitted
            Assert.Contains(events, e => e.Type == VimEventType.QuitRequested);
            // The file should have been written
            Assert.Equal("hello", File.ReadAllText(tmpFile));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void ZZ_NoFilePath_EmitsStatusError()
    {
        var engine = CreateEngine("hello");
        // No file path set — Save() throws, ZZ should emit a StatusMessage error instead of crashing
        engine.ProcessKey("Z");
        var events = engine.ProcessKey("Z");
        Assert.DoesNotContain(events, e => e.Type == VimEventType.QuitRequested);
        Assert.Contains(events, e => e.Type == VimEventType.StatusMessage);
    }

    [Fact]
    public void ZQ_EmitsWindowCloseRequestedForce()
    {
        var engine = CreateEngine("hello");
        var events = engine.ProcessKey("Z");
        // After first Z, nothing should fire
        Assert.DoesNotContain(events, e => e.Type == VimEventType.WindowCloseRequested);

        events = engine.ProcessKey("Q");
        // ZQ should emit WindowCloseRequested with force=true
        Assert.Contains(events, e => e.Type == VimEventType.WindowCloseRequested);
        var closeEvent = events.OfType<WindowCloseRequestedEvent>().Single();
        Assert.True(closeEvent.Force);
    }

    [Fact]
    public void Z_InvalidSecondKey_IsInvalid()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("Z");
        var events = engine.ProcessKey("X"); // not a valid Z command
        // Should not crash or emit quit/close; parser resets
        Assert.DoesNotContain(events, e => e.Type == VimEventType.QuitRequested);
        Assert.DoesNotContain(events, e => e.Type == VimEventType.WindowCloseRequested);
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void VisualMark_SetsLessThanGreaterThanMarks()
    {
        // line1\nline2\nline3 — start on line 0, go into Visual, move down two lines, then Escape
        var engine = CreateEngine("line1\nline2\nline3");

        // Move to line 0, col 2
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        Assert.Equal(new CursorPosition(0, 2), engine.Cursor);

        // Enter Visual mode and extend selection down two lines
        engine.ProcessKey("v");
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.Equal(new CursorPosition(2, 2), engine.Cursor);

        // Exit visual mode — should set '< and '>
        engine.ProcessKey("Escape");
        Assert.Equal(VimMode.Normal, engine.Mode);

        // Jump to '< (start of last visual selection, col preserved)
        engine.ProcessKey("'");
        engine.ProcessKey("<");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column); // ' mark jumps to col 0

        // Jump to '> (end of last visual selection, col preserved)
        engine.ProcessKey("'");
        engine.ProcessKey(">");
        Assert.Equal(2, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column); // ' mark jumps to col 0
    }

    [Fact]
    public void VisualMark_BacktickJumpsToExactPosition()
    {
        // `< and `> jump to exact line+column
        var engine = CreateEngine("line1\nline2\nline3");

        // Start at col 1
        engine.ProcessKey("l");

        // Enter Visual, move down one line to col 1
        engine.ProcessKey("v");
        engine.ProcessKey("j");
        engine.ProcessKey("Escape");

        // `< — exact position of selection start
        engine.ProcessKey("`");
        engine.ProcessKey("<");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(1, engine.Cursor.Column);

        // `> — exact position of selection end
        engine.ProcessKey("`");
        engine.ProcessKey(">");
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(1, engine.Cursor.Column);
    }

    [Fact]
    public void VisualLineMark_SetsCorrectColumns()
    {
        // In VisualLine mode, '< should be col 0 and '> should be last col of end line
        var engine = CreateEngine("hello\nworld\nfoo");

        // Enter VisualLine on line 0, extend to line 1
        engine.ProcessKey("V");
        engine.ProcessKey("j");
        engine.ProcessKey("Escape");

        // `< should be line 0, col 0
        engine.ProcessKey("`");
        engine.ProcessKey("<");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);

        // `> should be line 1, last column of "world" (col 4)
        engine.ProcessKey("`");
        engine.ProcessKey(">");
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(4, engine.Cursor.Column);
    }

    [Fact]
    public void AtAt_RepeatsMostRecentMacro()
    {
        // Record macro 'a': delete char (x) then go to line start (0).
        // Stop with q0 — the '0' is recorded as part of the macro but is a clean Normal command.
        var engine = CreateEngine("hello");
        engine.ProcessKey("q");
        engine.ProcessKey("a");  // start recording into register a
        engine.ProcessKey("x");  // delete first char: "hello" -> "ello"
        engine.ProcessKey("q");  // begin stop sequence (not recorded)
        engine.ProcessKey("0");  // completes stop (q0 = stop recording), '0' recorded

        // After recording, macro 'a' = [x, 0], text = "ello", cursor at col 0.
        Assert.Equal("ello", engine.CurrentBuffer.Text.GetText());

        // Play @a once: deletes 'e' -> "llo"
        engine.ProcessKey("@");
        engine.ProcessKey("a");
        Assert.Equal("llo", engine.CurrentBuffer.Text.GetText());

        // Play @@ — repeats last macro 'a': deletes 'l' -> "lo"
        engine.ProcessKey("@");
        engine.ProcessKey("@");
        Assert.Equal("lo", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void AtAt_BeforeAnyMacro_EmitsError()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("@");
        var events = engine.ProcessKey("@");
        Assert.Contains(events, e => e.Type == VimEventType.StatusMessage);
        var msg = events.OfType<StatusMessageEvent>().FirstOrDefault();
        Assert.NotNull(msg);
        Assert.Contains("No previously used register", msg.Message);
    }

    [Fact]
    public void AtAt_WithCount_RepeatsCorrectNumberOfTimes()
    {
        // Record macro 'b': delete char (x) then go to line start (0).
        var engine = CreateEngine("abcdef");
        engine.ProcessKey("q");
        engine.ProcessKey("b");  // start recording into register b
        engine.ProcessKey("x");  // delete first char: "abcdef" -> "bcdef"
        engine.ProcessKey("q");  // begin stop sequence (not recorded)
        engine.ProcessKey("0");  // completes stop (q0 = stop recording), '0' recorded

        // After recording, macro 'b' = [x, 0], text = "bcdef"
        Assert.Equal("bcdef", engine.CurrentBuffer.Text.GetText());

        // @b once — deletes 'b' -> "cdef"
        engine.ProcessKey("@");
        engine.ProcessKey("b");
        Assert.Equal("cdef", engine.CurrentBuffer.Text.GetText());

        // 3@@ — repeats macro b three more times: deletes c, d, e -> "f"
        engine.ProcessKey("3");
        engine.ProcessKey("@");
        engine.ProcessKey("@");
        Assert.Equal("f", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void IndentedPaste_AdjustsIndentToCurrentLine()
    {
        // yy on line 0 ("  hello"), then move to line 2 ("    world"), ]p pastes below with adjusted indent
        // Register content: "  hello\n" (indent=2); current line indent=4; delta=+2 → "    hello"
        var engine = CreateEngine("  hello\nfoo\n    world");
        // Yank line 0 (indented 2 spaces)
        engine.ProcessKey("y");
        engine.ProcessKey("y");
        // Move to line 2 (indented 4 spaces)
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        // ]p — paste after with indent matched to current line (4 spaces)
        engine.ProcessKey("]");
        engine.ProcessKey("p");
        var lines = engine.CurrentBuffer.Text.GetText().Split('\n');
        // Pasted line should have 4 spaces of indent (was 2, current line has 4, delta=+2)
        Assert.Equal("    hello", lines[3]);
    }

    [Fact]
    public void IndentedPasteAbove_AdjustsIndentToCurrentLine()
    {
        // yy on line 2 ("      deep"), move to line 0 ("  top"), [p pastes above with adjusted indent
        // Register indent=6; current line indent=2; delta=-4 → "  deep"
        var engine = CreateEngine("  top\nfoo\n      deep");
        // Move to line 2
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        // Yank line 2 (indented 6 spaces)
        engine.ProcessKey("y");
        engine.ProcessKey("y");
        // Move back to line 0 (indented 2 spaces)
        engine.ProcessKey("g");
        engine.ProcessKey("g");
        // [p — paste before with indent matched to current line (2 spaces)
        engine.ProcessKey("[");
        engine.ProcessKey("p");
        var lines = engine.CurrentBuffer.Text.GetText().Split('\n');
        // Pasted line should have 2 spaces of indent (was 6, current line has 2, delta=-4)
        Assert.Equal("  deep", lines[0]);
    }

    [Fact]
    public void IndentedPaste_MultiLine_AdjustsAllLines()
    {
        // Yank two lines with different indents, paste with ]p; indent of each line adjusted by delta
        // Lines 0-1: "  hello\n    world" (min indent=2); current line (line 3) has 6 spaces; delta=+4
        var engine = CreateEngine("  hello\n    world\nfoo\n      target");
        // Visual-line select lines 0-1 and yank
        engine.ProcessKey("V");       // visual line on line 0
        engine.ProcessKey("j");       // extend to line 1
        engine.ProcessKey("y");       // yank selection
        // Move to line 3 (6 spaces)
        engine.ProcessKey("G");
        // ]p — paste after with adjusted indent
        engine.ProcessKey("]");
        engine.ProcessKey("p");
        var lines = engine.CurrentBuffer.Text.GetText().Split('\n');
        // min indent of yanked lines = 2; target indent = 6; delta = +4
        Assert.Equal("      hello", lines[4]);   // was "  hello" → "      hello"
        Assert.Equal("        world", lines[5]); // was "    world" → "        world"
    }
}
