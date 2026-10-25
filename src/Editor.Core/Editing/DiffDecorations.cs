namespace Editor.Core.Editing;

/// <summary>差分表示で1行に付ける種別。行の背景色が変わる。</summary>
public enum DiffDecorationKind
{
    /// <summary>反対側に無い行（新側に足された行）。</summary>
    Added,
    /// <summary>反対側から消えた行（旧側にしか無い行）。</summary>
    Removed,
}

/// <summary>1行の中の文字範囲（UTF-16 の桁と長さ）。行内差分で「実際に変わった文字」を示す。
/// 「行末まで」は大きな <see cref="Length"/>（<see cref="int.MaxValue"/> でよい）で表せる。</summary>
public readonly record struct DiffInlineRange(int Start, int Length)
{
    /// <summary>終わりの桁（排他）。<c>Start + Length</c> が int を溢れるときは <see cref="int.MaxValue"/> に留める
    /// （溢れて負になると、範囲が黙って描かれなくなる）。</summary>
    public int End => (int)Math.Clamp((long)Start + Length, int.MinValue, int.MaxValue);
}

/// <summary>
/// 2つのエディタを左右に並べて差分を見せるための装飾。ホストが左右それぞれに1つずつ渡す。
///
/// <para><see cref="Lines"/> はバッファ行（0始まり）→ 種別。<see cref="SpacersBefore"/> は
/// バッファ行 N（0始まり）の<b>直前</b>に挿し込む空き行の数で、N = 行数（最終行の次）は末尾に付く。
/// 空き行は本文を持たない表示だけの行（CodeLens の注釈行と同じ扱い）で、キャレットも選択も乗らない。
/// 反対側にしか無い行の数だけこちらへ空き行を入れれば、左右の行の高さが揃う。</para>
///
/// <para><see cref="InlineChanges"/> はバッファ行 → その行の中で実際に変わった文字の範囲（行内差分）。
/// 行の背景（<see cref="Lines"/> の種別の色）の上に、同じ色相の濃い色で重ねる。種別の無い行に
/// 付けた範囲は描かない（行内差分は書き換えた行にしか意味が無い）。</para>
///
/// <para>装飾は渡したときのバッファ行を指す。編集で行がずれたら、ホストが差分を取り直して渡し直す
/// （それまでの間は古い位置に出る）。</para>
/// </summary>
public sealed class DiffDecorations
{
    private static readonly IReadOnlyDictionary<int, IReadOnlyList<DiffInlineRange>> NoInlineChanges
        = new Dictionary<int, IReadOnlyList<DiffInlineRange>>();

    public static DiffDecorations Empty { get; } = new(
        new Dictionary<int, DiffDecorationKind>(), new Dictionary<int, int>());

    public DiffDecorations(
        IReadOnlyDictionary<int, DiffDecorationKind> lines,
        IReadOnlyDictionary<int, int> spacersBefore)
        : this(lines, spacersBefore, null)
    {
    }

    public DiffDecorations(
        IReadOnlyDictionary<int, DiffDecorationKind> lines,
        IReadOnlyDictionary<int, int> spacersBefore,
        IReadOnlyDictionary<int, IReadOnlyList<DiffInlineRange>>? inlineChanges)
    {
        Lines = lines;
        SpacersBefore = spacersBefore;
        InlineChanges = inlineChanges ?? NoInlineChanges;
    }

    public IReadOnlyDictionary<int, DiffDecorationKind> Lines { get; }

    public IReadOnlyDictionary<int, int> SpacersBefore { get; }

    /// <summary>バッファ行 → 行内差分の範囲（昇順・重なり無し）。無ければ空。</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<DiffInlineRange>> InlineChanges { get; }

    /// <summary>
    /// 空き行が反対側の何を表すか（スクロールバーの変更位置の印で、空き行をどの色で示すか）。null なら印を付けない。
    /// 右（新側）のエディタの空き行は左で消えた行、左（旧側）の空き行は右で足された行にあたる——片側だけに
    /// スクロールバーを出すホストでも、削除だけの変更の位置が見えるようにするためのもの。
    /// </summary>
    public DiffDecorationKind? SpacerKind { get; init; }

    /// <summary>
    /// バッファ行 → 移動したブロックの行か（true＝移動元・false＝移動先）。削除と追加が実は同じブロックの移動であるものを、
    /// ホストが見分けて渡す（git の <c>--color-moved</c> と同じ考え方）。<see cref="Lines"/> の種別（追加／削除）はそのまま持ち、
    /// 行の背景だけを移動の色にする。移動の行には行内差分を描かない（相手は別の場所にあり、同じかたまりの行と比べた範囲は嘘になる）。
    /// </summary>
    public IReadOnlyDictionary<int, bool> MovedLines { get; init; } = NoMovedLines;

    private static readonly IReadOnlyDictionary<int, bool> NoMovedLines = new Dictionary<int, bool>();

    /// <summary>描くものが何も無いか。<see cref="InlineChanges"/> は種別のある行にしか描かれないので数えない
    /// （行内差分だけを渡しても何も出ない——それを「空でない」と答えると、描画と食い違う）。</summary>
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
