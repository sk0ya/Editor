namespace Editor.Core.Text;

/// <summary>hover 本文のブロック種別。</summary>
public enum HoverBlockKind
{
    /// <summary>本文（段落・見出し・箇条書き）。<see cref="HoverBlock.Spans"/> に装飾つきで入る。</summary>
    Text,
    /// <summary>コードフェンス。<see cref="HoverBlock.Code"/> が中身、<see cref="HoverBlock.Language"/> が情報文字列。</summary>
    Code,
    /// <summary>水平線（<c>---</c>）。シグネチャと要約の区切りとしてサーバーがよく挟む。</summary>
    Rule,
}

/// <summary>本文中の装飾。</summary>
public enum HoverSpanStyle
{
    Normal,
    Bold,
    Italic,
    /// <summary>インラインコード（<c>`x`</c>）。</summary>
    Code,
    /// <summary>ブロック内の改行。<see cref="HoverSpan.Text"/> は空。</summary>
    LineBreak,
}

/// <summary>本文の一区切り。</summary>
public readonly record struct HoverSpan(string Text, HoverSpanStyle Style);

/// <summary>hover 本文のブロック 1 つ。</summary>
public sealed record HoverBlock(
    HoverBlockKind Kind,
    IReadOnlyList<HoverSpan> Spans,
    string Code = "",
    string? Language = null);

/// <summary>
/// 言語サーバーが <c>textDocument/hover</c> で返す <b>Markdown</b> を、表示用のブロック列へ直す。
///
/// <para>サーバーはシグネチャを <c>```csharp … ```</c> のコードフェンスに入れ、その下に要約を書く
/// （プレーンテキストで返すサーバーもある——その場合は素通しの Text ブロック 1 つになる）。
/// 素のまま出すとフェンス記号や <c>\_value</c> のようなエスケープがそのまま見えるので、
/// ここで構造（コード／本文／区切り）と装飾（太字・斜体・インラインコード）に分解する。</para>
///
/// <para><b>斜体はアスタリスクだけ</b>を見る。<c>_</c> 版まで拾うと <c>_value</c> や
/// <c>MAX_LEN</c> のような識別子が斜体化して<b>文字が消える</b>ため、識別子を守る側に倒している。</para>
/// </summary>
public static class HoverMarkdown
{
    /// <summary>Markdown をブロック列へ。中身が無ければ空。</summary>
    public static IReadOnlyList<HoverBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<HoverBlock>();
        var paragraph = new List<HoverSpan>();

        void FlushParagraph()
        {
            TrimTrailingBreaks(paragraph);
            if (paragraph.Count > 0) blocks.Add(new HoverBlock(HoverBlockKind.Text, paragraph.ToArray()));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            var trimmed = raw.TrimStart();

            if (TryReadFence(trimmed, out var fence))
            {
                FlushParagraph();
                var code = new List<string>();
                i++;
                for (; i < lines.Length; i++)
                {
                    var body = lines[i].TrimEnd();
                    if (TryReadFence(body.TrimStart(), out var closing) && closing.Length == 0) break;
                    code.Add(body);
                }
                blocks.Add(new HoverBlock(
                    HoverBlockKind.Code, [], string.Join("\n", TrimBlankEdges(code)), Normalize(fence)));
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushParagraph();
                // 先頭の区切りと連続する区切りは、間に何も無いので出さない。
                if (blocks.Count > 0 && blocks[^1].Kind != HoverBlockKind.Rule)
                    blocks.Add(new HoverBlock(HoverBlockKind.Rule, []));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (paragraph.Count > 0) paragraph.Add(new HoverSpan("", HoverSpanStyle.LineBreak));
            AppendLine(paragraph, raw);
        }

        FlushParagraph();
        while (blocks.Count > 0 && blocks[^1].Kind == HoverBlockKind.Rule)
            blocks.RemoveAt(blocks.Count - 1);
        return blocks;
    }

    /// <summary>ステータスバーなど 1 行しか出せない場所のための素のテキスト。</summary>
    public static string PlainText(string? markdown)
    {
        var parts = new List<string>();
        foreach (var block in Parse(markdown))
        {
            if (block.Kind == HoverBlockKind.Code) { parts.Add(block.Code); continue; }
            if (block.Kind == HoverBlockKind.Rule) continue;
            var text = new System.Text.StringBuilder();
            foreach (var span in block.Spans)
                text.Append(span.Style == HoverSpanStyle.LineBreak ? "\n" : span.Text);
            parts.Add(text.ToString());
        }
        return string.Join("\n", parts);
    }

