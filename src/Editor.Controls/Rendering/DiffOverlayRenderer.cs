using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Editing;

namespace Editor.Controls.Rendering;

/// <summary>
/// 差分表示の上乗せ：行内差分（書き換えた行の中で実際に変わった文字）と、スクロールバーへ写す
/// 変更位置の印。どちらも <see cref="DiffDecorations"/> から描く。<see cref="EditorCanvas"/> が1つ持つ。
///
/// <para>行の背景だけでは「1文字直した行」と「丸ごと書き換えた行」が同じに見える。行内差分は
/// 行の背景の上に同じ色相の濃い色で重ね、変わった文字だけを浮かせる。</para>
///
/// <para>スクロールバーの印は<b>表示行</b>で位置を決める（<see cref="OverviewRowMap"/> /
/// <see cref="DiffOverviewMarks"/>）。左右のエディタは空き行で高さを揃えているので、表示行で測れば
/// 左右の印が同じ高さに並び、サムとも合う。印は<b>サムの下</b>に描く——変更が続く範囲は太い帯になるので、
/// 上に描くと全行が追加のファイルではトラックが一色に潰れ、いまの位置が見えなくなる。</para>
/// </summary>
internal sealed class DiffOverlayRenderer
{
    // 印の畳み込み結果のキャッシュ。表示行の並びか装飾が変われば作り直す（描画のたびに全行を走らない）。
    private IReadOnlyList<DiffOverviewMark> _marks = [];
    private EditorCanvas.VisualLineSegment[]? _marksLayout;
    private DiffDecorations? _marksSource;

    /// <summary>
    /// 1つの表示行へ行内差分の範囲を塗る。<paramref name="segmentStart"/>〜<paramref name="segmentEnd"/> は
    /// この表示行が受け持つ桁（折り返しの区間）、<paramref name="xAt"/> は桁 → 画面の x。
    /// 塗る範囲は <paramref name="clipLeft"/>〜<paramref name="clipRight"/>（本文の領域）に収める。
    /// </summary>
    public static void DrawInlineChanges(
        DrawingContext dc, IReadOnlyList<DiffInlineRange> ranges, Brush brush,
        int segmentStart, int segmentEnd, Func<int, double> xAt,
        double y, double height, double clipLeft, double clipRight)
    {
        foreach (var range in ranges)
        {
            int start = Math.Clamp(range.Start, segmentStart, segmentEnd);
            int end = Math.Clamp(range.End, start, segmentEnd);
            if (end <= start) continue;
            double left = Math.Max(xAt(start), clipLeft);
            double right = Math.Min(xAt(end), clipRight);
            if (right <= left) continue;
            dc.DrawRectangle(brush, null, new Rect(left, y, right - left, height));
        }
    }

    /// <summary>
    /// 表示行ごとの差分の種別から作った、スクロールバーの印（キャッシュ付き）。畳んで隠れた行の変更
    /// （その行の直前の空き行も含む）は、畳みの見出し行の印に入れる——畳んだだけで変更が消えないように。
    /// </summary>
    public IReadOnlyList<DiffOverviewMark> Marks(
        EditorCanvas.VisualLineSegment[] layout, DiffDecorations decorations, OverviewRowMap rows)
    {
        if (ReferenceEquals(_marksLayout, layout) && ReferenceEquals(_marksSource, decorations))
            return _marks;
        _marksLayout = layout;
        _marksSource = decorations;
        var spacerKind = decorations.SpacerKind;
        if (decorations.Lines.Count == 0 && (spacerKind is null || decorations.SpacersBefore.Count == 0))
        {
            _marks = [];
            return _marks;
        }

        var kinds = new DiffDecorationKind?[layout.Length];
        for (int i = 0; i < layout.Length; i++)
        {
            var segment = layout[i];
            if (segment.IsSpacer)
                kinds[i] = spacerKind;   // 反対側にだけある行（指定が無ければ印なし）
            else if (!segment.IsVirtualRow && decorations.Lines.TryGetValue(segment.BufferLine, out var kind))
                kinds[i] = kind;
        }

        // 畳んで表示行を持たない行は、見出し行（の先頭の表示行）へ。見出し行に自分の種別があればそれを残す。
        foreach (var (line, kind) in decorations.Lines)
            MarkHidden(line, kind);
        if (spacerKind is { } hiddenSpacerKind)
            foreach (var (line, count) in decorations.SpacersBefore)
                if (count > 0) MarkHidden(line, hiddenSpacerKind);

        _marks = DiffOverviewMarks.Build(kinds);
        return _marks;

        void MarkHidden(int line, DiffDecorationKind kind)
        {
            if (!rows.IsHidden(line)) return;
            int row = rows.RowOf(line);
            if (row >= 0 && row < kinds.Length) kinds[row] ??= kind;
        }
    }

    /// <summary>
    /// スクロールバーのトラックへ差分の印を引く。<b>トラックの後・サムの前</b>に呼ぶ
    /// （<see cref="OverlayRenderer.DrawScrollbars"/> のコールバックから）。
    /// </summary>
    public static void DrawMarks(
        DrawingContext dc, EditorTheme theme, IReadOnlyList<DiffOverviewMark> marks,
        int rowCount, double trackX, double trackHeight)
    {
        if (marks.Count == 0 || trackHeight <= 0) return;
        rowCount = Math.Max(1, rowCount);
        foreach (var mark in marks)
        {
            var (markY, height) = DiffOverviewMarks.TrackRect(
                mark, rowCount, trackHeight, OverlayRenderer.DiagnosticMarkMinHeight);
            if (height <= 0) continue;
            var brush = mark.Kind == DiffDecorationKind.Added ? theme.DiffAddedMark : theme.DiffRemovedMark;
            dc.DrawRectangle(brush, null, new Rect(trackX, markY, OverlayRenderer.ScrollbarSize, height));
        }
    }
}
