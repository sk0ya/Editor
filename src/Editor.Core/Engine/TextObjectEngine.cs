using Editor.Core.Buffer;
using Editor.Core.Models;
using Editor.Core.Text;

namespace Editor.Core.Engine;

/// <summary>
/// Computes text-object ranges (iw/aw, i(/a(, it/at, is/as, ip/ap, etc.) and the
/// backward word-end motion (ge/gE) against a <see cref="TextBuffer"/>. Stateless —
/// constructed per call the same way as <see cref="MotionEngine"/>.
/// </summary>
public sealed class TextObjectEngine(TextBuffer buffer)
{
    private readonly TextBuffer _buf = buffer;

    public CursorPosition WordEndBackward(CursorPosition cursor, int count)
    {
        var buf = _buf;
        var pos = cursor;

        for (int c = 0; c < count; c++)
        {
            int line = pos.Line;
            int col = pos.Column - 1;

            while (true)
            {
                var text = buf.GetLine(line);

                if (text.Length == 0 || col < 0)
                {
                    if (line == 0)
                    {
                        pos = CursorPosition.Zero;
                        break;
                    }
                    line--;
                    col = buf.GetLine(line).Length - 1;
                    continue;
                }

                while (col >= 0 && char.IsWhiteSpace(text[col])) col--;
                if (col >= 0)
                {
                    pos = new CursorPosition(line, GraphemeCluster.ClusterStart(text, col));
                    break;
                }

                if (line == 0)
                {
                    pos = CursorPosition.Zero;
                    break;
                }

                line--;
                col = buf.GetLine(line).Length - 1;
            }
        }

        return pos;
    }

    public (CursorPosition Start, CursorPosition End)? GetRange(string textObject, CursorPosition cursor, int count = 1)
    {
        if (textObject.Length != 2) return null;

        bool around = textObject[0] == 'a';
        char kind = textObject[1];
        count = Math.Max(1, count);

        return kind switch
        {
            'w' or 'W' => GetWordRange(kind == 'W', around, cursor, count),
            '(' or ')' or 'b' => FindEnclosingPair('(', ')', cursor, around),
            '{' or '}' or 'B' => FindEnclosingPair('{', '}', cursor, around),
            '[' or ']' => FindEnclosingPair('[', ']', cursor, around),
            '<' or '>' => FindEnclosingPair('<', '>', cursor, around),
            '"' => FindEnclosingQuote('"', cursor, around),
            '\'' => FindEnclosingQuote('\'', cursor, around),
            '`' => FindEnclosingQuote('`', cursor, around),
            't' => GetTagRange(cursor, around),
            's' => GetSentenceRange(cursor, around),
            'p' => GetParagraphRange(cursor, around),
            _ => null
        };
    }

