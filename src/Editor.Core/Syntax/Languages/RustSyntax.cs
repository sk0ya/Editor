namespace Editor.Core.Syntax.Languages;

public class RustSyntax : ISyntaxLanguage
{
    public string Name => "Rust";
    public string[] Extensions => [".rs"];

    private static readonly HashSet<string> Keywords =
    [
        "as", "async", "await", "break", "const", "continue", "crate", "dyn",
        "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in",
        "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return",
        "self", "Self", "static", "struct", "super", "trait", "true", "type",
        "unsafe", "use", "where", "while",
    ];

    private static readonly HashSet<string> Types =
    [
        "u8", "u16", "u32", "u64", "u128", "usize",
        "i8", "i16", "i32", "i64", "i128", "isize",
        "f32", "f64", "bool", "char", "str",
        "String", "Vec", "Option", "Result", "Box", "Rc", "Arc",
        "HashMap", "HashSet", "BTreeMap", "BTreeSet",
        "Some", "None", "Ok", "Err",
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

            // Line comment (// //! ///)
            if (i + 1 < len && line[i] == '/' && line[i + 1] == '/')
            {
                tokens.Add(new SyntaxToken(i, len - i, TokenKind.Comment));
                break;
            }

            // Block comment
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

            // Attribute: #[...] or #![...]
            if (line[i] == '#')
            {
                int start = i;
                int bracketOpen = line.IndexOf('[', i);
                if (bracketOpen >= 0)
                {
                    int bracketClose = line.IndexOf(']', bracketOpen);
                    if (bracketClose >= 0)
                    {
                        tokens.Add(new SyntaxToken(start, bracketClose + 1 - start, TokenKind.Attribute));
                        i = bracketClose + 1;
                        continue;
                    }
                }
                i++;
                continue;
            }

            // Raw string: r#"..."# or r"..."
            if (line[i] == 'r' && i + 1 < len && (line[i + 1] == '"' || line[i + 1] == '#'))
            {
                int start = i++;
                int hashes = 0;
                while (i < len && line[i] == '#') { hashes++; i++; }
                if (i < len && line[i] == '"')
                {
                    i++;
                    string closing = "\"" + new string('#', hashes);
                    int end = line.IndexOf(closing, i, StringComparison.Ordinal);
                    if (end >= 0) i = end + closing.Length;
                    else i = len;
                    tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                    continue;
                }
                i = start; // not a raw string, fall through
            }

            // String
            if (line[i] == '"')
            {
                int start = i++;
                while (i < len && line[i] != '"')
                {
                    if (line[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Lifetime or char literal
            if (line[i] == '\'')
            {
                int start = i++;
                if (i < len && (char.IsLetter(line[i]) || line[i] == '_'))
                {
                    // Could be lifetime 'a or char 'a'
                    while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    if (i < len && line[i] == '\'')
                    {
                        // Char literal: 'a'
                        i++;
                        tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                    }
                    else
                    {
                        // Lifetime: 'a 'static
                        tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                    }
                    continue;
                }
                // Other char literal: '\n', ' ', etc.
                while (i < len && line[i] != '\'')
                {
                    if (line[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.String));
                continue;
            }

            // Number: decimal, 0x hex, 0b binary, 0o octal
            if (char.IsDigit(line[i]))
            {
                int start = i;
                if (line[i] == '0' && i + 1 < len &&
                    (line[i + 1] == 'x' || line[i + 1] == 'X' ||
                     line[i + 1] == 'b' || line[i + 1] == 'B' ||
                     line[i + 1] == 'o' || line[i + 1] == 'O'))
                {
                    i += 2;
                    while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                }
                else
                {
                    while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_')) i++;
                }
                tokens.Add(new SyntaxToken(start, i - start, TokenKind.Number));
                continue;
            }

            // Identifier / keyword / type / macro
            if (char.IsLetter(line[i]) || line[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                var word = line[start..i];

                // Macro call: name!
                if (i < len && line[i] == '!')
                {
                    i++;
                    tokens.Add(new SyntaxToken(start, i - start, TokenKind.Attribute));
                    continue;
                }

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
