using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;
using Xunit;

namespace Editor.Core.Tests;

/// <summary>Vim 無効時（plain モード）の IDE 標準キー（Loomo 設計書 §36）。</summary>
public class PlainIdeKeyTests
{
    private static VimEngine Plain(string text, int line = 0, int column = 0)
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(text);
        engine.SetVimEnabled(false);
        engine.SetCursorPosition(new CursorPosition(line, column));
        return engine;
    }

    private static string Text(VimEngine engine) => engine.CurrentBuffer.Text.GetText();

    [Fact]
    public void CtrlA_SelectsWholeDocument_AndTypingReplacesIt()
    {
        var engine = Plain("one\ntwo", 0, 1);
        engine.ProcessKey("a", ctrl: true);
        engine.ProcessKey("x");
        Assert.Equal("x", Text(engine));
    }

    [Fact]
    public void CtrlRight_MovesToStartOfNextWord_SkippingTrailingSpace()
    {
        var engine = Plain("foo.bar  baz");
        engine.ProcessKey("Right", ctrl: true);
        Assert.Equal(3, engine.Cursor.Column);
        engine.ProcessKey("Right", ctrl: true);
        Assert.Equal(4, engine.Cursor.Column);
        engine.ProcessKey("Right", ctrl: true);
        Assert.Equal(9, engine.Cursor.Column);
    }

    [Fact]
    public void CtrlLeft_AtLineStart_GoesToEndOfPreviousLine()
    {
        var engine = Plain("abc\ndef", 1, 0);
        engine.ProcessKey("Left", ctrl: true);
        Assert.Equal(new CursorPosition(0, 3), engine.Cursor);
    }

    [Fact]
    public void CtrlShiftRight_ExtendsSelection()
    {
        var engine = Plain("hello world");
        engine.ProcessKey("Right", ctrl: true, shift: true);
        engine.ProcessKey("X");
        Assert.Equal("Xworld", Text(engine));
    }

    [Fact]
    public void CtrlEnd_And_CtrlHome_JumpToDocumentEnds()
    {
        var engine = Plain("a\nbb\nccc", 1, 1);
        engine.ProcessKey("End", ctrl: true);
        Assert.Equal(new CursorPosition(2, 3), engine.Cursor);
        engine.ProcessKey("Home", ctrl: true);
        Assert.Equal(new CursorPosition(0, 0), engine.Cursor);
    }

    [Fact]
    public void CtrlBackspace_DeletesPreviousWord_AsOneUndoStep()
    {
        var engine = Plain("var name", 0, 8);
        engine.ProcessKey("Back", ctrl: true);
        Assert.Equal("var ", Text(engine));
        engine.ProcessKey("z", ctrl: true);
        Assert.Equal("var name", Text(engine));
    }

    [Fact]
    public void CtrlDelete_DeletesToNextWord()
    {
        var engine = Plain("foo bar", 0, 0);
        engine.ProcessKey("Delete", ctrl: true);
        Assert.Equal("bar", Text(engine));
    }

    [Fact]
    public void CtrlD_DuplicatesCurrentLine_AndMovesDown()
    {
        var engine = Plain("a\nb", 0, 1);
        engine.ProcessKey("d", ctrl: true);
        Assert.Equal("a\na\nb", Text(engine));
        Assert.Equal(new CursorPosition(1, 1), engine.Cursor);
    }

    [Fact]
    public void CtrlD_WithSelection_DuplicatesSelectionAfterIt()
    {
        var engine = Plain("ab");
        engine.ProcessKey("End", shift: true);
        engine.ProcessKey("d", ctrl: true);
        Assert.Equal("abab", Text(engine));
    }

    [Fact]
    public void CtrlD_IsItsOwnUndoStep_NotMergedWithTyping()
    {
        var engine = Plain("", 0, 0);
        engine.ProcessKey("x");
        engine.ProcessKey("d", ctrl: true);
        engine.ProcessKey("z", ctrl: true);
        Assert.Equal("x", Text(engine));
    }

    [Fact]
    public void AltShiftDown_MovesLineDown_AndBack()
    {
        var engine = Plain("one\ntwo\nthree", 0, 1);
        engine.ProcessKey("Down", shift: true, alt: true);
        Assert.Equal("two\none\nthree", Text(engine));
        Assert.Equal(new CursorPosition(1, 1), engine.Cursor);
        engine.ProcessKey("Up", shift: true, alt: true);
        Assert.Equal("one\ntwo\nthree", Text(engine));
    }

    [Fact]
    public void AltShiftUp_OnFirstLine_DoesNothing()
    {
        var engine = Plain("one\ntwo", 0, 0);
        engine.ProcessKey("Up", shift: true, alt: true);
        Assert.Equal("one\ntwo", Text(engine));
    }

    [Fact]
    public void CtrlSlash_TogglesLineComment()
    {
        var engine = Plain("x = 1;", 0, 0);
        engine.Syntax.SetLanguage("C#");
        engine.ProcessKey("/", ctrl: true);
        Assert.StartsWith("//", Text(engine));
        engine.ProcessKey("/", ctrl: true);
        Assert.Equal("x = 1;", Text(engine));
    }

    [Fact]
    public void CtrlX_WithoutSelection_CutsWholeLine()
    {
        var engine = Plain("keep\ncut\nkeep2", 1, 1);
        engine.ProcessKey("x", ctrl: true);
        Assert.Equal("keep\nkeep2", Text(engine));
    }

    [Fact]
    public void CtrlShiftZ_Redoes()
    {
        var engine = Plain("", 0, 0);
        engine.ProcessKey("a");
        engine.ProcessKey("z", ctrl: true);
        Assert.Equal("", Text(engine));
        engine.ProcessKey("z", ctrl: true, shift: true);
        Assert.Equal("a", Text(engine));
    }

    [Fact]
    public void ShiftEnter_StartsNewLineBelow_FromMidLine()
    {
        var engine = Plain("abc\nnext", 0, 1);
        engine.ProcessKey("Return", shift: true);
        Assert.Equal("abc\n\nnext", Text(engine));
        Assert.Equal(1, engine.Cursor.Line);
    }

    [Fact]
    public void CtrlAltEnter_StartsNewLineAbove_KeepingIndent()
    {
        var engine = Plain("    body", 0, 6);
        engine.ProcessKey("Return", ctrl: true, alt: true);
        Assert.Equal("    \n    body", Text(engine));
        Assert.Equal(new CursorPosition(0, 4), engine.Cursor);
    }

    [Fact]
    public void Home_TogglesBetweenFirstNonBlankAndColumnZero()
    {
        var engine = Plain("    code", 0, 6);
        engine.ProcessKey("Home");
        Assert.Equal(4, engine.Cursor.Column);
        engine.ProcessKey("Home");
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void PageDown_MovesByViewport()
    {
        var engine = Plain(string.Join("\n", Enumerable.Range(0, 50).Select(i => $"l{i}")));
        engine.SetViewportState(0, 11);
        engine.ProcessKey("PageDown");
        Assert.Equal(10, engine.Cursor.Line);
    }

    [Fact]
    public void ReadOnly_RejectsDuplicateAndMove()
    {
        var engine = Plain("a\nb", 0, 0);
        engine.IsReadOnly = true;
        engine.ProcessKey("d", ctrl: true);
        engine.ProcessKey("Down", shift: true, alt: true);
        Assert.Equal("a\nb", Text(engine));
    }

    [Fact]
    public void VimEnabled_CtrlD_IsNotDuplicate()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText("a\nb");
        engine.ProcessKey("d", ctrl: true);
        Assert.Equal("a\nb", Text(engine));
    }
}
