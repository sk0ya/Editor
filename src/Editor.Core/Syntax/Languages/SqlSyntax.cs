namespace Editor.Core.Syntax.Languages;

public class SqlSyntax : ISyntaxLanguage
{
    public string Name => "SQL";
    public string[] Extensions => [".sql"];

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "join", "on", "as", "inner", "left", "right",
        "full", "outer", "cross", "group", "by", "order", "having", "limit",
        "offset", "distinct", "all", "union", "intersect", "except",
        "insert", "into", "values", "update", "set", "delete",
        "create", "table", "view", "index", "trigger", "procedure", "function",
        "drop", "alter", "add", "column", "rename", "modify",
        "primary", "foreign", "key", "references", "unique", "not", "null",
        "default", "check", "constraint", "auto_increment", "identity",
        "and", "or", "in", "between", "like", "is", "exists", "any", "some",
        "case", "when", "then", "else", "end", "if", "begin", "commit",
        "rollback", "transaction", "savepoint", "grant", "revoke",
        "with", "recursive", "over", "partition", "rows", "range",
        "count", "sum", "avg", "min", "max", "coalesce", "nullif", "cast",
        "asc", "desc", "true", "false", "null",
    };

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inBlockComment);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens, ref bool inBlockComment)
    {
        int i = 0, len = line.Length;

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

            // Line comment: --
            if (i + 1 < len && line[i] == '-' && line[i + 1] == '-')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // Block comment: /* */
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

            if (char.IsWhiteSpace(line[i])) { i++; continue; }

            // String: '...'
            if (line[i] == '\'')
            {
                int start = i++;
                while (i < len && !(line[i] == '\'' && (i + 1 >= len || line[i + 1] != '\'')))
                {
                    if (line[i] == '\'' && i + 1 < len && line[i + 1] == '\'') i++; // escaped ''
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Quoted identifier: `ident` or [ident]
            if (line[i] == '`' || line[i] == '[')
            {
                char close = line[i] == '`' ? '`' : ']';
                int start = i++;
                while (i < len && line[i] != close) i++;
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Identifier));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'e' ||
                                   line[i] == 'E' || line[i] == '+' || line[i] == '-'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
