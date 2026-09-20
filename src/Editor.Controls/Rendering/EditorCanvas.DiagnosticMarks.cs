using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Editor.Core.Lsp;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas の「スクロールバーの診断マーカー」——画面外の問題を縦一本に射影して常に見せ、
/// 押せばそこへ飛ぶ（VS Code の overview ruler）。
///
/// <para>本文の波線は<b>見えている範囲にしか無い</b>。500 行のファイルの 400 行目のエラーは、
/// 探しに行かないと存在すら判らない。スクロールバーは唯一「文書全体」を写している場所なので、
/// ここに印を置く。</para>
///
/// <para>行への畳み込みと座標対応は <see cref="DiagnosticOverviewMarks"/>（Editor.Core・純粋計算）、
/// 描画は <c>OverlayRenderer.DrawScrollbarDiagnosticMarks</c>、ここはその 2 つを現在の状態へ
/// 結び付けてマウスを受けるだけ。診断の出どころは <c>SetDiagnostics</c> が入れる
/// <c>_diagnostics</c>＝LSP とホストを合流させた後のもの（ホストの診断だけ印が出ない、を防ぐ）。</para>
/// </summary>
public partial class EditorCanvas
{
    // 畳み込み結果のキャッシュ。診断か行数が変われば作り直す（描画のたびに全診断を走らない）。
    private IReadOnlyList<DiagnosticOverviewMark> _diagnosticMarks = [];
    private IReadOnlyList<LspDiagnostic>? _diagnosticMarksSource;
    private int _diagnosticMarksLineCount = -1;

    /// <summary>スクロールバーの診断マーカーが押されたとき。ホストはその位置へキャレットを移す。</summary>
    public event Action<DiagnosticOverviewMark>? DiagnosticMarkClicked;

    /// <summary>マーカーの当たり判定の縦の許容（px）。印そのものは細いので、指で押せる程度に広げる。</summary>
    private const double DiagnosticMarkHitTolerance = 5.0;

    private IReadOnlyList<DiagnosticOverviewMark> CurrentDiagnosticMarks()
    {
        int lineCount = Math.Max(1, _lines.Length);
        if (!ReferenceEquals(_diagnosticMarksSource, _diagnostics) || _diagnosticMarksLineCount != lineCount)
        {
            _diagnosticMarksSource = _diagnostics;
            _diagnosticMarksLineCount = lineCount;
            _diagnosticMarks = DiagnosticOverviewMarks.Build(_diagnostics, lineCount);
        }
        return _diagnosticMarks;
    }

    /// <summary>スクロールバーのトラックへ印を引く（サムの後に呼ぶこと）。</summary>
    private void DrawDiagnosticMarks(DrawingContext dc, Size size, OverlayRenderer.ScrollbarLayout layout)
        => OverlayRenderer.DrawScrollbarDiagnosticMarks(
            dc, Theme, size, layout, CurrentDiagnosticMarks(), Math.Max(1, _lines.Length));

    /// <summary>点 <paramref name="point"/> が垂直スクロールバーの印の上にあればそれを返す。
    /// 掴む幅は <c>ScrollbarHitSize</c>（サムのドラッグと同じ帯）で、その中では
    /// <b>印が優先</b>される——スクロール量の調整はどこを押しても出来るが、印は印の上でしか押せない。</summary>
    private DiagnosticOverviewMark? DiagnosticMarkAt(Point point)
    {
        var size = RenderSize;
        var layout = ComputeScrollbarLayout(size);
        if (!layout.NeedVert || layout.VertTrackH <= 0) return null;
        if (point.X < size.Width - ScrollbarHitSize || point.Y > layout.VertTrackH) return null;

        return DiagnosticOverviewMarks.HitTest(
            CurrentDiagnosticMarks(), point.Y, Math.Max(1, _lines.Length), layout.VertTrackH,
            DiagnosticMarkHitTolerance);
    }

    /// <summary>印の上のクリックならホストへ通知して true（スクロールバーのドラッグは始めない）。</summary>
    internal bool TryClickDiagnosticMark(Point point)
    {
        if (DiagnosticMarkAt(point) is not { } mark) return false;
        DiagnosticMarkClicked?.Invoke(mark);
        return true;
    }
}
