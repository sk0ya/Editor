namespace Editor.Core.Editing;

/// <summary>スクロールバーへ射影する差分の印1つ：表示行 <see cref="StartRow"/> から <see cref="RowCount"/> 行ぶんの変更。</summary>
public readonly record struct DiffOverviewMark(int StartRow, int RowCount, DiffDecorationKind Kind);

/// <summary>
/// 差分の変更位置をスクロールバーへ写すための純粋計算（VS Code の overview ruler の差分版）。
///
/// <para>左右並びの差分は全文を出すので、長いファイルでは「変更がどこにあるか」がスクロールしないと
/// 分からない。スクロールバーは文書全体を写す唯一の場所なので、ここに変更の位置を置く。</para>
///
/// <para>位置は<b>バッファ行ではなく表示行</b>で測る。左右のエディタは反対側にしか無い行のぶん空き行を
/// 挿して高さを揃えているので、表示行で測れば左右のスクロールバーの印が同じ高さに並ぶ（バッファ行で測ると、
/// 空き行の多い側ほど印がずれる）。スクロールのサムも表示行で動いているので、印とサムの位置も一致する。</para>
/// </summary>
public static class DiffOverviewMarks
{
    /// <summary>
    /// 表示行ごとの種別（変更の無い行・空き行は null）から、同じ種別が続く表示行をまとめた印を作る。
    /// </summary>
    public static IReadOnlyList<DiffOverviewMark> Build(IReadOnlyList<DiffDecorationKind?> rowKinds)
    {
        var marks = new List<DiffOverviewMark>();
        var row = 0;
        while (row < rowKinds.Count)
        {
            if (rowKinds[row] is not { } kind)
            {
                row++;
                continue;
            }
            var start = row;
            while (row < rowKinds.Count && rowKinds[row] == kind) row++;
            marks.Add(new DiffOverviewMark(start, row - start, kind));
        }
        return marks;
    }

    /// <summary>
    /// 印の縦の位置と高さ（トラック内の px）。高さは <paramref name="minHeight"/> を下回らない
    /// （1行だけの変更が 0.1px になって消えないように）。トラックの外へははみ出さない。
    /// </summary>
    public static (double Y, double Height) TrackRect(
        DiffOverviewMark mark, int rowCount, double trackHeight, double minHeight)
    {
        if (rowCount <= 0 || trackHeight <= 0) return (0, 0);
        var top = trackHeight * Math.Clamp(mark.StartRow, 0, rowCount) / rowCount;
        var bottom = trackHeight * Math.Clamp(mark.StartRow + mark.RowCount, 0, rowCount) / rowCount;
        var height = Math.Min(trackHeight, Math.Max(minHeight, bottom - top));
        var center = (top + bottom) / 2;
        var y = Math.Clamp(center - height / 2, 0, Math.Max(0, trackHeight - height));
        return (y, height);
    }
}
