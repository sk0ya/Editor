namespace Editor.Core.Lsp;

/// <summary>スクロールバーに 1 本だけ引く印。<paramref name="Line"/> は 0 始まりのバッファ行、
/// <paramref name="Column"/> はその行で最初に見つかった診断の開始桁（クリックの飛び先）。</summary>
public readonly record struct DiagnosticOverviewMark(int Line, int Column, DiagnosticSeverity Severity);

/// <summary>
/// 診断を<b>スクロールバー上の印</b>へ畳む。画面外の問題は本文の波線では見えないので、
/// 「どこに、どれくらい、どの重さで問題があるか」を縦一本に射影して常に見せる（VS Code の
/// overview ruler と同じ考え方）。
///
/// <para>ここは純粋な計算だけ——描画は <c>OverlayRenderer</c>、当たり判定の呼び出しは
/// <c>EditorCanvas</c>。<see cref="DiagnosticDecorations"/> と同じ流儀で、行への畳み込みと
/// 座標の対応付けだけをテスト可能な形で持つ。</para>
///
/// <para><b>1 行 1 印</b>にする。同じ行に複数あるときは<b>最も重い</b>ものが勝つ
/// （赤の上に黄が重なって色が入れ替わる、という見え方を防ぐ）。<b>重大度では捨てない</b>——
/// 警告も、未使用の using のような <see cref="DiagnosticSeverity.Hint"/> も印を出す。本文では
/// 波線を引かず薄字にする種類（<see cref="DiagnosticDecorations"/>）だが、<b>画面外にあると
/// 薄字も見えない</b>ので、スクロールバーには出す。代わりに色でうるささを分ける（描画側で
/// エラー＝赤 … ヒント＝地味な色）。</para>
/// </summary>
public static class DiagnosticOverviewMarks
{
    /// <summary>診断を行ごとに畳む。返り値は行の昇順。</summary>
    /// <param name="lineCount">文書の行数。範囲外の行を持つ診断（サーバーの応答が本文より新しい／
    /// 古いときに起きる）はここで捨てる。</param>
    public static IReadOnlyList<DiagnosticOverviewMark> Build(
        IReadOnlyList<LspDiagnostic> diagnostics, int lineCount)
    {
        if (diagnostics.Count == 0 || lineCount <= 0) return [];

        var worst = new Dictionary<int, DiagnosticOverviewMark>();
        foreach (var diagnostic in diagnostics)
        {
            int line = diagnostic.Range.Start.Line;
            if (line < 0 || line >= lineCount) continue;
            int column = Math.Max(0, diagnostic.Range.Start.Character);

            // Severity は 1(Error) が最も重い＝数値が小さいほど重い。
            if (worst.TryGetValue(line, out var existing) && existing.Severity <= diagnostic.Severity)
                continue;
            worst[line] = new DiagnosticOverviewMark(line, column, diagnostic.Severity);
        }

        if (worst.Count == 0) return [];
        var marks = new List<DiagnosticOverviewMark>(worst.Values);
        marks.Sort(static (a, b) => a.Line.CompareTo(b.Line));
        return marks;
    }

    /// <summary>行 <paramref name="line"/> の印を置くトラック上の中心 Y。
    /// サム（つまみ）のスクロール量換算ではなく<b>文書全体を等分</b>する射影にする——
    /// 印は「文書のどのあたりか」を示すもので、いまのスクロール位置に依らず動いてはいけない。</summary>
    public static double TrackY(int line, int lineCount, double trackHeight)
    {
        if (lineCount <= 0 || trackHeight <= 0) return 0;
        return trackHeight * (Math.Clamp(line, 0, lineCount - 1) + 0.5) / lineCount;
    }

    /// <summary><paramref name="y"/> に最も近い印を返す（<paramref name="tolerance"/> px 以内）。
    /// 印が密集していると 1px の差で別の問題へ飛ぶので、<b>最も近いもの</b>を選ぶ。
    /// 同じ距離なら重い方（エラー）を優先する。該当が無ければ null。</summary>
    public static DiagnosticOverviewMark? HitTest(
        IReadOnlyList<DiagnosticOverviewMark> marks, double y, int lineCount, double trackHeight,
        double tolerance)
    {
        DiagnosticOverviewMark? best = null;
        double bestDistance = double.MaxValue;
        foreach (var mark in marks)
        {
            double distance = Math.Abs(TrackY(mark.Line, lineCount, trackHeight) - y);
            if (distance > tolerance) continue;
            if (distance > bestDistance) continue;
            if (distance == bestDistance && best is { } current && current.Severity <= mark.Severity) continue;
            best = mark;
            bestDistance = distance;
        }
        return best;
    }
}
