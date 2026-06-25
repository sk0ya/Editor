using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas のデバッグ用ガター（ブレークポイント列・実行中行ハイライト）。
/// <see cref="_breakpointsEnabled"/> が false の間は <c>GetGutterMetrics</c> がブレークポイント列の幅を 0 にし、
/// 描画・ヒットテストとも一切作動しないため、デバッグを使わない通常のエディタ利用には影響しない。
/// ホストは <see cref="SetBreakpointsEnabled"/> で列を有効化し、<see cref="BreakpointToggled"/> を購読して
/// ブレークポイントの追加/削除を受け取り、<see cref="SetBreakpoints"/>/<see cref="SetExecutionLine"/> で表示を更新する。
/// </summary>
public partial class EditorCanvas
{
    private bool _breakpointsEnabled;
    private readonly HashSet<int> _breakpoints = new();
    private int _executionLine = -1;       // デバッガが現在停止しているバッファ行（無ければ -1）
    private int _hoveredBreakpointLine = -1;

    // 自己完結のため配色は固定（テーマ非依存）。赤＝ブレークポイント、薄赤＝ホバー候補、琥珀＝実行中行。
    // Freeze ヘルパーは EditorCanvas 本体（同一 partial クラス）の既存メンバーを共用する。
    private static readonly Brush BreakpointBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)));
    private static readonly Brush BreakpointHoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0xE5, 0x14, 0x00)));
    private static readonly Brush ExecutionLineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xD7, 0x00)));
    private static readonly Brush ExecutionArrowBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)));

    /// <summary>ブレークポイント列がクリックされ、その行のブレークポイントがトグルされたとき。引数はバッファ行（0始まり）。</summary>
    public event Action<int>? BreakpointToggled;

    /// <summary>ブレークポイント列の有効/無効を切り替える（デバッグ機能を使うホストが有効化する）。</summary>
    public void SetBreakpointsEnabled(bool enabled)
    {
        if (_breakpointsEnabled == enabled) return;
        _breakpointsEnabled = enabled;
        if (!enabled)
        {
            _executionLine = -1;
            _hoveredBreakpointLine = -1;
        }
        InvalidateVisual();
    }

    /// <summary>このドキュメントの全ブレークポイント行（0始まり）を設定する。</summary>
    public void SetBreakpoints(IEnumerable<int> lines)
    {
        _breakpoints.Clear();
        foreach (var l in lines) if (l >= 0) _breakpoints.Add(l);
        InvalidateVisual();
    }

    /// <summary>デバッガが停止している現在行（0始まり、無ければ -1）を設定する。</summary>
    public void SetExecutionLine(int bufferLine)
    {
        if (_executionLine == bufferLine) return;
        _executionLine = bufferLine;
        InvalidateVisual();
    }

    private void SetHoveredBreakpointLine(int line)
    {
        if (_hoveredBreakpointLine == line) return;
        _hoveredBreakpointLine = line;
        if (_breakpointsEnabled) InvalidateVisual();
    }

    /// <summary>ブレークポイント列（左端）に、その行の赤丸／ホバー候補の薄赤丸／実行中行の琥珀矢印を描く。</summary>
    private void DrawBreakpointGlyph(DrawingContext dc, int line, double y, int bpColWidth)
    {
        double cx = bpColWidth / 2.0;
        double cy = y + _lineHeight / 2.0;
        double r = Math.Max(3.0, Math.Min(bpColWidth, _lineHeight) / 2.0 - 3.0);

        if (_breakpoints.Contains(line))
            dc.DrawEllipse(BreakpointBrush, null, new Point(cx, cy), r, r);
        else if (_hoveredBreakpointLine == line)
            dc.DrawEllipse(BreakpointHoverBrush, null, new Point(cx, cy), r, r);

        // 実行中行は琥珀の右向き矢印（ブレークポイントの上に重ねる）。
        if (line == _executionLine)
        {
            var arrow = FormatText("▶", ExecutionArrowBrush);
            dc.DrawText(arrow, new Point(cx - arrow.Width / 2.0, y + (_lineHeight - arrow.Height) / 2.0));
        }
    }
}
