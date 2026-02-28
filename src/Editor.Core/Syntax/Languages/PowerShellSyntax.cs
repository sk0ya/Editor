namespace Editor.Core.Syntax.Languages;

public class PowerShellSyntax : ISyntaxLanguage
{
    public string Name => "PowerShell";
    public string[] Extensions => [".ps1", ".psm1", ".psd1"];

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "elseif", "switch", "foreach", "for", "while", "do",
        "until", "break", "continue", "return", "function", "filter", "class",
        "enum", "try", "catch", "finally", "throw", "trap", "begin", "process",
        "end", "param", "exit", "in", "not", "and", "or", "using", "module",
    };

    // Comparison / type operators: -eq -ne -like -match etc.
    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "lt", "gt", "le", "ge",
        "like", "notlike", "match", "notmatch",
        "contains", "notcontains", "in", "notin",
        "is", "isnot", "as", "not", "and", "or", "xor",
        "band", "bnot", "bor", "bxor", "shl", "shr",
        "replace", "split", "join", "f",
    };

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;
        bool inHereDouble = false;  // @"..."@
        bool inHereSingle = false;  // @'...'@

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inBlockComment, ref inHereDouble, ref inHereSingle);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inBlockComment, ref bool inHereDouble, ref bool inHereSingle)
    {
        int i = 0, len = line.Length;

        // Here-string content
        if (inHereDouble || inHereSingle)
        {
            char q = inHereDouble ? '"' : '\'';
            var closing = $"{q}@";
            if (line.TrimStart() == closing || line == closing)
            {
                tokens.Add(new SyntaxToken(0, len, TokenKind.String));
                inHereDouble = inHereSingle = false;
            }
            else if (len > 0)
            {
                tokens.Add(new SyntaxToken(0, len, TokenKind.String));
            }
            return;
        }

        while (i < len)
        {
            if (inBlockComment)
            {
                int end = line.IndexOf("#>", i, StringComparison.Ordinal);
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

            // Block comment: <# ... #>
            if (i + 1 < len && line[i] == '<' && line[i + 1] == '#')
            {
                int end = line.IndexOf("#>", i + 2, StringComparison.Ordinal);
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

            // Line comment: #
            if (line[i] == '#')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            if (char.IsWhiteSpace(line[i])) { i++; continue; }

            // Here-string start: @" or @'
            if (line[i] == '@' && i + 1 < len && (line[i + 1] == '"' || line[i + 1] == '\''))
            {
                char q = line[i + 1];
                int start = i; i += 2;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                if (q == '"') inHereDouble = true; else inHereSingle = true;
                i = len; // rest of line is here-string delimiter
                continue;
            }

            // Double-quoted string (expandable)
            if (line[i] == '"')
            {
                int start = i++;
                while (i < len && line[i] != '"') { if (line[i] == '`') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Single-quoted string (literal)
            if (line[i] == '\'')
            {
                int start = i++;
                while (i < len && !(line[i] == '\'' && (i + 1 >= len || line[i + 1] != '\'')))
                {
                    if (line[i] == '\'' && i + 1 < len && line[i + 1] == '\'') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Variable: $name ${name} $_ $true $false $null
            if (line[i] == '$')
            {
                int start = i++;
                if (i < len && line[i] == '{')
                {
                    int end = line.IndexOf('}', i);
                    i = end >= 0 ? end + 1 : len;
                }
                else
                {
                    while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                }
                var varName = line[(start + 1)..i];
                bool isSpecial = varName is "true" or "false" or "null";
                tokens.Add(new SyntaxToken(start, i - start, isSpecial ? TokenKind.Keyword : TokenKind.Attribute));
                continue;
            }

            // Type accelerator: [int] [string] [System.IO.File]
            if (line[i] == '[')
            {
                int start = i++;
                while (i < len && line[i] != ']' && line[i] != '\n') i++;
                if (i < len && line[i] == ']') i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Type));
                continue;
            }

            // Comparison / unary operator: -eq -ne -like -match -not etc.
            if (line[i] == '-' && i + 1 < len && char.IsLetter(line[i + 1]))
            {
                int start = i++;
                while (i < len && char.IsLetter(line[i])) i++;
                var op = line[(start + 1)..i];
                var kind = Operators.Contains(op) ? TokenKind.Keyword : TokenKind.Operator;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword (cmdlets like Get-Item, Write-Host)
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '-')) i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
