namespace Editor.Core.Syntax.Languages;

public class MarkdownSyntax : ISyntaxLanguage
{
    public string Name => "Markdown";
    public string[] Extensions => [".md", ".markdown"];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inFencedCode = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var tokens = new List<SyntaxToken>();

            var stripped = line.TrimStart();
            if (stripped.StartsWith("```") || stripped.StartsWith("~~~"))
            {
                tokens.Add(new SyntaxToken(0, line.Length, TokenKind.String));
                inFencedCode = !inFencedCode;
                result.Add(new LineTokens(i, [.. tokens]));
                continue;
            }

            if (inFencedCode)
            {
                if (line.Length > 0)
                    tokens.Add(new SyntaxToken(0, line.Length, TokenKind.String));
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
        if (line.Length == 0) return;

        // Heading: # ## ###
        if (line[0] == '#')
        {
            int h = 0;
            while (h < line.Length && line[h] == '#') h++;
            if (h == line.Length || line[h] == ' ')
            {
                tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Keyword));
                return;
            }
        }

        // Blockquote: > text
        if (line[0] == '>')
        {
            tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Comment));
            return;
        }

        // Horizontal rule: ---, ***, ___
        if (IsHorizontalRule(line.Trim()))
        {
            tokens.Add(new SyntaxToken(0, line.Length, TokenKind.Operator));
            return;
        }

        // List marker: -, *, +, 1.
        int listLen = GetListMarkerLength(line);
        if (listLen > 0)
            tokens.Add(new SyntaxToken(0, listLen, TokenKind.Type));

        TokenizeInline(line, listLen, tokens);
    }

    private static void TokenizeInline(string line, int start, List<SyntaxToken> tokens)
    {
        int i = start;
        int len = line.Length;

        while (i < len)
        {
            // Inline code: `code`
            if (line[i] == '`')
            {
                int end = line.IndexOf('`', i + 1);
                if (end > i)
                {
                    tokens.Add(new SyntaxToken(i, end + 1 - i, TokenKind.String));
                    i = end + 1;
                    continue;
                }
            }

            // Image: ![alt](url)
            if (i + 1 < len && line[i] == '!' && line[i + 1] == '[')
            {
                int cb = line.IndexOf(']', i + 2);
                if (cb > 0 && cb + 1 < len && line[cb + 1] == '(')
                {
                    int cp = line.IndexOf(')', cb + 2);
                    if (cp > 0)
                    {
                        tokens.Add(new SyntaxToken(i, cb + 1 - i, TokenKind.Identifier));
                        tokens.Add(new SyntaxToken(cb + 1, cp + 1 - cb - 1, TokenKind.Comment));
                        i = cp + 1;
                        continue;
                    }
                }
            }

            // Link: [text](url)
            if (line[i] == '[')
            {
                int cb = line.IndexOf(']', i + 1);
                if (cb > 0 && cb + 1 < len && line[cb + 1] == '(')
                {
                    int cp = line.IndexOf(')', cb + 2);
                    if (cp > 0)
                    {
                        tokens.Add(new SyntaxToken(i, cb + 1 - i, TokenKind.Identifier));
                        tokens.Add(new SyntaxToken(cb + 1, cp + 1 - cb - 1, TokenKind.Comment));
                        i = cp + 1;
                        continue;
                    }
                }
            }

            // Bold: **text** or __text__
            if (i + 1 < len && ((line[i] == '*' && line[i + 1] == '*') ||
                                  (line[i] == '_' && line[i + 1] == '_')))
            {
                char m = line[i];
                string marker = new(m, 2);
                int end = line.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    tokens.Add(new SyntaxToken(i, end + 2 - i, TokenKind.Operator));
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *text* or _text_
            if (line[i] == '*' || line[i] == '_')
            {
                char m = line[i];
                int end = line.IndexOf(m, i + 1);
                if (end > i)
                {
                    tokens.Add(new SyntaxToken(i, end + 1 - i, TokenKind.Operator));
                    i = end + 1;
                    continue;
                }
            }

            i++;
        }
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        char c = trimmed[0];
        if (c != '-' && c != '*' && c != '_') return false;
        int count = 0;
        foreach (char ch in trimmed)
        {
            if (ch != c && ch != ' ') return false;
            if (ch == c) count++;
        }
        return count >= 3;
    }

    private static int GetListMarkerLength(string line)
    {
        int i = 0;
        while (i < 3 && i < line.Length && line[i] == ' ') i++;
        if (i >= line.Length) return 0;

        // Unordered: -, *, +
        if ((line[i] == '-' || line[i] == '*' || line[i] == '+') &&
            i + 1 < line.Length && line[i + 1] == ' ')
            return i + 2;

        // Ordered: 1. or 1)
        if (char.IsDigit(line[i]))
        {
            int j = i;
            while (j < line.Length && char.IsDigit(line[j])) j++;
            if (j < line.Length && (line[j] == '.' || line[j] == ')') &&
                j + 1 < line.Length && line[j + 1] == ' ')
                return j + 2;
        }

        return 0;
    }
}
