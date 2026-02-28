namespace Editor.Core.Syntax.Languages;

public class GoSyntax : ISyntaxLanguage
{
    public string Name => "Go";
    public string[] Extensions => [".go"];

    private static readonly HashSet<string> Keywords =
    [
        "break", "case", "chan", "const", "continue", "default", "defer",
        "else", "fallthrough", "for", "func", "go", "goto", "if", "import",
        "interface", "map", "package", "range", "return", "select", "struct",
        "switch", "type", "var",
    ];

    private static readonly HashSet<string> Types =
    [
        "bool", "byte", "complex64", "complex128", "error",
        "float32", "float64",
        "int", "int8", "int16", "int32", "int64",
        "uint", "uint8", "uint16", "uint32", "uint64", "uintptr",
        "rune", "string",
        "true", "false", "nil", "iota",
        "append", "cap", "close", "copy", "delete", "len", "make",
        "new", "panic", "print", "println", "recover",
    ];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;
        bool inRawString = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inBlockComment, ref inRawString);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inBlockComment, ref bool inRawString)
    {
        int i = 0, len = line.Length;

        // Raw string continuation (backtick)
        if (inRawString)
        {
            int end = line.IndexOf('`', i);
            if (end >= 0)
            {
                tokens.Add(new SyntaxToken(0, end + 1, TokenKind.String));
                i = end + 1;
                inRawString = false;
            }
            else
            {
                if (len > 0) tokens.Add(new SyntaxToken(0, len, TokenKind.String));
                return;
            }
        }

        while (i < len)
        {
            if (inBlockComment)
            {
                int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end >= 0)
                {
                    tokens.Add(new SyntaxToken(i, end + 2 - i, TokenKind.Comment));
                    i = end + 2;
                    inBlockComment = false;
                }
                else
                {
                    tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                    i = len;
                }
                continue;
            }

            // Line comment
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '/')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // Block comment
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    tokens.Add(new SyntaxToken(i, end + 2 - i, TokenKind.Comment));
                    i = end + 2;
                }
                else
                {
                    tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                    inBlockComment = true;
                    break;
                }
                continue;
            }

            // Raw string: `...` (may span lines)
            if (line[i] == '`')
            {
                int start = i++;
                int end = line.IndexOf('`', i);
                if (end >= 0)
                {
                    i = end + 1;
                    tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                }
                else
                {
                    tokens.Add(new SyntaxToken(start, len - start, TokenKind.String));
                    inRawString = true;
                    i = len;
                }
                continue;
            }

            // Interpreted string
            if (line[i] == '"')
            {
                int start = i++;
                while (i < len && line[i] != '"') { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Rune literal
            if (line[i] == '\'')
            {
                int start = i++;
                while (i < len && line[i] != '\'') { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                if (line[i] == '0' && i + 1 < len &&
                    (line[i + 1] == 'x' || line[i + 1] == 'X' ||
                     line[i + 1] == 'b' || line[i + 1] == 'B' ||
                     line[i + 1] == 'o' || line[i + 1] == 'O'))
                    i += 2;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword / type / builtin
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                TokenKind kind = Keywords.Contains(word) ? TokenKind.Keyword
                               : Types.Contains(word)    ? TokenKind.Type
                               : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
