using System.Text;
using System.Text.RegularExpressions;

namespace Editor.Core.Snippets;

/// <summary>
/// Represents a parsed tab stop inside an expanded snippet.
/// </summary>
public record SnippetTabStop(int Index, int Line, int Column, int Length);

/// <summary>
/// Result of expanding a snippet: the text to insert and the ordered tab stops.
/// </summary>
public record SnippetExpansion(string[] Lines, IReadOnlyList<SnippetTabStop> TabStops);

/// <summary>
/// Manages snippet definitions (trigger → body) and parses/expands them.
/// Built-in snippets are registered per file extension. User snippets can be
/// registered via `:snippet {trigger} {body}` in vimrc.
/// </summary>
public class SnippetManager
{
    // trigger → raw snippet body (may contain $1, $2, ..., $0)
    private readonly Dictionary<string, string> _snippets = new(StringComparer.Ordinal);

    // Built-in snippets keyed by file extension (dot-prefixed, lower-case)
    private static readonly Dictionary<string, Dictionary<string, string>> _builtins = BuildBuiltins();

    public SnippetManager()
    {
        // Built-ins are NOT pre-loaded here; they are merged on demand via
        // GetSnippetForExtension so that user definitions can override them.
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>Register (or overwrite) a user-defined snippet.</summary>
    public void Register(string trigger, string body)
    {
        if (!string.IsNullOrEmpty(trigger))
            _snippets[trigger] = body;
    }

    /// <summary>Remove a user-defined snippet.</summary>
    public void Unregister(string trigger) => _snippets.Remove(trigger);

    public IReadOnlyDictionary<string, string> UserSnippets => _snippets;

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Try to find a snippet for <paramref name="trigger"/>.
    /// User snippets shadow built-ins.
    /// <paramref name="extension"/> should be e.g. ".cs", ".py" (lower-case).
    /// </summary>
    public bool TryGet(string trigger, string? extension, out string body)
    {
        if (_snippets.TryGetValue(trigger, out body!))
            return true;

        if (extension != null &&
            _builtins.TryGetValue(extension.ToLowerInvariant(), out var builtinMap) &&
            builtinMap.TryGetValue(trigger, out body!))
            return true;

        body = "";
        return false;
    }

    // ── Expansion ─────────────────────────────────────────────────────────────

    // Matches $N and ${N} and ${N:default} tab stop markers.
    private static readonly Regex TabStopRegex =
        new(@"\$\{(\d+)(?::[^}]*)?\}|\$(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Expand a snippet body at a given insertion point.
    /// The body may contain \n for newlines, \t for tabs, and $N / ${N} / ${N:default} markers.
    /// Returns the text split into lines and a list of tab stops ordered by stop index.
    /// $0 is the final position; if absent a $0 is appended at the end.
    /// </summary>
    public static SnippetExpansion Expand(string body, int insertLine, int insertCol, int tabSize, bool expandTab)
    {
        // Normalise newline style
        body = body.Replace("\\n", "\n").Replace("\\t", "\t");

        // Build the expanded text, collecting tab stop positions as we go.
        var tabStops = new Dictionary<int, (int absLine, int absCol, int len)>();

        var sb = new StringBuilder();
        int currentLine = insertLine;
        int currentCol = insertCol;

        int pos = 0;
        foreach (Match m in TabStopRegex.Matches(body))
        {
            // Append literal text before the match
            AppendLiteral(body[pos..m.Index], sb, ref currentLine, ref currentCol, insertLine, insertCol, tabSize, expandTab);

            int stopIdx = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);

            // Extract optional default text: ${N:default} → "default"
            string defaultText = "";
            if (m.Groups[1].Success)
            {
                var fullStop = m.Value; // "${N:default}"
                var colonIdx = fullStop.IndexOf(':');
                if (colonIdx >= 0)
                    defaultText = fullStop[(colonIdx + 1)..^1]; // strip trailing }
            }

            int stopLine = currentLine;
            int stopCol  = currentCol;

            // Insert the default text (if any)
            if (defaultText.Length > 0)
            {
                AppendLiteral(defaultText, sb, ref currentLine, ref currentCol, insertLine, insertCol, tabSize, expandTab);
            }

            int stopLen = currentCol - stopCol; // 0 if no default text on same line

            // Only record first occurrence of each stop index
            if (!tabStops.ContainsKey(stopIdx))
                tabStops[stopIdx] = (stopLine, stopCol, stopLen);

            pos = m.Index + m.Length;
        }

        // Remaining literal text after last marker
        AppendLiteral(body[pos..], sb, ref currentLine, ref currentCol, insertLine, insertCol, tabSize, expandTab);

        // Ensure $0 exists (final cursor position = end of inserted text)
        if (!tabStops.ContainsKey(0))
            tabStops[0] = (currentLine, currentCol, 0);

        // Convert to ordered list: $1, $2, …, $0 last
        var ordered = tabStops
            .OrderBy(kv => kv.Key == 0 ? int.MaxValue : kv.Key)
            .Select(kv => new SnippetTabStop(kv.Key, kv.Value.absLine, kv.Value.absCol, kv.Value.len))
            .ToList();

        // Split expanded text into lines
        var lines = sb.ToString().Split('\n');
        return new SnippetExpansion(lines, ordered);
    }

    // ── LSP Snippet format expansion ─────────────────────────────────────────

    /// <summary>
    /// Expand an LSP snippet-format string (insertTextFormat == 2).
    /// LSP uses ${1:placeholder}, ${1|choice1,choice2|}, $TM_FILENAME etc.
    /// We strip variable references and choice markers to plain text, keeping
    /// only numeric tab stops.
    /// </summary>
    public static SnippetExpansion ExpandLsp(string lspBody, int insertLine, int insertCol, int tabSize, bool expandTab)
    {
        // Normalise LSP-style variable references: ${VAR}, ${VAR:default}, $VAR → keep default or empty
        var normalized = NormalizeLspBody(lspBody);
        return Expand(normalized, insertLine, insertCol, tabSize, expandTab);
    }

    private static readonly Regex LspVarRegex =
        new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::([^}]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static readonly Regex LspChoiceRegex =
        new(@"\$\{(\d+)\|([^|]*)\|}", RegexOptions.Compiled);

    private static string NormalizeLspBody(string body)
    {
        // Replace ${1|a,b,c|} with ${1:a} (use first choice as default)
        body = LspChoiceRegex.Replace(body, m =>
        {
            var idx = m.Groups[1].Value;
            var first = m.Groups[2].Value.Split(',')[0];
            return $"${{{idx}:{first}}}";
        });

        // Remove variable references: ${VAR:default} → default, $VAR → ""
        body = LspVarRegex.Replace(body, m =>
        {
            if (m.Groups[2].Success) return m.Groups[2].Value; // ${VAR:default}
            return ""; // $VAR or ${VAR}
        });

        return body;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AppendLiteral(
        string text, StringBuilder sb,
        ref int line, ref int col,
        int insertLine, int insertCol,
        int tabSize, bool expandTab)
    {
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                sb.Append('\n');
                line++;
                col = 0; // new line starts at column 0 (indentation handled by VimEngine)
            }
            else if (ch == '\t' && expandTab)
            {
                var spaces = new string(' ', tabSize);
                sb.Append(spaces);
                col += tabSize;
            }
            else
            {
                sb.Append(ch);
                col++;
            }
        }
    }

