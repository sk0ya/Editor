using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Editor.Core.Editing;

namespace Editor.Controls.Rendering;

/// <summary>
/// 連動編集の範囲（いま一緒に書き換わっている開き/閉じタグ名）を細い枠で囲む。
/// 「もう片方も変わっている」ことが、閉じタグが画面内にあれば目で追える。
/// 範囲そのものはエンジン（<see cref="Core.Engine.VimEngine.LinkedEditingRanges"/>）が持ち、
/// ここは渡された範囲を描くだけ。
/// </summary>
public partial class EditorCanvas
{
    private IReadOnlyList<LinkedRange> _linkedEditingRanges = [];

    public void SetLinkedEditingRanges(IReadOnlyList<LinkedRange> ranges)
    {
        if (ReferenceEquals(_linkedEditingRanges, ranges)) return;
        if (_linkedEditingRanges.Count == 0 && ranges.Count == 0) return;
        _linkedEditingRanges = ranges;
        InvalidateVisual();
    }

    private void DrawLinkedEditingRanges(DrawingContext dc, int line, double y, double textLeft, string lineText)
    {
        if (_linkedEditingRanges.Count == 0) return;
        Pen? pen = null;
        foreach (var r in _linkedEditingRanges)
        {
            if (r.Line != line) continue;
            int start = Math.Clamp(r.Start, 0, lineText.Length);
            int end = Math.Clamp(r.End, start, lineText.Length);
            double x0 = textLeft + GetVisualX(lineText, start) - _scrollOffsetX;
            double x1 = textLeft + GetVisualX(lineText, end) - _scrollOffsetX;
            // 名前を消し切った途中（ciw 直後）でも、どこが連動しているかは見せる
            double width = Math.Max(2, x1 - x0);
            pen ??= CreateLinkedEditingPen();
            dc.DrawRectangle(null, pen, new Rect(x0 + 0.5, y + 0.5, width - 1, _lineHeight - 1));
        }
    }

    private Pen CreateLinkedEditingPen()
    {
        var pen = new Pen(Theme.BracketGuideActiveBrush, 1.0);
        pen.Freeze();
        return pen;
    }
}
