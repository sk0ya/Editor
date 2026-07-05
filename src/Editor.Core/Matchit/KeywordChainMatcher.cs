using Editor.Core.Buffer;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Matchit;

/// <summary>
/// Implements matchit-style keyword chain matching (e.g. if/elseif/else/end) for the `%`
/// motion. Multiple chains that end in the same closing keyword (e.g. Lua's if/end,
/// for/do/end, while/do/end and function/end all close on "end") are treated as one
/// shared-depth "group" so that nesting across different construct types is still
/// counted correctly — Lua/Ruby-style block languages are always properly nested, so a
/// generic opener(++)/closer(--) counter over the whole group lands on the right partner
/// regardless of which specific construct opened it.
/// </summary>
public static class KeywordChainMatcher
{
    public static Motion? Match(TextBuffer buffer, IMatchitLanguage language, CursorPosition cursor)
    {
        var word = WordAt(buffer, cursor, language.ExtraWordChars, out int wordStart, out int wordEnd);
        if (word == null) return null;

        var chains = language.KeywordChains;

        foreach (var chain in chains)
        {
            int slot = FindSlot(chain, word);
            if (slot < 0) continue;

            var group = chains.Where(c => SharesCloser(c, chain)).ToArray();
            var cursorWord = new WordSpan(cursor.Line, wordStart, wordEnd);

            Motion? result = slot == 0
                ? ScanOpenerToCloser(buffer, cursorWord, group, language.ExtraWordChars)
                : slot == chain.Length - 1
                    ? ScanCloserToOpener(buffer, cursorWord, group, language.ExtraWordChars)
                    : ScanMiddleToNextSibling(buffer, cursorWord, chain, group, language.ExtraWordChars);

            if (result != null) return result;
        }

        return null;
    }

    private static int FindSlot(string[][] chain, string word)
    {
        for (int i = 0; i < chain.Length; i++)
            if (Array.IndexOf(chain[i], word) >= 0) return i;
        return -1;
    }

    private static bool SharesCloser(string[][] a, string[][] b) => a[^1].Intersect(b[^1]).Any();

    private static bool IsGroupOpener(IReadOnlyList<string[][]> group, string word) =>
        group.Any(c => Array.IndexOf(c[0], word) >= 0);

    private static bool IsGroupCloser(IReadOnlyList<string[][]> group, string word) =>
        group.Any(c => Array.IndexOf(c[^1], word) >= 0);

    private static bool IsChainMiddle(string[][] chain, string word)
    {
        for (int i = 1; i < chain.Length - 1; i++)
            if (Array.IndexOf(chain[i], word) >= 0) return true;
        return false;
    }

    // Opener → closer (forward) and closer → opener (backward) share the same
    // open(++)/close(--) rule; only the scan direction differs. Mirrors MotionEngine.MatchBracket.
    private static Motion? ScanOpenerToCloser(TextBuffer buffer, WordSpan cursorWord, IReadOnlyList<string[][]> group, char[] extra)
    {
        int depth = 0;
        foreach (var tok in WordsForward(buffer, cursorWord.Line, cursorWord.Start, extra))
        {
            if (IsGroupOpener(group, tok.Word)) depth++;
            else if (IsGroupCloser(group, tok.Word)) depth--;
            if (depth == 0) return new Motion(new CursorPosition(tok.Line, tok.Start), MotionType.Inclusive);
        }
        return null;
    }

    private static Motion? ScanCloserToOpener(TextBuffer buffer, WordSpan cursorWord, IReadOnlyList<string[][]> group, char[] extra)
    {
        int depth = 0;
        foreach (var tok in WordsBackward(buffer, cursorWord.Line, cursorWord.End, extra))
        {
            if (IsGroupOpener(group, tok.Word)) depth++;
            else if (IsGroupCloser(group, tok.Word)) depth--;
            if (depth == 0) return new Motion(new CursorPosition(tok.Line, tok.Start), MotionType.Inclusive);
        }
        return null;
    }

    private static Motion? ScanMiddleToNextSibling(TextBuffer buffer, WordSpan cursorWord, string[][] chain, IReadOnlyList<string[][]> group, char[] extra)
    {
        int depth = 0;
        // Skip the current word itself — we want the *next* sibling, not this one.
        foreach (var tok in WordsForward(buffer, cursorWord.Line, cursorWord.End + 1, extra))
        {
            if (IsGroupOpener(group, tok.Word)) { depth++; continue; }
            if (IsGroupCloser(group, tok.Word))
            {
                if (depth == 0) return new Motion(new CursorPosition(tok.Line, tok.Start), MotionType.Inclusive);
                depth--;
                continue;
            }
            if (depth == 0 && IsChainMiddle(chain, tok.Word))
                return new Motion(new CursorPosition(tok.Line, tok.Start), MotionType.Inclusive);
        }
        return null;
    }

    private readonly record struct WordSpan(int Line, int Start, int End);
    private readonly record struct WordToken(int Line, int Start, string Word);

    private static bool IsWordChar(char c, char[] extra) => char.IsLetterOrDigit(c) || c == '_' || Array.IndexOf(extra, c) >= 0;

    private static string? WordAt(TextBuffer buffer, CursorPosition cursor, char[] extra, out int start, out int end)
    {
        var line = buffer.GetLine(cursor.Line);
        start = end = -1;
        if (cursor.Column < 0 || cursor.Column >= line.Length || !IsWordChar(line[cursor.Column], extra)) return null;

        start = cursor.Column;
        end = cursor.Column;
        while (start > 0 && IsWordChar(line[start - 1], extra)) start--;
        while (end < line.Length - 1 && IsWordChar(line[end + 1], extra)) end++;
        return line[start..(end + 1)];
    }

    // Enumerates word tokens starting at (fromLine, fromCol) INCLUSIVE (i.e. it includes the
    // word the cursor sits on, which callers rely on to seed depth==1/-1 like MatchBracket).
    private static IEnumerable<WordToken> WordsForward(TextBuffer buffer, int fromLine, int fromCol, char[] extra)
    {
        for (int l = fromLine; l < buffer.LineCount; l++)
        {
            var line = buffer.GetLine(l);
            int i = l == fromLine ? fromCol : 0;
            while (i < line.Length)
            {
                if (IsWordChar(line[i], extra))
                {
                    int s = i;
                    while (i < line.Length && IsWordChar(line[i], extra)) i++;
                    yield return new WordToken(l, s, line[s..i]);
                }
                else i++;
            }
        }
    }

    // Enumerates word tokens backward ending at (fromLine, fromCol) INCLUSIVE, walking toward
    // the start of the buffer.
    private static IEnumerable<WordToken> WordsBackward(TextBuffer buffer, int fromLine, int fromCol, char[] extra)
    {
        for (int l = fromLine; l >= 0; l--)
        {
            var line = buffer.GetLine(l);
            int i = l == fromLine ? fromCol : line.Length - 1;
            while (i >= 0)
            {
                if (IsWordChar(line[i], extra))
                {
                    int e = i;
                    while (i >= 0 && IsWordChar(line[i], extra)) i--;
                    // word spans (i+1 .. e); yield its start column
                    yield return new WordToken(l, i + 1, line[(i + 1)..(e + 1)]);
                }
                else i--;
            }
        }
    }
}
