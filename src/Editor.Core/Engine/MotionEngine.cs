using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Engine;

public enum MotionType { Exclusive, Inclusive, Linewise }

public record struct Motion(CursorPosition Target, MotionType Type, bool LinewiseForced = false);

public class MotionEngine
{
    private readonly TextBuffer _buffer;

    public MotionEngine(TextBuffer buffer) => _buffer = buffer;

    public Motion? Calculate(string motion, CursorPosition cursor, int count = 1)
    {
        return motion switch
        {
            "h" => MoveH(cursor, -count),
            "l" => MoveH(cursor, count),
            "j" => MoveV(cursor, count),
            "k" => MoveV(cursor, -count),
            "0" => new Motion(cursor with { Column = 0 }, MotionType.Exclusive),
            "^" => FirstNonBlank(cursor),
            "$" => EndOfLine(cursor, count - 1),
            "w" => WordForward(cursor, count, false),
            "W" => WordForward(cursor, count, true),
            "b" => WordBackward(cursor, count, false),
            "B" => WordBackward(cursor, count, true),
            "e" => WordEnd(cursor, count, false),
            "E" => WordEnd(cursor, count, true),
            "gg" => new Motion(new CursorPosition(0, 0), MotionType.Linewise),
            "G" => new Motion(new CursorPosition(_buffer.LineCount - 1, 0), MotionType.Linewise),
            "{" => ParagraphBackward(cursor),
            "}" => ParagraphForward(cursor),
            "(" => SentenceBackward(cursor, count),
            ")" => SentenceForward(cursor, count),
            "%" => MatchBracket(cursor),
            "H" => ScreenTop(cursor),
            "M" => ScreenMiddle(cursor),
            "L" => ScreenBottom(cursor),
            "g_" => LastNonBlank(cursor),
            _ => null
        };
    }

    public CursorPosition MoveLeft(CursorPosition cursor, int count = 1)
    {
        var col = Math.Max(0, cursor.Column - count);
        return cursor with { Column = col };
    }

    public CursorPosition MoveRight(CursorPosition cursor, int count = 1, bool insertMode = false)
    {
        var line = _buffer.GetLine(cursor.Line);
        var maxCol = insertMode ? line.Length : Math.Max(0, line.Length - 1);
        var col = Math.Min(maxCol, cursor.Column + count);
        return cursor with { Column = col };
    }

    public CursorPosition MoveDown(CursorPosition cursor, int count = 1)
    {
        var line = Math.Min(_buffer.LineCount - 1, cursor.Line + count);
        var col = Math.Min(cursor.Column, Math.Max(0, _buffer.GetLineLength(line) - 1));
        return new CursorPosition(line, col);
    }

    public CursorPosition MoveUp(CursorPosition cursor, int count = 1)
    {
        var line = Math.Max(0, cursor.Line - count);
        var col = Math.Min(cursor.Column, Math.Max(0, _buffer.GetLineLength(line) - 1));
        return new CursorPosition(line, col);
    }

    private Motion MoveH(CursorPosition cursor, int delta)
    {
        var col = Math.Clamp(cursor.Column + delta, 0, Math.Max(0, _buffer.GetLineLength(cursor.Line) - 1));
        return new Motion(cursor with { Column = col }, MotionType.Exclusive);
    }

    private Motion MoveV(CursorPosition cursor, int delta)
    {
        var line = Math.Clamp(cursor.Line + delta, 0, _buffer.LineCount - 1);
        var col = Math.Min(cursor.Column, Math.Max(0, _buffer.GetLineLength(line) - 1));
        return new Motion(new CursorPosition(line, col), MotionType.Linewise);
    }

    private Motion FirstNonBlank(CursorPosition cursor)
    {
        var line = _buffer.GetLine(cursor.Line);
        int col = 0;
        while (col < line.Length && char.IsWhiteSpace(line[col])) col++;
        return new Motion(cursor with { Column = col }, MotionType.Exclusive);
    }

    private Motion LastNonBlank(CursorPosition cursor)
    {
        var line = _buffer.GetLine(cursor.Line);
        int col = line.Length - 1;
        while (col > 0 && char.IsWhiteSpace(line[col])) col--;
        return new Motion(cursor with { Column = Math.Max(0, col) }, MotionType.Inclusive);
    }

    private Motion EndOfLine(CursorPosition cursor, int lineOffset)
    {
        var lineNum = Math.Min(_buffer.LineCount - 1, cursor.Line + lineOffset);
        var lineLen = _buffer.GetLineLength(lineNum);
        var col = Math.Max(0, lineLen - 1);
        return new Motion(new CursorPosition(lineNum, col), MotionType.Inclusive);
    }