    // ── Built-in snippets ─────────────────────────────────────────────────────

    private static Dictionary<string, Dictionary<string, string>> BuildBuiltins()
    {
        var d = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // C# snippets
        d[".cs"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["if"]     = "if ($1)\n{\n\t$2\n}",
            ["else"]   = "else\n{\n\t$1\n}",
            ["for"]    = "for (int $1 = 0; $1 < $2; $1++)\n{\n\t$3\n}",
            ["fore"]   = "foreach (var $1 in $2)\n{\n\t$3\n}",
            ["while"]  = "while ($1)\n{\n\t$2\n}",
            ["switch"] = "switch ($1)\n{\n\tcase $2:\n\t\t$3\n\t\tbreak;\n\tdefault:\n\t\tbreak;\n}",
            ["try"]    = "try\n{\n\t$1\n}\ncatch (Exception $2)\n{\n\t$3\n}",
            ["prop"]   = "public $1 $2 { get; set; }",
            ["ctor"]   = "public $1()\n{\n\t$2\n}",
            ["class"]  = "public class $1\n{\n\t$2\n}",
            ["iface"]  = "public interface I$1\n{\n\t$2\n}",
            ["cw"]     = "Console.WriteLine($1);",
            ["svm"]    = "static void Main(string[] args)\n{\n\t$1\n}",
        };

        // Python snippets
        d[".py"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["if"]    = "if $1:\n\t$2",
            ["elif"]  = "elif $1:\n\t$2",
            ["else"]  = "else:\n\t$1",
            ["for"]   = "for $1 in $2:\n\t$3",
            ["while"] = "while $1:\n\t$2",
            ["def"]   = "def $1($2):\n\t$3",
            ["class"] = "class $1:\n\tdef __init__(self):\n\t\t$2",
            ["try"]   = "try:\n\t$1\nexcept $2 as e:\n\t$3",
            ["with"]  = "with $1 as $2:\n\t$3",
            ["main"]  = "if __name__ == '__main__':\n\t$1",
            ["pr"]    = "print($1)",
        };

        // JavaScript / TypeScript snippets (shared)
        var jsSnippets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["if"]    = "if ($1) {\n\t$2\n}",
            ["else"]  = "else {\n\t$1\n}",
            ["for"]   = "for (let $1 = 0; $1 < $2; $1++) {\n\t$3\n}",
            ["fore"]  = "for (const $1 of $2) {\n\t$3\n}",
            ["while"] = "while ($1) {\n\t$2\n}",
            ["fun"]   = "function $1($2) {\n\t$3\n}",
            ["afun"]  = "async function $1($2) {\n\t$3\n}",
            ["arr"]   = "const $1 = [$2];",
            ["obj"]   = "const $1 = {\n\t$2\n};",
            ["cl"]    = "class $1 {\n\tconstructor($2) {\n\t\t$3\n\t}\n}",
            ["log"]   = "console.log($1);",
            ["err"]   = "console.error($1);",
            ["prom"]  = "new Promise(($1resolve, $2reject) => {\n\t$3\n})",
        };
        d[".js"]  = jsSnippets;
        d[".ts"]  = jsSnippets;
        d[".jsx"] = jsSnippets;
        d[".tsx"] = jsSnippets;

