namespace Editor.Core.Syntax.Languages;

public class RubySyntax : ISyntaxLanguage
{
    public string Name => "Ruby";
    public string[] Extensions => [".rb"];
    public string? LineCommentPrefix => "#";
    public string? BlockCommentPrefix => "=begin";
    public string? BlockCommentSuffix => "=end";

    private static readonly HashSet<string> Keywords =
    [
        "__ENCODING__", "__LINE__", "__FILE__", "BEGIN", "END", "alias", "and",
        "begin", "break", "case", "class", "def", "defined?", "do", "else",
        "elsif", "end", "ensure", "false", "for", "if", "in", "module", "next",
        "nil", "not", "or", "redo", "rescue", "retry", "return", "self", "super",
        "then", "true", "undef", "unless", "until", "when", "while", "yield",
        "require", "require_relative", "attr_accessor", "attr_reader", "attr_writer",
        "puts", "print", "raise", "new", "lambda", "proc",
    ];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var tokens = new List<SyntaxToken>();

            if (inBlockComment)
            {
                tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Comment));
                if (line.TrimEnd() == "=end") inBlockComment = false;
                result.Add(new LineTokens(i, [.. tokens]));
                continue;
            }
            if (line.TrimEnd() == "=begin")
            {
                tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Comment));
                inBlockComment = true;
                result.Add(new LineTokens(i, [.. tokens]));
                continue;
            }

            TokenizeLine(line, tokens);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens)
    {
        int i = 0, len = line.Length;

        while (i < len)
        {
            char c = line[i];

            // Comment
            if (c == '#')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // String / backtick shell literal
            if (c == '"' || c == '\'' || c == '`')
            {
                int start = i++;
                while (i < len && line[i] != c) { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // %w[...] / %i[...] literal arrays (kept as a single string-colored token)
            if (c == '%' && i + 2 < len && (line[i + 1] is 'w' or 'i' or 'W' or 'I') && IsOpenDelim(line[i + 2]))
            {
                int start = i;
                char close = CloseDelim(line[i + 2]);
                i += 3;
                while (i < len && line[i] != close) i++;
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Symbol :name — require an identifier/quote after ':' and not a "::" or "? :" context
            if (c == ':' && i + 1 < len && (char.IsLetter(line[i + 1]) || line[i + 1] == '_') && (i == 0 || line[i - 1] != ':'))
            {
                int start = i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                if (i < len && (line[i] == '?' || line[i] == '!' || line[i] == '=')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Instance/class variable @foo / @@foo, global $foo
            if (c == '@' || c == '$')
            {
                int start = i++;
                if (i < len && line[i] == '@') i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Identifier));
                continue;
            }

            // Number
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword / constant
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                if (i < len && (line[i] == '?' || line[i] == '!')) i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword
                    : char.IsUpper(word[0]) ? TokenKind.Type
                    : SyntaxHeuristics.ClassifyIdentifier(line, start, start + word.Length);
                tokens.Add(new SyntaxToken(start, word.Length, kind));
                continue;
            }

            i++;
        }
    }

    private static bool IsOpenDelim(char c) => c is '[' or '(' or '{' or '<';
    private static char CloseDelim(char c) => c switch { '[' => ']', '(' => ')', '{' => '}', '<' => '>', _ => c };
}
