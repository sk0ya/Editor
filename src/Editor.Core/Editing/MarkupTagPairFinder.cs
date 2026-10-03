using System.Text;

namespace Editor.Core.Editing;

/// <summary>タグの読み方の違い。HTML は名前の大小を区別せず、空要素（br/img…）は閉じタグを持たず、
/// script/style の中身はタグとして読まない。</summary>
public enum MarkupDialect { Xml, Html }

/// <summary>
/// 連動編集のフォールバック: 言語サーバーが無い（または linkedEditingRange を持たない）マークアップ
/// （とくに XAML / XML）で、カーソルのあるタグ名と対になるタグ名の範囲をテキストから求める。
///
/// <para>文書の先頭から1回なめてタグを拾い、スタックで開き/閉じを対応づける。行単位の正規表現
/// （<see cref="Matchit.TagMatcher"/>）と違い、複数行にまたがるコメント・CDATA・処理命令・
/// 属性値の中の <c>&lt;</c> / <c>&gt;</c> をタグと取り違えない。自己終了タグは対にならない。
/// 文書全体をなめるのは連動を<b>始める</b>瞬間だけで、打鍵ごとではない。</para>
/// </summary>
public static class MarkupTagPairFinder
{
    /// <summary>これより大きい文書ではフォールバックを諦める（打鍵の経路で走るため）。</summary>
    public const int MaxCharacters = 400_000;

