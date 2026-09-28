namespace Editor.Core.Editing;

/// <summary>差分表示で1行に付ける種別。行の背景色が変わる。</summary>
public enum DiffDecorationKind
{
    /// <summary>反対側に無い行（新側に足された行）。</summary>
    Added,
    /// <summary>反対側から消えた行（旧側にしか無い行）。</summary>
    Removed,
}

/// <summary>
/// 2つのエディタを左右に並べて差分を見せるための装飾。ホストが左右それぞれに1つずつ渡す。
///
/// <para><see cref="Lines"/> はバッファ行（0始まり）→ 種別。<see cref="SpacersBefore"/> は
/// バッファ行 N（0始まり）の<b>直前</b>に挿し込む空き行の数で、N = 行数（最終行の次）は末尾に付く。
/// 空き行は本文を持たない表示だけの行（CodeLens の注釈行と同じ扱い）で、キャレットも選択も乗らない。
/// 反対側にしか無い行の数だけこちらへ空き行を入れれば、左右の行の高さが揃う。</para>
///
/// <para>装飾は渡したときのバッファ行を指す。編集で行がずれたら、ホストが差分を取り直して渡し直す
/// （それまでの間は古い位置に出る）。</para>
/// </summary>
public sealed class DiffDecorations
{
    public static DiffDecorations Empty { get; } = new(
        new Dictionary<int, DiffDecorationKind>(), new Dictionary<int, int>());

    public DiffDecorations(
        IReadOnlyDictionary<int, DiffDecorationKind> lines,
        IReadOnlyDictionary<int, int> spacersBefore)
    {
        Lines = lines;
        SpacersBefore = spacersBefore;
    }

    public IReadOnlyDictionary<int, DiffDecorationKind> Lines { get; }

    public IReadOnlyDictionary<int, int> SpacersBefore { get; }

    public bool IsEmpty => Lines.Count == 0 && SpacersBefore.Count == 0;

    /// <summary>空き行の配置が同じか（行の背景だけが変わったなら、行レイアウトは組み直さなくてよい）。</summary>
    public bool HasSameSpacers(DiffDecorations other)
    {
        if (SpacersBefore.Count != other.SpacersBefore.Count) return false;
        foreach (var (line, count) in SpacersBefore)
            if (!other.SpacersBefore.TryGetValue(line, out var otherCount) || otherCount != count)
                return false;
        return true;
    }
}