    private Motion WordForward(CursorPosition cursor, int count, bool WORD)
    {
        var pos = cursor;
        for (int c = 0; c < count; c++)
        {
            var line = _buffer.GetLine(pos.Line);
            int col = pos.Column;

            if (col >= line.Length)
            {
                if (pos.Line < _buffer.LineCount - 1)
                { pos = new CursorPosition(pos.Line + 1, 0); continue; }
                break;
            }

            bool inWord = WORD ? !char.IsWhiteSpace(line[col]) : IsWordChar(line[col]);

            // Skip current word/WORD
            if (inWord)
            {
                while (col < line.Length && (WORD ? !char.IsWhiteSpace(line[col]) : IsWordChar(line[col])))
                    col++;
            }
            else if (!char.IsWhiteSpace(line[col]))
            {
                while (col < line.Length && !IsWordChar(line[col]) && !char.IsWhiteSpace(line[col]))
                    col++;
            }

            // Skip whitespace
            while (col < line.Length && char.IsWhiteSpace(line[col])) col++;

            if (col >= line.Length && pos.Line < _buffer.LineCount - 1)
                pos = new CursorPosition(pos.Line + 1, 0);
            else
                pos = pos with { Column = Math.Min(col, Math.Max(0, line.Length - 1)) };
        }
        return new Motion(pos, MotionType.Exclusive);
    }

    private Motion WordBackward(CursorPosition cursor, int count, bool WORD)
    {
        var pos = cursor;
        for (int c = 0; c < count; c++)
        {
            var line = _buffer.GetLine(pos.Line);
            int col = pos.Column;

            if (col == 0)
            {
                if (pos.Line > 0)
                {
                    var prevLine = _buffer.GetLine(pos.Line - 1);
                    pos = new CursorPosition(pos.Line - 1, Math.Max(0, prevLine.Length - 1));
                    continue;
                }
                break;
            }

            col--;
            while (col > 0 && char.IsWhiteSpace(line[col])) col--;

            if (WORD)
            {
                while (col > 0 && !char.IsWhiteSpace(line[col - 1])) col--;
            }
            else
            {
                bool inWord = IsWordChar(line[col]);
                while (col > 0 && IsWordChar(line[col - 1]) == inWord) col--;
            }

            pos = pos with { Column = col };
        }
        return new Motion(pos, MotionType.Exclusive);
    }

    private Motion WordEnd(CursorPosition cursor, int count, bool WORD)
    {
        var pos = cursor;
        for (int c = 0; c < count; c++)
        {
            var line = _buffer.GetLine(pos.Line);
            int col = pos.Column + 1;

            if (col >= line.Length)
            {
                if (pos.Line < _buffer.LineCount - 1)
                { pos = new CursorPosition(pos.Line + 1, 0); col = 1; line = _buffer.GetLine(pos.Line); }
                else break;
            }

            while (col < line.Length && char.IsWhiteSpace(line[col])) col++;

            if (WORD)
                while (col < line.Length - 1 && !char.IsWhiteSpace(line[col + 1])) col++;
            else
            {
                bool inWord = IsWordChar(line[col]);
                while (col < line.Length - 1 && IsWordChar(line[col + 1]) == inWord) col++;
            }

            pos = pos with { Column = Math.Min(col, line.Length - 1) };
        }
        return new Motion(pos, MotionType.Inclusive);
    }

    private Motion ParagraphForward(CursorPosition cursor)
    {
        int line = cursor.Line + 1;
        while (line < _buffer.LineCount && !string.IsNullOrWhiteSpace(_buffer.GetLine(line)))
            line++;
        line = Math.Min(line, _buffer.LineCount - 1);
        return new Motion(new CursorPosition(line, 0), MotionType.Linewise);
    }

    private Motion ParagraphBackward(CursorPosition cursor)
    {
        int line = cursor.Line - 1;
        while (line > 0 && !string.IsNullOrWhiteSpace(_buffer.GetLine(line)))
            line--;
        return new Motion(new CursorPosition(Math.Max(0, line), 0), MotionType.Linewise);
    }

