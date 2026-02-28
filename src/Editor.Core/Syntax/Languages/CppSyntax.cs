namespace Editor.Core.Syntax.Languages;

public class CppSyntax : ISyntaxLanguage
{
    public string Name => "C/C++";
    public string[] Extensions => [".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hh"];

    private static readonly HashSet<string> Keywords =
    [
        // C keywords
        "auto", "break", "case", "char", "const", "continue", "default", "do",
        "double", "else", "enum", "extern", "float", "for", "goto", "if",
        "inline", "int", "long", "register", "restrict", "return", "short",
        "signed", "sizeof", "static", "struct", "switch", "typedef", "union",
        "unsigned", "void", "volatile", "while",
        // C++ keywords
        "alignas", "alignof", "and", "and_eq", "asm", "bitand", "bitor",
        "bool", "catch", "class", "compl", "concept", "consteval", "constexpr",
        "constinit", "co_await", "co_return", "co_yield", "decltype", "delete",
        "dynamic_cast", "explicit", "export", "false", "friend", "mutable",
        "namespace", "new", "noexcept", "not", "not_eq", "nullptr", "operator",
        "or", "or_eq", "override", "final", "private", "protected", "public",
        "reinterpret_cast", "requires", "static_assert", "static_cast",
        "template", "this", "thread_local", "throw", "true", "try", "typeid",
        "typename", "using", "virtual", "xor", "xor_eq",
    ];

    private static readonly HashSet<string> Types =
    [
        "int8_t", "int16_t", "int32_t", "int64_t",
        "uint8_t", "uint16_t", "uint32_t", "uint64_t",
        "size_t", "ptrdiff_t", "ssize_t", "off_t",
        "string", "wstring", "vector", "map", "set", "unordered_map",
        "unordered_set", "list", "deque", "queue", "stack", "pair",
        "shared_ptr", "unique_ptr", "weak_ptr", "optional", "variant",
        "nullptr_t", "max_align_t",
    ];

    public LineTokens[] Tokenize(string[] lines)
    {
        var result = new List<LineTokens>();
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = new List<SyntaxToken>();
            TokenizeLine(lines[i], tokens, ref inBlockComment);
            result.Add(new LineTokens(i, [.. tokens]));
        }
        return [.. result];
    }

    private static void TokenizeLine(string line, List<SyntaxToken> tokens, ref bool inBlockComment)
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

            // Line comment
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '/')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
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

            // Preprocessor: #include #define #ifdef etc.
            if (i == 0 && line.TrimStart().StartsWith('#'))
            {
                // Highlight directive keyword
                int hashPos = line.IndexOf('#');
                int end = hashPos + 1;
                while (end < len && char.IsLetter(line[end])) end++;
                tokens.Add(new SyntaxToken(hashPos, end - hashPos, TokenKind.Preprocessor));

                // Highlight <file> in #include
                int lt = line.IndexOf('<', end);
                int gt = lt >= 0 ? line.IndexOf('>', lt) : -1;
                if (lt >= 0 && gt >= 0)
                    tokens.Add(new SyntaxToken(lt, gt + 1 - lt, TokenKind.String));

                // Highlight "file"
                int q1 = line.IndexOf('"', end);
                int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                if (q1 >= 0 && q2 >= 0)
                    tokens.Add(new SyntaxToken(q1, q2 + 1 - q1, TokenKind.String));
                break;
            }

            // String
            if (line[i] == '"')
            {
                int start = i++;
                while (i < len && line[i] != '"') { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Char literal
            if (line[i] == '\'')
            {
                int start = i++;
                while (i < len && line[i] != '\'') { if (line[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number
            if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                int start = i;
                if (line[i] == '0' && i + 1 < len &&
                    (line[i + 1] == 'x' || line[i + 1] == 'X' ||
                     line[i + 1] == 'b' || line[i + 1] == 'B'))
                    i += 2;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_')) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword / type
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];
                TokenKind kind = Keywords.Contains(word) ? TokenKind.Keyword
                               : Types.Contains(word)    ? TokenKind.Type
                               : TokenKind.Identifier;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            i++;
        }
    }
}
