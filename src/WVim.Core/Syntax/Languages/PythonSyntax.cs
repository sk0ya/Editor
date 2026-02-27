namespace WVim.Core.Syntax.Languages;

public class PythonSyntax : ISyntaxLanguage
{
    public string Name => "Python";
    public string[] Extensions => [".py", ".pyw"];

    private static readonly HashSet<string> Keywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await",
        "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "nonlocal", "not", "or", "pass", "raise", "return",
        "try", "while", "with", "yield",
        "print", "range", "len", "type", "str", "int", "float", "list",
        "dict", "set", "tuple", "bool", "bytes", "super", "self"
    ];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inTripleDouble = false;
        bool inTripleSingle = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var tokens = new List<SyntaxToken>();
            TokenizeLine(line, tokens, ref inTripleDouble, ref inTripleSingle);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inTripleDouble, ref bool inTripleSingle)
    {
        int i = 0, len = line.Length;

        while (i < len)
        {
            // Triple-quoted string continuation
            if (inTripleDouble || inTripleSingle)
            {
                var closing = inTripleDouble ? "\"\"\"" : "'''";
                int end = line.IndexOf(closing, i);
                if (end >= 0)
                {
                    tokens.Add(new SyntaxToken(i, end + 3 - i, TokenKind.String));
                    i = end + 3;
                    inTripleDouble = inTripleSingle = false;
                }
                else
                {
                    tokens.Add(new SyntaxToken(i, len - i, TokenKind.String));
                    i = len;
                }
                continue;
            }

            // Comment
            if (line[i] == '#')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // Triple strings
            if (i + 2 < len && line[i] == '"' && line[i + 1] == '"' && line[i + 2] == '"')
            {
                int start = i; i += 3;
                int end = line.IndexOf("\"\"\"", i);
                if (end >= 0) { i = end + 3; }
                else { inTripleDouble = true; i = len; }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }
            if (i + 2 < len && line[i] == '\'' && line[i + 1] == '\'' && line[i + 2] == '\'')
            {
                int start = i; i += 3;
                int end = line.IndexOf("'''", i);
                if (end >= 0) { i = end + 3; }
                else { inTripleSingle = true; i = len; }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Regular string
            if (line[i] == '"' || line[i] == '\'')
            {
                char quote = line[i];
                int start = i++;
                while (i < len && line[i] != quote)
                {
                    if (line[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier/keyword
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, word.Length, kind));
                continue;
            }

            // Decorator
            if (line[i] == '@')
            {
                int start = i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                continue;
            }

            i++;
        }
    }
}
