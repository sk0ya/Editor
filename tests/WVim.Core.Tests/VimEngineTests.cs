using WVim.Core.Config;
using WVim.Core.Engine;
using WVim.Core.Models;

namespace WVim.Core.Tests;

public class VimEngineTests
{
    private VimEngine CreateEngine(string text = "")
    {
        var engine = new VimEngine(new VimConfig());
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
    public void CtrlNormalBindings_WorksForWHJMAndBracket()
    {
        var engine = CreateEngine("one two\nnext line");

        engine.ProcessKey("w", ctrl: true);
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
}
