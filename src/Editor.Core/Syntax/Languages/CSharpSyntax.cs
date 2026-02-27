using System.Text.RegularExpressions;

namespace Editor.Core.Syntax.Languages;

public class CSharpSyntax : ISyntaxLanguage
{
    public string Name => "C#";
    public string[] Extensions => [".cs"];

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "while",
        "async", "await", "record", "init", "with", "get", "set", "value", "yield",
        "when", "from", "select", "where", "orderby", "group", "join", "into",
        "let", "on", "equals", "by", "ascending", "descending", "dynamic",
        "nameof", "global", "nint", "nuint", "not", "and", "or", "required",
        "file", "scoped", "managed", "unmanaged"
    ];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;
        bool inVerbatimString = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var tokens = new List<SyntaxToken>();
            TokenizeLine(line, tokens, ref inBlockComment, ref inVerbatimString);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private void TokenizeLine(string line, List<SyntaxToken> tokens,
        ref bool inBlockComment, ref bool inVerbatimString)
    {
        int i = 0;
        int len = line.Length;

        while (i < len)
        {
            if (inBlockComment)
            {
                int end = line.IndexOf("*/", i);
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

            // Line comment
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '/')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // Block comment start
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2);
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

            // Preprocessor
            if (i == 0 && line.Length > 0 && line[0] == '#')
            {
                tokens.Add(new SyntaxToken(0, len, TokenKind.Preprocessor));
                break;
            }

            // String
            if (line[i] == '"' || (line[i] == '@' && i + 1 < len && line[i + 1] == '"'))
            {
                bool verbatim = line[i] == '@';
                int start = i;
                i += verbatim ? 2 : 1;
                while (i < len)
                {
                    if (verbatim && line[i] == '"' && i + 1 < len && line[i + 1] == '"')
                    { i += 2; continue; }
                    if (line[i] == '"') { i++; break; }
                    if (!verbatim && line[i] == '\\') i++;
                    i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Char literal
            if (line[i] == '\'')
            {
                int start = i++;
                while (i < len && line[i] != '\'')
                {
                    if (line[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier/keyword
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                var word = line[start..i];
                var kind = Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
