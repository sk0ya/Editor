using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Editor.Core.Editing;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas の差分表示の上乗せ：行内差分（書き換えた行の中で実際に変わった文字）と、
/// スクロールバーへ写す変更位置の印。どちらも <see cref="DiffDecorations"/> から描く。
///
/// <para>行の背景だけでは「1文字直した行」と「丸ごと書き換えた行」が同じに見える。行内差分は
/// 行の背景の上に同じ色相の濃い色で重ね、変わった文字だけを浮かせる。</para>
///
/// <para>スクロールバーの印は<b>表示行</b>で位置を決める（<see cref="DiffOverviewMarks"/>）。左右の
/// エディタは空き行で高さを揃えているので、表示行で測れば左右の印が同じ高さに並び、サムとも合う。</para>
/// </summary>
public partial class EditorCanvas
{
    // 印の畳み込み結果のキャッシュ。表示行の並びか装飾が変われば作り直す（描画のたびに全行を走らない）。
    private IReadOnlyList<DiffOverviewMark> _diffMarks = [];
    private VisualLineSegment[]? _diffMarksLayout;
    private DiffDecorations? _diffMarksSource;

    /// <summary>
    /// 表示行 <paramref name="visualLine"/>（バッファ行 <paramref name="line"/> の1区間）へ、行内差分の範囲を塗る。
    /// 行の背景を描いた直後・本文を描く前に呼ぶ（文字の下に敷く）。
    /// </summary>
    private void DrawDiffInlineChanges(
        DrawingContext dc, int visualLine, VisualLineSegment segment, int line, DiffDecorationKind kind,
        string lineText, double y, double textLeft, Size size)
    {
        if (!_diffDecorations.InlineChanges.TryGetValue(line, out var ranges) || ranges.Count == 0) return;
        // 巨大な1行では桁の実測をしない（本文の描画と同じ打ち切り）。
        if (lineText.Length > LongLineCharacterThreshold) return;

        // 折り返しているときは、この表示行が受け持つ桁の区間だけを塗る。
        int segmentStart = segment.StartColumn;
        int segmentEnd = lineText.Length;
        if (EffectiveWrapLines && visualLine + 1 < TotalVisualLines)
        {
            var next = GetVisualSegment(visualLine + 1);
            if (next.BufferLine == line && next.IsContinuation && !next.IsVirtualRow)
                segmentEnd = next.StartColumn;
        }

        var brush = kind == DiffDecorationKind.Added ? Theme.DiffAddedChangeBg : Theme.DiffRemovedChangeBg;
        foreach (var range in ranges)
        {
            int start = Math.Clamp(range.Start, segmentStart, segmentEnd);
            int end = Math.Clamp(range.End, start, segmentEnd);
            if (end <= start) continue;
            double left = textLeft + GetVisualX(lineText, start) - _scrollOffsetX;
            double right = textLeft + GetVisualX(lineText, end) - _scrollOffsetX;
            left = Math.Max(left, textLeft);
            right = Math.Min(right, size.Width);
            if (right <= left) continue;
            dc.DrawRectangle(brush, null, new Rect(left, y, right - left, _lineHeight));
        }
    }

    /// <summary>表示行ごとの差分の種別から作った、スクロールバーの印（キャッシュ付き）。</summary>
    internal IReadOnlyList<DiffOverviewMark> CurrentDiffMarks()
    {
        if (ReferenceEquals(_diffMarksLayout, _visualLines) && ReferenceEquals(_diffMarksSource, _diffDecorations))
            return _diffMarks;
        _diffMarksLayout = _visualLines;
        _diffMarksSource = _diffDecorations;
        var spacerKind = _diffDecorations.SpacerKind;
        if (_diffDecorations.Lines.Count == 0 && (spacerKind is null || _diffDecorations.SpacersBefore.Count == 0))
        {
            _diffMarks = [];
            return _diffMarks;
        }

        var kinds = new DiffDecorationKind?[_visualLines.Length];
        for (int i = 0; i < _visualLines.Length; i++)
        {
            var segment = _visualLines[i];
            if (segment.IsSpacer)
                kinds[i] = spacerKind;   // 反対側にだけある行（指定が無ければ印なし）
            else if (!segment.IsVirtualRow && _diffDecorations.Lines.TryGetValue(segment.BufferLine, out var kind))
                kinds[i] = kind;
        }
        _diffMarks = DiffOverviewMarks.Build(kinds);
        return _diffMarks;
    }

    /// <summary>スクロールバーのトラックへ差分の印を引く（サムの後・診断の印の前に呼ぶ）。</summary>
    private void DrawDiffMarks(DrawingContext dc, Size size, OverlayRenderer.ScrollbarLayout layout)
    {
        if (!layout.NeedVert || layout.VertTrackH <= 0) return;
        var marks = CurrentDiffMarks();
        if (marks.Count == 0) return;

        int rowCount = Math.Max(1, TotalVisualLines);
        double trackX = size.Width - OverlayRenderer.ScrollbarSize;
        foreach (var mark in marks)
        {
            var (markY, height) = DiffOverviewMarks.TrackRect(
                mark, rowCount, layout.VertTrackH, OverlayRenderer.DiagnosticMarkMinHeight);
            if (height <= 0) continue;
            var brush = mark.Kind == DiffDecorationKind.Added ? Theme.DiffAddedMark : Theme.DiffRemovedMark;
            dc.DrawRectangle(brush, null, new Rect(trackX, markY, OverlayRenderer.ScrollbarSize, height));
        }
    }
}
