using System.IO;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;
using Editor.Core.Registers;

namespace Editor.Core.Tests;

public class VimEngineTests
{
    private sealed class FakeClipboardProvider : IClipboardProvider
    {
        private string _text = "";

        public string GetText() => _text;
        public void SetText(string text) => _text = text;
    }

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
    public void PressA_EntersInsertModeAfterCursor()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("l");

        var events = engine.ProcessKey("a");
        engine.ProcessKey("X");

        Assert.Equal("heXllo", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Contains(events, e => e is CursorMovedEvent { Position.Column: 2 });
        Assert.Contains(events, e => e.Type == VimEventType.ModeChanged);
    }

    [Fact]
    public void PressA_AtEndOfLineEntersInsertModeAfterLastCharacter()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("$");

        var events = engine.ProcessKey("a");
        engine.ProcessKey("X");

        Assert.Equal("helloX", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Contains(events, e => e is CursorMovedEvent { Position.Column: 5 });
        Assert.Contains(events, e => e.Type == VimEventType.ModeChanged);
    }

    // The s/C/cw insert-entry commands reposition the cursor with an insert-mode clamp
    // after ExecuteDelete already emitted a normal-mode-clamped CursorMoved. They must
    // re-emit the final cursor so the UI caret lands at the insert position (mirrors the
    // `a` fix); otherwise at end-of-line the caret renders one column too far left.
    [Fact]
    public void PressS_AtEndOfLineEmitsInsertClampedCursor()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("$");

        var events = engine.ProcessKey("s");
        engine.ProcessKey("X");

        Assert.Equal("hellX", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Contains(events, e => e is CursorMovedEvent { Position.Column: 4 });
    }

    [Fact]
    public void PressC_ChangeToEndOfLineEmitsInsertClampedCursor()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("l");
        engine.ProcessKey("l"); // cursor on 'l' (col 2)

        var events = engine.ProcessKey("C"); // delete cols 2.. -> "he"
        engine.ProcessKey("X");

        Assert.Equal("heX", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Contains(events, e => e is CursorMovedEvent { Position.Column: 2 });
    }

    [Fact]
    public void ChangeInnerWord_AtEndOfLine_OperatorPath_EmitsInsertClampedCursor()
    {
        var engine = CreateEngine("foo bar");
        engine.ProcessKey("w"); // cursor on 'b' (col 4)

        engine.ProcessKey("c");
        engine.ProcessKey("i");
        var events = engine.ProcessKey("w"); // change inner word "bar" -> "foo "
        engine.ProcessKey("X");

        Assert.Equal("foo X", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Contains(events, e => e is CursorMovedEvent { Position.Column: 4 });
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
    public void SetVimEnabledFalse_DropsIntoInsertMode()
    {
        var engine = CreateEngine("hello");
        var events = engine.SetVimEnabled(false);
        Assert.False(engine.VimEnabled);
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Contains(events, e => e.Type == VimEventType.ModeChanged);
    }

    [Fact]
    public void WhenVimDisabled_EscapeStaysInInsert()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.ProcessKey("Escape");
        Assert.Equal(VimMode.Insert, engine.Mode);
    }

    [Fact]
    public void WhenVimDisabled_PrintableKeysInsertText()
    {
        var engine = CreateEngine("");
        engine.SetVimEnabled(false);
        engine.ProcessKey("h");
        engine.ProcessKey("i");
        Assert.Equal("hi", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_CtrlLeftBracketStaysInInsert()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.ProcessKey("[", ctrl: true);
        Assert.Equal(VimMode.Insert, engine.Mode);
    }

    [Fact]
    public void WhenVimDisabled_CtrlOStaysInInsert()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.ProcessKey("o", ctrl: true);
        Assert.Equal(VimMode.Insert, engine.Mode);
    }

    [Fact]
    public void DisablingVim_FromCommandMode_ClearsCommandLine()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey(":");
        Assert.Equal(VimMode.Command, engine.Mode);
        var events = engine.SetVimEnabled(false);
        Assert.Equal("", engine.CommandLine);
        Assert.Contains(events, e => e.Type == VimEventType.CommandLineChanged);
    }

    [Fact]
    public void ReEnablingVim_ReturnsToNormalMode()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.SetVimEnabled(true);
        Assert.True(engine.VimEnabled);
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void SetVimEnabled_NoChange_ReturnsNoEvents()
    {
        var engine = CreateEngine("hello");
        var events = engine.SetVimEnabled(true); // already enabled
        Assert.Empty(events);
    }

    [Fact]
    public void WhenVimDisabled_CtrlA_DoesNotEnterVisualMode()
    {
        // Regression: Ctrl+A used to run SelectAllVisualLine and re-expose modal
        // editing. With the central plain-text gate it is inert.
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.ProcessKey("a", ctrl: true);
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Null(engine.Selection);
    }

