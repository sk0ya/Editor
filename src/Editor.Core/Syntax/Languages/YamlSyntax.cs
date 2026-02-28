namespace Editor.Core.Syntax.Languages;

public class YamlSyntax : ISyntaxLanguage
{
    public string Name => "YAML";
    public string[] Extensions => [".yml", ".yaml"];

    private static readonly HashSet<string> BoolNull = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "on", "off", "null", "~",
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

        // Skip indentation
        while (i < len && line[i] == ' ') i++;
        if (i >= len) return;

        // Document markers: --- or ...
        if (i + 2 < len && ((line[i] == '-' && line[i + 1] == '-' && line[i + 2] == '-') ||
                             (line[i] == '.' && line[i + 1] == '.' && line[i + 2] == '.')))
        {
            tokens.Add(new SyntaxToken(i, len - i, TokenKind.Operator));
            return;
        }

        // List item marker: - (not ---)
        if (line[i] == '-' && (i + 1 >= len || line[i + 1] == ' '))
        {
            tokens.Add(new SyntaxToken(i, 1, TokenKind.Type));
            i++;
            while (i < len && line[i] == ' ') i++;
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

            // Anchor: &name
            if (line[i] == '&')
            {
                int start = i++;
                while (i < len && !char.IsWhiteSpace(line[i])) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                continue;
            }

            // Alias: *name
            if (line[i] == '*')
            {
                int start = i++;
                while (i < len && !char.IsWhiteSpace(line[i])) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                continue;
            }

            // Tag: !!type or !tag
            if (line[i] == '!')
            {
                int start = i++;
                if (i < len && line[i] == '!') i++;
                while (i < len && !char.IsWhiteSpace(line[i])) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                continue;
            }

            // Block scalar indicators: | >
            if ((line[i] == '|' || line[i] == '>') && (i + 1 >= len || char.IsWhiteSpace(line[i + 1])))
            {
                tokens.Add(new SyntaxToken(i, 1, TokenKind.Operator));
                i++;
                continue;
            }

            // Quoted string (key or value)
            if (line[i] == '"' || line[i] == '\'')
            {
                char q = line[i];
                int start = i++;
                while (i < len && line[i] != q) { if (line[i] == '\\' && q == '"') i++; i++; }
                if (i < len) i++;
                int end = i;

                // Check if key (followed by ':')
                while (i < len && line[i] == ' ') i++;
                bool isKey = i < len && line[i] == ':' && (i + 1 >= len || line[i + 1] == ' ' || i + 1 == len);
                tokens.Add(new SyntaxToken(start, end - start, isKey ? TokenKind.Identifier : TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]) ||
                ((line[i] == '-' || line[i] == '+') && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_' ||
                                   line[i] == '-' || line[i] == ':' || line[i] == '+'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Bare identifier (key or keyword/value)
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                // Consume until ':' (with space/eol) or end of meaningful content
                while (i < len && line[i] != ':' && line[i] != '#') i++;

                int wordEnd = i;
                // Trim trailing spaces from word
                while (wordEnd > start && line[wordEnd - 1] == ' ') wordEnd--;

                // Check if this is a key: followed by ':' then space or eol
                bool isKey = i < len && line[i] == ':' && (i + 1 >= len || line[i + 1] == ' ');

                var word = line[start..wordEnd];
                TokenKind kind;
                if (isKey)
                    kind = TokenKind.Identifier;
                else if (BoolNull.Contains(word.Trim()))
                    kind = TokenKind.Keyword;
                else
                    kind = TokenKind.Text;

                if (wordEnd > start)
                    tokens.Add(new SyntaxToken(start, wordEnd - start, kind));
                continue;
            }

            i++;
        }
    }
}
