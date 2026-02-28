namespace Editor.Core.Syntax.Languages;

public class JavaScriptSyntax : ISyntaxLanguage
{
    public virtual string Name => "JavaScript";
    public virtual string[] Extensions => [".js", ".jsx"];

    protected virtual IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        "var", "let", "const", "function", "return", "if", "else", "for",
        "while", "do", "break", "continue", "switch", "case", "default",
        "throw", "try", "catch", "finally", "new", "delete", "typeof",
        "instanceof", "in", "of", "class", "extends", "super", "this",
        "import", "export", "from", "async", "await", "yield",
        "null", "undefined", "true", "false", "void", "static", "get", "set",
        "debugger",
    };

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;
        bool inTemplateLiteral = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inBlockComment, ref inTemplateLiteral);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inBlockComment, ref bool inTemplateLiteral)
    {
        int i = 0, len = line.Length;

        while (i < len)
        {
            if (inBlockComment)
            {
                int end = line.IndexOf("*/", i);
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

            if (inTemplateLiteral)
            {
                int end = line.IndexOf('`', i);
                if (end >= 0)
                {
                    tokens.Add(new SyntaxToken(i, end + 1 - i, TokenKind.String));
                    i = end + 1;
                    inTemplateLiteral = false;
                }
                else
                {
                    tokens.Add(new SyntaxToken(i, len - i, TokenKind.String));
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
                int end = line.IndexOf("*/", i + 2);
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

            // Decorator: @name
            if (line[i] == '@')
            {
                int start = i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                continue;
            }

            // Template literal (may span lines)
            if (line[i] == '`')
            {
                int start = i++;
                while (i < len && line[i] != '`')
                {
                    if (line[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                else inTemplateLiteral = true;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // String
            if (line[i] == '"' || line[i] == '\'')
            {
                char q = line[i];
                int start = i++;
                while (i < len && line[i] != q)
                {
                    if (line[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword
            if (char.IsLetter(line[i]) || line[i] == '_' || line[i] == '$')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '$'))
                    i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