    public (CursorPosition Start, CursorPosition End)? GetWordRange(bool bigWord, bool around, CursorPosition cursor, int count)
    {
        var buf = _buf;
        var lineNo = cursor.Line;

        // For a WORD (iW/aW) every non-blank char shares one class; otherwise use the
        // 3-class Vim rule (whitespace / punctuation / keyword). An inner word is the
        // maximal run of the class under the cursor — this is what makes `iw` on a lone
        // symbol (e.g. ☑) select the symbol rather than skipping to the next word.
        int ClassOf(char ch) => bigWord
            ? (char.IsWhiteSpace(ch) ? 0 : 1)
            : MotionEngine.CharClass(ch);

        // The next contiguous class-run starting at or after (line, col). Whitespace
        // runs are returned as their own units (Cls == 0); a bare line break is treated
        // as a run boundary and skipped (words never cross it as a single run).
        (int Line, int Start, int End, int Cls)? NextRunAfter(int startLine, int startCol)
        {
            for (int line = startLine; line < buf.LineCount; line++)
            {
                var text = buf.GetLine(line);
                int col = line == startLine ? Math.Clamp(startCol, 0, text.Length) : 0;
                if (col >= text.Length) continue;

                int cls = ClassOf(text[col]);
                int end = col;
                while (end + 1 < text.Length && ClassOf(text[end + 1]) == cls) end++;
                return (line, col, end, cls);
            }

            return null;
        }

        var currentLine = buf.GetLine(lineNo);
        if (currentLine.Length == 0)
            return null;

        int cursorCol = Math.Clamp(cursor.Column, 0, Math.Max(0, currentLine.Length - 1));

        // Primary object: the run of characters sharing the cursor character's class
        // (whitespace, keyword, or punctuation), all within the current line.
        int cursorClass = ClassOf(currentLine[cursorCol]);
        int startLine = lineNo;
        int endLine = lineNo;
        int start = cursorCol;
        int end = cursorCol;
        while (start > 0 && ClassOf(currentLine[start - 1]) == cursorClass) start--;
        while (end + 1 < currentLine.Length && ClassOf(currentLine[end + 1]) == cursorClass) end++;

        if (!around)
        {
            // iw: select exactly `count` consecutive class-runs (whitespace runs count too),
            // starting with the run under the cursor. `d3iw` on "foo bar baz" → "foo bar".
            for (int i = 1; i < count; i++)
            {
                var next = NextRunAfter(endLine, end + 1);
                if (next == null) break;
                endLine = next.Value.Line;
                end = next.Value.End;
            }
            return (new CursorPosition(startLine, start), new CursorPosition(endLine, end));
        }

        // aw: `count` "words" (non-whitespace runs), including any whitespace runs between
        // them, plus trailing whitespace after the last word — or, if there is none,
        // leading whitespace before the first word.
        if (cursorClass == 0)
        {
            // Cursor on whitespace: the leading whitespace run is already selected; append
            // `count` following words (with whitespace between them).
            int wordsLeft = count;
            int line = endLine, col = end + 1;
            while (wordsLeft > 0)
            {
                var r = NextRunAfter(line, col);
                if (r == null) break;
                endLine = r.Value.Line; end = r.Value.End;
                line = r.Value.Line; col = r.Value.End + 1;
                if (r.Value.Cls != 0) wordsLeft--;
            }
            return (new CursorPosition(startLine, start), new CursorPosition(endLine, end));
        }

        // Cursor on a word: the primary run is word #1; extend over the remaining words.
        {
            int wordsLeft = count - 1;
            int line = endLine, col = end + 1;
            while (wordsLeft > 0)
            {
                var r = NextRunAfter(line, col);
                if (r == null) break;
                endLine = r.Value.Line; end = r.Value.End;
                line = r.Value.Line; col = r.Value.End + 1;
                if (r.Value.Cls != 0) wordsLeft--;
            }
        }

        var lastLine = buf.GetLine(endLine);
        int trailingEnd = end;
        while (trailingEnd + 1 < lastLine.Length && char.IsWhiteSpace(lastLine[trailingEnd + 1])) trailingEnd++;
        if (trailingEnd > end)
            end = trailingEnd;
        else if (startLine == endLine)
        {
            var firstLine = buf.GetLine(startLine);
            while (start > 0 && char.IsWhiteSpace(firstLine[start - 1])) start--;
        }

        return (new CursorPosition(startLine, start), new CursorPosition(endLine, end));
    }

    // Shared helper: trim inner content to exclude delimiter characters at (openLine,openCol) and (closeLine,closeCol)
    private static (CursorPosition Start, CursorPosition End)? InnerRange(
        TextBuffer buf, int innerOpenLine, int innerOpenCol, int innerCloseLine, int innerCloseCol)
    {
        int iLine = innerOpenLine, iCol = innerOpenCol;
        if (iCol >= buf.GetLine(iLine).Length && iLine + 1 < buf.LineCount)
        { iLine++; iCol = 0; }

        int eLine = innerCloseLine, eCol = innerCloseCol;
        if (eCol < 0 && eLine > 0)
        { eLine--; eCol = buf.GetLine(eLine).Length - 1; }

        if (eLine < iLine || (eLine == iLine && eCol < iCol))
            return null;
        return (new CursorPosition(iLine, iCol), new CursorPosition(eLine, eCol));
    }