    private static readonly HashSet<string> s_htmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm",
    };

    private static readonly HashSet<string> s_xmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".xaml", ".axaml", ".xhtml", ".svg", ".xsd", ".xsl", ".xslt", ".resx", ".config",
        ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".proj", ".props", ".targets", ".nuspec",
        ".manifest", ".plist", ".wxs", ".xlf", ".xliff", ".storyboard",
    };

    private static readonly HashSet<string> s_htmlVoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param",
        "source", "track", "wbr",
    };

    /// <summary>フォールバックの対象になるファイルなら、その読み方。JSX/TSX は対象外 —
    /// <c>a &lt; b</c> やジェネリクスと区別できないので言語サーバーに任せる。</summary>
    public static MarkupDialect? DialectFor(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;
        if (s_htmlExtensions.Contains(ext)) return MarkupDialect.Html;
        if (s_xmlExtensions.Contains(ext)) return MarkupDialect.Xml;
        return null;
    }

    public static bool IsTagNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    private static bool IsTagNameStart(char c) => char.IsLetter(c) || c is '_' or ':';

    /// <summary>
    /// 安い事前判定: 行 <paramref name="line"/> の <paramref name="column"/> がタグ名（開き・閉じ）の
    /// 中か直後か。文書をなめる前に、1行だけでふるい落とすのに使う。
    /// </summary>
    public static bool IsAtTagName(string line, int column)
    {
        if (column < 0 || column > line.Length) return false;
        int start = column;
        while (start > 0 && IsTagNameChar(line[start - 1])) start--;
        if (start == 0) return false;
        int lt = line[start - 1] == '/' ? start - 2 : start - 1;
        if (lt < 0 || line[lt] != '<') return false;
        int end = column;
        while (end < line.Length && IsTagNameChar(line[end])) end++;
        return end > start && IsTagNameStart(line[start]);
    }

    /// <summary>
    /// カーソル（<paramref name="line"/>, <paramref name="column"/>）のあるタグ名と、対になるタグ名の範囲。
    /// カーソルがタグ名に無い・対が無い・自己終了タグ・文書が大きすぎるときは null。
    /// </summary>
    public static LinkedEditingRanges? Find(
        Func<int, string> getLine, int lineCount, int line, int column, MarkupDialect dialect)
    {
        if (line < 0 || line >= lineCount) return null;
        if (!IsAtTagName(getLine(line), column)) return null;

        var sb = new StringBuilder();
        var lineStarts = new int[lineCount];
        for (int l = 0; l < lineCount; l++)
        {
            lineStarts[l] = sb.Length;
            sb.Append(getLine(l));
            if (sb.Length > MaxCharacters) return null;
            if (l < lineCount - 1) sb.Append('\n');
        }
        var text = sb.ToString();
        int caret = lineStarts[line] + column;

        var tags = Scan(text, dialect);
        var html = dialect == MarkupDialect.Html;
        var pairOf = new int[tags.Count];
        Array.Fill(pairOf, -1);
        var stack = new List<int>();
        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            if (!t.IsClose)
            {
                if (t.SelfClosing || (html && s_htmlVoidElements.Contains(t.Name))) continue;
                stack.Add(i);
                continue;
            }
            // 閉じタグは、同じ名前の開きが積まれていればそこまでを閉じる（間の閉じ忘れは暗黙に閉じる）。
            // 対応する開きが無い迷子の閉じタグは無視する。
            for (int s = stack.Count - 1; s >= 0; s--)
            {
                if (!NameEquals(tags[stack[s]].Name, t.Name, html)) continue;
                pairOf[stack[s]] = i;
                pairOf[i] = stack[s];
                stack.RemoveRange(s, stack.Count - s);
                break;
            }
        }

        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            if (caret < t.NameStart || caret > t.NameEnd) continue;
            if (pairOf[i] < 0) return null;
            var other = tags[pairOf[i]];
            if (!string.Equals(t.Name, other.Name, StringComparison.Ordinal)) return null; // HTML の大小違いは連動させない
            return new LinkedEditingRanges([ToRange(t, lineStarts), ToRange(other, lineStarts)]);
        }
        return null;
    }

    private static bool NameEquals(string a, string b, bool html) =>
        string.Equals(a, b, html ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static LinkedRange ToRange(Tag t, int[] lineStarts)
    {
        int idx = Array.BinarySearch(lineStarts, t.NameStart);
        int line = idx >= 0 ? idx : ~idx - 1;
        // 空行が続くと同じ開始位置が並ぶ — 位置を含む最後の行を取る
        while (line + 1 < lineStarts.Length && lineStarts[line + 1] <= t.NameStart) line++;
        int start = t.NameStart - lineStarts[line];
        return new LinkedRange(line, start, start + (t.NameEnd - t.NameStart));
    }

    private readonly record struct Tag(int NameStart, int NameEnd, string Name, bool IsClose, bool SelfClosing);

    private static List<Tag> Scan(string text, MarkupDialect dialect)
    {
        var tags = new List<Tag>();
        int n = text.Length;
        int i = 0;
        while (i < n)
        {
            int lt = text.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= n) break;

            if (string.CompareOrdinal(text, lt, "<!--", 0, 4) == 0)
            {
                int end = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (end < 0) break; // 閉じていないコメントの先はすべてコメント
                i = end + 3;
                continue;
            }
            if (string.CompareOrdinal(text, lt, "<![CDATA[", 0, 9) == 0)
            {
                int end = text.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 3;
                continue;
            }
            if (text[lt + 1] == '?')
            {
                int end = text.IndexOf("?>", lt + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 2;
                continue;
            }
            if (text[lt + 1] == '!')
            {
                int end = text.IndexOf('>', lt + 2);
                if (end < 0) break;
                i = end + 1;
                continue;
            }

            bool isClose = text[lt + 1] == '/';
            int nameStart = lt + (isClose ? 2 : 1);
            if (nameStart >= n || !IsTagNameStart(text[nameStart])) { i = lt + 1; continue; }
            int nameEnd = nameStart;
            while (nameEnd < n && IsTagNameChar(text[nameEnd])) nameEnd++;

            // 属性部分を '>' までなめる。引用符の中の '<' '>' は文字。引用符の外の '<' は
            // このタグが書きかけだった印なので捨て、その '<' から読み直す。
            int p = nameEnd;
            char quote = '\0';
            bool afterEquals = false;
            int gt = -1;
            int restart = -1;
            while (p < n)
            {
                char c = text[p];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '>') { gt = p; break; }
                else if (c == '<') { restart = p; break; }
                else if (!isClose && afterEquals && (c == '"' || c == '\''))
                {
                    quote = c;
                    afterEquals = false;
                }
                else if (c == '=') afterEquals = true;
                else if (!char.IsWhiteSpace(c)) afterEquals = false;
                p++;
            }
            if (gt < 0)
            {
                if (restart < 0) break;
                i = restart;
                continue;
            }

            var name = text.Substring(nameStart, nameEnd - nameStart);
            bool selfClosing = !isClose && text[gt - 1] == '/';
            tags.Add(new Tag(nameStart, nameEnd, name, isClose, selfClosing));
            i = gt + 1;

            if (dialect == MarkupDialect.Html && !isClose && !selfClosing &&
                (name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                // 中身は生テキスト（if (a < b) をタグと読まない）。閉じタグの手前まで飛ばす。
                int end = text.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (end < 0) break;
                i = end;
            }
        }
        return tags;
    }
}
