using Editor.Core.Lsp;

namespace Editor.Controls.Lsp;

/// <summary>補完候補の合流と、受け入れ時の置換範囲の補正（純ロジック）。</summary>
internal static class CompletionMerge
{
    /// <summary>ホストの追加候補を並行に走らせる。失敗・取り消しは「候補なし」として扱い、
    /// LSP 側の結果を巻き込んで落とさない。</summary>
    public static Task<IReadOnlyList<LspCompletionItem>> Start(
        Func<int, int, CancellationToken, Task<IReadOnlyList<LspCompletionItem>>>? provider,
        int line, int character, CancellationToken ct)
    {
        if (provider is null)
            return Task.FromResult<IReadOnlyList<LspCompletionItem>>([]);
        return Run();

        async Task<IReadOnlyList<LspCompletionItem>> Run()
        {
            try { return await provider(line, character, ct); }
            catch { return []; }
        }
    }

    /// <summary>LSP の候補へホストの候補を足す。ラベルと種類の両方が同じものだけを重複とみなし LSP 側を残す
    /// ——キーワードの <c>foreach</c> と雛形（Snippet）の <c>foreach</c> は別物なので両方並べる。</summary>
    public static IReadOnlyList<LspCompletionItem> Merge(
        IReadOnlyList<LspCompletionItem> supplemental, IReadOnlyList<LspCompletionItem> items)
    {
        if (supplemental.Count == 0) return items;
        if (items.Count == 0) return supplemental;
        var keys = new HashSet<(string, CompletionItemKind)>(items.Select(i => (i.Label, i.Kind)));
        var merged = new List<LspCompletionItem>(items.Count + supplemental.Count);
        merged.AddRange(items);
        merged.AddRange(supplemental.Where(i => !keys.Contains((i.Label, i.Kind))));
        return merged;
    }

    /// <summary>
    /// 補完の TextEdit は要求した時点の位置で作られている。ポップアップが出てから打ち足した文字は
    /// 置換範囲の外にあるので、範囲の終わりがキャレットより手前（同じ行）ならキャレットまで伸ばす——
    /// 伸ばさないと、打ち足した <c>if</c> が残って <c>if (x)if</c> のように二重になる（VS Code と同じ扱い）。
    /// </summary>
    public static LspRange ExtendToCaret(LspRange range, int caretLine, int caretColumn)
    {
        if (range.End.Line != caretLine || range.Start.Line > caretLine)
            return range;
        if (range.End.Character >= caretColumn)
            return range;
        return range with { End = new LspPosition(caretLine, caretColumn) };
    }
}
