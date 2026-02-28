namespace Editor.Core.Syntax.Languages;

public class CssSyntax : ISyntaxLanguage
{
    public string Name => "CSS";
    public string[] Extensions => [".css", ".scss", ".less"];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;
        int braceDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inBlockComment, ref braceDepth);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inBlockComment, ref int braceDepth)
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

            // Line comment (SCSS/LESS)
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '/')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // Track braces
            if (line[i] == '{') { braceDepth++; i++; continue; }
            if (line[i] == '}') { if (braceDepth > 0) braceDepth--; i++; continue; }

            if (char.IsWhiteSpace(line[i])) { i++; continue; }

            // At-rule: @media @import @keyframes etc.
            if (line[i] == '@')
            {
                int start = i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '-')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Keyword));
                continue;
            }

            // SCSS variable: $variable
            if (line[i] == '$')
            {
                int start = i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '-' || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                continue;
            }

            // String
            if (line[i] == '"' || line[i] == '\'')
            {
                char q = line[i];
                int start = i++;
                while (i < len && line[i] != q) { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Hex color: #rrggbb or id selector #id
            if (line[i] == '#')
            {
                int start = i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '-' || line[i] == '_')) i++;
                // Hex color: inside braces and all hex chars and 3/4/6/8 len
                var body = line[(start + 1)..i];
                bool isHex = braceDepth > 0 && body.Length is 3 or 4 or 6 or 8 &&
                             body.All(c => char.IsAsciiHexDigit(c));
                tokens.Add(new SyntaxToken(start, i - start, isHex ? TokenKind.Number : TokenKind.Identifier));
                continue;
            }

            // Number with optional unit (10px, 2.5em, 100%, 0.5)
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                while (i < len && (char.IsLetter(line[i]) || line[i] == '%')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier: property (inside braces) or selector/pseudo (outside)
            if (char.IsLetter(line[i]) || line[i] == '_' || line[i] == '-')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '-' || line[i] == '_')) i++;

                int j = i;
                while (j < len && line[j] == ' ') j++;
                bool followedByColon = j < len && line[j] == ':' &&
                                       (j + 1 >= len || line[j + 1] == ' ' || line[j + 1] == '\t');

                TokenKind kind = (braceDepth > 0 && followedByColon)
                    ? TokenKind.Attribute  // CSS property
                    : TokenKind.Identifier; // selector / value keyword
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            // Pseudo-class/element: :hover ::before
            if (line[i] == ':')
            {
                int start = i++;
                if (i < len && line[i] == ':') i++;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '-')) i++;
                if (i > start + 1)
                    tokens.Add(new SyntaxToken(start, i - start, TokenKind.Keyword));
                continue;
            }

            i++;
        }
    }
}
