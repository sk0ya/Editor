using Editor.Core.Buffer;
using Editor.Core.Models;
using Editor.Core.Registers;

namespace Editor.Core.Engine.Ops;

/// <summary>
/// Delete/yank/paste primitives shared by Normal-mode operators (d/y/p family) and
/// Visual-mode delete/paste/yank. Takes the buffer/register collaborators directly
/// (same shape as <see cref="Commands.FoldCommands"/>) plus the handful of
/// cross-cutting VimEngine callbacks (undo snapshot, text/status event emission)
/// these mutating operations need — VimEngine still owns <c>_cursor</c> and passes
/// it in/gets the new value back, the same way <see cref="Commands.FoldCommands"/>
/// returns a cursor for the caller to apply.
/// </summary>
public sealed class ClipboardEditOps(
    BufferManager bufferManager,
    RegisterManager registerManager,
    Action snapshot,
    Action<List<VimEvent>, CursorPosition> emitTextAt,
    Action<List<VimEvent>, string> emitStatus)
{
    public CursorPosition ExecuteDelete(CursorPosition from, CursorPosition to, bool linewise, List<VimEvent> events, char register = '"')
    {
        snapshot();
        var buf = bufferManager.Current.Text;

        if (linewise)
            return DeleteLines(from.Line, to.Line, events, register);

        // The blackhole register "_ discards the deleted text without touching
        // the unnamed/yank registers.
        if (register != '_')
            YankRange(register, from, to, linewise, isDelete: true);

        CursorPosition cursor;
        if (from.Line == to.Line)
        {
            buf.DeleteRange(from.Line, from.Column, to.Column + 1);
            cursor = buf.ClampCursor(from);
        }
        else
        {
            // Multi-line delete
            buf.DeleteRange(from.Line, from.Column, buf.GetLineLength(from.Line));
            buf.DeleteRange(to.Line, 0, to.Column + 1);
            for (int l = to.Line - 1; l > from.Line; l--)
                buf.DeleteLines(l, l);
            buf.JoinLines(from.Line);
            cursor = buf.ClampCursor(from);
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    public CursorPosition DeleteLines(int start, int end, List<VimEvent> events, char register = '"')
    {
        var buf = bufferManager.Current.Text;
        if (register != '_')
            YankLines(start, end, register, events, isDelete: true);
        bufferManager.Current.Folds.OnLinesDeleted(start, end);
        buf.DeleteLines(start, end);
        var cursor = buf.ClampCursor(new CursorPosition(start, 0));
        emitTextAt(events, cursor);
        return cursor;
    }

    public void YankRange(char register, CursorPosition from, CursorPosition to, bool linewise, bool isDelete = false)
    {
        var buf = bufferManager.Current.Text;
        string text;
        if (from.Line == to.Line)
            text = buf.GetLine(from.Line)[from.Column..(Math.Min(to.Column + 1, buf.GetLineLength(from.Line)))];
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(buf.GetLine(from.Line)[from.Column..]);
            for (int l = from.Line + 1; l < to.Line; l++)
                sb.Append('\n').Append(buf.GetLine(l));
            sb.Append('\n').Append(buf.GetLine(to.Line)[..Math.Min(to.Column + 1, buf.GetLineLength(to.Line))]);
            text = sb.ToString();
        }
        var reg = new Register(text, linewise ? RegisterType.Line : RegisterType.Character);
        if (isDelete) registerManager.SetDelete(register, reg);
        else registerManager.SetYank(register, reg);
    }

    public void YankLines(int start, int end, char register, List<VimEvent> events, bool isDelete = false)
    {
        var buf = bufferManager.Current.Text;
        var lines = buf.GetLines(start, end);
        var text = string.Join("\n", lines);
        var reg = new Register(text, RegisterType.Line);
        if (isDelete) registerManager.SetDelete(register, reg);
        else registerManager.SetYank(register, reg);
        emitStatus(events, $"{lines.Length} line(s) yanked");
    }

    // cursorAfterPaste implements gp/gP: leave the cursor just past the inserted text
    // instead of on its last character (Vim's gp/gP semantics).
    public CursorPosition PasteAfter(CursorPosition cursor, char register, List<VimEvent> events, bool cursorAfterPaste = false)
    {
        snapshot();
        var reg = registerManager.Get(register);
        if (reg.IsEmpty) return cursor;
        var buf = bufferManager.Current.Text;

        if (reg.Type == RegisterType.Line)
            cursor = InsertLinewisePaste(cursor, reg.GetLines(), after: true, cursorAfterBlock: cursorAfterPaste);
        else
        {
            var col = Math.Min(cursor.Column + 1, buf.GetLineLength(cursor.Line));
            var end = InsertCharacterwiseText(buf, cursor.Line, col, reg.Text);
            cursor = cursorAfterPaste
                ? buf.ClampCursor(end)
                : CursorOnLastInsertedChar(buf, new CursorPosition(cursor.Line, col), end);
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    /// <summary>
    /// Pastes literal <paramref name="text"/> characterwise at (after=true) or before
    /// (after=false) the cursor, without touching any register. Mirrors the p/P cursor
    /// placement (rests on the last inserted character). Used for synthesised host pastes
    /// such as image → Markdown link.
    /// </summary>
    public CursorPosition PasteRawText(CursorPosition cursor, string text, bool after, List<VimEvent> events)
    {
        snapshot();
        var buf = bufferManager.Current.Text;
        var start = after
            ? new CursorPosition(cursor.Line, Math.Min(cursor.Column + 1, buf.GetLineLength(cursor.Line)))
            : cursor;
        var end = InsertCharacterwiseText(buf, start.Line, start.Column, text);
        var result = CursorOnLastInsertedChar(buf, start, end);
        emitTextAt(events, result);
        return result;
    }

    public CursorPosition PasteBefore(CursorPosition cursor, char register, List<VimEvent> events, bool cursorAfterPaste = false)
    {
        snapshot();
        var reg = registerManager.Get(register);
        if (reg.IsEmpty) return cursor;
        var buf = bufferManager.Current.Text;

        if (reg.Type == RegisterType.Line)
            cursor = InsertLinewisePaste(cursor, reg.GetLines(), after: false, cursorAfterBlock: cursorAfterPaste);
        else
        {
            var start = cursor;
            var end = InsertCharacterwiseText(buf, start.Line, start.Column, reg.Text);
            cursor = cursorAfterPaste
                ? buf.ClampCursor(end)
                : CursorOnLastInsertedChar(buf, start, end);
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    // Shared linewise insertion for p/P/]p/[p/gp/gP — does not call Snapshot or EmitText.
    // cursorAfterBlock places the cursor on the first line after the pasted block (gp/gP)
    // instead of on the first pasted line (p/P).
    public CursorPosition InsertLinewisePaste(CursorPosition cursor, string[] lines, bool after, bool cursorAfterBlock = false)
    {
        var buf = bufferManager.Current.Text;
        if (after)
        {
            bufferManager.Current.Folds.OnLinesInserted(cursor.Line, lines.Length);
            buf.InsertLines(cursor.Line, lines);
            return cursorAfterBlock
                ? buf.ClampCursor(new CursorPosition(cursor.Line + 1 + lines.Length, 0))
                : new CursorPosition(cursor.Line + 1, 0);
        }
        else
        {
            bufferManager.Current.Folds.OnLinesInserted(cursor.Line - 1, lines.Length);
            buf.InsertLines(cursor.Line - 1, lines);
            return cursorAfterBlock
                ? buf.ClampCursor(new CursorPosition(cursor.Line + lines.Length, 0))
                : new CursorPosition(cursor.Line, 0);
        }
    }

    public CursorPosition InsertCharacterwiseText(TextBuffer buf, int line, int col, string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var parts = normalized.Split('\n');
        buf.InsertText(line, col, normalized);
        if (parts.Length > 1)
            bufferManager.Current.Folds.OnLinesInserted(line, parts.Length - 1);
        return parts.Length == 1
            ? new CursorPosition(line, col + parts[0].Length)
            : new CursorPosition(line + parts.Length - 1, parts[^1].Length);
    }

    public static CursorPosition CursorOnLastInsertedChar(TextBuffer buf, CursorPosition start, CursorPosition insertionEnd)
    {
        if (insertionEnd.Column > 0)
            return buf.ClampCursor(insertionEnd with { Column = insertionEnd.Column - 1 });
        if (insertionEnd.Line > start.Line)
            return buf.ClampCursor(new CursorPosition(insertionEnd.Line - 1, int.MaxValue));
        return buf.ClampCursor(start);
    }

    /// <summary>
    /// Implements ]p (after=true) and [p (after=false): paste with indent adjusted to match current line.
    /// Only applies indent adjustment for linewise registers; character registers fall back to normal paste.
    /// </summary>
    public CursorPosition ExecuteIndentedPaste(CursorPosition cursor, bool after, char register, int tabStop, List<VimEvent> events)
    {
        snapshot();
        var reg = registerManager.Get(register);
        if (reg.IsEmpty) return cursor;
        var buf = bufferManager.Current.Text;

        if (reg.Type == RegisterType.Line)
        {
            var lines = reg.GetLines();

            // Minimum indent among non-empty lines in the register
            int minIndent = int.MaxValue;
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                int ind = CountIndentSpaces(line, tabStop);
                if (ind < minIndent) minIndent = ind;
            }
            if (minIndent == int.MaxValue) minIndent = 0;

            int delta = CountIndentSpaces(buf.GetLine(cursor.Line), tabStop) - minIndent;

            string[] adjusted = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                adjusted[i] = ApplyIndentDelta(lines[i], delta, tabStop);

            cursor = InsertLinewisePaste(cursor, adjusted, after);
        }
        else
        {
            // Character register: fall back to normal paste (no indent adjustment)
            if (after)
            {
                var col = Math.Min(cursor.Column + 1, buf.GetLineLength(cursor.Line));
                var end = InsertCharacterwiseText(buf, cursor.Line, col, reg.Text);
                cursor = CursorOnLastInsertedChar(buf, new CursorPosition(cursor.Line, col), end);
            }
            else
            {
                var start = cursor;
                var end = InsertCharacterwiseText(buf, start.Line, start.Column, reg.Text);
                cursor = CursorOnLastInsertedChar(buf, start, end);
            }
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    /// <summary>Returns the number of leading spaces in a line, expanding tabs using tabStop.</summary>
    private static int CountIndentSpaces(string line, int tabStop)
    {
        int spaces = 0;
        foreach (char c in line)
        {
            if (c == ' ') spaces++;
            else if (c == '\t') spaces += tabStop - (spaces % tabStop);
            else break;
        }
        return spaces;
    }

    /// <summary>
    /// Adjusts the leading indent of a line by <paramref name="delta"/> spaces.
    /// Positive delta adds spaces; negative delta removes them (clamped to 0).
    /// Preserves the original tab/space style of the line's existing indent.
    /// </summary>
    private static string ApplyIndentDelta(string line, int delta, int tabStop)
    {
        if (line.Length == 0) return line;

        // Count leading whitespace characters
        int ws = 0;
        while (ws < line.Length && (line[ws] == ' ' || line[ws] == '\t'))
            ws++;

        string content = line[ws..];
        int currentSpaces = CountIndentSpaces(line[..ws], tabStop);
        int newSpaces = Math.Max(0, currentSpaces + delta);

        // Rebuild indent as spaces (simplest, consistent with Vim behaviour)
        return new string(' ', newSpaces) + content;
    }

    public void DeleteBlock(Selection selection, bool blockToLineEnd, int lineEndStartColumn)
    {
        var buf = bufferManager.Current.Text;
        foreach (var range in BlockRangeCalculator.GetLineRanges(selection, buf, blockToLineEnd, lineEndStartColumn))
        {
            var length = buf.GetLineLength(range.Line);
            if (length <= range.StartColumn) continue;
            var endExclusive = Math.Min(length, range.EndColumn + 1);
            buf.DeleteRange(range.Line, range.StartColumn, endExclusive);
        }
    }

    public void YankBlock(char register, Selection selection, bool blockToLineEnd, int lineEndStartColumn, bool isDelete = false)
    {
        var buf = bufferManager.Current.Text;
        var lines = new List<string>();
        foreach (var range in BlockRangeCalculator.GetLineRanges(selection, buf, blockToLineEnd, lineEndStartColumn))
        {
            var text = buf.GetLine(range.Line);
            if (text.Length <= range.StartColumn)
            {
                lines.Add("");
                continue;
            }

            var endExclusive = Math.Min(text.Length, range.EndColumn + 1);
            lines.Add(text[range.StartColumn..endExclusive]);
        }

        var reg = new Register(string.Join("\n", lines), RegisterType.Block);
        if (isDelete) registerManager.SetDelete(register, reg);
        else registerManager.SetYank(register, reg);
    }
}
