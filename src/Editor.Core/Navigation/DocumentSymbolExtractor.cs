using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Core.Lsp;

namespace Editor.Core.Navigation;

/// <summary>
/// Heuristic, dependency-free document-symbol extractor used as a fallback for the breadcrumb
/// bar (and any symbol view) when no language server is available. It is intentionally
/// approximate: brace-language declarations are found by scanning for the declaration text that
/// precedes each block <c>{</c>, Python by indentation, Markdown by headings, and XML by tag
/// nesting. Names/ranges are good enough to render and jump to, not a full parser.
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

    // Markup handled as a tag tree. XAML/MSBuild/resx rarely carry an <?xml declaration,
    // so the extension has to answer for them; anything else is sniffed (see LooksLikeXml).
    private static readonly HashSet<string> XmlExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".xaml", ".axaml", ".xhtml", ".xsd", ".xsl", ".xslt", ".svg", ".rss", ".atom",
        ".resx", ".config", ".props", ".targets", ".nuspec", ".plist", ".wxs", ".vsixmanifest",
        ".csproj", ".vbproj", ".fsproj", ".wixproj", ".sqlproj", ".xib", ".storyboard",
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
        if (XmlExts.Contains(ext))
            return ExtractXml(lines);
        if (BraceExts.Contains(ext))
            return ExtractBrace(lines);
        // Extension unknown (.txt, no extension, an exported dump): an `<?xml` declaration at the
        // very top is the document telling us what it is, so trust it.
        if (LooksLikeXml(lines))
            return ExtractXml(lines);
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
    // ── XML / XAML (tag nesting) ──

    // Attributes that identify which one of many same-named siblings this is. Shown after the
    // tag name (Compile#Program.cs), because "Project › ItemGroup › PackageReference" repeated
    // three times says nothing about where the cursor actually sits. Namespace prefixes are
    // ignored, so x:Name / xml:id match too.
    private static readonly HashSet<string> XmlIdAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "id", "key", "include",
    };

    private static readonly Regex XmlAttrRx =
        new(@"([A-Za-z_][\w.:-]*)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled);

    private const int MaxIdAttrChars = 32;

    /// <summary>True when the document declares itself XML on its first non-blank line.</summary>
    private static bool LooksLikeXml(string[] lines)
    {
        foreach (var line in lines)
        {
            var t = line.TrimStart('﻿', ' ', '\t');
            if (t.Length == 0) continue;
            return t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private sealed class XmlNode
    {
        public string Name = "";        // tag name, used to match the closing tag
        public string Display = "";     // tag name + identifying attribute
        public int Start;               // offset of '<'
        public int NameStart;           // jump target (the tag name itself)
        public int NameLength;
        public int End = -1;            // offset just past the closing '>' (-1 = never closed)
        public readonly List<XmlNode> Children = [];
    }

    private static IReadOnlyList<DocumentSymbol> ExtractXml(string[] lines)
    {
        // Scanning one joined string keeps multi-line tags (very common in XAML) simple; the
        // offset → line/column mapping is recovered from the line-start table. Ranges are exact
        // (not whole-line like the brace path) so `<a><b/><c/></a>` on one line still resolves.
        var text = string.Join('\n', lines);
        var lineStarts = new int[lines.Length];
        for (int i = 0, off = 0; i < lines.Length; i++) { lineStarts[i] = off; off += lines[i].Length + 1; }

        LspPosition Pos(int offset)
        {
            int idx = Array.BinarySearch(lineStarts, offset);
            if (idx < 0) idx = Math.Max(0, ~idx - 1);
            return new LspPosition(idx, offset - lineStarts[idx]);
        }

        var roots = new List<XmlNode>();
        var stack = new Stack<XmlNode>();

        int p = 0;
        while (p < text.Length)
        {
            int lt = text.IndexOf('<', p);
            if (lt < 0) break;

            // Everything that looks like a tag but isn't one.
            if (StartsAt(text, lt, "<!--")) { p = SkipPast(text, lt + 4, "-->"); continue; }
            if (StartsAt(text, lt, "<![CDATA[")) { p = SkipPast(text, lt + 9, "]]>"); continue; }
            if (StartsAt(text, lt, "<?")) { p = SkipPast(text, lt + 2, "?>"); continue; }
            if (StartsAt(text, lt, "<!")) { p = SkipDeclaration(text, lt + 2); continue; }

            if (StartsAt(text, lt, "</"))
            {
                int close = text.IndexOf('>', lt + 2);
                if (close < 0) break;
                var closing = text[(lt + 2)..close].Trim();
                // Unwind to the matching open tag; a closing tag with no match (malformed or
                // half-typed markup, which is most of the time while editing) is ignored rather
                // than collapsing the whole stack.
                if (stack.Any(n => string.Equals(n.Name, closing, StringComparison.Ordinal)))
                {
                    while (stack.Count > 0)
                    {
                        var open = stack.Pop();
                        open.End = close + 1;
                        if (string.Equals(open.Name, closing, StringComparison.Ordinal)) break;
                    }
                }
                p = close + 1;
                continue;
            }

            int nameStart = lt + 1;
            int nameEnd = nameStart;
            if (nameEnd >= text.Length || !(char.IsLetter(text[nameEnd]) || text[nameEnd] is '_' or ':'))
            {
                p = lt + 1; // a bare '<' in text content
                continue;
            }
            while (nameEnd < text.Length &&
                   (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is '_' or ':' or '-' or '.'))
                nameEnd++;

            // '>' inside an attribute value doesn't end the tag, so track quotes.
            int gt = -1;
            char quote = '\0';
            for (int q = nameEnd; q < text.Length; q++)
            {
                char c = text[q];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '"' or '\'') quote = c;
                else if (c == '>') { gt = q; break; }
            }
            if (gt < 0) break; // unterminated tag: nothing reliable left to parse

            bool selfClosing = gt > nameEnd && text[gt - 1] == '/';
            var name = text[nameStart..nameEnd];
            var attrs = text[nameEnd..(selfClosing ? gt - 1 : gt)];
            var node = new XmlNode
            {
                Name = name,
                Display = name + IdSuffix(attrs),
                Start = lt,
                NameStart = nameStart,
                NameLength = nameEnd - nameStart,
            };

            if (stack.Count > 0) stack.Peek().Children.Add(node); else roots.Add(node);
            if (selfClosing) node.End = gt + 1; else stack.Push(node);
            p = gt + 1;
        }

        DocumentSymbol ToSymbol(XmlNode n)
        {
            var range = new LspRange(Pos(n.Start), Pos(n.End < 0 ? text.Length : n.End));
            var sel = new LspRange(Pos(n.NameStart), Pos(n.NameStart + n.NameLength));
            var children = n.Children.Count > 0 ? n.Children.Select(ToSymbol).ToArray() : null;
            return new DocumentSymbol(n.Display, SymbolKind.Object, range, sel, children);
        }

        return roots.Select(ToSymbol).ToList();
    }

    private static string IdSuffix(string attrs)
    {
        foreach (Match m in XmlAttrRx.Matches(attrs))
        {
            var local = m.Groups[1].Value;
            int colon = local.LastIndexOf(':');
            if (colon >= 0) local = local[(colon + 1)..];
            if (!XmlIdAttrs.Contains(local)) continue;

            var value = (m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value;
            value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            // A path (MSBuild Include, mostly) identifies itself by its tail, so keep the file
            // name and drop the head; a name/id/key identifies itself by its head.
            bool pathLike = value.IndexOfAny(['/', '\\']) >= 0;
            if (pathLike)
            {
                int sep = value.LastIndexOfAny(['/', '\\']);
                value = value[(sep + 1)..];
            }
            if (value.Length == 0) continue;
            if (value.Length > MaxIdAttrChars)
                value = pathLike ? "…" + value[^MaxIdAttrChars..] : value[..MaxIdAttrChars] + "…";
            return "#" + value;
        }
        return "";
    }

    private static bool StartsAt(string text, int index, string value)
        => index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static int SkipPast(string text, int from, string terminator)
    {
        int end = text.IndexOf(terminator, from, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + terminator.Length;
    }

    // <!DOCTYPE …> may carry an internal subset in brackets, whose content can contain '>'.
    private static int SkipDeclaration(string text, int from)
    {
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '>' && depth <= 0) return i + 1;
        }
        return text.Length;
    }
}
