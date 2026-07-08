using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Core.Engine.Ops;

public enum CaseConversion { Lower, Upper, Toggle }

/// <summary>
/// Text-transforming operators that don't touch registers: join, case conversion,
/// indent, comment toggle, gq format, and ys/cs/ds surround. Same shape as
/// <see cref="ClipboardEditOps"/> — takes its collaborators plus the VimEngine
/// callbacks it needs (undo snapshot, event emission, cursor motion), and passes
/// <c>_cursor</c> in/out rather than owning it.
/// </summary>
public sealed class TextTransformOps(
    BufferManager bufferManager,
    SyntaxEngine syntaxEngine,
    VimOptions options,
    Action snapshot,
    Action<List<VimEvent>> emitText,
    Action<List<VimEvent>, CursorPosition> emitTextAt,
    Action<List<VimEvent>> emitCursor,
    Action<List<VimEvent>, string> emitStatus,
    Action<CursorPosition, List<VimEvent>> moveCursor)
{
    public CursorPosition DeleteWordBack(CursorPosition cursor, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        if (cursor.Column == 0 && cursor.Line > 0)
        {
            var prevLen = buf.GetLineLength(cursor.Line - 1);
            buf.JoinLines(cursor.Line - 1);
            cursor = new CursorPosition(cursor.Line - 1, prevLen);
        }
        else
        {
            var line = buf.GetLine(cursor.Line);
            int col = cursor.Column - 1;
            while (col > 0 && char.IsWhiteSpace(line[col - 1])) col--;
            while (col > 0 && !char.IsWhiteSpace(line[col - 1])) col--;
            buf.DeleteRange(cursor.Line, col, cursor.Column);
            cursor = cursor with { Column = col };
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    public CursorPosition DeleteLineBack(CursorPosition cursor, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        buf.DeleteRange(cursor.Line, 0, cursor.Column);
        cursor = cursor with { Column = 0 };
        emitTextAt(events, cursor);
        return cursor;
    }

    public CursorPosition DeleteCharBack(CursorPosition cursor, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        if (cursor.Column > 0)
        {
            buf.DeleteChar(cursor.Line, cursor.Column - 1);
            cursor = cursor with { Column = cursor.Column - 1 };
        }
        else if (cursor.Line > 0)
        {
            var prevLen = buf.GetLineLength(cursor.Line - 1);
            buf.JoinLines(cursor.Line - 1);
            cursor = new CursorPosition(cursor.Line - 1, prevLen);
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    // Backspace inside leading indent composed entirely of spaces (soft tabs) removes
    // a full tabstop's worth at once, mirroring how Tab inserts a full tabstop — so
    // deleting indentation is symmetric with typing it.
    public bool TryDeleteIndentBack(ref CursorPosition cursor, List<VimEvent> events)
    {
        if (!options.ExpandTab || options.Paste) return false;
        int tabStop = options.TabStop;
        if (tabStop <= 1 || cursor.Column == 0 || cursor.Column % tabStop != 0) return false;

        var buf = bufferManager.Current.Text;
        var line = buf.GetLine(cursor.Line);
        for (int i = 0; i < cursor.Column; i++)
            if (line[i] != ' ') return false;

        buf.DeleteRange(cursor.Line, cursor.Column - tabStop, cursor.Column);
        cursor = cursor with { Column = cursor.Column - tabStop };
        emitTextAt(events, cursor);
        return true;
    }

    public CursorPosition ExecuteReplace(CursorPosition cursor, char ch, List<VimEvent> events)
    {
        snapshot();
        var buf = bufferManager.Current.Text;
        if (cursor.Column < buf.GetLineLength(cursor.Line))
        {
            buf.DeleteChar(cursor.Line, cursor.Column);
            buf.InsertChar(cursor.Line, cursor.Column, ch);
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    public CursorPosition ExecuteIncrementNumber(CursorPosition cursor, int count, bool increment, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        var line = buf.GetLine(cursor.Line);
        int col = cursor.Column;

        int numStart = -1, numEnd = -1;
        bool isHex = false, hexUpper = false;

        for (int i = col; i < line.Length; i++)
        {
            // Hex: 0x... or 0X...
            if (line[i] == '0' && i + 1 < line.Length && (line[i + 1] == 'x' || line[i + 1] == 'X'))
            {
                hexUpper = line[i + 1] == 'X';
                int j = i + 2;
                while (j < line.Length && char.IsAsciiHexDigit(line[j])) j++;
                if (j > i + 2) { numStart = i; numEnd = j; isHex = true; break; }
            }
            // Negative decimal: -N (not preceded by word char or underscore)
            if (line[i] == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1])
                && (i == 0 || (!char.IsLetterOrDigit(line[i - 1]) && line[i - 1] != '_')))
            {
                int j = i + 1;
                while (j < line.Length && char.IsDigit(line[j])) j++;
                numStart = i; numEnd = j; break;
            }
            // Decimal: walk backward first to find the true start of the number
            if (char.IsDigit(line[i]))
            {
                int start = i;
                while (start > 0 && char.IsDigit(line[start - 1])) start--;
                // Check for negative sign before the number
                if (start > 0 && line[start - 1] == '-'
                    && (start < 2 || (!char.IsLetterOrDigit(line[start - 2]) && line[start - 2] != '_')))
                    start--;
                int end = i + 1;
                while (end < line.Length && char.IsDigit(line[end])) end++;
                numStart = start; numEnd = end; break;
            }
        }

        if (numStart == -1) return cursor;

        long delta = increment ? count : -(long)count;
        string numStr = line[numStart..numEnd];
        string newStr;

        if (isHex)
        {
            ulong hexVal = Convert.ToUInt64(numStr[2..], 16);
            long newVal = (long)hexVal + delta;
            int digits = numStr.Length - 2;
            if (newVal >= 0)
            {
                string fmt = hexUpper ? "X" : "x";
                newStr = (hexUpper ? "0X" : "0x") + ((ulong)newVal).ToString(fmt).PadLeft(digits, '0');
            }
            else
            {
                newStr = newVal.ToString();
            }
        }
        else
        {
            long decVal = long.Parse(numStr);
            newStr = (decVal + delta).ToString();
        }

        snapshot();
        string newLine = line[..numStart] + newStr + line[numEnd..];
        buf.ReplaceLine(cursor.Line, newLine);
        cursor = cursor with { Column = Math.Min(numStart + newStr.Length - 1, Math.Max(0, newLine.Length - 1)) };
        emitTextAt(events, cursor);
        return cursor;
    }

    public void JoinLines(CursorPosition cursor, int count, List<VimEvent> events)
    {
        snapshot();
        var buf = bufferManager.Current.Text;
        for (int i = 0; i < count; i++)
        {
            if (cursor.Line < buf.LineCount - 1)
            {
                var len = buf.GetLineLength(cursor.Line);
                buf.JoinLines(cursor.Line);
                // Add space if needed
                if (len > 0 && cursor.Column < buf.GetLineLength(cursor.Line))
                {
                    var joined = buf.GetLine(cursor.Line);
                    if (joined.Length > len && joined[len] != ' ')
                        buf.InsertChar(cursor.Line, len, ' ');
                }
            }
        }
        emitText(events);
    }

    public CursorPosition JoinLinesNoSpace(CursorPosition cursor, int count, List<VimEvent> events)
    {
        snapshot();
        var buf = bufferManager.Current.Text;
        int joinCol = 0;
        for (int i = 0; i < count; i++)
        {
            if (cursor.Line < buf.LineCount - 1)
            {
                // Strip all leading whitespace from next line before joining (matches Vim gJ)
                var nextLine = buf.GetLine(cursor.Line + 1);
                int trimStart = nextLine.Length - nextLine.TrimStart().Length;
                if (trimStart > 0)
                    buf.DeleteRange(cursor.Line + 1, 0, trimStart);
                joinCol = buf.GetLineLength(cursor.Line);
                buf.JoinLines(cursor.Line);
            }
        }
        cursor = buf.ClampCursor(cursor with { Column = Math.Max(0, joinCol - 1) });
        emitTextAt(events, cursor);
        return cursor;
    }

    public CursorPosition ToggleCase(CursorPosition cursor, int count, List<VimEvent> events)
    {
        snapshot();
        var buf = bufferManager.Current.Text;
        for (int i = 0; i < count; i++)
        {
            var line = buf.GetLine(cursor.Line);
            if (cursor.Column < line.Length)
            {
                var ch = line[cursor.Column];
                var toggled = char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch);
                buf.DeleteChar(cursor.Line, cursor.Column);
                buf.InsertChar(cursor.Line, cursor.Column, toggled);
                if (cursor.Column < buf.GetLineLength(cursor.Line) - 1)
                    cursor = cursor with { Column = cursor.Column + 1 };
            }
        }
        emitTextAt(events, cursor);
        return cursor;
    }

    public void ApplyBlockCaseConversion(Selection selection, bool blockToLineEnd, int lineEndStartColumn, CaseConversion mode, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        foreach (var range in BlockRangeCalculator.GetLineRanges(selection, buf, blockToLineEnd, lineEndStartColumn))
        {
            var line = buf.GetLine(range.Line);
            if (line.Length <= range.StartColumn) continue;

            var chars = line.ToCharArray();
            var endColumn = Math.Min(range.EndColumn, chars.Length - 1);
            bool changed = false;
            for (int c = range.StartColumn; c <= endColumn; c++)
            {
                char converted = mode switch
                {
                    CaseConversion.Lower => char.ToLower(chars[c]),
                    CaseConversion.Upper => char.ToUpper(chars[c]),
                    _ => char.IsUpper(chars[c]) ? char.ToLower(chars[c]) : char.ToUpper(chars[c])
                };
                if (converted != chars[c]) { chars[c] = converted; changed = true; }
            }
            if (changed) buf.ReplaceLine(range.Line, new string(chars));
        }

        var (startLine, _, _, _) = BlockRangeCalculator.GetBounds(selection);
        var leftColumn = BlockRangeCalculator.GetLeftColumn(selection, blockToLineEnd, lineEndStartColumn);
        moveCursor(new CursorPosition(startLine, leftColumn), events);
        emitText(events);
    }

    public void ApplyCaseConversion(CursorPosition from, CursorPosition to, bool linewise, CaseConversion mode, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        var start = from.Line < to.Line || (from.Line == to.Line && from.Column <= to.Column) ? from : to;
        var end = start == from ? to : from;

        for (int l = start.Line; l <= end.Line; l++)
        {
            var line = buf.GetLine(l);
            int colStart = (!linewise && l == start.Line) ? start.Column : 0;
            int colEnd = (!linewise && l == end.Line) ? end.Column : line.Length - 1;
            var chars = line.ToCharArray();
            bool changed = false;
            for (int c = colStart; c <= colEnd && c < chars.Length; c++)
            {
                char converted = mode switch
                {
                    CaseConversion.Lower => char.ToLower(chars[c]),
                    CaseConversion.Upper => char.ToUpper(chars[c]),
                    _ => char.IsUpper(chars[c]) ? char.ToLower(chars[c]) : char.ToUpper(chars[c])
                };
                if (converted != chars[c]) { chars[c] = converted; changed = true; }
            }
            if (changed) buf.ReplaceLine(l, new string(chars));
        }
        moveCursor(start, events);
        emitText(events);
    }

    public void IndentRange(int start, int end, bool indent, List<VimEvent> events)
    {
        snapshot();
        var buf = bufferManager.Current.Text;
        var sw = options.ShiftWidth;
        var indentStr = options.ExpandTab ? new string(' ', sw) : "\t";

        for (int l = start; l <= end; l++)
        {
            if (indent)
                buf.InsertText(l, 0, indentStr);
            else
            {
                var line = buf.GetLine(l);
                int toRemove = 0;
                for (int i = 0; i < sw && i < line.Length && (line[i] == ' ' || line[i] == '\t'); i++)
                    toRemove++;
                if (toRemove > 0) buf.DeleteRange(l, 0, toRemove);
            }
        }
        emitText(events);
    }

    // Markdown list Tab indenting: in a .md/.markdown file, when the cursor line
    // is a list item, Tab should indent (and Shift+Tab dedent) the whole item by
    // one shiftwidth — matching Obsidian/VS Code — instead of inserting a tab at
    // the cursor. Callers test this first and fall back to normal Tab otherwise.
    public void ToggleCommentLines(int startLine, int endLine, List<VimEvent> events)
    {
        var prefix = syntaxEngine.GetCommentPrefix();
        if (prefix != null)
        {
            snapshot();
            ToggleLineCommentLines(startLine, endLine, prefix, events);
            return;
        }

        var block = syntaxEngine.GetBlockComment();
        if (block == null)
        {
            emitStatus(events, "No comment prefix for this file type");
            return;
        }

        snapshot();
        ToggleBlockCommentLines(startLine, endLine, block.Value.Prefix, block.Value.Suffix, events);
    }

    public void ToggleLineCommentLines(int startLine, int endLine, string prefix, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;

        // Collect line data and detect if ALL non-empty lines are already commented
        int count = endLine - startLine + 1;
        var lines = new (string Raw, string Trimmed, int Indent)[count];
        bool allCommented = true;
        for (int i = 0; i < count; i++)
        {
            var raw = buf.GetLine(startLine + i);
            var trimmed = raw.TrimStart();
            lines[i] = (raw, trimmed, raw.Length - trimmed.Length);
            if (trimmed.Length > 0 && !trimmed.StartsWith(prefix))
                allCommented = false;
        }

        for (int i = 0; i < count; i++)
        {
            var (raw, trimmed, indent) = lines[i];
            if (allCommented)
            {
                // Remove comment prefix (and one trailing space if present)
                if (!trimmed.StartsWith(prefix)) continue;
                string uncommented = trimmed[prefix.Length..];
                if (uncommented.StartsWith(" ")) uncommented = uncommented[1..];
                buf.ReplaceLine(startLine + i, raw[..indent] + uncommented);
            }
            else
            {
                // Add comment prefix; skip blank lines
                if (trimmed.Length == 0) continue;
                buf.ReplaceLine(startLine + i, raw[..indent] + prefix + " " + trimmed);
            }
        }

        emitText(events);
    }

    // Per-line block-comment toggle (used by languages with no line-comment syntax, e.g.
    // CSS's /* */ or XML/Markdown's <!-- -->): each non-empty line is individually wrapped
    // in open/close delimiters, mirroring the line-comment toggle's per-line semantics.
    public void ToggleBlockCommentLines(int startLine, int endLine, string open, string close, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;

        bool IsWrapped(string trimmed) =>
            trimmed.Length >= open.Length + close.Length &&
            trimmed.StartsWith(open) && trimmed.EndsWith(close);

        int count = endLine - startLine + 1;
        var lines = new (string Raw, string Trimmed, int Indent)[count];
        bool allCommented = true;
        for (int i = 0; i < count; i++)
        {
            var raw = buf.GetLine(startLine + i);
            var trimmed = raw.TrimStart();
            lines[i] = (raw, trimmed, raw.Length - trimmed.Length);
            if (trimmed.Length > 0 && !IsWrapped(trimmed))
                allCommented = false;
        }

        for (int i = 0; i < count; i++)
        {
            var (raw, trimmed, indent) = lines[i];
            if (allCommented)
            {
                if (!IsWrapped(trimmed)) continue;
                string inner = trimmed[open.Length..^close.Length];
                if (inner.StartsWith(" ")) inner = inner[1..];
                if (inner.EndsWith(" ")) inner = inner[..^1];
                buf.ReplaceLine(startLine + i, raw[..indent] + inner);
            }
            else
            {
                if (trimmed.Length == 0) continue;
                buf.ReplaceLine(startLine + i, raw[..indent] + open + " " + trimmed + " " + close);
            }
        }

        emitText(events);
    }

    public CursorPosition FormatText(CursorPosition cursor, int startLine, int endLine, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        int tw = options.TextWidth;
        if (tw <= 0) tw = 79;

        // Collect lines, preserving leading indent of first line in each paragraph
        var result = new List<string>();
        int i = startLine;
        while (i <= endLine)
        {
            var line = buf.GetLine(i);
            // Blank line: preserve as-is and start new paragraph
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(line);
                i++;
                continue;
            }

            // Detect indent from first line of paragraph
            var indent = "";
            int indentLen = 0;
            while (indentLen < line.Length && (line[indentLen] == ' ' || line[indentLen] == '\t'))
                indentLen++;
            indent = line[..indentLen];

            // Collect all non-blank lines of this paragraph
            var words = new List<string>();
            while (i <= endLine && !string.IsNullOrWhiteSpace(buf.GetLine(i)))
            {
                var l = buf.GetLine(i).TrimStart();
                words.AddRange(l.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                i++;
            }

            // Wrap words at textwidth
            var currentLine = new System.Text.StringBuilder(indent);
            int currentLen = indent.Length;
            bool first = true;
            foreach (var word in words)
            {
                if (first)
                {
                    currentLine.Append(word);
                    currentLen += word.Length;
                    first = false;
                }
                else if (currentLen + 1 + word.Length <= tw)
                {
                    currentLine.Append(' ');
                    currentLine.Append(word);
                    currentLen += 1 + word.Length;
                }
                else
                {
                    result.Add(currentLine.ToString());
                    currentLine = new System.Text.StringBuilder(indent);
                    currentLine.Append(word);
                    currentLen = indent.Length + word.Length;
                }
            }
            if (currentLine.Length > 0)
                result.Add(currentLine.ToString());
        }

        // Replace lines startLine..endLine with result lines
        // First replace existing lines, then insert/delete extras
        int origCount = endLine - startLine + 1;
        int newCount = result.Count;
        int replaceCount = Math.Min(origCount, newCount);
        for (int j = 0; j < replaceCount; j++)
            buf.ReplaceLine(startLine + j, result[j]);
        if (newCount > origCount)
        {
            for (int j = origCount; j < newCount; j++)
            {
                buf.InsertLineAbove(startLine + j, result[j]);
            }
        }
        else if (newCount < origCount)
        {
            buf.DeleteLines(startLine + newCount, startLine + origCount - 1);
        }

        cursor = cursor with { Line = Math.Min(startLine, buf.LineCount - 1), Column = 0 };
        emitTextAt(events, cursor);
        emitCursor(events);
        emitStatus(events, $"Formatted {endLine - startLine + 1} lines");
        return cursor;
    }

    // ─────────────── SURROUND ───────────────
    private static (string Open, string Close) GetSurroundPair(char ch) => ch switch
    {
        '(' or 'b' => ("( ", " )"),
        ')'        => ("(", ")"),
        '{' or 'B' => ("{ ", " }"),
        '}'        => ("{", "}"),
        '['        => ("[ ", " ]"),
        ']'        => ("[", "]"),
        '<'        => ("< ", " >"),
        '>'        => ("<", ">"),
        _          => (ch.ToString(), ch.ToString())
    };

    private static char GetSurroundOpen(char ch) => ch switch
    {
        ')' or 'b' => '(',
        '}' or 'B' => '{',
        ']'        => '[',
        '>'        => '<',
        _          => ch
    };

    private static char GetSurroundClose(char ch) => ch switch
    {
        '(' or 'b' => ')',
        '{' or 'B' => '}',
        '['        => ']',
        '<'        => '>',
        _          => ch
    };

    public CursorPosition ApplySurround(CursorPosition start, CursorPosition end, bool linewise, char ch, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        var (open, close) = GetSurroundPair(ch);
        CursorPosition cursor;

        if (start.Line == end.Line)
        {
            // Single-line: insert close first, then open (to preserve column indices)
            int closeCol = Math.Min(end.Column + 1, buf.GetLineLength(end.Line));
            buf.InsertText(end.Line, closeCol, close);
            buf.InsertText(start.Line, start.Column, open);
            cursor = start with { Column = start.Column };
        }
        else
        {
            // Multi-line: add close at end of last line, open at start of first line
            var lastLine = buf.GetLine(end.Line).TrimEnd();
            buf.ReplaceLine(end.Line, lastLine + close);
            var firstLine = buf.GetLine(start.Line);
            buf.ReplaceLine(start.Line, open + firstLine);
            cursor = start with { Column = 0 };
        }

        emitTextAt(events, cursor);
        emitCursor(events);
        return cursor;
    }

    public CursorPosition ExecuteDeleteSurround(CursorPosition cursor, char ch, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        char openCh = GetSurroundOpen(ch);
        char closeCh = GetSurroundClose(ch);

        var textObjs = new TextObjectEngine(buf);
        (CursorPosition Start, CursorPosition End)? pair;
        if (ch is '"' or '\'' or '`')
            pair = textObjs.FindEnclosingQuote(ch, cursor, true);
        else
            pair = textObjs.FindEnclosingPair(openCh, closeCh, cursor, true);

        if (pair == null) { emitStatus(events, $"No surrounding '{ch}' found"); return cursor; }

        var (s, e) = pair.Value;
        // Delete close char first (higher position), then open char
        if (e.Line > s.Line || (e.Line == s.Line && e.Column > s.Column))
        {
            buf.DeleteChar(e.Line, e.Column);
            buf.DeleteChar(s.Line, s.Column);
        }
        else
        {
            buf.DeleteChar(s.Line, s.Column);
        }
        cursor = buf.ClampCursor(s, false);
        emitTextAt(events, cursor);
        emitCursor(events);
        return cursor;
    }

    public CursorPosition ExecuteChangeSurround(CursorPosition cursor, char from, char to, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        char openFrom = GetSurroundOpen(from);
        char closeFrom = GetSurroundClose(from);

        var textObjs = new TextObjectEngine(buf);
        (CursorPosition Start, CursorPosition End)? pair;
        if (from is '"' or '\'' or '`')
            pair = textObjs.FindEnclosingQuote(from, cursor, true);
        else
            pair = textObjs.FindEnclosingPair(openFrom, closeFrom, cursor, true);

        if (pair == null) { emitStatus(events, $"No surrounding '{from}' found"); return cursor; }

        var (s, e) = pair.Value;
        var (openStr, closeStr) = GetSurroundPair(to);

        // Replace close first, then open (to preserve column indices)
        if (e.Line > s.Line)
        {
            // Multi-line: replace chars
            var closeLine = buf.GetLine(e.Line);
            buf.ReplaceLine(e.Line, closeLine[..e.Column] + closeStr + (e.Column + 1 < closeLine.Length ? closeLine[(e.Column + 1)..] : ""));
            var openLine = buf.GetLine(s.Line);
            buf.ReplaceLine(s.Line, openLine[..s.Column] + openStr + (s.Column + 1 < openLine.Length ? openLine[(s.Column + 1)..] : ""));
        }
        else if (e.Column > s.Column)
        {
            buf.DeleteRange(e.Line, e.Column, e.Column);
            buf.InsertText(e.Line, e.Column, closeStr);
            buf.DeleteRange(s.Line, s.Column, s.Column);
            buf.InsertText(s.Line, s.Column, openStr);
        }
        else
        {
            buf.DeleteRange(s.Line, s.Column, s.Column);
            buf.InsertText(s.Line, s.Column, openStr + closeStr);
        }

        cursor = buf.ClampCursor(s, false);
        emitTextAt(events, cursor);
        emitCursor(events);
        return cursor;
    }
}
