namespace Editor.Core.Lsp;

/// <summary>LSP候補をユーザー入力との一致品質で安定再ランキングする純ロジック。</summary>
public static class CompletionRanker
{
    public static IReadOnlyList<LspCompletionItem> Rank(IEnumerable<LspCompletionItem> items, string prefix) =>
        items.Select((item, serverIndex) => new
            {
                Item = item,
                ServerIndex = serverIndex,
                Match = MatchRank(item.FilterText ?? item.Label, prefix),
            })
            .Where(x => x.Match < int.MaxValue)
            .OrderBy(x => x.Match)
            .ThenBy(x => x.Item.SortText ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ServerIndex)
            .Select(x => x.Item)
            .ToList();

    private static int MatchRank(string candidate, string prefix)
    {
        if (prefix.Length == 0) return 0;
        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        for (var i = 1; i <= candidate.Length - prefix.Length; i++)
        {
            if (!candidate.AsSpan(i, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!char.IsLetterOrDigit(candidate[i - 1]) ||
                (char.IsLower(candidate[i - 1]) && char.IsUpper(candidate[i]))) return 1;
            return 2;
        }
        return int.MaxValue;
    }
}
