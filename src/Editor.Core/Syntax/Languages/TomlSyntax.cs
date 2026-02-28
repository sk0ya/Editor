namespace Editor.Core.Syntax.Languages;

public class TomlSyntax : ISyntaxLanguage
{
    public string Name => "TOML";
    public string[] Extensions => [".toml"];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inMultiLineDouble = false;
        bool inMultiLineSingle = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inMultiLineDouble, ref inMultiLineSingle);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inMultiLineDouble, ref bool inMultiLineSingle)
    {
        int i = 0, len = line.Length;

        // Multi-line string continuation
        if (inMultiLineDouble || inMultiLineSingle)
        {
            string closing = inMultiLineDouble ? "\"\"\"" : "'''";
            int end = line.IndexOf(closing, i, StringComparison.Ordinal);
            if (end >= 0)
            {
                tokens.Add(new SyntaxToken(0, end + 3, TokenKind.String));
                i = end + 3;
                inMultiLineDouble = inMultiLineSingle = false;
            }
            else
            {
                tokens.Add(new SyntaxToken(0, len, TokenKind.String));
                return;
            }
        }

        // Skip leading whitespace
        while (i < len && char.IsWhiteSpace(line[i])) i++;
        if (i >= len) return;

        // Section header: [table] or [[array]]
        if (line[i] == '[')
        {
            string marker = i + 1 < len && line[i + 1] == '[' ? "]]" : "]";
            int close = line.IndexOf(marker, i + marker.Length, StringComparison.Ordinal);
            int headerEnd = close >= 0 ? close + marker.Length : len;
            tokens.Add(new SyntaxToken(i, headerEnd - i, TokenKind.Keyword));
            i = headerEnd;
            // Fall through to handle inline comment
        }

        while (i < len)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }

            // Comment
            if (line[i] == '#')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // Multi-line basic string: """
            if (i + 2 < len && line[i] == '"' && line[i + 1] == '"' && line[i + 2] == '"')
            {
                int start = i; i += 3;
                int end = line.IndexOf("\"\"\"", i, StringComparison.Ordinal);
                if (end >= 0) { i = end + 3; }
                else { inMultiLineDouble = true; i = len; }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Multi-line literal string: '''
            if (i + 2 < len && line[i] == '\'' && line[i + 1] == '\'' && line[i + 2] == '\'')
            {
                int start = i; i += 3;
                int end = line.IndexOf("'''", i, StringComparison.Ordinal);
                if (end >= 0) { i = end + 3; }
                else { inMultiLineSingle = true; i = len; }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // String
            if (line[i] == '"' || line[i] == '\'')
            {
                char q = line[i];
                int start = i++;
                while (i < len && line[i] != q) { if (line[i] == '\\' && q == '"') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number (including dates: 2023-01-01, times: 12:00:00)
            if (char.IsDigit(line[i]) ||
                ((line[i] == '-' || line[i] == '+') && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == '_' ||
                                   line[i] == '-' || line[i] == ':' || line[i] == 'T' ||
                                   line[i] == 'Z' || line[i] == 'e' || line[i] == 'E' ||
                                   line[i] == '+' || line[i] == 'x' || line[i] == 'o' || line[i] == 'b'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier (key or boolean)
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '-')) i++;
                var word = line[start..i];

                TokenKind kind;
                if (word is "true" or "false" or "nan" or "inf")
                    kind = TokenKind.Keyword;
                else
                {
                    int j = i;
                    while (j < len && line[j] == ' ') j++;
                    kind = (j < len && line[j] == '=') ? TokenKind.Identifier : TokenKind.Text;
                }
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