        // Rust snippets
        d[".rs"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fn"]     = "fn $1($2) -> $3 {\n\t$4\n}",
            ["if"]     = "if $1 {\n\t$2\n}",
            ["else"]   = "else {\n\t$1\n}",
            ["for"]    = "for $1 in $2 {\n\t$3\n}",
            ["while"]  = "while $1 {\n\t$2\n}",
            ["match"]  = "match $1 {\n\t$2 => $3,\n\t_ => $4,\n}",
            ["struct"] = "struct $1 {\n\t$2\n}",
            ["impl"]   = "impl $1 {\n\t$2\n}",
            ["pl"]     = "println!(\"{}\", $1);",
            ["main"]   = "fn main() {\n\t$1\n}",
        };

        // Go snippets
        d[".go"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["if"]   = "if $1 {\n\t$2\n}",
            ["else"] = "else {\n\t$1\n}",
            ["for"]  = "for $1 := 0; $1 < $2; $1++ {\n\t$3\n}",
            ["fore"] = "for $1, $2 := range $3 {\n\t$4\n}",
            ["fn"]   = "func $1($2) $3 {\n\t$4\n}",
            ["st"]   = "type $1 struct {\n\t$2\n}",
            ["pl"]   = "fmt.Println($1)",
            ["main"] = "func main() {\n\t$1\n}",
        };

        // HTML snippets
        d[".html"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["html5"] = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n\t<meta charset=\"UTF-8\">\n\t<title>$1</title>\n</head>\n<body>\n\t$2\n</body>\n</html>",
            ["div"]   = "<div class=\"$1\">\n\t$2\n</div>",
            ["a"]     = "<a href=\"$1\">$2</a>",
            ["img"]   = "<img src=\"$1\" alt=\"$2\">",
            ["ul"]    = "<ul>\n\t<li>$1</li>\n</ul>",
            ["form"]  = "<form action=\"$1\" method=\"post\">\n\t$2\n</form>",
        };
        d[".htm"] = d[".html"];

        // CSS snippets
        d[".css"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rule"]   = "$1 {\n\t$2\n}",
            ["media"]  = "@media $1 {\n\t$2\n}",
            ["flex"]   = "display: flex;\njustify-content: $1;\nalign-items: $2;",
            ["grid"]   = "display: grid;\ngrid-template-columns: $1;",
        };

        return d;
    }
}
