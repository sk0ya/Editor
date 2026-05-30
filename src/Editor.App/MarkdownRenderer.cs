using System.Text;
using System.Text.RegularExpressions;

namespace Editor.App;

internal static class MarkdownRenderer
{
    public static string RenderToHtml(string markdown, string? title = null, string styleName = "Dracula")
    {
        var body = new StringBuilder();
        ProcessBlocks(markdown.Replace("\r\n", "\n").Replace("\r", "\n"), body);
        return BuildPage(body.ToString(), title, styleName);
    }

    private static void ProcessBlocks(string text, StringBuilder html)
    {
        var lines = text.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Fenced code block (``` or ~~~)
            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                var fence = line.TrimStart();
                var fenceChar = fence[0];
                var lang = fence.Length > 3 ? Encode(fence[3..].Trim()) : "";
                var cls = lang.Length > 0 ? $" class=\"language-{lang}\"" : "";
                html.Append($"<pre><code{cls}>");
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fenceChar.ToString() + fenceChar + fenceChar))
                {
                    html.Append(Encode(lines[i])).Append('\n');
                    i++;
                }
                i++;
                html.AppendLine("</code></pre>");
                continue;
            }

            // ATX heading
            var hm = HeaderRe.Match(line);
            if (hm.Success)
            {
                var level = hm.Groups[1].Length;
                var id = hm.Groups[2].Value.Trim().ToLowerInvariant().Replace(' ', '-');
                html.AppendLine($"<h{level} id=\"{Encode(id)}\">{Inline(hm.Groups[2].Value.Trim())}</h{level}>");
                i++;
                continue;
            }

            // Horizontal rule (must come before unordered list check)
            if (HrRe.IsMatch(line.Trim()) && !UlRe.IsMatch(line))
            {
                html.AppendLine("<hr>");
                i++;
                continue;
            }

            // Blockquote
            if (line.StartsWith(">"))
            {
                var qLines = new List<string>();
                while (i < lines.Length && lines[i].StartsWith(">"))
                {
                    var l = lines[i];
                    qLines.Add(l.Length > 1 && l[1] == ' ' ? l[2..] : l[1..]);
                    i++;
                }
                var inner = new StringBuilder();
                ProcessBlocks(string.Join("\n", qLines), inner);
                html.Append("<blockquote>").Append(inner).AppendLine("</blockquote>");
                continue;
            }

            // Unordered list
            if (UlRe.IsMatch(line))
            {
                html.AppendLine("<ul>");
                while (i < lines.Length && UlRe.IsMatch(lines[i]))
                {
                    var m = UlRe.Match(lines[i]);
                    html.AppendLine($"<li>{Inline(m.Groups[1].Value)}</li>");
                    i++;
                }
                html.AppendLine("</ul>");
                continue;
            }

            // Ordered list
            if (OlRe.IsMatch(line))
            {
                html.AppendLine("<ol>");
                while (i < lines.Length && OlRe.IsMatch(lines[i]))
                {
                    var m = OlRe.Match(lines[i]);
                    html.AppendLine($"<li>{Inline(m.Groups[1].Value)}</li>");
                    i++;
                }
                html.AppendLine("</ol>");
                continue;
            }

            // Table (header followed by alignment row)
            if (i + 1 < lines.Length && line.Contains('|') && TableSepRe.IsMatch(lines[i + 1]))
            {
                html.AppendLine("<table><thead><tr>");
                foreach (var cell in SplitRow(line))
                    html.AppendLine($"<th>{Inline(cell)}</th>");
                html.AppendLine("</tr></thead><tbody>");
                i += 2;
                while (i < lines.Length && lines[i].Contains('|'))
                {
                    html.AppendLine("<tr>");
                    foreach (var cell in SplitRow(lines[i]))
                        html.AppendLine($"<td>{Inline(cell)}</td>");
                    html.AppendLine("</tr>");
                    i++;
                }
                html.AppendLine("</tbody></table>");
                continue;
            }

            // Empty line
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            // Paragraph (collect consecutive non-special lines)
            var paraLines = new List<string>();
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !HeaderRe.IsMatch(lines[i])
                   && !lines[i].TrimStart().StartsWith("```")
                   && !lines[i].TrimStart().StartsWith("~~~")
                   && !lines[i].StartsWith(">")
                   && !UlRe.IsMatch(lines[i])
                   && !OlRe.IsMatch(lines[i])
                   && !(HrRe.IsMatch(lines[i].Trim()) && !UlRe.IsMatch(lines[i]))
                   && !(lines[i].Contains('|') && i + 1 < lines.Length && TableSepRe.IsMatch(lines[i + 1])))
            {
                paraLines.Add(lines[i]);
                i++;
            }

            if (paraLines.Count > 0)
                html.AppendLine($"<p>{Inline(string.Join(" ", paraLines))}</p>");
        }
    }

    private static string Inline(string text)
    {
        // Extract and protect code spans
        var codes = new List<string>();
        text = CodeSpanRe.Replace(text, m =>
        {
            codes.Add($"<code>{Encode(m.Groups[1].Value)}</code>");
            return $"\x01{codes.Count - 1}\x01";
        });

        // Images before links
        text = ImageRe.Replace(text, m =>
            $"<img src=\"{m.Groups[2].Value}\" alt=\"{Encode(m.Groups[1].Value)}\">");

        // Links
        text = LinkRe.Replace(text, m =>
            $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");

        // Bold (**text** and __text__)
        text = BoldStarRe.Replace(text, "<strong>$1</strong>");
        text = BoldUnderRe.Replace(text, "<strong>$1</strong>");

        // Italic (*text* and _text_) — only after bold to avoid over-matching
        text = ItalicStarRe.Replace(text, "<em>$1</em>");
        text = ItalicUnderRe.Replace(text, "<em>$1</em>");

        // Strikethrough
        text = StrikeRe.Replace(text, "<del>$1</del>");

        // Restore code spans
        for (int i = 0; i < codes.Count; i++)
            text = text.Replace($"\x01{i}\x01", codes[i]);

        return text;
    }

    private static string[] SplitRow(string line)
    {
        var s = line.Trim();
        if (s.StartsWith("|")) s = s[1..];
        if (s.EndsWith("|")) s = s[..^1];
        return s.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static string Encode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static readonly Regex HeaderRe    = new(@"^(#{1,6})\s+(.+)$",        RegexOptions.Compiled);
    private static readonly Regex HrRe        = new(@"^(\-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);
    private static readonly Regex UlRe        = new(@"^[ \t]*[\-\*\+]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex OlRe        = new(@"^\d+\.\s+(.+)$",           RegexOptions.Compiled);
    private static readonly Regex TableSepRe  = new(@"^\|?[\s\-:\|]+\|?$",       RegexOptions.Compiled);
    private static readonly Regex CodeSpanRe  = new(@"`([^`]+)`",                RegexOptions.Compiled);
    private static readonly Regex ImageRe     = new(@"!\[([^\]]*)\]\(([^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex LinkRe      = new(@"\[([^\]]+)\]\(([^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex BoldStarRe  = new(@"\*\*(.+?)\*\*",            RegexOptions.Compiled);
    private static readonly Regex BoldUnderRe = new(@"__(.+?)__",                RegexOptions.Compiled);
    private static readonly Regex ItalicStarRe  = new(@"\*([^\s\*].*?[^\s\*]?)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex ItalicUnderRe = new(@"_([^\s_].*?[^\s_]?)_(?!_)",      RegexOptions.Compiled);
    private static readonly Regex StrikeRe    = new(@"~~(.+?)~~",                RegexOptions.Compiled);

    private static string BuildPage(string body, string? title, string styleName)
    {
        var t = title != null ? Encode(title) : "Preview";
        var css = PreviewCss(styleName);
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <title>{{t}}</title>
            <style>
            {{css}}
            </style>
            <script>
            (() => {
                let suppressScrollMessage = false;

                function scrollRatio() {
                    const doc = document.documentElement;
                    const max = Math.max(0, doc.scrollHeight - window.innerHeight);
                    return max <= 0 ? 0 : window.scrollY / max;
                }

                window.setMarkdownPreviewScrollRatio = ratio => {
                    const doc = document.documentElement;
                    const max = Math.max(0, doc.scrollHeight - window.innerHeight);
                    suppressScrollMessage = true;
                    window.scrollTo(0, max * Math.min(1, Math.max(0, Number(ratio) || 0)));
                    window.setTimeout(() => suppressScrollMessage = false, 80);
                };

                window.addEventListener('scroll', () => {
                    if (suppressScrollMessage || !window.chrome?.webview) return;
                    window.chrome.webview.postMessage({
                        type: 'markdownPreviewScroll',
                        ratio: scrollRatio()
                    });
                }, { passive: true });
            })();
            </script>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }

    private static string PreviewCss(string styleName) => NormalizeStyle(styleName) switch
    {
        "Light" => BaseCss("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#0969DA", "#8250DF", "#953800", "#57606A", "#116329"),
        "GitHub" => BaseCss("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#0969DA", "#24292F", "#CF222E", "#57606A", "#0550AE"),
        "Dark" => BaseCss("#1E1E1E", "#D4D4D4", "#252526", "#3C3C3C", "#4FC1FF", "#DCDCAA", "#CE9178", "#9CDCFE", "#B5CEA8"),
        _ => BaseCss("#282A36", "#F8F8F2", "#1E1F29", "#44475A", "#8BE9FD", "#BD93F9", "#FFB86C", "#6272A4", "#50FA7B"),
    };

    public static string NormalizeStyle(string? styleName) =>
        styleName?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            "github" => "GitHub",
            _ => "Dracula",
        };

    private static string BaseCss(
        string bg,
        string fg,
        string panel,
        string border,
        string link,
        string heading,
        string strong,
        string muted,
        string code)
    {
        return $$"""
            * { box-sizing: border-box; margin: 0; padding: 0; }
            ::-webkit-scrollbar { width: 10px; height: 10px; }
            ::-webkit-scrollbar-track { background: {{panel}}; }
            ::-webkit-scrollbar-thumb { background: {{border}}; border-radius: 2px; }
            ::-webkit-scrollbar-thumb:hover { background: {{muted}}; }
            ::-webkit-scrollbar-corner { background: {{panel}}; }
            pre::-webkit-scrollbar { height: 8px; }
            pre::-webkit-scrollbar-track { background: {{panel}}; }
            pre::-webkit-scrollbar-thumb { background: {{border}}; border-radius: 2px; }
            pre::-webkit-scrollbar-thumb:hover { background: {{muted}}; }
            body {
                background: {{bg}};
                color: {{fg}};
                font-family: 'Segoe UI', Arial, sans-serif;
                font-size: 14px;
                line-height: 1.7;
                padding: 20px 24px 40px;
            }
            h1, h2, h3, h4, h5, h6 {
                color: {{heading}};
                font-weight: 600;
                margin-top: 20px;
                margin-bottom: 8px;
                padding-bottom: 5px;
                border-bottom: 1px solid {{border}};
            }
            h1 { font-size: 1.8em; }
            h2 { font-size: 1.4em; }
            h3 { font-size: 1.15em; border-bottom: none; }
            h4, h5, h6 { font-size: 1em; border-bottom: none; color: {{fg}}; }
            p { margin: 10px 0; }
            a { color: {{link}}; text-decoration: none; }
            a:hover { text-decoration: underline; }
            strong { color: {{strong}}; font-weight: 600; }
            em { color: {{heading}}; font-style: italic; }
            del { color: {{muted}}; text-decoration: line-through; }
            code {
                background: {{panel}};
                color: {{code}};
                padding: 1px 5px;
                border-radius: 3px;
                font-family: 'Cascadia Code', Consolas, monospace;
                font-size: 0.88em;
            }
            pre {
                background: {{panel}};
                border: 1px solid {{border}};
                border-radius: 6px;
                padding: 14px 16px;
                overflow-x: auto;
                margin: 14px 0;
            }
            pre code {
                background: none;
                padding: 0;
                color: {{fg}};
                font-size: 0.87em;
                line-height: 1.5;
            }
            blockquote {
                border-left: 4px solid {{muted}};
                margin: 14px 0;
                padding: 8px 16px;
                color: {{muted}};
                background: {{panel}};
                border-radius: 0 4px 4px 0;
            }
            blockquote p { margin: 4px 0; }
            ul, ol { padding-left: 24px; margin: 8px 0; }
            li { margin-bottom: 4px; }
            table {
                border-collapse: collapse;
                width: 100%;
                margin: 14px 0;
            }
            th, td {
                border: 1px solid {{border}};
                padding: 7px 12px;
                text-align: left;
            }
            th { background: {{panel}}; color: {{fg}}; font-weight: 600; }
            tr:nth-child(even) { background: color-mix(in srgb, {{panel}} 55%, transparent); }
            img { max-width: 100%; border-radius: 4px; display: block; margin: 8px 0; }
            hr { border: none; border-top: 1px solid {{border}}; margin: 20px 0; }
            """;
    }
}
