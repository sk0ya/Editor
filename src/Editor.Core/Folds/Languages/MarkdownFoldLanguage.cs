namespace Editor.Core.Folds.Languages;

/// <summary>
/// Markdown のフォールド検出。
/// ・見出し (# ## ###) → 同レベル以上の次の見出しまでを fold
/// ・コードブロック (``` ... ```) を fold
/// </summary>
public class MarkdownFoldLanguage : IFoldLanguage
{
    public string[] Extensions => [".md", ".markdown"];

    public IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();
        AddHeadingFolds(lines, result);
        AddCodeBlockFolds(lines, result);
        return result;
    }

    private static int HeadingLevel(string line)
    {
        if (line.Length == 0 || line[0] != '#') return 0;
        int level = 0;
        while (level < line.Length && line[level] == '#') level++;
        // '#' の直後にスペースが必要 (ATX heading)
        return (level < line.Length && line[level] == ' ') ? level : 0;
    }

    private static void AddHeadingFolds(string[] lines, List<(int, int)> result)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            int level = HeadingLevel(lines[i]);
            if (level == 0) continue;

            // 同レベル以上の次の見出し行を探す
            int end = i + 1;
            while (end < lines.Length)
            {
                int nextLevel = HeadingLevel(lines[end]);
                if (nextLevel > 0 && nextLevel <= level) break;
                end++;
            }
            end--;  // 1つ戻して最後のコンテンツ行

            // 末尾の空行を詰める
            while (end > i && string.IsNullOrWhiteSpace(lines[end])) end--;

            if (end > i) result.Add((i, end));
        }
    }

    private static void AddCodeBlockFolds(string[] lines, List<(int, int)> result)
    {
        int? blockStart = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                if (blockStart == null)
                    blockStart = i;
                else
                {
                    if (i > blockStart.Value) result.Add((blockStart.Value, i));
                    blockStart = null;
                }
            }
        }
    }
}
