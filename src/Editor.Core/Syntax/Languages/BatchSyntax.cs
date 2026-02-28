namespace Editor.Core.Syntax.Languages;

public class BatchSyntax : ISyntaxLanguage
{
    public string Name => "Batch";
    public string[] Extensions => [".bat", ".cmd"];

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "echo", "set", "if", "else", "for", "goto", "call", "exit", "pause",
        "start", "cd", "dir", "md", "mkdir", "rd", "rmdir", "copy", "move",
        "del", "ren", "rename", "type", "cls", "color", "title", "prompt",
        "pushd", "popd", "shift", "setlocal", "endlocal", "defined", "not",
        "exist", "equ", "neq", "lss", "leq", "gtr", "geq", "do", "in",
        "errorlevel",
    };

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens)
    {
        int i = 0, len = line.Length;

        // Skip leading @
        if (i < len && line[i] == '@')
        {
            tokens.Add(new SyntaxToken(i, 1, TokenKind.Operator));
            i++;
        }

        // Skip whitespace
        while (i < len && line[i] == ' ') i++;
        if (i >= len) return;

        // :: comment
        if (i + 1 < len && line[i] == ':' && line[i + 1] == ':')
        {
            tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
            return;
        }

        // Label: :labelname
        if (line[i] == ':')
        {
            tokens.Add(new SyntaxToken(i, len - i, TokenKind.Type));
            return;
        }

        // REM at line start
        if (i + 2 < len &&
            char.ToUpperInvariant(line[i]) == 'R' &&
            char.ToUpperInvariant(line[i + 1]) == 'E' &&
            char.ToUpperInvariant(line[i + 2]) == 'M' &&
            (i + 3 >= len || !char.IsLetterOrDigit(line[i + 3])))
        {
            tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
            return;
        }

        while (i < len)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }

            // %variable% or %1 %2 (positional)
            if (line[i] == '%')
            {
                int start = i++;
                if (i < len && char.IsDigit(line[i]))
                {
                    i++;
                    tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                }
                else
                {
                    int end = line.IndexOf('%', i);
                    i = end >= 0 ? end + 1 : len;
                    tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                }
                continue;
            }

            // !variable! (delayed expansion)
            if (line[i] == '!')
            {
                int start = i++;
                int end = line.IndexOf('!', i);
                if (end >= 0)
                {
                    i = end + 1;
                    tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                }
                else
                {
                    i = start + 1; // lone !, skip
                }
                continue;
            }

            // String
            if (line[i] == '"')
            {
                int start = i++;
                while (i < len && line[i] != '"') i++;
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]))
            {
                int start = i;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'x')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword (also handles inline REM)
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];

                if (string.Equals(word, "rem", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add(new SyntaxToken(start, len - start, TokenKind.Comment));
                    break;
                }

                var kind = Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
