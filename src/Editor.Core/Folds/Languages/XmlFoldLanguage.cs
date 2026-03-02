using System.Text.RegularExpressions;

namespace Editor.Core.Folds.Languages;

/// <summary>
/// XML / XAML のフォールド検出。
/// 開きタグ &lt;Tag&gt; と閉じタグ &lt;/Tag&gt; を対応させてフォールド領域を作る。
/// 自己閉じタグ &lt;Tag /&gt; はフォールドを生成しない。
/// </summary>
public partial class XmlFoldLanguage : IFoldLanguage
{
    public string[] Extensions => [".xml", ".xaml", ".html", ".htm", ".svg", ".csproj", ".props", ".targets"];

    // <TagName または <ns:TagName にマッチ（属性付きでもOK）
    [GeneratedRegex(@"<([\w:\.]+)(?:\s[^>]*)?>", RegexOptions.None)]
    private static partial Regex OpenTagRegex();

    // 自己閉じ <Tag ... />
    [GeneratedRegex(@"<[\w:\.]+(?:\s[^>]*)?\s*/>", RegexOptions.None)]
    private static partial Regex SelfCloseRegex();

    // </TagName>
    [GeneratedRegex(@"</([\w:\.]+)>", RegexOptions.None)]
    private static partial Regex CloseTagRegex();

    public IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();
        var stack = new Stack<(string Tag, int Line)>();
        bool inComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // XML コメント <!-- --> をスキップ
            if (inComment)
            {
                if (line.Contains("-->")) inComment = false;
                continue;
            }
            if (line.Contains("<!--"))
            {
                if (!line.Contains("-->")) inComment = true;
                continue;
            }

            // 自己閉じタグの位置を記録して開きタグ検出から除外
            var selfCloseRanges = SelfCloseRegex().Matches(line)
                .Select(m => (m.Index, m.Index + m.Length))
                .ToList();

            // 開きタグ
            foreach (Match m in OpenTagRegex().Matches(line))
            {
                // 自己閉じ範囲内なら無視
                if (selfCloseRanges.Any(r => m.Index >= r.Item1 && m.Index < r.Item2)) continue;
                // 同行に閉じタグがある場合（<Tag>value</Tag>）もフォールド不要
                string tagName = m.Groups[1].Value;
                if (line.Contains($"</{tagName}>")) continue;

                stack.Push((tagName, i));
            }

            // 閉じタグ
            foreach (Match m in CloseTagRegex().Matches(line))
            {
                string tagName = m.Groups[1].Value;

                // スタックを遡って対応する開きタグを探す（ネスト不一致に対応）
                while (stack.Count > 0 && stack.Peek().Tag != tagName)
                    stack.Pop();

                if (stack.Count > 0 && stack.Peek().Tag == tagName)
                {
                    var (_, startLine) = stack.Pop();
                    if (i > startLine) result.Add((startLine, i));
                }
            }
        }

        return result;
    }
}