    private static void AppendLine(List<HoverSpan> spans, string line)
    {
        var indent = line.Length - line.TrimStart().Length;
        var text = line.TrimStart();

        // 見出しは行まるごと太字にする（ポップアップにフォントサイズの段は作らない）。
        var heading = 0;
        while (heading < text.Length && text[heading] == '#') heading++;
        if (heading is > 0 and <= 6 && heading < text.Length && text[heading] == ' ')
        {
            AppendInline(spans, text[(heading + 1)..].TrimStart(), HoverSpanStyle.Bold);
            return;
        }

        // 引用符（サーバーが注記に使う）は記号を落として本文だけ残す。
        if (text.StartsWith("> ", StringComparison.Ordinal)) text = text[2..];

        // 箇条書きの記号は、等幅でない本文フォントでも列が揃うように「・」へ寄せる。
        if (text.Length > 2 && (text[0] is '-' or '*' or '+') && text[1] == ' ')
            text = "・" + text[2..];

        if (indent > 0) spans.Add(new HoverSpan(new string(' ', indent), HoverSpanStyle.Normal));
        AppendInline(spans, text, HoverSpanStyle.Normal);
    }

    private static void AppendInline(List<HoverSpan> spans, string text, HoverSpanStyle baseStyle)
    {
        var plain = new System.Text.StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            spans.Add(new HoverSpan(plain.ToString(), baseStyle));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Markdown のエスケープ（サーバーは識別子の "_" を "\_" と書いて返す）を外す。
            if (c == '\\' && i + 1 < text.Length && EscapablePunctuation.Contains(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '`' && TryReadDelimited(text, i, "`", out var code, out var codeEnd))
            {
                FlushPlain();
                spans.Add(new HoverSpan(code, HoverSpanStyle.Code));
                i = codeEnd;
                continue;
            }

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' &&
                TryReadDelimited(text, i, "**", out var bold, out var boldEnd))
            {
                FlushPlain();
                AppendInline(spans, bold, HoverSpanStyle.Bold);
                i = boldEnd;
                continue;
            }

            if (c == '*' && IsItalicOpening(text, i) &&
                TryReadDelimited(text, i, "*", out var italic, out var italicEnd))
            {
                FlushPlain();
                AppendInline(spans, italic, HoverSpanStyle.Italic);
                i = italicEnd;
                continue;
            }

            // リンクは押せないので、表示文字だけ残して URL は落とす。
            if (c == '[' && TryReadLink(text, i, out var label, out var linkEnd))
            {
                FlushPlain();
                AppendInline(spans, label, baseStyle);
                i = linkEnd;
                continue;
            }

            plain.Append(c);
        }

        FlushPlain();
    }

    /// <summary>その <c>*</c> が斜体の開始になれるか。CommonMark では空白が続く <c>*</c> は開始にならない——
    /// これを見ないと <c>width * height * depth</c> が斜体と読まれ、<b>アスタリスクごと消える</b>
    /// （アンダースコアを斜体扱いしないのと同じ理由。本文を失う方が、装飾を諦めるより悪い）。</summary>
    private static bool IsItalicOpening(string text, int start)
    {
        // 開き記号の直後は非空白、かつ閉じ記号の直前も非空白であること。
        if (start + 1 >= text.Length || char.IsWhiteSpace(text[start + 1])) return false;
        var close = text.IndexOf('*', start + 1);
        return close > start + 1 && !char.IsWhiteSpace(text[close - 1]);
    }

    /// <summary><paramref name="start"/> の区切り記号から次の同じ記号までを取り出す。閉じが無ければ false。</summary>
    private static bool TryReadDelimited(string text, int start, string delimiter, out string inner, out int end)
    {
        inner = "";
        end = start;
        var from = start + delimiter.Length;
        var close = text.IndexOf(delimiter, from, StringComparison.Ordinal);
        if (close < 0 || close == from) return false;
        inner = text[from..close];
        end = close + delimiter.Length - 1;
        return true;
    }

    private static bool TryReadLink(string text, int start, out string label, out int end)
    {
        label = "";
        end = start;
        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;
        var urlEnd = text.IndexOf(')', close + 2);
        if (urlEnd < 0) return false;
        label = text[(start + 1)..close];
        end = urlEnd;
        return true;
    }

    /// <summary>コードフェンス行なら true。<paramref name="info"/> は情報文字列（閉じフェンスでは空）。</summary>
    private static bool TryReadFence(string trimmed, out string info)
    {
        info = "";
        var marker = trimmed.StartsWith("```", StringComparison.Ordinal) ? "```"
            : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~"
            : null;
        if (marker is null) return false;
        var rest = trimmed[marker.Length..].TrimStart('`', '~').Trim();
        info = rest;
        return true;
    }

    private static bool IsRule(string trimmed) =>
        trimmed.Length >= 3 &&
        (trimmed.All(c => c == '-') || trimmed.All(c => c == '*') || trimmed.All(c => c == '_'));

    private static string? Normalize(string info)
    {
        if (info.Length == 0) return null;
        // "csharp {…}" のような属性つき情報文字列は先頭語だけを見る。
        var space = info.IndexOfAny([' ', '\t', ',']);
        var name = (space < 0 ? info : info[..space]).Trim().ToLowerInvariant();
        return name.Length == 0 ? null : name;
    }

    private static List<string> TrimBlankEdges(List<string> lines)
    {
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static void TrimTrailingBreaks(List<HoverSpan> spans)
    {
        while (spans.Count > 0 && spans[^1].Style == HoverSpanStyle.LineBreak)
            spans.RemoveAt(spans.Count - 1);
    }

    private const string EscapablePunctuation = @"\`*_{}[]()#+-.!<>|~";
}
