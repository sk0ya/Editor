namespace Editor.Core.Syntax.Languages;

public class LuaSyntax : ISyntaxLanguage
{
    public string Name => "Lua";
    public string[] Extensions => [".lua"];
    public string? LineCommentPrefix => "--";
    public string? BlockCommentPrefix => "--[[";
    public string? BlockCommentSuffix => "]]";

    private static readonly HashSet<string> Keywords =
    [
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function",
        "goto", "if", "in", "local", "nil", "not", "or", "repeat", "return",
        "then", "true", "until", "while",
    ];

    private static readonly HashSet<string> Builtins =
    [
        "string", "table", "math", "io", "os", "coroutine", "pairs", "ipairs",
        "print", "require", "pcall", "xpcall", "type", "tostring", "tonumber",
        "setmetatable", "getmetatable", "rawget", "rawset", "rawequal", "select",
        "unpack", "assert", "error", "self",
    ];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        int longStringLevel = -1;
        int longCommentLevel = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref longStringLevel, ref longCommentLevel);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens, ref int longStringLevel, ref int longCommentLevel)
    {
        int i = 0, len = line.Length;

        if (longCommentLevel >= 0)
        {
            int close = FindLongBracketClose(line, i, longCommentLevel);
            if (close >= 0) { i = close + CloseLength(longCommentLevel); tokens.Add(new SyntaxToken(0, i, TokenKind.Comment)); longCommentLevel = -1; }
            else { if (len > 0) tokens.Add(new SyntaxToken(0, len, TokenKind.Comment)); return; }
        }

        if (longStringLevel >= 0)
        {
            int close = FindLongBracketClose(line, i, longStringLevel);
            if (close >= 0) { i = close + CloseLength(longStringLevel); tokens.Add(new SyntaxToken(0, i, TokenKind.String)); longStringLevel = -1; }
            else { if (len > 0) tokens.Add(new SyntaxToken(0, len, TokenKind.String)); return; }
        }

        while (i < len)
        {
            char c = line[i];

            // Line comment or long comment --[[ ... ]]
            if (c == '-' && i + 1 < len && line[i + 1] == '-')
            {
                int level = TryMatchLongBracketOpen(line, i + 2);
                if (level >= 0)
                {
                    int start = i;
                    int searchFrom = i + 4 + level;
                    int close = FindLongBracketClose(line, searchFrom, level);
                    if (close >= 0) { i = close + CloseLength(level); tokens.Add(new SyntaxToken(start, i - start, TokenKind.Comment)); }
                    else { tokens.Add(new SyntaxToken(start, len - start, TokenKind.Comment)); longCommentLevel = level; i = len; }
                }
                else
                {
                    tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                    i = len;
                }
                continue;
            }

            // Long string [[ ... ]] / [=[ ... ]=]
            if (c == '[')
            {
                int level = TryMatchLongBracketOpen(line, i);
                if (level >= 0)
                {
                    int start = i;
                    int close = FindLongBracketClose(line, i + 2 + level, level);
                    if (close >= 0) { i = close + CloseLength(level); tokens.Add(new SyntaxToken(start, i - start, TokenKind.String)); }
                    else { tokens.Add(new SyntaxToken(start, len - start, TokenKind.String)); longStringLevel = level; i = len; }
                    continue;
                }
            }

            // Regular string
            if (c == '"' || c == '\'')
            {
                int start = i++;
                while (i < len && line[i] != c) { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier/keyword/builtin
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword
                    : Builtins.Contains(word) ? TokenKind.Type
                    : SyntaxHeuristics.ClassifyIdentifier(line, start, i);
                tokens.Add(new SyntaxToken(start, word.Length, kind));
                continue;
            }

            i++;
        }
    }

    // Matches "[" ("=" * n) "[" starting at pos; returns level n, or -1 if no match.
    private static int TryMatchLongBracketOpen(string line, int pos)
    {
        if (pos >= line.Length || line[pos] != '[') return -1;
        int j = pos + 1, level = 0;
        while (j < line.Length && line[j] == '=') { level++; j++; }
        if (j < line.Length && line[j] == '[') return level;
        return -1;
    }

    private static int CloseLength(int level) => 2 + level;

    // Finds "]" ("=" * level) "]" starting search at pos; returns the start index of "]", or -1.
    private static int FindLongBracketClose(string line, int pos, int level)
    {
        if (pos < 0 || pos > line.Length) return -1;
        var closer = "]" + new string('=', level) + "]";
        return line.IndexOf(closer, pos, StringComparison.Ordinal);
    }
}