    private Motion SentenceForward(CursorPosition cursor, int count)
    {
        int line = cursor.Line;
        int col = cursor.Column;

        for (int c = 0; c < count; c++)
        {
            bool moved = false;

            // Advance one position to avoid being stuck on current sentence end
            col++;
            if (col >= _buffer.GetLineLength(line))
            {
                line++;
                col = 0;
                if (line >= _buffer.LineCount)
                {
                    line = _buffer.LineCount - 1;
                    col = Math.Max(0, _buffer.GetLineLength(line) - 1);
                    break;
                }
            }

            while (line < _buffer.LineCount)
            {
                var ln = _buffer.GetLine(line);

                // Empty line = paragraph boundary = sentence start
                if (string.IsNullOrWhiteSpace(ln))
                {
                    while (line < _buffer.LineCount && string.IsNullOrWhiteSpace(_buffer.GetLine(line)))
                        line++;
                    col = 0;
                    moved = true;
                    break;
                }

                // Scan for sentence-ending punctuation followed by whitespace/end
                while (col < ln.Length)
                {
                    if (ln[col] is '.' or '!' or '?')
                    {
                        int next = col + 1;
                        while (next < ln.Length && ln[next] is ')' or ']' or '"' or '\'')
                            next++;

                        if (next >= ln.Length)
                        {
                            // End of line — next sentence starts at first char of next non-blank line
                            (line, col) = NextSentenceContent(line);
                            moved = true;
                            break;
                        }
                        else if (char.IsWhiteSpace(ln[next]))
                        {
                            // Skip whitespace; if it reaches EOL, jump to next non-blank line
                            int nextCol = next;
                            while (nextCol < ln.Length && char.IsWhiteSpace(ln[nextCol])) nextCol++;
                            if (nextCol < ln.Length)
                                col = nextCol;
                            else
                                (line, col) = NextSentenceContent(line);
                            moved = true;
                            break;
                        }
                    }
                    col++;
                }

                if (moved) break;

                line++;
                col = 0;
                if (line >= _buffer.LineCount)
                {
                    line = _buffer.LineCount - 1;
                    col = Math.Max(0, _buffer.GetLineLength(line) - 1);
                    break;
                }
            }
        }

        line = Math.Clamp(line, 0, _buffer.LineCount - 1);
        col = Math.Clamp(col, 0, Math.Max(0, _buffer.GetLineLength(line) - 1));
        return new Motion(new CursorPosition(line, col), MotionType.Exclusive);
    }

    // Find the first non-whitespace character on the next non-blank line after `afterLine`.
    private (int line, int col) NextSentenceContent(int afterLine)
    {
        int nextLine = afterLine + 1;
        while (nextLine < _buffer.LineCount && string.IsNullOrWhiteSpace(_buffer.GetLine(nextLine)))
            nextLine++;
        if (nextLine < _buffer.LineCount)
        {
            var text = _buffer.GetLine(nextLine);
            int col = 0;
            while (col < text.Length && char.IsWhiteSpace(text[col])) col++;
            return (nextLine, col);
        }
        int lastLine = _buffer.LineCount - 1;
        return (lastLine, Math.Max(0, _buffer.GetLineLength(lastLine) - 1));
    }

    // Given sentence-ending punctuation on `searchLine` with the character after punct at `afterPunctCol`,
    // return the position of the start of the next sentence.
    private (int line, int col) SentenceStartAfterPunct(int searchLine, int afterPunctCol)
    {
        var text = _buffer.GetLine(searchLine);
        int startCol = afterPunctCol;
        while (startCol < text.Length && char.IsWhiteSpace(text[startCol])) startCol++;
        return startCol < text.Length ? (searchLine, startCol) : NextSentenceContent(searchLine);
    }

    private Motion SentenceBackward(CursorPosition cursor, int count)
    {
        int line = cursor.Line;
        int col = cursor.Column;

        for (int c = 0; c < count; c++)
        {
            col--;
            if (col < 0)
            {
                line--;
                if (line < 0) { line = 0; col = 0; break; }
                col = Math.Max(0, _buffer.GetLineLength(line) - 1);
            }
            (line, col) = FindPrevSentenceStart(line, col, cursor);
        }

        line = Math.Clamp(line, 0, _buffer.LineCount - 1);
        col = Math.Clamp(col, 0, Math.Max(0, _buffer.GetLineLength(line) - 1));
        return new Motion(new CursorPosition(line, col), MotionType.Exclusive);
    }

