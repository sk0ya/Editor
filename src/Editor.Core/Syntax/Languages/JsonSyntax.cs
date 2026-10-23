namespace Editor.Core.Syntax.Languages;

public class JsonSyntax : ILineStateSyntaxLanguage
{
    public string Name => "JSON";
    public string[] Extensions => [".json"];
    public string? LineCommentPrefix => null; // JSON has no comments

    private readonly record struct LineState;

    public object InitialState { get; } = new LineState();

    public LineTokens[] Tokenize(string[] lines) => LineStateTokenizer.TokenizeAll(this, lines);

    public SyntaxToken[] TokenizeLine(string line, ref object state)
    {
        var tokens = new List<SyntaxToken>();
        TokenizeLine(line, tokens);
        return [.. tokens];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens)
    {
        int i = 0, len = line.Length;

        while (i < len)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }

            // String (key or value)
            if (line[i] == '"')
            {
                int start = i++;
                while (i < len && line[i] != '"')
                {
                    if (line[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                int end = i;

                // Skip whitespace, then check if followed by ':' (object key)
                while (i < len && line[i] == ' ') i++;
                bool isKey = i < len && line[i] == ':';

                tokens.Add(new SyntaxToken(start, end - start, isKey ? TokenKind.Identifier : TokenKind.String));
                continue;
            }

            // Number (including negative)
            if (char.IsDigit(line[i]) || (line[i] == '-' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                if (line[i] == '-') i++;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.' ||
                                   line[i] == 'e' || line[i] == 'E' ||
                                   line[i] == '+' || line[i] == '-'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // true / false / null
            if (char.IsLetter(line[i]))
            {
                int start = i;
                while (i < len && char.IsLetter(line[i])) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Keyword));
                continue;
            }

            i++;
        }
    }
}
