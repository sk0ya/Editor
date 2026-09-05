namespace Editor.Core.Lsp;

/// <summary>
/// ホバーの説明ポップアップに並べる「その場で押せる修正」の絞り込み。
///
/// <para>サーバーは <c>only: ["quickfix"]</c> を<b>守らないことがある</b>——実測（C# の言語サーバー）で、
/// 未使用変数の警告に対して「メソッドを抽出する」「暗黙的な型の使用」といったリファクタリングまで返ってきた。
/// 警告を直したい人の目の前に、警告と関係のない書き換えを 8 件並べるのは案内ではなく妨害なので、
/// <b>種別が分かるものは quickfix 系だけ</b>に絞り、種別を名乗らないサーバーの分は落とさずに残す
/// （落とすと「修正が無い」ように見えてしまう）。</para>
///
/// <para>ポップアップでは既定で<b>電球だけ</b>を出し、押されたときにこの一覧を開く（読みに来ただけの人の
/// 目の前に候補を積み上げない）。それでも多いときは頭から数件だけ出す——全件は Alt+Enter が持っている。</para>
/// </summary>
public static class HoverFixSelection
{
    /// <summary>ポップアップに並べる上限。これを超えた分は件数だけ知らせる。</summary>
    public const int MaxFixes = 8;

    /// <summary>表示する修正と、入りきらなかった件数。</summary>
    public static (IReadOnlyList<LspCodeAction> Shown, int Hidden) Take(
        IReadOnlyList<LspCodeAction> actions, int max = MaxFixes)
    {
        if (actions.Count == 0 || max <= 0) return ([], 0);

        var candidates = actions.Where(IsFix).ToList();
        // preferred（サーバーが「まずこれ」と言っているもの）を先頭へ。それ以外は元の順のまま。
        var ordered = candidates.Where(a => a.IsPreferred)
            .Concat(candidates.Where(a => !a.IsPreferred))
            .ToList();

        return ordered.Count <= max
            ? (ordered, 0)
            : (ordered.Take(max).ToArray(), ordered.Count - max);
    }

    /// <summary>「直す」ためのアクションか。種別を名乗らないもの（<c>Kind == null</c>）は
    /// サーバーが quickfix として返した前提で通す。</summary>
    private static bool IsFix(LspCodeAction action) =>
        action.Kind is null ||
        LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.QuickFix) ||
        LspCodeActionKinds.Matches(action.Kind, LspCodeActionKinds.SourceFixAll);
}
