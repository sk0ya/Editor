namespace Editor.Core.Syntax.Languages;

public class TypeScriptSyntax : JavaScriptSyntax
{
    public override string Name => "TypeScript";
    public override string[] Extensions => [".ts", ".tsx"];

    protected override IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        // JavaScript keywords
        "var", "let", "const", "function", "return", "if", "else", "for",
        "while", "do", "break", "continue", "switch", "case", "default",
        "throw", "try", "catch", "finally", "new", "delete", "typeof",
        "instanceof", "in", "of", "class", "extends", "super", "this",
        "import", "export", "from", "async", "await", "yield",
        "null", "undefined", "true", "false", "void", "static", "get", "set",
        "debugger",
        // TypeScript-specific
        "interface", "type", "enum", "namespace", "module", "declare",
        "abstract", "implements", "readonly", "as", "is", "keyof", "infer",
        "never", "any", "unknown", "string", "number", "boolean", "object",
        "symbol", "bigint", "override", "satisfies", "using", "accessor",
        "private", "protected", "public",
    };
}