    // Find the innermost enclosing bracket pair (possibly multi-line, ±500 line limit)
    public (CursorPosition Start, CursorPosition End)? FindEnclosingPair(char open, char close, CursorPosition cursor, bool around)
    {
        var buf = _buf;
        int curLine = cursor.Line;
        int curCol = Math.Clamp(cursor.Column, 0, Math.Max(0, buf.GetLine(curLine).Length - 1));

        // Search backward for the unmatched opening bracket
        int depth = 0;
        int openLine = -1, openCol = -1;

        for (int l = curLine; l >= Math.Max(0, curLine - 500); l--)
        {
            var lineText = buf.GetLine(l);
            int startC = (l == curLine) ? curCol : lineText.Length - 1;

            for (int c = startC; c >= 0; c--)
            {
                char ch = lineText[c];
                if (ch == close) depth++;
                else if (ch == open)
                {
                    if (depth == 0) { openLine = l; openCol = c; goto foundOpen; }
                    depth--;
                }
            }
        }
        return null;

        foundOpen:
        // Search forward from opening bracket for the matching close
        depth = 0;
        for (int l = openLine; l < buf.LineCount; l++)
        {
            var lineText = buf.GetLine(l);
            int startC = (l == openLine) ? openCol : 0;

            for (int c = startC; c < lineText.Length; c++)
            {
                char ch = lineText[c];
                if (ch == open) depth++;
                else if (ch == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (around)
                            return (new CursorPosition(openLine, openCol), new CursorPosition(l, c));
                        return InnerRange(buf, openLine, openCol + 1, l, c - 1);
                    }
                }
            }
        }
        return null;
    }

    // Find enclosing quote pair on the current line (single-pass, no allocation)
    public (CursorPosition Start, CursorPosition End)? FindEnclosingQuote(char quote, CursorPosition cursor, bool around)
    {
        var buf = _buf;
        int lineNo = cursor.Line;
        var line = buf.GetLine(lineNo);
        if (line.Length == 0) return null;

        int col = Math.Clamp(cursor.Column, 0, line.Length - 1);

        // Walk left to find opening quote (respecting backslash escapes)
        int q1 = -1;
        for (int i = col; i >= 0; i--)
        {
            if (i > 0 && line[i - 1] == '\\') continue;
            if (line[i] == quote) { q1 = i; break; }
        }
        if (q1 < 0) return null;

        // Walk right from q1+1 to find closing quote
        int q2 = -1;
        for (int i = q1 + 1; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == quote) { q2 = i; break; }
        }
        if (q2 < 0) return null;

        if (around)
            return (new CursorPosition(lineNo, q1), new CursorPosition(lineNo, q2));
        if (q2 > q1 + 1)
            return (new CursorPosition(lineNo, q1 + 1), new CursorPosition(lineNo, q2 - 1));
        return null; // empty quotes
    }

    // Find enclosing HTML/XML tag  <tag>...</tag>
    public (CursorPosition Start, CursorPosition End)? GetTagRange(CursorPosition cursor, bool around)
    {
        var buf = _buf;
        int curLine = cursor.Line;
        int curCol = Math.Clamp(cursor.Column, 0, Math.Max(0, buf.GetLine(curLine).Length - 1));

        // Search backward for an opening tag <tagname>
        string? tagName = null;
        int openLine = -1, openCol = -1, openEnd = -1;

        for (int l = curLine; l >= Math.Max(0, curLine - 200); l--)
        {
            var lineText = buf.GetLine(l);
            int endC = (l == curLine) ? curCol : lineText.Length - 1;

            for (int c = endC; c >= 0; c--)
            {
                if (lineText[c] != '<') continue;
                if (c + 1 < lineText.Length && lineText[c + 1] == '/') continue; // skip closing tags

                int nameStart = c + 1;
                int nameEnd = nameStart;
                while (nameEnd < lineText.Length &&
                       lineText[nameEnd] != '>' && lineText[nameEnd] != ' ' &&
                       lineText[nameEnd] != '\t' && lineText[nameEnd] != '/')
                    nameEnd++;

                if (nameEnd <= nameStart || nameEnd >= lineText.Length) continue;

                // Find closing '>' of the opening tag
                int closeGt = nameEnd;
                while (closeGt < lineText.Length && lineText[closeGt] != '>') closeGt++;
                if (closeGt >= lineText.Length) continue;
                if (lineText[closeGt - 1] == '/') continue; // skip self-closing tags <br/> <img />

                tagName = lineText[nameStart..nameEnd];
                openLine = l;
                openCol = c;
                openEnd = closeGt;
                goto foundOpenTag;
            }
        }
        return null;

        foundOpenTag:
        string closeTag = $"</{tagName}>";
        for (int l = openLine; l < Math.Min(buf.LineCount, openLine + 200); l++)
        {
            var lineText = buf.GetLine(l);
            int startC = (l == openLine) ? openEnd + 1 : 0;
            int idx = lineText.IndexOf(closeTag, startC, StringComparison.Ordinal);
            if (idx >= 0)
            {
                if (around)
                    return (new CursorPosition(openLine, openCol),
                            new CursorPosition(l, idx + closeTag.Length - 1));
                return InnerRange(buf, openLine, openEnd + 1, l, idx - 1);
            }
        }
        return null;
    }

    // Sentence text object (single-line approximation)
    public (CursorPosition Start, CursorPosition End)? GetSentenceRange(CursorPosition cursor, bool around)
    {
        var buf = _buf;
        int lineNo = cursor.Line;
        var line = buf.GetLine(lineNo);
        if (line.Length == 0) return null;

        int col = Math.Clamp(cursor.Column, 0, line.Length - 1);

        static bool IsSentenceTerminator(char c) => c is '.' or '!' or '?';

        // Find sentence start: go left past whitespace, then to after previous terminator
        int start = col;
        while (start > 0 && char.IsWhiteSpace(line[start])) start--;
        // Walk left until we hit a sentence terminator or BOL
        while (start > 0 && !IsSentenceTerminator(line[start - 1])) start--;
        // Skip leading whitespace after terminator
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;

        // Find sentence end: walk right to terminator or EOL
        int end = col;
        while (end < line.Length - 1 && !IsSentenceTerminator(line[end])) end++;
        // end is at terminator or last char

        if (around)
        {
            // Include trailing whitespace
            int te = end;
            while (te + 1 < line.Length && char.IsWhiteSpace(line[te + 1])) te++;
            end = te;
        }

        return (new CursorPosition(lineNo, start), new CursorPosition(lineNo, end));
    }

    // Paragraph text object (multi-line, blank-line delimited)
    public (CursorPosition Start, CursorPosition End)? GetParagraphRange(CursorPosition cursor, bool around)
    {
        var buf = _buf;
        int curLine = cursor.Line;

        bool IsBlank(int l) => string.IsNullOrWhiteSpace(buf.GetLine(l));

        if (IsBlank(curLine))
        {
            if (!around) return null;
            // ap on blank line: expand blank block then grab next paragraph
            int start = curLine;
            while (start > 0 && IsBlank(start - 1)) start--;
            int end = curLine;
            while (end + 1 < buf.LineCount && IsBlank(end + 1)) end++;
            // include next paragraph
            while (end + 1 < buf.LineCount && !IsBlank(end + 1)) end++;
            return (new CursorPosition(start, 0),
                    new CursorPosition(end, Math.Max(0, buf.GetLine(end).Length - 1)));
        }

        // Find paragraph boundaries (non-blank block containing cursor)
        int pStart = curLine;
        while (pStart > 0 && !IsBlank(pStart - 1)) pStart--;

        int pEnd = curLine;
        while (pEnd + 1 < buf.LineCount && !IsBlank(pEnd + 1)) pEnd++;

        if (around)
        {
            // Include trailing blank lines
            int end = pEnd;
            while (end + 1 < buf.LineCount && IsBlank(end + 1)) end++;
            return (new CursorPosition(pStart, 0),
                    new CursorPosition(end, Math.Max(0, buf.GetLine(end).Length - 1)));
        }

        return (new CursorPosition(pStart, 0),
                new CursorPosition(pEnd, Math.Max(0, buf.GetLine(pEnd).Length - 1)));
    }
}
