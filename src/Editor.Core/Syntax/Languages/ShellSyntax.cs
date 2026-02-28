namespace Editor.Core.Syntax.Languages;

public class ShellSyntax : ISyntaxLanguage
{
    public string Name => "Shell";
    public string[] Extensions => [".sh", ".bash", ".zsh", ".fish"];

    private static readonly HashSet<string> Keywords =
    [
        "if", "then", "else", "elif", "fi", "for", "while", "until",
        "do", "done", "case", "esac", "in", "select", "function",
        "return", "exit", "break", "continue", "shift",
        "export", "local", "readonly", "declare", "typeset", "unset",
        "source", "alias", "unalias", "set", "shopt",
        "echo", "printf", "read", "test", "true", "false",
    ];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inHereDoc = false;
        string hereDocEnd = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inHereDoc, ref hereDocEnd);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inHereDoc, ref string hereDocEnd)
    {
        int i = 0, len = line.Length;

        // Heredoc content
        if (inHereDoc)
        {
            if (line.Trim() == hereDocEnd)
            {
                tokens.Add(new SyntaxToken(0, len, TokenKind.Keyword));
                inHereDoc = false;
            }
            else
            {
                if (len > 0) tokens.Add(new SyntaxToken(0, len, TokenKind.String));
            }
            return;
        }

        while (i < len)
        {
            // Shebang / comment
            if (line[i] == '#')
            {
                var kind = (i == 0 && i + 1 < len && line[i + 1] == '!')
                    ? TokenKind.Preprocessor
                    : TokenKind.Comment;
                tokens.Add(new SyntaxToken(i, len - i, kind));
                break;
            }

            // Heredoc: <<EOF or <<'EOF' or <<-EOF
            if (i + 1 < len && line[i] == '<' && line[i + 1] == '<')
            {
                int start = i; i += 2;
                if (i < len && line[i] == '-') i++;
                // Collect delimiter
                while (i < len && line[i] == ' ') i++;
                bool quoted = i < len && (line[i] == '\'' || line[i] == '"');
                char q = quoted ? line[i++] : '\0';
                int delimStart = i;
                while (i < len && (quoted ? line[i] != q : !char.IsWhiteSpace(line[i]))) i++;
                hereDocEnd = line[delimStart..i];
                if (quoted && i < len) i++;
                inHereDoc = hereDocEnd.Length > 0;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Double-quoted string (with variable expansion)
            if (line[i] == '"')
            {
                int start = i++;
                while (i < len && line[i] != '"') { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Single-quoted string (literal)
            if (line[i] == '\'')
            {
                int start = i++;
                while (i < len && line[i] != '\'') i++;
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Variable: $var ${var} $1 $@ $# etc.
            if (line[i] == '$')
            {
                int start = i++;
                if (i < len && line[i] == '{')
                {
                    int end = line.IndexOf('}', i);
                    i = end >= 0 ? end + 1 : len;
                }
                else if (i < len && line[i] == '(')
                {
                    // Command substitution $(...) — skip to matching )
                    int depth = 1; i++;
                    while (i < len && depth > 0)
                    {
                        if (line[i] == '(') depth++;
                        else if (line[i] == ')') depth--;
                        i++;
                    }
                }
                else
                {
                    while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                continue;
            }

            // Identifier / keyword
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '-')) i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]))
            {
                int start = i;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            i++;
        }
    }
}
