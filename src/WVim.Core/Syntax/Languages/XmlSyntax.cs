namespace WVim.Core.Syntax.Languages;

public class XmlSyntax : ISyntaxLanguage
{
    public string Name => "XML";
    public string[] Extensions => [".xml", ".xaml", ".html", ".htm", ".svg", ".csproj", ".sln"];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inComment);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private void TokenizeLine(string line, List<SyntaxToken> tokens, ref bool inComment)
    {
        int i = 0, len = line.Length;

        while (i < len)
        {
            if (inComment)
            {
                int end = line.IndexOf("-->", i);
                if (end >= 0)
                {
                    tokens.Add(new SyntaxToken(i, end + 3 - i, TokenKind.Comment));
                    i = end + 3;
                    inComment = false;
                }
                else
                {
                    tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                    i = len;
                }
                continue;
            }

            // Comment
            if (i + 3 < len && line[i] == '<' && line[i + 1] == '!' && line[i + 2] == '-' && line[i + 3] == '-')
            {
                int start = i;
                int end = line.IndexOf("-->", i + 4);
                if (end >= 0)
                {
                    tokens.Add(new SyntaxToken(start, end + 3 - start, TokenKind.Comment));
                    i = end + 3;
                }
                else
                {
                    tokens.Add(new SyntaxToken(start, len - start, TokenKind.Comment));
                    inComment = true;
                    i = len;
                }
                continue;
            }

            // Tag
            if (line[i] == '<')
            {
                int start = i++;
                // Read tag name
                bool closing = i < len && line[i] == '/';
                if (closing) i++;
                int nameStart = i;
                while (i < len && line[i] != '>' && line[i] != ' ' && line[i] != '/')
                    i++;
                if (i > nameStart)
                    tokens.Add(new SyntaxToken(nameStart, i - nameStart, TokenKind.Keyword));

                // Attributes inside tag
                while (i < len && line[i] != '>')
                {
                    if (char.IsLetter(line[i]) || line[i] == '_')
                    {
                        int attrStart = i;
                        while (i < len && line[i] != '=' && line[i] != '>' && line[i] != ' ')
                            i++;
                        tokens.Add(new SyntaxToken(attrStart, i - attrStart, TokenKind.Attribute));
                    }
                    else if (line[i] == '"' || line[i] == '\'')
                    {
                        char q = line[i];
                        int sStart = i++;
                        while (i < len && line[i] != q) i++;
                        if (i < len) i++;
                        tokens.Add(new SyntaxToken(sStart, i - sStart, TokenKind.String));
                    }
                    else i++;
                }
                if (i < len) i++; // skip >
                continue;
            }

            i++;
        }
    }
}
