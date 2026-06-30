namespace Editor.Core.Syntax;

public enum TokenKind
{
    Text,
    Keyword,
    Type,
    String,
    Comment,
    Number,
    Operator,
    Preprocessor,
    Identifier,
    Attribute,
    Function,
}

public static class SyntaxHeuristics
{
    /// <summary>
    /// Classifies a non-keyword identifier word at [start, end) in <paramref name="line"/> using
    /// lightweight neighbor heuristics (no semantic info): followed by '(' → Function (call);
    /// preceded by '.' → Identifier (member access, kept in the default color so properties don't
    /// share the Type color); PascalCase → Type; otherwise → Identifier.
    /// </summary>
    public static TokenKind ClassifyIdentifier(string line, int start, int end)
    {
        int j = end;
        while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
        if (j < line.Length && line[j] == '(') return TokenKind.Function;

        int k = start - 1;
        while (k >= 0 && (line[k] == ' ' || line[k] == '\t')) k--;
        if (k >= 0 && line[k] == '.') return TokenKind.Identifier;

        return char.IsUpper(line[start]) ? TokenKind.Type : TokenKind.Identifier;
    }
}

public record struct SyntaxToken(int StartColumn, int Length, TokenKind Kind);

public record struct LineTokens(int Line, SyntaxToken[] Tokens);
