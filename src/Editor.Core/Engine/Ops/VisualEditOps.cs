using Editor.Core.Buffer;
using Editor.Core.Models;
using Editor.Core.Registers;

namespace Editor.Core.Engine.Ops;

/// <summary>
/// The Visual-mode operators that act on the current selection (delete/paste/yank,
/// indent, comment, case conversion, character replace) across characterwise,
/// linewise and blockwise selections. Mirrors <see cref="ClipboardEditOps"/> /
/// <see cref="TextTransformOps"/>: buffers/registers are injected; the handful of
/// <see cref="VimEngine"/> re-entry points (cursor commit, snapshot, emit, exit
/// Visual mode) are taken as callbacks, and Visual-mode state is read through getters.
/// </summary>
public sealed class VisualEditOps(
    BufferManager bufferManager,
    RegisterManager registerManager,
    ClipboardEditOps clipboardOps,
    TextTransformOps textTransform,
    RepeatTracker repeatTracker,
    Func<Selection?> getSelection,
    Func<VimMode> getMode,
    Func<bool> getBlockToLineEnd,
    Func<int> getBlockLineEndStartColumn,
    Action<CursorPosition> setCursor,
    Action snapshot,
    Action<List<VimEvent>> emitText,
    Action<List<VimEvent>> exitVisualMode,
    Action<CursorPosition, List<VimEvent>> moveCursor)
{
    private VimBuffer CurrentBuffer => bufferManager.Current;
    private TextBuffer Buf => bufferManager.Current.Text;

    public void ExecuteVisualDelete(char register, List<VimEvent> events)
    {
        if (getSelection() is not { } sel) { exitVisualMode(events); return; }
        if (getMode() == VimMode.VisualBlock)
        {
            var (startLine, _, _, _) = BlockRangeCalculator.GetBounds(sel);
            var leftColumn = BlockRangeCalculator.GetLeftColumn(sel, getBlockToLineEnd(), getBlockLineEndStartColumn());
            snapshot();
            if (register != '_')
                clipboardOps.YankBlock(register, sel, getBlockToLineEnd(), getBlockLineEndStartColumn(), isDelete: true);
            clipboardOps.DeleteBlock(sel, getBlockToLineEnd(), getBlockLineEndStartColumn());
            setCursor(Buf.ClampCursor(new CursorPosition(startLine, leftColumn)));
            emitText(events);
            exitVisualMode(events);
            return;
        }

        snapshot();
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        if (getMode() == VimMode.VisualLine)
        {
            if (register != '_')
                clipboardOps.YankLines(start.Line, end.Line, register, events, isDelete: true);
            CurrentBuffer.Folds.OnLinesDeleted(start.Line, end.Line);
            Buf.DeleteLines(start.Line, end.Line);
            setCursor(Buf.ClampCursor(new CursorPosition(start.Line, 0)));
            emitText(events);
        }
        else
        {
            setCursor(clipboardOps.ExecuteDelete(start, end, false, events, register));
        }
        exitVisualMode(events);
    }

    // Visual-mode paste: replaces the selection with the register contents.
    // The deleted selection is yanked into the unnamed register (Vim behavior),
    // so the register is read up front before any delete overwrites it.
    public void ExecuteVisualPaste(char register, List<VimEvent> events)
    {
        if (getSelection() is not { } sel) { exitVisualMode(events); return; }

        var reg = registerManager.Get(register);
        if (reg.IsEmpty) { ExecuteVisualDelete('"', events); return; }
        var regType = reg.Type;
        var regText = reg.Text;
        var regLines = reg.GetLines();
        if (!repeatTracker.IsDotReplaying)
            repeatTracker.PendingVisualRepeatRegister = (register, new Register(reg.Text, reg.Type));

        var mode = getMode();
        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        snapshot();
        var buf = Buf;

        if (mode == VimMode.VisualBlock)
        {
            var (blockStartLine, _, _, _) = BlockRangeCalculator.GetBounds(sel);
            var leftColumn = BlockRangeCalculator.GetLeftColumn(sel, getBlockToLineEnd(), getBlockLineEndStartColumn());
            clipboardOps.YankBlock('"', sel, getBlockToLineEnd(), getBlockLineEndStartColumn(), isDelete: true);
            clipboardOps.DeleteBlock(sel, getBlockToLineEnd(), getBlockLineEndStartColumn());
            var cursor = buf.ClampCursor(new CursorPosition(blockStartLine, leftColumn));
            if (regType == RegisterType.Line)
                cursor = clipboardOps.InsertLinewisePaste(cursor, regLines, after: false);
            else
            {
                var startPos = cursor;
                var endPos = clipboardOps.InsertCharacterwiseText(buf, startPos.Line, startPos.Column, regText);
                cursor = ClipboardEditOps.CursorOnLastInsertedChar(buf, startPos, endPos);
            }
            setCursor(cursor);
            emitText(events);
            exitVisualMode(events);
            return;
        }

        if (mode == VimMode.VisualLine)
        {
            var deleted = buf.GetLines(start.Line, end.Line);
            registerManager.SetDelete('"', new Register(string.Join("\n", deleted), RegisterType.Line));

            string[] newLines = regType == RegisterType.Line ? regLines : regText.Split('\n');
            bool deletedAll = start.Line == 0 && end.Line >= buf.LineCount - 1;

            CurrentBuffer.Folds.OnLinesDeleted(start.Line, end.Line);
            buf.DeleteLines(start.Line, end.Line);

            if (deletedAll)
            {
                buf.ReplaceLine(0, newLines[0]);
                if (newLines.Length > 1)
                    buf.InsertLines(0, newLines[1..]);
            }
            else
            {
                for (int i = newLines.Length - 1; i >= 0; i--)
                    buf.InsertLineAbove(start.Line, newLines[i]);
            }
            CurrentBuffer.Folds.OnLinesInserted(start.Line, newLines.Length);
            setCursor(buf.ClampCursor(new CursorPosition(start.Line, 0)));
            emitText(events);
            exitVisualMode(events);
            return;
        }

        // Characterwise visual selection.
        clipboardOps.YankRange('"', start, end, false, isDelete: true);
        if (start.Line == end.Line)
            buf.DeleteRange(start.Line, start.Column, Math.Min(end.Column + 1, buf.GetLineLength(start.Line)));
        else
        {
            buf.DeleteRange(start.Line, start.Column, buf.GetLineLength(start.Line));
            buf.DeleteRange(end.Line, 0, Math.Min(end.Column + 1, buf.GetLineLength(end.Line)));
            for (int l = end.Line - 1; l > start.Line; l--)
                buf.DeleteLines(l, l);
            buf.JoinLines(start.Line);
        }

        if (regType == RegisterType.Line)
        {
            string curLine = buf.GetLine(start.Line);
            string before = curLine[..Math.Min(start.Column, curLine.Length)];
            string after = curLine[Math.Min(start.Column, curLine.Length)..];
            buf.ReplaceLine(start.Line, before);
            var insert = new List<string>(regLines) { after };
            buf.InsertLines(start.Line, insert);
            setCursor(buf.ClampCursor(new CursorPosition(start.Line + 1, 0)));
        }
        else
        {
            var endPos = clipboardOps.InsertCharacterwiseText(buf, start.Line, start.Column, regText);
            setCursor(ClipboardEditOps.CursorOnLastInsertedChar(buf, start, endPos));
        }
        emitText(events);
        exitVisualMode(events);
    }

    public void ExecuteVisualYank(char register, List<VimEvent> events)
    {
        if (getSelection() is not { } sel) { exitVisualMode(events); return; }
        if (getMode() == VimMode.VisualBlock)
        {
            var (startLine, _, _, _) = BlockRangeCalculator.GetBounds(sel);
            var leftColumn = BlockRangeCalculator.GetLeftColumn(sel, getBlockToLineEnd(), getBlockLineEndStartColumn());
            clipboardOps.YankBlock(register, sel, getBlockToLineEnd(), getBlockLineEndStartColumn());
            moveCursor(new CursorPosition(startLine, leftColumn), events);
            exitVisualMode(events);
            return;
        }

        var start = sel.NormalizedStart;
        var end = sel.NormalizedEnd;

        if (getMode() == VimMode.VisualLine)
            clipboardOps.YankLines(start.Line, end.Line, register, events);
        else
            clipboardOps.YankRange(register, start, end, false);

        moveCursor(start, events);
        exitVisualMode(events);
    }

    public void ExecuteVisualIndent(bool indent, List<VimEvent> events)
    {
        if (getSelection() is not { } sel) { exitVisualMode(events); return; }
        textTransform.IndentRange(sel.NormalizedStart.Line, sel.NormalizedEnd.Line, indent, events);
        exitVisualMode(events);
    }

    public void ExecuteVisualComment(List<VimEvent> events)
    {
        if (getSelection() is not { } sel) { exitVisualMode(events); return; }
        textTransform.ToggleCommentLines(sel.NormalizedStart.Line, sel.NormalizedEnd.Line, events);
        exitVisualMode(events);
    }

    public void ExecuteVisualToggleCase(List<VimEvent> events) =>
        ExecuteVisualCaseConvert(CaseConversion.Toggle, events);

    public void ExecuteVisualCaseConvert(CaseConversion mode, List<VimEvent> events)
    {
        if (getSelection() is not { } sel) { exitVisualMode(events); return; }
        snapshot();
        if (getMode() == VimMode.VisualBlock)
        {
            textTransform.ApplyBlockCaseConversion(sel, getBlockToLineEnd(), getBlockLineEndStartColumn(), mode, events);
            exitVisualMode(events);
            return;
        }

        bool linewise = getMode() == VimMode.VisualLine;
        textTransform.ApplyCaseConversion(sel.NormalizedStart, sel.NormalizedEnd, linewise, mode, events);
        exitVisualMode(events);
    }

    public void ExecuteVisualReplace(char replacement, List<VimEvent> events)
    {
        if (getSelection() is not { } sel) { exitVisualMode(events); return; }
        snapshot();

        if (getMode() == VimMode.VisualBlock)
            textTransform.ReplaceBlock(sel, getBlockToLineEnd(), getBlockLineEndStartColumn(), replacement);
        else
            textTransform.ReplaceCharwise(sel, replacement);

        emitText(events);
        exitVisualMode(events);
    }
}
