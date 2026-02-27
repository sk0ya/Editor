namespace WVim.Core.Syntax;

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
}

public record struct SyntaxToken(int StartColumn, int Length, TokenKind Kind);

public record struct LineTokens(int Line, SyntaxToken[] Tokens);