    private (int line, int col) FindPrevSentenceStart(int line, int col, CursorPosition origin)
    {
        // Skip whitespace backward to land on a non-whitespace character
        var ln = _buffer.GetLine(line);
        while (col >= 0 && (col >= ln.Length || char.IsWhiteSpace(ln[col])))
        {
            col--;
            if (col < 0)
            {
                line--;
                if (line < 0) return (0, 0);
                ln = _buffer.GetLine(line);
                col = ln.Length - 1;
                if (string.IsNullOrWhiteSpace(ln))
                {
                    while (line > 0 && string.IsNullOrWhiteSpace(_buffer.GetLine(line - 1)))
                        line--;
                    return (line, 0);
                }
            }
        }

        // Scan backward for sentence-ending punctuation
        int searchLine = line;
        int searchCol = col;

        while (searchLine >= 0)
        {
            var sln = _buffer.GetLine(searchLine);

            if (string.IsNullOrWhiteSpace(sln))
            {
                // Blank line: sentence starts at first non-whitespace of next non-blank line
                return NextSentenceContent(searchLine - 1);
            }

            bool continueSearch = false;
            for (int i = searchCol; i >= 0; i--)
            {
                if (sln[i] is not ('.' or '!' or '?')) continue;

                int next = i + 1;
                while (next < sln.Length && sln[next] is ')' or ']' or '"' or '\'') next++;

                if (next >= sln.Length || char.IsWhiteSpace(sln[next]))
                {
                    var (startLine, startCol) = SentenceStartAfterPunct(searchLine, next);
                    if (startLine == origin.Line && startCol == origin.Column)
                    {
                        // Same as origin — keep searching further back
                        searchCol = i - 1;
                        if (searchCol < 0)
                        {
                            searchLine--;
                            if (searchLine < 0) return (0, 0);
                            searchCol = Math.Max(0, _buffer.GetLineLength(searchLine) - 1);
                        }
                        continueSearch = true;
                        break;
                    }
                    return (startLine, startCol);
                }
            }
            if (continueSearch) continue;

            // No punctuation on this line — go to previous line
            searchLine--;
            if (searchLine >= 0 && string.IsNullOrWhiteSpace(_buffer.GetLine(searchLine)))
            {
                // Blank line boundary: sentence starts at first char of current block
                var text = _buffer.GetLine(searchLine + 1);
                int nextCol = 0;
                while (nextCol < text.Length && char.IsWhiteSpace(text[nextCol])) nextCol++;
                return (searchLine + 1, nextCol);
            }
            searchCol = searchLine >= 0 ? Math.Max(0, _buffer.GetLineLength(searchLine) - 1) : 0;
        }

        return (0, 0);
    }

    private Motion? MatchBracket(CursorPosition cursor)
    {
        var line = _buffer.GetLine(cursor.Line);
        if (cursor.Column >= line.Length) return null;

        char ch = line[cursor.Column];
        char? open = ch switch { '(' => '(', '[' => '[', '{' => '{', ')' => '(', ']' => '[', '}' => '{', _ => null };
        if (open == null) return null;

        char close = open switch { '(' => ')', '[' => ']', '{' => '}', _ => ' ' };
        bool forward = ch == open;

        int depth = 0;
        int l = cursor.Line, c = cursor.Column;

        while (l >= 0 && l < _buffer.LineCount)
        {
            var ln = _buffer.GetLine(l);
            int start = l == cursor.Line ? c : (forward ? 0 : ln.Length - 1);
            int end = forward ? ln.Length : -1;
            int step = forward ? 1 : -1;

            for (int i = start; forward ? i < end : i > end; i += step)
            {
                if (ln[i] == open) depth++;
                else if (ln[i] == close) depth--;
                if (depth == 0) return new Motion(new CursorPosition(l, i), MotionType.Inclusive);
            }
            l += forward ? 1 : -1;
        }
        return null;
    }

    private Motion ScreenTop(CursorPosition cursor) =>
        new(new CursorPosition(Math.Max(0, cursor.Line - 10), 0), MotionType.Linewise);
    private Motion ScreenMiddle(CursorPosition cursor) =>
        new(cursor, MotionType.Linewise);
    private Motion ScreenBottom(CursorPosition cursor) =>
        new(new CursorPosition(Math.Min(_buffer.LineCount - 1, cursor.Line + 10), 0), MotionType.Linewise);

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public CursorPosition FindChar(CursorPosition cursor, char target, bool forward, bool before, int count = 1)
    {
        var line = _buffer.GetLine(cursor.Line);
        int col = cursor.Column;
        int found = 0;

        if (forward)
        {
            for (int i = col + 1; i < line.Length; i++)
            {
                if (line[i] == target)
                {
                    found++;
                    if (found == count) return cursor with { Column = before ? i - 1 : i };
                }
            }
        }
        else
        {
            for (int i = col - 1; i >= 0; i--)
            {
                if (line[i] == target)
                {
                    found++;
                    if (found == count) return cursor with { Column = before ? i + 1 : i };
                }
            }
        }
        return cursor;
    }
}