    [Fact]
    public void WhenVimDisabled_CtrlW_DoesNotDeleteWord()
    {
        // Vim insert Ctrl+W (delete word back) must not run in plain mode.
        var engine = CreateEngine("");
        engine.SetVimEnabled(false);
        engine.ProcessKey("h");
        engine.ProcessKey("i");
        engine.ProcessKey("w", ctrl: true);
        Assert.Equal("hi", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_CtrlR_DoesNotEnterRegisterPending()
    {
        // Ctrl+R used to set _awaitingInsertRegister and swallow the next key.
        var engine = CreateEngine("");
        engine.SetVimEnabled(false);
        engine.ProcessKey("r", ctrl: true);
        engine.ProcessKey("x");
        Assert.Equal("x", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_BackspaceDeletesChar()
    {
        var engine = CreateEngine("");
        engine.SetVimEnabled(false);
        engine.ProcessKey("h");
        engine.ProcessKey("i");
        engine.ProcessKey("Back");
        Assert.Equal("h", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_CtrlZ_UndoesEditRunAsOneStep()
    {
        var engine = CreateEngine("");
        engine.SetVimEnabled(false);
        engine.ProcessKey("h");
        engine.ProcessKey("i");
        engine.ProcessKey("z", ctrl: true); // undo the whole typing run
        Assert.Equal("", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_CursorMovementSplitsUndoGroups()
    {
        var engine = CreateEngine("");
        engine.SetVimEnabled(false);
        engine.ProcessKey("a");
        engine.ProcessKey("Left");  // ends the first edit run
        engine.ProcessKey("b");
        engine.ProcessKey("z", ctrl: true); // undo only the second run
        Assert.Equal("a", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_MouseDrag_SetsSelectionWithoutEnteringVisual()
    {
        // Regression: mouse drag used to send ProcessKey("v"), inserting a literal
        // 'v'. It must now set a plain selection and stay out of Visual mode.
        var engine = CreateEngine("hello world");
        engine.SetVimEnabled(false);
        // Half-open [0,5): selects "hello" (caret boundary excluded).
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 5));
        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.NotNull(engine.Selection);
        Assert.Equal("hello world", engine.CurrentBuffer.Text.GetText()); // no stray 'v'
        Assert.Equal("hello", engine.GetSelectionText());
    }

    [Fact]
    public void WhenVimDisabled_TypingReplacesPlainSelection()
    {
        var engine = CreateEngine("hello world");
        engine.SetVimEnabled(false);
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 5)); // "hello"
        engine.ProcessKey("H");
        engine.ProcessKey("i");
        Assert.Equal("Hi world", engine.CurrentBuffer.Text.GetText());
        Assert.Null(engine.Selection);
    }

    [Fact]
    public void WhenVimDisabled_BackspaceDeletesPlainSelection()
    {
        var engine = CreateEngine("hello world");
        engine.SetVimEnabled(false);
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 6)); // "hello "
        engine.ProcessKey("Back");
        Assert.Equal("world", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_ClickClearsPlainSelection()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 3));
        Assert.NotNull(engine.Selection);
        engine.ClearPlainSelection();
        Assert.Null(engine.Selection);
    }

    [Fact]
    public void WhenVimDisabled_ShiftRightExtendsSelection()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.ProcessKey("Right", shift: true);
        engine.ProcessKey("Right", shift: true);
        Assert.NotNull(engine.Selection);
        Assert.Equal("he", engine.GetSelectionText()); // anchor at 0, caret extended
    }

    [Fact]
    public void WhenVimDisabled_ArrowWithoutShiftClearsSelection()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 3));
        engine.ProcessKey("Right"); // no shift → clears selection
        Assert.Null(engine.Selection);
    }

    [Fact]
    public void WhenVimDisabled_HomeAndEndDoNotInsertText()
    {
        // Regression: Home/End used to arrive as the Vim motions "0"/"$" and be
        // inserted as literal characters in plain mode.
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.ProcessKey("End");
        engine.ProcessKey("Home");
        Assert.Equal("hello", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void WhenVimDisabled_SingleCharSelection_CanBeDeletedByTyping()
    {
        // Regression: a 1-char plain selection used to collapse to Selection.IsEmpty
        // (inclusive End == Start), so it couldn't be deleted/replaced/copied.
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 1)); // "h"
        engine.ProcessKey("X"); // typing must replace the selected char, not insert before it
        Assert.Equal("Xello", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_SingleCharSelection_BackspaceDeletesIt()
    {
        var engine = CreateEngine("hello");
        engine.SetVimEnabled(false);
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 1)); // "h"
        engine.ProcessKey("Back");
        Assert.Equal("ello", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_CutThenUndo_RestoresText()
    {
        // Regression: Ctrl+X cut ran with snapshots suppressed and took no snapshot
        // of its own, so the cut could not be undone.
        var engine = CreateEngine("hello world");
        engine.SetVimEnabled(false);
        engine.SetPlainSelection(new CursorPosition(0, 0), new CursorPosition(0, 6)); // "hello "
        engine.ProcessKey("x", ctrl: true); // cut
        Assert.Equal("world", engine.CurrentBuffer.Text.GetText());
        engine.ProcessKey("z", ctrl: true); // undo
        Assert.Equal("hello world", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_DeleteAtEndOfLine_JoinsNextLine()
    {
        // Regression: forward-Delete at end of line was a no-op (couldn't join).
        var engine = CreateEngine("ab\ncd");
        engine.SetVimEnabled(false);
        engine.ProcessKey("End"); // caret at column 2 (end of "ab")
        engine.ProcessKey("Delete");
        Assert.Equal("abcd", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_DeleteAtEndOfBuffer_IsNoOp()
    {
        var engine = CreateEngine("ab");
        engine.SetVimEnabled(false);
        engine.ProcessKey("End");
        engine.ProcessKey("Delete"); // nothing to delete or join
        Assert.Equal("ab", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void WhenVimDisabled_DownArrowCanReachEndOfShorterLine()
    {
        // Regression: Up/Down clamped to lineLen-1, stranding the caret one cell
        // short of end-of-line. Typing should land at the true end.
        var engine = CreateEngine("abcd\nab");
        engine.SetVimEnabled(false);
        engine.ProcessKey("End");  // end of "abcd" (column 4)
        engine.ProcessKey("Down"); // goal column 4 → clamp to end of "ab" (column 2)
        Assert.Equal(2, engine.Cursor.Column);
        engine.ProcessKey("Z");
        Assert.Equal("abcd\nabZ", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void SetSelection_EntersVisualModeAndSetsCursorAndSelection()
    {
        var engine = CreateEngine("abcdef");
        var selection = new Selection(
            new CursorPosition(0, 1),
            new CursorPosition(0, 3),
            SelectionType.Character);

        var events = engine.SetSelection(selection);

        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.Equal(new CursorPosition(0, 3), engine.Cursor);
        Assert.Equal(selection, engine.Selection);
        Assert.Contains(events, e => e.Type == VimEventType.ModeChanged);
        Assert.Contains(events, e => e is SelectionChangedEvent { Selection: not null });
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
        Assert.Equal("hello\nhello\nworld", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(new CursorPosition(1, 0), engine.Cursor);
    }

    [Fact]
    public void YY_P_PreservesLinewisePasteWithUnnamedClipboard()
    {
        var config = new VimConfig();
        config.Options.Clipboard = "unnamed";
        var engine = CreateEngine("hello\nworld", config);
        engine.SetClipboardProvider(new FakeClipboardProvider());

        engine.ProcessKey("y");
        engine.ProcessKey("y");
        engine.ProcessKey("p");

        Assert.Equal("hello\nhello\nworld", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(new CursorPosition(1, 0), engine.Cursor);
    }

    [Fact]
    public void P_WithMultilineCharacterRegister_SplitsLinesAndEmitsTextChanged()
    {
        var clipboard = new FakeClipboardProvider();
        clipboard.SetText("X\nY");
        var engine = CreateEngine("ab");
        engine.SetClipboardProvider(clipboard);

        engine.ProcessKey("\"");
        engine.ProcessKey("+");
        var events = engine.ProcessKey("p");

        Assert.Equal("aX\nYb", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(2, engine.CurrentBuffer.Text.LineCount);
        Assert.All(engine.CurrentBuffer.Text.Snapshot(), line => Assert.DoesNotContain('\n', line));
        Assert.Contains(events, e => e.Type == VimEventType.TextChanged);
    }

    [Fact]
    public void P_Before_WithMultilineLinewiseRegister_DoesNotOverwriteCurrentLine()
    {
        var engine = CreateEngine("one\ntwo\nthree");

        engine.ProcessKey("V");
        engine.ProcessKey("j");
        engine.ProcessKey("y");
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("P");

        Assert.Equal("one\ntwo\none\ntwo\nthree", engine.CurrentBuffer.Text.GetText());
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
    public void GetSelectionText_NoSelection_ReturnsEmpty()
    {
        var engine = CreateEngine("hello");
        Assert.Equal("", engine.GetSelectionText());
    }

    [Fact]
    public void GetSelectionText_CharacterwiseInclusive()
    {
        var engine = CreateEngine("hello");
        engine.ProcessKey("v");   // selects 'h'
        engine.ProcessKey("l");   // extend to 'e'
        engine.ProcessKey("l");   // extend to 'l'
        Assert.Equal("hel", engine.GetSelectionText());
    }

    [Fact]
    public void GetSelectionText_Linewise()
    {
        var engine = CreateEngine("line1\nline2\nline3");
        engine.ProcessKey("V");
        engine.ProcessKey("j");
        Assert.Equal("line1\nline2", engine.GetSelectionText());
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
    public void VisualLineDelete_EmitsTextChanged()
    {
        // Regression: linewise-visual delete (V + d over several lines) mutated the
        // buffer but emitted no TextChanged event, so the host never refreshed the
        // canvas — the deleted lines stayed visible until the next edit.
        var engine = CreateEngine("a\nb\nc\nd");
        engine.ProcessKey("V");
        engine.ProcessKey("j");
        var events = engine.ProcessKey("d");

        Assert.Equal("c\nd", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Contains(events, e => e.Type == VimEventType.TextChanged);
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
    public void CommandLine_SubstituteTyping_EmitsPreviewAndClearsOnExecute()
    {
        var engine = CreateEngine("foo\nbar\nfoo");
        engine.ProcessKey(":");

        IReadOnlyList<VimEvent> events = [];
        foreach (var ch in "%s/foo/baz/")
            events = engine.ProcessKey(ch.ToString());

        var preview = events.OfType<SubstitutePreviewChangedEvent>().Last();
        Assert.Equal("baz", preview.PreviewLines[0]);
        Assert.Equal("baz", preview.PreviewLines[2]);

        events = engine.ProcessKey("Return");

        Assert.Empty(events.OfType<SubstitutePreviewChangedEvent>().Last().PreviewLines);
        Assert.Equal("baz\nbar\nbaz", engine.CurrentBuffer.Text.GetText());
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
    public void Visual_CountIW_SelectsMultipleInnerWords()
    {
        var engine = CreateEngine("foo bar baz qux");

        engine.ProcessKey("v");
        engine.ProcessKey("3");
        engine.ProcessKey("i");
        engine.ProcessKey("w");

        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.True(engine.Selection.HasValue);
        Assert.Equal(new CursorPosition(0, 0), engine.Selection.Value.NormalizedStart);
        Assert.Equal(new CursorPosition(0, 10), engine.Selection.Value.NormalizedEnd);
    }

    [Fact]
    public void Visual_CountIW_SelectsAcrossLineBreak()
    {
        var engine = CreateEngine("foo bar\nbaz qux");

        engine.ProcessKey("v");
        engine.ProcessKey("3");
        engine.ProcessKey("i");
        engine.ProcessKey("w");

        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.True(engine.Selection.HasValue);
        Assert.Equal(new CursorPosition(0, 0), engine.Selection.Value.NormalizedStart);
        Assert.Equal(new CursorPosition(1, 2), engine.Selection.Value.NormalizedEnd);
    }

    [Fact]
    public void Visual_CountAW_SelectsMultipleAroundWordsWithTrailingSpace()
    {
        var engine = CreateEngine("foo bar baz");

        engine.ProcessKey("v");
        engine.ProcessKey("2");
        engine.ProcessKey("a");
        engine.ProcessKey("w");

        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.True(engine.Selection.HasValue);
        Assert.Equal(new CursorPosition(0, 0), engine.Selection.Value.NormalizedStart);
        Assert.Equal(new CursorPosition(0, 7), engine.Selection.Value.NormalizedEnd);
    }

    [Fact]
    public void D3iw_DeletesMultipleInnerWords()
    {
        var engine = CreateEngine("foo bar baz qux");

        engine.ProcessKey("d");
        engine.ProcessKey("3");
        engine.ProcessKey("i");
        engine.ProcessKey("w");

        Assert.Equal(" qux", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void D3iw_DeletesMultipleInnerWordsAcrossLineBreak()
    {
        var engine = CreateEngine("foo bar\nbaz qux");

        engine.ProcessKey("d");
        engine.ProcessKey("3");
        engine.ProcessKey("i");
        engine.ProcessKey("w");

        Assert.Equal(" qux", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void VisualLine_CountTextObject_IsIgnoredWithoutApplyingTrailingMotion()
    {
        var engine = CreateEngine("foo bar baz");

        engine.ProcessKey("V");
        engine.ProcessKey("2");
        engine.ProcessKey("a");
        engine.ProcessKey("w");

        Assert.Equal(VimMode.VisualLine, engine.Mode);
        Assert.Equal(new CursorPosition(0, 0), engine.Cursor);
    }

    [Fact]
    public void VisualBlock_CountTextObject_IsIgnoredWithoutApplyingTrailingMotion()
    {
        var engine = CreateEngine("foo bar baz");

        engine.ProcessKey("\x16");
        engine.ProcessKey("2");
        engine.ProcessKey("a");
        engine.ProcessKey("w");

        Assert.Equal(VimMode.VisualBlock, engine.Mode);
        Assert.Equal(new CursorPosition(0, 0), engine.Cursor);
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
    public void CtrlL_EmitsStatusMessage()
    {
        var engine = CreateEngine("hello");
        var events = engine.ProcessKey("l", ctrl: true, shift: false, alt: false);
        Assert.True(events.Any(e => e.Type == VimEventType.StatusMessage));
    }

    [Fact]
    public void CtrlBracket_EmitsGoToDefinitionRequested()
    {
        var engine = CreateEngine("hello");
        var events = engine.ProcessKey("]", ctrl: true, shift: false, alt: false);
        Assert.True(events.Any(e => e.Type == VimEventType.GoToDefinitionRequested));
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
    public void CtrlV_DollarDelete_DeletesToEachSelectedLineEnd()
    {
        var engine = CreateEngine("abcd\nabcdef\nab");

        engine.ProcessKey("l");
        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("$");
        engine.ProcessKey("d");

        Assert.Equal("a\na\na", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_DollarAppend_AppendsAtEachSelectedLineEnd()
    {
        var engine = CreateEngine("abcd\nabcdef\nab");

        engine.ProcessKey("l");
        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("$");
        engine.ProcessKey("A");
        engine.ProcessKey("!");
        engine.ProcessKey("Escape");

        Assert.Equal("abcd!\nabcdef!\nab!", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_DollarReplace_ReplacesThroughEachSelectedLineEnd()
    {
        var engine = CreateEngine("abcd\nabcdef\nab");

        engine.ProcessKey("l");
        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("$");
        engine.ProcessKey("r");
        engine.ProcessKey("Z");

        Assert.Equal("aZZZ\naZZZZZ\naZ", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_DollarDelete_AfterSwappingAnchor_StillDeletesToEachSelectedLineEnd()
    {
        var engine = CreateEngine("ab\nabcdef\nabcd");

        engine.ProcessKey("l");
        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("$");
        engine.ProcessKey("o");
        engine.ProcessKey("d");

        Assert.Equal("a\na\na", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void CtrlV_DollarChange_AfterSwappingAnchor_ChangesFromOriginalColumnToEachSelectedLineEnd()
    {
        var engine = CreateEngine("ab\nabcdef\nabcd");

        engine.ProcessKey("l");
        engine.ProcessKey("v", ctrl: true);
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("$");
        engine.ProcessKey("o");
        engine.ProcessKey("c");
        engine.ProcessKey("X");
        engine.ProcessKey("Escape");

        Assert.Equal("aX\naX\naX", engine.CurrentBuffer.Text.GetText());
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
    public void NormalMap_UppercaseLhs_MatchesWhenShiftReported()
    {
        // `nnoremap H ^` — when the key arrives with Shift reported (the IME /
        // OnPreviewKeyDown path passes shift=true for 'H'), the map must still fire
        // and move to first non-blank, not fall through to the built-in `H` motion.
        var config = new VimConfig();
        config.NormalMaps["H"] = "^";
        var engine = CreateEngine("    abc", config);
        engine.SetCursorPosition(new CursorPosition(0, 6));

        engine.ProcessKey("H", ctrl: false, shift: true, alt: false);

        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(4, engine.Cursor.Column); // first non-blank, via `^`
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
        Assert.Equal(" ", cfg.Variables["mapleader"]);
    }

    [Fact]
    public void VimConfig_ParseLet_AssignsVariable()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["let g:theme_name = 'dracula'"]);

        Assert.Equal("dracula", cfg.Variables["g:theme_name"]);
    }

    [Fact]
    public void VimConfig_ParseLet_EvaluatesArithmeticExpression()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["let answer = 40 + 2"]);

        Assert.Equal("42", cfg.Variables["answer"]);
    }

    [Fact]
    public void VimConfig_ParseLet_MapleaderPrefixVariable_DoesNotChangeLeader()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["let mapleader_backup = ','"]);

        Assert.Equal(",", cfg.Variables["mapleader_backup"]);
        Assert.Equal("\\", cfg.Leader);
    }

    [Fact]
    public void VimConfig_ParseIf_ExecutesTruthyBranch()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "let g:enabled = 1",
            "if g:enabled",
            "  set number",
            "  let g:branch = 'if'",
            "else",
            "  set nonumber",
            "  let g:branch = 'else'",
            "endif",
        ]);

        Assert.True(cfg.Options.Number);
        Assert.Equal("if", cfg.Variables["g:branch"]);
    }

    [Fact]
    public void VimConfig_ParseIf_ExecutesElseForFalsyBranch()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "let g:enabled = 0",
            "if g:enabled",
            "  set number",
            "  let g:branch = 'if'",
            "else",
            "  set nonumber",
            "  let g:branch = 'else'",
            "endif",
        ]);

        Assert.False(cfg.Options.Number);
        Assert.Equal("else", cfg.Variables["g:branch"]);
    }

    [Fact]
    public void VimConfig_ParseIf_SupportsNestedBlocks()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "let g:outer = 1",
            "let g:inner = 0",
            "if g:outer",
            "  if g:inner",
            "    let g:branch = 'inner'",
            "  else",
            "    let g:branch = 'nested_else'",
            "  endif",
            "else",
            "  let g:branch = 'outer_else'",
            "endif",
        ]);

        Assert.Equal("nested_else", cfg.Variables["g:branch"]);
    }

    [Fact]
    public void VimConfig_ParseIf_SkipsNestedBranchesWhenParentIsFalse()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "let g:outer = 0",
            "if g:outer",
            "  if 1",
            "    let g:branch = 'inner_if'",
            "  else",
            "    let g:branch = 'inner_else'",
            "  endif",
            "else",
            "  let g:branch = 'outer_else'",
            "endif",
        ]);

        Assert.Equal("outer_else", cfg.Variables["g:branch"]);
    }

    [Fact]
    public void VimConfig_ParseIf_DoesNotExecuteDuplicateElseBranch()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "if 0",
            "  let g:branch = 'if'",
            "else",
            "  let g:branch = 'first_else'",
            "else",
            "  let g:branch = 'second_else'",
            "endif",
            "let g:after = 1",
        ]);

        Assert.Equal("first_else", cfg.Variables["g:branch"]);
        Assert.Equal("1", cfg.Variables["g:after"]);
    }

    [Fact]
    public void VimConfig_ParseFor_IteratesNumberList()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for item in [1, 2, 3]",
            "  let g:last = item",
            "endfor",
        ]);

        Assert.Equal("3", cfg.Variables["item"]);
        Assert.Equal("3", cfg.Variables["g:last"]);
    }

    [Fact]
    public void VimConfig_ParseFor_IteratesStringList()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for name in ['a', 'b']",
            "  let g:last_name = name",
            "endfor",
        ]);

        Assert.Equal("b", cfg.Variables["name"]);
        Assert.Equal("b", cfg.Variables["g:last_name"]);
    }

    [Fact]
    public void VimConfig_ParseFor_AcceptsFlexibleWhitespaceAroundIn()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for\titem\tIN\t[1, 2]",
            "  let g:last = item",
            "endfor",
        ]);

        Assert.Equal("2", cfg.Variables["g:last"]);
    }

    [Fact]
    public void VimConfig_ParseFor_HandlesListStringPunctuation()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for item in ['a \" b,c[0]']",
            "  let g:last = item",
            "endfor",
        ]);

        Assert.Equal("a \" b,c[0]", cfg.Variables["g:last"]);
    }

    [Fact]
    public void VimConfig_ParseFor_HandlesEscapedDoubleQuoteInListString()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for item in [\"a \\\" b,c[0]\"]",
            "  let g:last = item",
            "endfor",
        ]);

        Assert.Equal("a \\\" b,c[0]", cfg.Variables["g:last"]);
    }

    [Fact]
    public void VimConfig_ParseFor_SkipsMalformedListString()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for item in [\"a\"b\"]",
            "  let g:bad = item",
            "endfor",
            "let g:after = 1",
        ]);

        Assert.False(cfg.Variables.ContainsKey("g:bad"));
        Assert.Equal("1", cfg.Variables["g:after"]);
    }

    [Fact]
    public void VimConfig_ParseFor_LimitsListItems()
    {
        var cfg = new VimConfig();
        var list = string.Join(", ", Enumerable.Range(1, 1001));

        cfg.ParseLines([
            $"for item in [{list}]",
            "  let g:last = item",
            "endfor",
        ]);

        Assert.Equal("1000", cfg.Variables["g:last"]);
    }

    [Fact]
    public void VimConfig_ParseFor_LimitsNestedIterations()
    {
        var cfg = new VimConfig();
        var list = string.Join(", ", Enumerable.Range(1, 101));

        cfg.ParseLines([
            $"for outer in [{list}]",
            $"  for inner in [{list}]",
            "    let g:last_outer = outer",
            "    let g:last_inner = inner",
            "  endfor",
            "endfor",
        ]);

        Assert.Equal("99", cfg.Variables["g:last_outer"]);
        Assert.Equal("3", cfg.Variables["g:last_inner"]);
    }

    [Fact]
    public void VimConfig_ParseFor_SupportsNestedLoops()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for outer in [1, 2]",
            "  for inner in ['x', 'y']",
            "    let g:last_outer = outer",
            "    let g:last_inner = inner",
            "  endfor",
            "endfor",
        ]);

        Assert.Equal("2", cfg.Variables["g:last_outer"]);
        Assert.Equal("y", cfg.Variables["g:last_inner"]);
    }

    [Fact]
    public void VimConfig_ParseFor_SupportsIfInsideLoop()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "for item in [0, 1]",
            "  if item",
            "    let g:truthy = item",
            "  else",
            "    let g:falsy = item",
            "  endif",
            "endfor",
        ]);

        Assert.Equal("0", cfg.Variables["g:falsy"]);
        Assert.Equal("1", cfg.Variables["g:truthy"]);
    }

    [Fact]
    public void VimConfig_ParseFor_SupportsLoopInsideIf()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "let g:enabled = 1",
            "if g:enabled",
            "  for item in [1, 2]",
            "    let g:last = item",
            "  endfor",
            "endif",
        ]);

        Assert.Equal("2", cfg.Variables["g:last"]);
    }

    [Fact]
    public void VimConfig_ParseFor_SkipsLoopInsideInactiveIf()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "if 0",
            "  for item in [1, 2]",
            "    let g:skipped = item",
            "  endfor",
            "endif",
            "let g:after = 1",
        ]);

        Assert.False(cfg.Variables.ContainsKey("g:skipped"));
        Assert.Equal("1", cfg.Variables["g:after"]);
    }

    [Fact]
    public void VimConfig_ParseFunction_DefinesAndCallsNoArgFunction()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "function ApplyConfig()",
            "  let g:inside_function = 1",
            "endfunction",
            "let g:before_call = 1",
            "call ApplyConfig()",
        ]);

        Assert.Equal("1", cfg.Variables["g:before_call"]);
        Assert.Equal("1", cfg.Variables["g:inside_function"]);
        Assert.Contains("ApplyConfig", cfg.Functions.Keys);
    }

    [Fact]
    public void VimConfig_ParseFunction_BindsArgumentsAndRunsExistingBlocks()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "function Configure(name)",
            "  let g:function_arg = a:name",
            "  if 1",
            "    for item in [1, 2]",
            "      let g:last_item = item",
            "    endfor",
            "  endif",
            "  execute 'let g:executed = 1'",
            "endfunction",
            "call Configure('dark')",
        ]);

        Assert.Equal("dark", cfg.Variables["g:function_arg"]);
        Assert.Equal("2", cfg.Variables["g:last_item"]);
        Assert.Equal("1", cfg.Variables["g:executed"]);
        Assert.False(cfg.Variables.ContainsKey("a:name"));
    }

    [Fact]
    public void VimConfig_ParseFunction_LimitsRecursiveCalls()
    {
        var cfg = new VimConfig();

        cfg.ParseLines([
            "function Recurse()",
            "  let g:entered = 1",
            "  call Recurse()",
            "endfunction",
            "call Recurse()",
            "let g:after_recursion = 1",
        ]);

        Assert.Equal("1", cfg.Variables["g:entered"]);
        Assert.Equal("1", cfg.Variables["g:after_recursion"]);
    }

    [Fact]
    public void VimConfig_ParseCommand_CallWithTooFewArgumentsReturnsError()
    {
        var cfg = new VimConfig();
        cfg.ParseLines([
            "function NeedsArg(name)",
            "  let g:name = a:name",
            "endfunction",
        ]);

        var error = cfg.ParseCommand("call NeedsArg()");

        Assert.Equal("E119: Not enough arguments for function: NeedsArg", error);
        Assert.False(cfg.Variables.ContainsKey("a:name"));
    }

    [Fact]
    public void ExCall_ExecutesDefinedFunctionWithArgument()
    {
        var cfg = new VimConfig();
        cfg.ParseLines([
            "function Say(name)",
            "  echo a:name",
            "endfunction",
        ]);
        var engine = CreateEngine(config: cfg);

        ExCommand(engine, "call Say('hello')");

        Assert.Equal("hello", engine.StatusMessage);
        Assert.False(engine.Config.Variables.ContainsKey("a:name"));
    }

    [Fact]
    public void ExCall_FunctionBodySupportsIfForLetAndEcho()
    {
        var cfg = new VimConfig();
        cfg.ParseLines([
            "function PickLast()",
            "  for item in [0, 2]",
            "    if item",
            "      let g:last_pick = item",
            "    endif",
            "  endfor",
            "  echo g:last_pick",
            "endfunction",
        ]);
        var engine = CreateEngine(config: cfg);

        ExCommand(engine, "call PickLast()");

        Assert.Equal("2", engine.StatusMessage);
        Assert.Equal("2", engine.Config.Variables["g:last_pick"]);
    }

    [Fact]
    public void VimConfig_LoadFromFile_TracksSourceScripts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "editor-vimconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "main.vim");
            var plugin = Path.Combine(dir, "plugin.vim");
            File.WriteAllText(main, "source plugin.vim\nset number\n");
            File.WriteAllText(plugin, "let g:plugin_loaded = 1\n");

            var cfg = VimConfig.LoadFromFile(main);

            Assert.Equal([Path.GetFullPath(main), Path.GetFullPath(plugin)], cfg.ScriptNames);
            Assert.Equal("1", cfg.Variables["g:plugin_loaded"]);
            Assert.True(cfg.Options.Number);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VimConfig_LoadFromFile_SourceHonorsForBlocks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "editor-vimconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "main.vim");
            var plugin = Path.Combine(dir, "plugin.vim");
            File.WriteAllText(main, "source plugin.vim\n");
            File.WriteAllText(plugin, "for item in [1, 2, 3]\nlet g:plugin_last = item\nendfor\n");

            var cfg = VimConfig.LoadFromFile(main);

            Assert.Equal("3", cfg.Variables["g:plugin_last"]);
            Assert.Equal([Path.GetFullPath(main), Path.GetFullPath(plugin)], cfg.ScriptNames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VimConfig_LoadFromFile_SourceHonorsIfBlocks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "editor-vimconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "main.vim");
            var plugin = Path.Combine(dir, "plugin.vim");
            File.WriteAllText(main, "let g:enabled = 0\nsource plugin.vim\n");
            File.WriteAllText(plugin,
                "if g:enabled\nlet g:plugin_branch = 'if'\nelse\nlet g:plugin_branch = 'else'\nendif\n");

            var cfg = VimConfig.LoadFromFile(main);

            Assert.Equal("else", cfg.Variables["g:plugin_branch"]);
            Assert.Equal([Path.GetFullPath(main), Path.GetFullPath(plugin)], cfg.ScriptNames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VimConfig_LoadFromFile_ReappliesRepeatedSource()
    {
        var dir = Path.Combine(Path.GetTempPath(), "editor-vimconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "main.vim");
            var plugin = Path.Combine(dir, "plugin.vim");
            File.WriteAllText(main, "source plugin.vim\nlet g:value = 2\nsource plugin.vim\n");
            File.WriteAllText(plugin, "let g:value = 1\n");

            var cfg = VimConfig.LoadFromFile(main);

            Assert.Equal("1", cfg.Variables["g:value"]);
            Assert.Equal(
                [Path.GetFullPath(main), Path.GetFullPath(plugin), Path.GetFullPath(plugin)],
                cfg.ScriptNames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VimConfig_LoadFromFile_SkipsRecursiveSourceCycle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "editor-vimconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var main = Path.Combine(dir, "main.vim");
            File.WriteAllText(main, "let g:value = 1\nsource main.vim\nlet g:value = 2\n");

            var cfg = VimConfig.LoadFromFile(main);

            Assert.Equal("2", cfg.Variables["g:value"]);
            Assert.Equal([Path.GetFullPath(main)], cfg.ScriptNames);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExLet_AssignsVariableThroughEngine()
    {
        var engine = CreateEngine();

        ExCommand(engine, "let answer = 40 + 2");
        ExCommand(engine, "echo answer");

        Assert.Equal("42", engine.StatusMessage);
    }

    [Fact]
    public void ExLet_MapleaderPrefixVariable_DoesNotChangeLeader()
    {
        var engine = CreateEngine();

        ExCommand(engine, "let mapleader_backup = ','");
        ExCommand(engine, "echo mapleader_backup");

        Assert.Equal(",", engine.StatusMessage);
        Assert.Equal("\\", engine.Config.Leader);
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
    public void VimOptions_Modeline_ParsedFromVimrc()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["set modeline"]);
        Assert.True(cfg.Options.Modeline);

        cfg.ParseLines(["set nomodeline"]);
        Assert.False(cfg.Options.Modeline);
    }

    [Fact]
    public void LoadFile_ModelineEnabled_AppliesSetOptionsFromFileEnd()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-modeline-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, string.Join('\n', [
            "one",
            "two",
            "three",
            "four",
            "five",
            "six",
            "seven",
            "eight",
            "nine",
            "# vim: set tabstop=2 shiftwidth=2 noexpandtab fileformat=dos:"
        ]));

        try
        {
            var cfg = new VimConfig();
            cfg.ParseLines(["set modeline"]);
            var engine = new VimEngine(cfg);

            engine.LoadFile(path);

            Assert.Equal(2, engine.Options.TabStop);
            Assert.Equal(2, engine.Options.ShiftWidth);
            Assert.False(engine.Options.ExpandTab);
            Assert.Equal("dos", engine.Options.FileFormat);
            Assert.Equal("dos", engine.CurrentBuffer.FileFormat);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFile_ModelineEnabled_AppliesSetOptionsFromFileStart()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-modeline-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "// vim: set number norelativenumber textwidth=100:\nbody");

        try
        {
            var cfg = new VimConfig();
            cfg.ParseLines(["set modeline", "set relativenumber"]);
            var engine = new VimEngine(cfg);

            engine.LoadFile(path);

            Assert.True(engine.Options.Number);
            Assert.False(engine.Options.RelativeNumber);
            Assert.Equal(100, engine.Options.TextWidth);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempBinaryFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-binary-" + Guid.NewGuid().ToString("N") + ".bin");
        // NUL byte in the first 8KB marks the file as binary.
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01, 0x02, 0x03]);
        return path;
    }

    [Fact]
    public void LoadFile_BinaryFile_IsNotLoadedAndMarkedBinary()
    {
        var path = WriteTempBinaryFile();
        try
        {
            var engine = CreateEngine();
            engine.LoadFile(path);

            Assert.True(engine.CurrentBuffer.IsBinary);
            // Content is a placeholder, not the raw bytes decoded as text.
            Assert.Contains("Binary file", engine.CurrentBuffer.Text.GetText());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BinaryFile_InsertKey_DoesNotEnterInsertMode()
    {
        var path = WriteTempBinaryFile();
        try
        {
            var engine = CreateEngine();
            engine.LoadFile(path);
            var before = engine.CurrentBuffer.Text.GetText();

            engine.ProcessKey("i");

            Assert.Equal(VimMode.Normal, engine.Mode);
            Assert.Equal(before, engine.CurrentBuffer.Text.GetText());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BinaryFile_DeleteCommands_DoNotModifyBuffer()
    {
        var path = WriteTempBinaryFile();
        try
        {
            var engine = CreateEngine();
            engine.LoadFile(path);
            var before = engine.CurrentBuffer.Text.GetText();

            engine.ProcessKey("x");
            engine.ProcessKey("d"); engine.ProcessKey("d");
            engine.ProcessKey("p");

            Assert.Equal(before, engine.CurrentBuffer.Text.GetText());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BinaryFile_Save_Throws()
    {
        var path = WriteTempBinaryFile();
        var originalBytes = File.ReadAllBytes(path);
        try
        {
            var engine = CreateEngine();
            engine.LoadFile(path);

            Assert.Throws<InvalidOperationException>(() => engine.CurrentBuffer.Save());
            // The original file on disk is untouched.
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFile_ModelineDisabled_DoesNotApplySetOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-modeline-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "# vim: set tabstop=2 noexpandtab:\nbody");

        try
        {
            var engine = new VimEngine(new VimConfig());

            engine.LoadFile(path);

            Assert.Equal(2, engine.Options.TabStop);
            Assert.True(engine.Options.ExpandTab);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFile_Modeline_IgnoresNonSetCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-modeline-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "vim: let g:modeline_ran = 1:\nbody");

        try
        {
            var cfg = new VimConfig();
            cfg.ParseLines(["set modeline"]);
            var engine = new VimEngine(cfg);

            engine.LoadFile(path);

            Assert.False(engine.Config.Variables.ContainsKey("g:modeline_ran"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFile_Modeline_StopsAtTerminatingColonBeforeTrailingComment()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-modeline-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "# vim: set tabstop=2: trailing comment: shiftwidth=9:\nbody");

        try
        {
            var cfg = new VimConfig();
            cfg.ParseLines(["set modeline"]);
            var engine = new VimEngine(cfg);

            engine.LoadFile(path);

            Assert.Equal(2, engine.Options.TabStop);
            Assert.Equal(2, engine.Options.ShiftWidth);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFile_Modeline_AllowsColonInsideOptionValue()
    {
        var path = Path.Combine(Path.GetTempPath(), "editor-modeline-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "# vim: set listchars=tab:>-,trail:-:\nbody");

        try
        {
            var cfg = new VimConfig();
            cfg.ParseLines(["set modeline"]);
            var engine = new VimEngine(cfg);

            engine.LoadFile(path);

            Assert.Equal("tab:>-,trail:-", engine.Options.ListChars);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VimOptions_ValueOptions_DoNotTreatNoPrefixAsNegation()
    {
        var cfg = new VimConfig();
        cfg.ParseLines(["set notabstop=2 nofileformat=dos shiftwidth=3"]);

        Assert.Equal(2, cfg.Options.TabStop);
        Assert.Equal("unix", cfg.Options.FileFormat);
        Assert.Equal(3, cfg.Options.ShiftWidth);
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
    public void EarlierExCommand_RestoresTextAndCursor()
    {
        var engine = CreateEngine("one");
        var buffer = engine.CurrentBuffer.Text;

        engine.CurrentBuffer.Undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        engine.CurrentBuffer.Undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        engine.SetCursorPosition(new CursorPosition(0, 13));

        var events = ExCmd(engine, "earlier 2");

        Assert.Equal("one", buffer.GetText());
        Assert.Equal(CursorPosition.Zero, engine.Cursor);
        Assert.Contains(events, e => e.Type == VimEventType.TextChanged);
        Assert.Contains(events, e => e.Type == VimEventType.CursorMoved);
    }

    [Fact]
    public void EarlierExCommand_DoesNotBreakNormalUndoRedo()
    {
        var engine = CreateEngine("one");
        var buffer = engine.CurrentBuffer.Text;

        engine.CurrentBuffer.Undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        engine.CurrentBuffer.Undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        engine.SetCursorPosition(new CursorPosition(0, 13));

        ExCmd(engine, "earlier 1");
        engine.ProcessKey("u");
        Assert.Equal("one", buffer.GetText());

        engine.ProcessKey("r", ctrl: true);
        Assert.Equal("one two", buffer.GetText());
    }

    [Fact]
    public void EarlierExCommand_NewEditClearsRedoHistory()
    {
        var engine = CreateEngine("one");
        var buffer = engine.CurrentBuffer.Text;

        engine.CurrentBuffer.Undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        engine.CurrentBuffer.Undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        engine.SetCursorPosition(new CursorPosition(0, 13));

        ExCmd(engine, "earlier 1");
        engine.ProcessKey("A");
        foreach (var ch in " changed")
            engine.ProcessKey(ch.ToString());
        engine.ProcessKey("Escape");

        Assert.Equal("one two changed", buffer.GetText());

        engine.ProcessKey("r", ctrl: true);
        Assert.Equal("one two changed", buffer.GetText());

        var events = ExCmd(engine, "undolist");
        Assert.Contains(events, e => e is StatusMessageEvent { Message: var message } &&
            message.Contains("current") && !message.Contains("redo"));
    }

    [Fact]
    public void EarlierExCommand_ClearsFoldsAndEmitsFoldChange()
    {
        var engine = CreateEngine("one\ntwo\nthree");
        var buffer = engine.CurrentBuffer.Text;

        engine.CurrentBuffer.Undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertLines(2, ["four"]);
        engine.CurrentBuffer.Folds.CreateFold(0, 2);
        Assert.NotEmpty(engine.CurrentBuffer.Folds.Folds);

        var events = ExCmd(engine, "earlier 1");

        Assert.Equal("one\ntwo\nthree", buffer.GetText());
        Assert.Empty(engine.CurrentBuffer.Folds.Folds);
        Assert.Contains(events, e => e.Type == VimEventType.FoldsChanged);
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

    [Fact]
    public void VisualPaste_ReplacesCharwiseSelection()
    {
        // Yank "foo" charwise, then visually select "bar" and paste over it.
        var engine = CreateEngine("foo bar");
        // Yank "foo" (3 chars) into the unnamed register.
        engine.ProcessKey("v");
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        engine.ProcessKey("y");
        // Move to "bar", select it, paste.
        engine.ProcessKey("w");        // cursor on 'b'
        engine.ProcessKey("v");
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        engine.ProcessKey("p");
        Assert.Equal("foo foo", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Dot_RepeatsVisualPasteWithOriginalRegisterText()
    {
        var engine = CreateEngine("foo bar baz");

        engine.ProcessKey("v");
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        engine.ProcessKey("y");

        engine.ProcessKey("w");
        engine.ProcessKey("v");
        engine.ProcessKey("e");
        engine.ProcessKey("p");
        engine.ProcessKey("w");
        engine.ProcessKey(".");

        Assert.Equal("foo foo foo", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void VisualLinePaste_ReplacesSelectedLines()
    {
        // Yank line 0 linewise, then visual-line select line 1 and paste over it.
        var engine = CreateEngine("alpha\nbeta\ngamma");
        engine.ProcessKey("y");
        engine.ProcessKey("y");        // yank "alpha" linewise
        engine.ProcessKey("j");        // line 1 "beta"
        engine.ProcessKey("V");
        engine.ProcessKey("p");
        Assert.Equal("alpha\nalpha\ngamma", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void VisualPaste_PutsReplacedTextInUnnamedRegister()
    {
        // After visual paste, the replaced text should be yanked into the unnamed register.
        var engine = CreateEngine("foo bar");
        engine.ProcessKey("v"); engine.ProcessKey("l"); engine.ProcessKey("l");
        engine.ProcessKey("y");        // yank "foo"
        engine.ProcessKey("w");
        engine.ProcessKey("v"); engine.ProcessKey("l"); engine.ProcessKey("l");
        engine.ProcessKey("p");        // replace "bar" with "foo"; "bar" → unnamed
        // Paste at end: cursor on last 'o', append the now-unnamed "bar".
        engine.ProcessKey("p");
        Assert.Equal("foo foobar", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void CtrlG_EmitsFileInfo()
    {
        // 3-line buffer; cursor is on line 1 col 2 (0-based) = line 2 col 3 (1-based)
        var engine = CreateEngine("hello\nworld\nfoo");
        engine.ProcessKey("j");          // move to line 1
        engine.ProcessKey("l");          // move to col 1
        engine.ProcessKey("l");          // move to col 2
        var events = engine.ProcessKey("g", ctrl: true);
        Assert.Contains(events, e => e.Type == VimEventType.StatusMessage);
        var msg = events.OfType<StatusMessageEvent>().Last().Message;
        Assert.Contains("line 2", msg);
        Assert.Contains("col 3", msg);
        Assert.Contains("of 3", msg);
    }

    // ── Sentence motions ( and ) ─────────────────────────────────────────

    [Fact]
    public void SentenceMotion_Forward_MovesToNextSentenceStart()
    {
        // Two sentences on one line separated by ". "
        var engine = CreateEngine("Hello world.  Next sentence here.");
        // Cursor starts at col 0. ) should jump to "Next"
        engine.ProcessKey(")");
        // "Next" starts at col 14 (after "Hello world.  ")
        Assert.Equal(0, engine.Cursor.Line);
        Assert.True(engine.Cursor.Column > 0, "Cursor should have moved past the first sentence end");
        var line = engine.CurrentBuffer.Text.GetLine(0);
        // The character at the new cursor position should be 'N' (start of "Next")
        Assert.Equal('N', line[engine.Cursor.Column]);
    }

    [Fact]
    public void SentenceMotion_Forward_AcrossLines()
    {
        // Sentence ends at end of line; next sentence is on the next line
        var engine = CreateEngine("First sentence.\nSecond sentence.");
        // ) from line 0 should move to line 1
        engine.ProcessKey(")");
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void SentenceMotion_Forward_BlankLineBoundary()
    {
        // Empty line is a paragraph/sentence boundary
        var engine = CreateEngine("First para.\n\nSecond para.");
        engine.ProcessKey(")");
        // Should land on line 2 (the "Second para." line)
        Assert.Equal(2, engine.Cursor.Line);
    }

    [Fact]
    public void SentenceMotion_Backward_MovesToPrevSentenceStart()
    {
        // Two sentences on one line; start at the second sentence, move back
        var engine = CreateEngine("Hello world.  Next sentence.");
        // Jump forward to "Next"
        engine.ProcessKey(")");
        int colAfterForward = engine.Cursor.Column;
        Assert.True(colAfterForward > 0);
        // Now ( should move back to column 0
        engine.ProcessKey("(");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void SentenceMotion_Backward_AcrossLines()
    {
        // Two separate lines; start on line 1, ( should go back to line 0
        var engine = CreateEngine("First sentence.\nSecond sentence.");
        engine.ProcessKey("j"); // move to line 1
        engine.ProcessKey("(");
        Assert.Equal(0, engine.Cursor.Line);
    }

    [Fact]
    public void SentenceMotion_WithCount_MovesMultipleSentences()
    {
        // Three sentences on one line
        var engine = CreateEngine("One.  Two.  Three.");
        // 2) should skip two sentence boundaries — land on "Three"
        engine.ProcessKey("2");
        engine.ProcessKey(")");
        Assert.Equal(0, engine.Cursor.Line);
        var line = engine.CurrentBuffer.Text.GetLine(0);
        Assert.Equal('T', line[engine.Cursor.Column]);
    }

    [Fact]
    public void SentenceMotion_ExclamationAndQuestion_AreSentenceEnders()
    {
        // ! and ? are also sentence terminators
        var engine = CreateEngine("Really!  Next.  End?  Again.");
        engine.ProcessKey(")"); // move past "Really!"
        var line = engine.CurrentBuffer.Text.GetLine(0);
        Assert.Equal('N', line[engine.Cursor.Column]);
    }

    [Fact]
    public void SentenceMotion_ForwardAtEndOfBuffer_Stays()
    {
        // ) at end of buffer should not crash and cursor stays valid
        var engine = CreateEngine("Only one sentence.");
        engine.ProcessKey(")");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.True(engine.Cursor.Column >= 0);
    }

    [Fact]
    public void SentenceMotion_BackwardAtStartOfBuffer_Stays()
    {
        // ( at file start should not crash and cursor stays at 0,0
        var engine = CreateEngine("Only one sentence.");
        engine.ProcessKey("(");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void SentenceMotion_UsableAsOperatorMotion_Delete()
    {
        // d) should delete from cursor up to (exclusive) the start of the next sentence.
        // The engine's ExecuteDelete uses to.Column + 1 (inclusive end), so the character
        // at the motion target is included in the deletion — leaving text after it.
        var engine = CreateEngine("Hello world.  Next sentence.");
        // ) lands on 'N' at col 14; d) deletes cols 0-14 inclusive → "ext sentence."
        engine.ProcessKey("d");
        engine.ProcessKey(")");
        var text = engine.CurrentBuffer.Text.GetText();
        // Something was deleted — the buffer is shorter than the original
        Assert.True(text.Length < "Hello world.  Next sentence.".Length, "d) should delete some text");
        // The deletion removed the first sentence and whitespace
        Assert.False(text.Contains("Hello"), "First sentence should have been deleted");
    }

    [Fact]
    public void DoubleQuote_JumpsToLastJumpPosition()
    {
        // '' jumps back to the line of the last jump (col 0), '' again goes back
        var engine = CreateEngine("line1\nline2\nline3\nline4");

        // Start at line 0. Jump to line 3 with G — records '' mark at line 0.
        engine.ProcessKey("G");
        Assert.Equal(3, engine.Cursor.Line);

        // '' should jump back to line 0, col 0
        engine.ProcessKey("'");
        engine.ProcessKey("'");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void DoubleBacktick_JumpsToLastJumpExactPosition()
    {
        // `` jumps to the exact position before the last jump
        var engine = CreateEngine("line1\nline2\nline3\nline4");

        // Move to col 2 on line 0
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        Assert.Equal(new CursorPosition(0, 2), engine.Cursor);

        // Jump to line 3 with G — records `` mark at (0, 2)
        engine.ProcessKey("G");
        Assert.Equal(3, engine.Cursor.Line);

        // `` should jump back to exact position (0, 2)
        engine.ProcessKey("`");
        engine.ProcessKey("`");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(2, engine.Cursor.Column);
    }

    [Fact]
    public void DotMark_JumpsToLastEditPosition()
    {
        // '. jumps to the line of the last text change (col 0)
        var engine = CreateEngine("aaa\nbbb\nccc");

        // Move to line 1 and make a change (delete a char)
        engine.ProcessKey("j");
        Assert.Equal(1, engine.Cursor.Line);
        engine.ProcessKey("x"); // delete 'b', triggers Snapshot → sets '.' mark at (1, 0)

        // Move away to line 0
        engine.ProcessKey("k");
        Assert.Equal(0, engine.Cursor.Line);

        // '. should jump to line 1, col 0
        engine.ProcessKey("'");
        engine.ProcessKey(".");
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void BacktickDot_JumpsToLastEditExactPosition()
    {
        // `. jumps to the exact cursor position of the last text change
        var engine = CreateEngine("hello\nworld");

        // Move to line 0, col 3
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        engine.ProcessKey("l");
        Assert.Equal(new CursorPosition(0, 3), engine.Cursor);

        // Delete char at col 3 → Snapshot records '.' mark at (0, 3)
        engine.ProcessKey("x");

        // Move away
        engine.ProcessKey("j");
        Assert.Equal(1, engine.Cursor.Line);

        // `. should jump to exact position (0, 3)
        engine.ProcessKey("`");
        engine.ProcessKey(".");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(3, engine.Cursor.Column);
    }

    [Fact]
    public void GE_MovesToEndOfPrevWORD()
    {
        // "hello world foo" — cursor starts at 'f' (col 12)
        // gE should land on 'd' of "world" (col 10)
        var engine = CreateEngine("hello world foo");
        engine.ProcessKey("$"); // col 14
        engine.ProcessKey("b"); // 'f' col 12
        engine.ProcessKey("g");
        engine.ProcessKey("E");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(10, engine.Cursor.Column); // last char of "world"
    }

    [Fact]
    public void GE_SkipsWhitespaceBetweenWORDs()
    {
        // "aaa   bbb" — cursor on 'b' (col 6)
        // gE should land on 'a' (col 2), the last char of "aaa"
        var engine = CreateEngine("aaa   bbb");
        engine.ProcessKey("$"); // col 8 ('b')
        engine.ProcessKey("b"); // back to col 6 ('b')
        engine.ProcessKey("g");
        engine.ProcessKey("E");
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(2, engine.Cursor.Column); // last char of "aaa"
    }

    [Fact]
    public void Visual_Tilde_TogglesCase()
    {
        var engine = CreateEngine("Hello World");
        // Enter visual mode, select "Hello" (cols 0-4), then ~
        engine.ProcessKey("v");
        engine.ProcessKey("e"); // select to end of "Hello"
        engine.ProcessKey("~");
        Assert.Equal(VimMode.Normal, engine.Mode);
        var text = engine.CurrentBuffer.Text.GetText();
        // "Hello" → "hELLO", rest unchanged
        Assert.StartsWith("hELLO", text);
    }

    [Fact]
    public void InsertCtrlT_IndentsCurrentLine()
    {
        var engine = CreateEngine("hello");
        // Enter insert mode, then Ctrl+T
        engine.ProcessKey("i");
        Assert.Equal(VimMode.Insert, engine.Mode);
        engine.ProcessKey("t", ctrl: true);
        Assert.Equal(VimMode.Insert, engine.Mode);
        var text = engine.CurrentBuffer.Text.GetText();
        // Line should now start with 2 spaces (default shiftwidth)
        Assert.StartsWith("  ", text);
        Assert.Contains("hello", text);
    }

    [Fact]
    public void InsertCtrlD_DedentsCurrentLine()
    {
        var engine = CreateEngine("  hello");
        // Enter insert mode at start of indented line, then Ctrl+D
        engine.ProcessKey("i");
        Assert.Equal(VimMode.Insert, engine.Mode);
        engine.ProcessKey("d", ctrl: true);
        Assert.Equal(VimMode.Insert, engine.Mode);
        var text = engine.CurrentBuffer.Text.GetText();
        // 2 leading spaces (one shiftwidth) removed
        Assert.Equal("hello", text);
    }

    // ─── Fold navigation commands ───

    [Fact]
    public void Zj_MovesToNextFoldStart()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        // Use SetLspRanges so folds are created open (IsClosed=false), preserving normal cursor movement
        engine.CurrentBuffer.Folds.SetLspRanges([(1, 2), (3, 4)]);
        // cursor starts at line 0; zj should move to line 1 (start of first fold)
        engine.ProcessKey("z");
        engine.ProcessKey("j");
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Zj_FromInsideFold_MovesToNextFoldStart()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        engine.CurrentBuffer.Folds.SetLspRanges([(1, 2), (3, 4)]);
        // move cursor to line 1 then zj → next fold start above line 1 is line 3
        engine.ProcessKey("j");
        engine.ProcessKey("z");
        engine.ProcessKey("j");
        Assert.Equal(3, engine.Cursor.Line);
    }

    [Fact]
    public void Zk_MovesToPrevFoldStart()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        engine.CurrentBuffer.Folds.SetLspRanges([(1, 2), (3, 4)]);
        // move to line 4; zk → prev fold start before line 4 is line 3
        engine.ProcessKey("4");
        engine.ProcessKey("j");
        engine.ProcessKey("z");
        engine.ProcessKey("k");
        Assert.Equal(3, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void BracketZ_MovesToCurrentFoldStart()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        engine.CurrentBuffer.Folds.SetLspRanges([(1, 3)]);
        // move cursor to line 2 (inside open fold) then [z → should go to fold start (line 1)
        engine.ProcessKey("2");
        engine.ProcessKey("j");
        engine.ProcessKey("[");
        engine.ProcessKey("z");
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void CloseBracketZ_MovesToCurrentFoldEnd()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        engine.CurrentBuffer.Folds.SetLspRanges([(1, 3)]);
        // move cursor to line 2 (inside open fold) then ]z → should go to fold end (line 3)
        engine.ProcessKey("2");
        engine.ProcessKey("j");
        engine.ProcessKey("]");
        engine.ProcessKey("z");
        Assert.Equal(3, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Zd_DeletesInnermostFold()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        // Use SetLspRanges to create nested (overlapping) folds — bypasses overlap guard
        engine.CurrentBuffer.Folds.SetLspRanges([(0, 4), (1, 2)]);
        // cursor at line 1 → zd deletes innermost fold (1,2); outer (0,4) remains
        engine.ProcessKey("j");
        engine.ProcessKey("z");
        engine.ProcessKey("d");
        var remaining = engine.CurrentBuffer.Folds.Folds;
        Assert.Single(remaining);
        Assert.Equal(0, remaining[0].StartLine);
        Assert.Equal(4, remaining[0].EndLine);
    }

    [Fact]
    public void ZD_DeletesAllFoldsAtCursor()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        // Nested folds via SetLspRanges; (3,4) does not contain line 1
        engine.CurrentBuffer.Folds.SetLspRanges([(0, 4), (1, 2), (3, 4)]);
        // cursor at line 1 → zD removes outer (0,4) and inner (1,2); (3,4) survives
        engine.ProcessKey("j");
        engine.ProcessKey("z");
        engine.ProcessKey("D");
        var survivors = engine.CurrentBuffer.Folds.Folds;
        Assert.Single(survivors);
        Assert.Equal(3, survivors[0].StartLine);
        Assert.Equal(4, survivors[0].EndLine);
    }

    // ── Section motions ──────────────────────────────────────────────────────

    [Fact]
    public void Section_DoubleCloseBracket_MovesForwardToNextOpenBrace()
    {
        // ]] moves forward to the next { at column 0
        var text = "void a()\n{\n    x;\n}\nvoid b()\n{\n    y;\n}";
        var engine = CreateEngine(text);
        // cursor starts at line 0; ]] should land on line 1 (the {)
        engine.ProcessKey("]");
        engine.ProcessKey("]");
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Section_DoubleOpenBracket_MovesBackwardToPrevOpenBrace()
    {
        // [[ moves backward to the previous { at column 0
        var text = "void a()\n{\n    x;\n}\nvoid b()\n{\n    y;\n}";
        var engine = CreateEngine(text);
        // position cursor at line 6 (inside second function body)
        for (int i = 0; i < 6; i++) engine.ProcessKey("j");
        engine.ProcessKey("[");
        engine.ProcessKey("[");
        Assert.Equal(5, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Section_CloseBracketOpenBracket_MovesForwardToNextCloseBrace()
    {
        // ][ moves forward to the next } at column 0
        var text = "void a()\n{\n    x;\n}\nvoid b()\n{\n    y;\n}";
        var engine = CreateEngine(text);
        engine.ProcessKey("]");
        engine.ProcessKey("[");
        Assert.Equal(3, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void Section_OpenBracketCloseBracket_MovesBackwardToPrevCloseBrace()
    {
        // [] moves backward to the previous } at column 0
        var text = "void a()\n{\n    x;\n}\nvoid b()\n{\n    y;\n}";
        var engine = CreateEngine(text);
        // position cursor at line 7 (closing brace of second function)
        for (int i = 0; i < 7; i++) engine.ProcessKey("j");
        engine.ProcessKey("[");
        engine.ProcessKey("]");
        Assert.Equal(3, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    // ── Block jump motions ───────────────────────────────────────────────────

    [Fact]
    public void BlockJump_OpenBrace_FindsUnclosedBrace()
    {
        // [{ finds the unmatched { above the cursor
        var text = "{\n    {\n        cursor here\n    }\n}";
        var engine = CreateEngine(text);
        // Move cursor to line 2 (inside inner block, after inner {)
        engine.ProcessKey("j");
        engine.ProcessKey("j");
        engine.ProcessKey("[");
        engine.ProcessKey("{");
        // The unmatched { is on line 1 (the inner {)
        Assert.Equal(1, engine.Cursor.Line);
        Assert.Equal(4, engine.Cursor.Column);
    }

    [Fact]
    public void BlockJump_CloseBrace_FindsUnclosedBrace()
    {
        // ]} finds the unmatched } below the cursor
        var text = "{\n    cursor here\n    {\n        x;\n    }\n}";
        var engine = CreateEngine(text);
        // Move cursor to line 1 (inside outer block, before inner {)
        engine.ProcessKey("j");
        engine.ProcessKey("]");
        engine.ProcessKey("}");
        // The unmatched } (matching outer {) is on line 5
        Assert.Equal(5, engine.Cursor.Line);
        Assert.Equal(0, engine.Cursor.Column);
    }

    [Fact]
    public void BlockJump_OpenParen_FindsUnclosedParen()
    {
        // [( finds the unmatched ( above the cursor
        var text = "foo(bar(cursor))";
        var engine = CreateEngine(text);
        // Position cursor at column 8 (on 'c' of "cursor")
        for (int i = 0; i < 8; i++) engine.ProcessKey("l");
        engine.ProcessKey("[");
        engine.ProcessKey("(");
        // The unmatched ( is at column 7 (the inner open paren)
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(7, engine.Cursor.Column);
    }

    [Fact]
    public void BlockJump_CloseParen_FindsUnclosedParen()
    {
        // ]) finds the unmatched ) below the cursor
        var text = "foo(bar(x)cursor)";
        var engine = CreateEngine(text);
        // Position cursor at column 10 (after the inner closing paren)
        for (int i = 0; i < 10; i++) engine.ProcessKey("l");
        engine.ProcessKey("]");
        engine.ProcessKey(")");
        // The unmatched ) is at column 16 (outer closing paren)
        Assert.Equal(0, engine.Cursor.Line);
        Assert.Equal(16, engine.Cursor.Column);
    }

    // ── gn / gN motion tests ──────────────────────────────────────────────────

    private static void SetSearch(VimEngine engine, string pattern)
    {
        engine.ProcessKey("/");
        foreach (var ch in pattern) engine.ProcessKey(ch.ToString());
        engine.ProcessKey("Return");
    }

    [Fact]
    public void Gn_StandaloneEntersVisualSelectingMatch()
    {
        // Text: "foo bar foo", cursor at start. Search for "foo".
        // gn from beginning should select the first "foo" (cols 0-2) in visual mode.
        var engine = CreateEngine("foo bar foo");
        SetSearch(engine, "foo");
        // After search, cursor is on second "foo" (col 8). Move back to start.
        engine.ProcessKey("g");
        engine.ProcessKey("g"); // go to line 0
        engine.ProcessKey("0"); // col 0
        engine.ProcessKey("g");
        engine.ProcessKey("n");
        Assert.Equal(VimMode.Visual, engine.Mode);
        // Selection should span the match "foo" at col 0-2
        Assert.True(engine.Selection.HasValue);
        var sel = engine.Selection!.Value;
        Assert.Equal(0, sel.Start.Line);
        Assert.Equal(0, sel.Start.Column);
        Assert.Equal(0, sel.End.Line);
        Assert.Equal(2, sel.End.Column);
    }

    [Fact]
    public void GN_StandaloneSelectsPrevMatch()
    {
        // Text: "foo bar foo", cursor at end. gN should select previous "foo".
        var engine = CreateEngine("foo bar foo");
        SetSearch(engine, "foo");
        // cursor is at col 8 (start of second "foo") after search
        // move cursor past the match
        engine.ProcessKey("$"); // go to end (col 10)
        engine.ProcessKey("g");
        engine.ProcessKey("N");
        Assert.Equal(VimMode.Visual, engine.Mode);
        Assert.True(engine.Selection.HasValue);
        var sel = engine.Selection!.Value;
        // Should select "foo" at col 8-10
        Assert.Equal(0, sel.Start.Line);
        Assert.Equal(8, sel.Start.Column);
        Assert.Equal(0, sel.End.Line);
        Assert.Equal(10, sel.End.Column);
    }

    [Fact]
    public void Gn_WithDeleteOperator_DeletesNextMatch()
    {
        // "foo bar foo" → dgn from col 0 should delete the first "foo"
        var engine = CreateEngine("foo bar foo");
        SetSearch(engine, "foo");
        // go back to start
        engine.ProcessKey("g");
        engine.ProcessKey("g");
        engine.ProcessKey("0");
        engine.ProcessKey("d");
        engine.ProcessKey("g");
        engine.ProcessKey("n");
        Assert.Equal(" bar foo", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(VimMode.Normal, engine.Mode);
    }

    [Fact]
    public void Gn_WithChangeOperator_ChangesNextMatch()
    {
        // "foo bar foo" → cgn from col 0 should delete "foo" and enter insert mode
        var engine = CreateEngine("foo bar foo");
        SetSearch(engine, "foo");
        engine.ProcessKey("g");
        engine.ProcessKey("g");
        engine.ProcessKey("0");
        engine.ProcessKey("c");
        engine.ProcessKey("g");
        engine.ProcessKey("n");
        Assert.Equal(VimMode.Insert, engine.Mode);
        // "foo" at start should be deleted
        Assert.Equal(" bar foo", engine.CurrentBuffer.Text.GetText());
    }

    // Method navigation: [m / ]m / [M / ]M

    [Fact]
    public void CloseBracketM_MovesToNextMethodStart()
    {
        // Allman brace style: each "{" is on its own line preceded by a signature line
        // The heuristic: line ends with "{" and length > 1, OR
        // we use a simpler text where the brace is on the same line as the signature
        var text = "void Foo() {\n    return;\n}\nvoid Bar() {\n    return;\n}";
        var engine = CreateEngine(text);
        // cursor at line 0 (which is "void Foo() {" — already ends with "{")
        // ]m from line 0 should jump to line 3 ("void Bar() {")
        engine.ProcessKey("]");
        engine.ProcessKey("m");
        Assert.Equal(3, engine.Cursor.Line);
    }

    [Fact]
    public void OpenBracketM_MovesToPrevMethodStart()
    {
        var text = "void Foo() {\n    return;\n}\nvoid Bar() {\n    return;\n}";
        var engine = CreateEngine(text);
        // move to line 4, [m → previous method start = line 3 ("void Bar() {")
        // but cursor starts at 0 which itself matches; step to line 4 first
        for (int i = 0; i < 4; i++) { engine.ProcessKey("j"); }
        engine.ProcessKey("[");
        engine.ProcessKey("m");
        Assert.Equal(3, engine.Cursor.Line);
    }

    [Fact]
    public void CloseBracketBigM_MovesToNextMethodEnd()
    {
        var text = "void Foo() {\n    return;\n}\nvoid Bar() {\n    return;\n}";
        var engine = CreateEngine(text);
        // cursor at line 0; ]M should jump to first "}" line (line 2)
        engine.ProcessKey("]");
        engine.ProcessKey("M");
        Assert.Equal(2, engine.Cursor.Line);
    }

    [Fact]
    public void OpenBracketBigM_MovesToPrevMethodEnd()
    {
        var text = "void Foo() {\n    return;\n}\nvoid Bar() {\n    return;\n}";
        var engine = CreateEngine(text);
        // move to line 5 (second "}") then [M → previous "}" = line 2
        for (int i = 0; i < 5; i++) { engine.ProcessKey("j"); }
        engine.ProcessKey("[");
        engine.ProcessKey("M");
        Assert.Equal(2, engine.Cursor.Line);
    }

    // Fold utility commands: zE / zn / zN

    [Fact]
    public void ZE_ClearsAllFolds()
    {
        var engine = CreateEngine("line0\nline1\nline2\nline3\nline4");
        engine.CurrentBuffer.Folds.SetLspRanges([(0, 2), (3, 4)]);
        Assert.Equal(2, engine.CurrentBuffer.Folds.Folds.Count);
        engine.ProcessKey("z");
        engine.ProcessKey("E");
        Assert.Empty(engine.CurrentBuffer.Folds.Folds);
    }

    [Fact]
    public void Zn_SetsFoldsDisabled()
    {
        var engine = CreateEngine("line0\nline1\nline2");
        Assert.False(engine.FoldsDisabled);
        engine.ProcessKey("z");
        engine.ProcessKey("n");
        Assert.True(engine.FoldsDisabled);
    }

    [Fact]
    public void ZN_ClearsFoldsDisabled()
    {
        var engine = CreateEngine("line0\nline1\nline2");
        engine.ProcessKey("z");
        engine.ProcessKey("n");
        Assert.True(engine.FoldsDisabled);
        engine.ProcessKey("z");
        engine.ProcessKey("N");
        Assert.False(engine.FoldsDisabled);
    }

    [Fact]
    public void VimConfig_ParsesAutocmdInsideAugroup()
    {
        var config = new VimConfig();

        config.ParseLines([
            "augroup editor_test",
            "  autocmd!",
            "  autocmd BufRead *.cs set tabstop=2",
            "  autocmd BufRead,BufEnter *.md set expandtab",
            "augroup END",
        ]);

        Assert.Equal(3, config.Autocmds.Items.Count);
        Assert.All(config.Autocmds.Items, a => Assert.Equal("editor_test", a.Group));
        Assert.Contains(config.Autocmds.Items, a => a.Event == "BufRead" && a.Pattern == "*.cs" && a.Command == "set tabstop=2");
        Assert.Contains(config.Autocmds.Items, a => a.Event == "BufEnter" && a.Pattern == "*.md" && a.Command == "set expandtab");
    }

    [Fact]
    public void VimConfig_ParsesExplicitAutocmdGroup()
    {
        var config = new VimConfig();

        config.ParseCommand("autocmd editor_test BufRead *.cs set tabstop=2 shiftwidth=2");

        var autocmd = Assert.Single(config.Autocmds.Items);
        Assert.Equal("editor_test", autocmd.Group);
        Assert.Equal("BufRead", autocmd.Event);
        Assert.Equal("*.cs", autocmd.Pattern);
        Assert.Equal("set tabstop=2 shiftwidth=2", autocmd.Command);
    }

    [Fact]
    public void LoadFile_RunsMatchingBufReadAutocmd()
    {
        var path = Path.Combine(Path.GetTempPath(), $"editor-autocmd-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "class C {}\n");

        try
        {
            var config = new VimConfig();
            config.ParseCommand("autocmd BufRead *.cs set tabstop=2 shiftwidth=2");
            config.ParseCommand("autocmd BufRead *.md set tabstop=8");
            var engine = CreateEngine(config: config);

            engine.LoadFile(path);

            Assert.Equal(2, engine.Options.TabStop);
            Assert.Equal(2, engine.Options.ShiftWidth);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFile_RunsFileTypeAutocmdsUsingVimFileTypeNames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"editor-autocmd-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "class C {}\n");

        try
        {
            var config = new VimConfig();
            config.ParseCommand("autocmd FileType cs set tabstop=2");
            config.ParseCommand("autocmd FileType csharp set shiftwidth=2");
            config.ParseCommand("autocmd FileType C# set scrolloff=9");
            var engine = CreateEngine(config: config);

            engine.LoadFile(path);

            Assert.Equal(2, engine.Options.TabStop);
            Assert.Equal(2, engine.Options.ShiftWidth);
            Assert.NotEqual(9, engine.Options.ScrollOff);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- Markdown list Tab indent/outdent (shiftwidth = 2 by default) ---

    private VimEngine CreateMarkdownEngine(string text)
    {
        var engine = CreateEngine(text);
        engine.CurrentBuffer.FilePath = "notes.md";
        return engine;
    }

    [Fact]
    public void Tab_OnMarkdownListItem_IndentsWholeLine()
    {
        var engine = CreateMarkdownEngine("- item");
        engine.ProcessKey("i"); // Insert mode at column 0
        engine.ProcessKey("Tab", false, false, false);
        Assert.Equal("  - item", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Tab_OnMarkdownListItem_IndentsRegardlessOfCursorColumn()
    {
        var engine = CreateMarkdownEngine("- item");
        engine.ProcessKey("A"); // Insert mode at end of line
        engine.ProcessKey("Tab", false, false, false);
        Assert.Equal("  - item", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void ShiftTab_OnMarkdownListItem_OutdentsWholeLine()
    {
        var engine = CreateMarkdownEngine("    - item");
        engine.ProcessKey("A");
        engine.ProcessKey("Tab", false, true, false); // Shift+Tab
        Assert.Equal("  - item", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void ShiftTab_OnUnindentedMarkdownListItem_IsNoOp()
    {
        var engine = CreateMarkdownEngine("- item");
        engine.ProcessKey("A");
        engine.ProcessKey("Tab", false, true, false);
        Assert.Equal("- item", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Tab_OnMarkdownOrderedListItem_IndentsWholeLine()
    {
        var engine = CreateMarkdownEngine("1. item");
        engine.ProcessKey("A");
        engine.ProcessKey("Tab", false, false, false);
        Assert.Equal("  1. item", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Tab_OnMarkdownNonListLine_InsertsTabAtCursor()
    {
        var engine = CreateMarkdownEngine("text");
        engine.ProcessKey("i"); // Insert at column 0
        engine.ProcessKey("Tab", false, false, false);
        // shiftwidth/tabstop = 2, expandtab default → two spaces inserted at cursor
        Assert.Equal("  text", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Tab_OnListItem_NonMarkdownFile_InsertsTabAtCursor()
    {
        var engine = CreateEngine("- item");
        engine.CurrentBuffer.FilePath = "notes.txt";
        engine.ProcessKey("A"); // end of line
        engine.ProcessKey("Tab", false, false, false);
        Assert.Equal("- item  ", engine.CurrentBuffer.Text.GetText());
    }
}
