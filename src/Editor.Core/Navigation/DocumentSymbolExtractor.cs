using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Core.Lsp;

namespace Editor.Core.Navigation;

/// <summary>
/// Heuristic, dependency-free document-symbol extractor used as a fallback for the breadcrumb
/// bar (and any symbol view) when no language server is available. It is intentionally
/// approximate: brace-language declarations are found by scanning for the declaration text that
/// precedes each block <c>{</c>, and Python by indentation. Names/ranges are good enough to
/// render and jump to, not a full parser.
///
/// Emits <see cref="DocumentSymbol"/> so it shares <see cref="BreadcrumbBuilder"/> with the LSP path.
/// </summary>
public static class DocumentSymbolExtractor
{
    private static readonly HashSet<string> BraceExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".mjs", ".cjs",
        ".java", ".go", ".rs", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp",
        ".swift", ".kt", ".kts", ".scala", ".php", ".dart", ".css", ".scss",
    };

    private static readonly Regex NamespaceRx = new(@"\bnamespace\s+([A-Za-z_][\w.]*)", RegexOptions.Compiled);
    private static readonly Regex TypeRx = new(@"\b(class|struct|interface|enum|record|trait|protocol)\b\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex FuncRx = new(@"([A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex PyDefRx = new(@"^(?:async\s+)?def\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex PyClassRx = new(@"^class\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex MdHeadingRx = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    // Identifiers that precede '(' '{' but are not declarations.
    private static readonly HashSet<string> NonDecl = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "foreach", "catch", "using", "lock", "fixed",
        "do", "else", "try", "return", "new", "sizeof", "typeof", "nameof", "await",
        "yield", "throw", "when", "with", "match", "unsafe", "checked", "unchecked",
        "get", "set", "add", "remove", "in", "is", "as", "function", "where", "select",
    };

    public static IReadOnlyList<DocumentSymbol> Extract(string[] lines, string? filePath)
    {
        if (lines is null || lines.Length == 0) return [];
        var ext = filePath is { Length: > 0 } ? Path.GetExtension(filePath) : "";
        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".pyw", StringComparison.OrdinalIgnoreCase))
            return ExtractPython(lines);
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            return ExtractMarkdown(lines);
        if (BraceExts.Contains(ext))
            return ExtractBrace(lines);
        return [];
    }

    private sealed class Node
    {
        public string Name = "";
        public SymbolKind Kind;
        public int StartLine;
        public int NameCol;
        public int EndLine = -1;   // -1 = still open (set when the block closes / at EOF)
        public readonly List<Node> Children = [];
    }

    private static DocumentSymbol ToSymbol(Node n)
    {
        int end = Math.Max(n.EndLine, n.StartLine);
        var range = new LspRange(new LspPosition(n.StartLine, 0), new LspPosition(end, int.MaxValue));
        var sel = new LspRange(
            new LspPosition(n.StartLine, n.NameCol),
            new LspPosition(n.StartLine, n.NameCol + n.Name.Length));
        var children = n.Children.Count > 0 ? n.Children.Select(ToSymbol).ToArray() : null;
        return new DocumentSymbol(n.Name, n.Kind, range, sel, children);
    }

    // ── Brace languages ──
    private static IReadOnlyList<DocumentSymbol> ExtractBrace(string[] lines)
    {
        var roots = new List<Node>();
        var braceStack = new Stack<Node?>();   // one entry per open '{' (null = non-symbol block)
        var header = new StringBuilder();
        int headerLine = -1, headerCol = -1;
        bool capturing = false;
        bool inBlockComment = false;

        void ResetHeader() { capturing = false; header.Clear(); headerLine = -1; headerCol = -1; }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            bool inLineComment = false, inString = false;
            char stringChar = '"';

            for (int j = 0; j < line.Length; j++)
            {
                char c = line[j];
                char next = j + 1 < line.Length ? line[j + 1] : '\0';

                if (inBlockComment) { if (c == '*' && next == '/') { inBlockComment = false; j++; } continue; }
                if (inLineComment) break;
                if (inString)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == stringChar) inString = false;
                    continue;
                }
                if (c == '/' && next == '/') { inLineComment = true; continue; }
                if (c == '/' && next == '*') { inBlockComment = true; j++; continue; }
                if (c == '"' || c == '\'' || c == '`') { inString = true; stringChar = c; continue; }

                if (c == '{')
                {
                    var node = TryMakeBraceNode(header.ToString(), headerLine, headerCol);
                    if (node != null)
                    {
                        var parent = PeekSymbol(braceStack);
                        if (parent != null) parent.Children.Add(node); else roots.Add(node);
                    }
                    braceStack.Push(node);
                    ResetHeader();
                }
                else if (c == '}')
                {
                    if (braceStack.Count > 0)
                    {
                        var entry = braceStack.Pop();
                        if (entry != null) entry.EndLine = i;
                    }
                    ResetHeader();
                }
                else if (c == ';')
                {
                    ResetHeader();
                }
                else
                {
                    if (!capturing && !char.IsWhiteSpace(c)) { capturing = true; headerLine = i; headerCol = j; }
                    if (capturing) header.Append(c);
                }
            }
            if (capturing) header.Append(' '); // newline → token separator for multi-line headers
        }

        // Close any unbalanced blocks at EOF.
        foreach (var n in braceStack)
            if (n != null && n.EndLine < n.StartLine) n.EndLine = lines.Length - 1;

        return roots.Select(ToSymbol).ToList();
    }

    private static Node? PeekSymbol(Stack<Node?> stack)
    {
        foreach (var n in stack) if (n != null) return n; // top → bottom
        return null;
    }

    private static Node? TryMakeBraceNode(string header, int line, int col)
    {
        if (line < 0) return null;
        header = header.Trim();
        if (header.Length == 0) return null;

        var ns = NamespaceRx.Match(header);
        if (ns.Success)
            return new Node { Name = ns.Groups[1].Value, Kind = SymbolKind.Namespace, StartLine = line, NameCol = col };

        var ty = TypeRx.Match(header);
        if (ty.Success)
            return new Node { Name = ty.Groups[2].Value, StartLine = line, NameCol = col, Kind = ty.Groups[1].Value switch
            {
                "interface" => SymbolKind.Interface,
                "enum"      => SymbolKind.Enum,
                "struct"    => SymbolKind.Struct,
                _           => SymbolKind.Class,
            } };

        // Function / method: take the identifier nearest the opening paren that isn't a keyword.
        string? fnName = null;
        foreach (Match m in FuncRx.Matches(header))
        {
            var cand = m.Groups[1].Value;
            if (!NonDecl.Contains(cand)) fnName = cand; // last non-keyword wins
        }
        if (fnName != null)
            return new Node { Name = fnName, Kind = SymbolKind.Method, StartLine = line, NameCol = col };

        return null;
    }

    // ── Markdown (ATX heading hierarchy) ──
    private static IReadOnlyList<DocumentSymbol> ExtractMarkdown(string[] lines)
    {
        var roots = new List<Node>();
        var stack = new Stack<(Node Node, int Level)>();
        bool inFence = false;
        string fence = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Fenced code blocks (``` or ~~~) — ignore headings inside them.
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = trimmed[..3];
                if (!inFence) { inFence = true; fence = marker; }
                else if (trimmed.StartsWith(fence, StringComparison.Ordinal)) inFence = false;
                continue;
            }
            if (inFence) continue;

            var m = MdHeadingRx.Match(trimmed);
            if (!m.Success) continue;

            int level = m.Groups[1].Value.Length;
            var name = m.Groups[2].Value.Trim();
            if (name.Length == 0) continue;

            // A heading closes all open headings at the same or deeper level.
            while (stack.Count > 0 && stack.Peek().Level >= level)
            {
                var (closed, _) = stack.Pop();
                if (closed.EndLine < closed.StartLine) closed.EndLine = Math.Max(closed.StartLine, i - 1);
            }

            int indent = line.Length - trimmed.Length;
            var node = new Node { Name = name, Kind = SymbolKind.String, StartLine = i, NameCol = indent };
            var parent = stack.Count > 0 ? stack.Peek().Node : null;
            if (parent != null) parent.Children.Add(node); else roots.Add(node);
            stack.Push((node, level));
        }

        foreach (var (n, _) in stack)
            if (n.EndLine < n.StartLine) n.EndLine = lines.Length - 1;

        return roots.Select(ToSymbol).ToList();
    }

    // ── Python (indentation based) ──
    private static IReadOnlyList<DocumentSymbol> ExtractPython(string[] lines)
    {
        var roots = new List<Node>();
        var stack = new Stack<(Node Node, int Indent)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
            var trimmed = line[indent..];

            // Close blocks we've dedented out of.
            while (stack.Count > 0 && indent <= stack.Peek().Indent)
            {
                var (closed, _) = stack.Pop();
                if (closed.EndLine < closed.StartLine) closed.EndLine = Math.Max(closed.StartLine, i - 1);
            }

            Node? node = null;
            var cls = PyClassRx.Match(trimmed);
            var def = PyDefRx.Match(trimmed);
            if (cls.Success)
                node = new Node { Name = cls.Groups[1].Value, Kind = SymbolKind.Class, StartLine = i, NameCol = indent + cls.Groups[1].Index };
            else if (def.Success)
                node = new Node { Name = def.Groups[1].Value, Kind = SymbolKind.Method, StartLine = i, NameCol = indent + def.Groups[1].Index };

            if (node != null)
            {
                var parent = stack.Count > 0 ? stack.Peek().Node : null;
                if (parent != null) parent.Children.Add(node); else roots.Add(node);
                stack.Push((node, indent));
            }
        }

        foreach (var (n, _) in stack)
            if (n.EndLine < n.StartLine) n.EndLine = lines.Length - 1;

        return roots.Select(ToSymbol).ToList();
    }
}
