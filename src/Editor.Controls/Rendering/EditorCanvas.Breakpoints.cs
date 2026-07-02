using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas のデバッグ用ガター（ブレークポイント列・実行中行ハイライト）と DataTip ホバー連携。
/// <see cref="_breakpointsEnabled"/> が false の間は <c>GetGutterMetrics</c> がブレークポイント列の幅を 0 にし、
/// 描画・ヒットテストとも一切作動しないため、デバッグを使わない通常のエディタ利用には影響しない。
/// ホストは <see cref="SetBreakpointsEnabled"/> で列を有効化し、<see cref="BreakpointToggled"/> を購読して
/// ブレークポイントの追加/削除を受け取り、<see cref="SetBreakpoints(IReadOnlyList{EditorBreakpoint})"/>/
/// <see cref="SetExecutionLine"/> で表示を更新する。条件付き・ログポイント・無効状態はガターのグリフで描き分ける。
/// 停止中は <see cref="SetDataTipsEnabled"/> を有効化すると本文ホバーで <see cref="DataTipHoverChanged"/> が上がり、
/// ホストが式を評価して値ポップアップ（DataTip）を出せる。
/// </summary>
public partial class EditorCanvas
{
    private bool _breakpointsEnabled;
    // 行 → (グリフ種別, 有効/無効)。条件付き・ログポイント・無効ブレークポイントを描き分けるためのメタを持つ。
    private readonly Dictionary<int, (BreakpointGlyphKind Kind, bool Enabled)> _breakpoints = new();
    private int _executionLine = -1;       // デバッガが現在停止しているバッファ行（無ければ -1）
    private int _hoveredBreakpointLine = -1;

    // 自己完結のため配色は固定（テーマ非依存）。赤＝ブレークポイント、薄赤＝ホバー候補、琥珀＝実行中行。
    // Freeze ヘルパーは EditorCanvas 本体（同一 partial クラス）の既存メンバーを共用する。
    private static readonly Brush BreakpointBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x14, 0x00)));
    private static readonly Pen BreakpointOutlinePen = FreezePen(new Pen(BreakpointBrush, 1.4));
    private static readonly Brush BreakpointGlyphFg = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly Pen BreakpointGlyphPen = FreezePen(new Pen(BreakpointGlyphFg, 1.4));
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

    /// <summary>このドキュメントの全ブレークポイント行（0始まり）を設定する（種別なしの単純なブレークポイント）。</summary>
    public void SetBreakpoints(IEnumerable<int> lines)
    {
        _breakpoints.Clear();
        foreach (var l in lines) if (l >= 0) _breakpoints[l] = (BreakpointGlyphKind.Normal, true);
        InvalidateVisual();
    }

    /// <summary>このドキュメントの全ブレークポイントを種別・有効状態つきで設定する（条件付き＋／ログポイント◆／無効は中抜きで描き分ける）。</summary>
    public void SetBreakpoints(IReadOnlyList<EditorBreakpoint> breakpoints)
    {
        _breakpoints.Clear();
        foreach (var bp in breakpoints) if (bp.Line >= 0) _breakpoints[bp.Line] = (bp.Glyph, bp.Enabled);
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

    /// <summary>ブレークポイント列（blame 非表示時は左端、表示時はその右）に、その行のブレークポイント／
    /// ホバー候補の薄赤丸／実行中行の琥珀矢印を描く。<paramref name="x"/> は列の左端。</summary>
    private void DrawBreakpointGlyph(DrawingContext dc, int line, double y, double x, int bpColWidth)
    {
        double cx = x + bpColWidth / 2.0;
        double cy = y + _lineHeight / 2.0;
        double r = Math.Max(3.0, Math.Min(bpColWidth, _lineHeight) / 2.0 - 3.0);

        if (_breakpoints.TryGetValue(line, out var bp))
            DrawBreakpointShape(dc, cx, cy, r, bp.Kind, bp.Enabled);
        else if (_hoveredBreakpointLine == line)
            dc.DrawEllipse(BreakpointHoverBrush, null, new Point(cx, cy), r, r);

        // 実行中行は琥珀の右向き矢印（ブレークポイントの上に重ねる）。
        if (line == _executionLine)
        {
            var arrow = FormatText("▶", ExecutionArrowBrush);
            dc.DrawText(arrow, new Point(cx - arrow.Width / 2.0, y + (_lineHeight - arrow.Height) / 2.0));
        }
    }

    // VS 風の描き分け：通常＝塗りつぶし丸／条件付き＝丸＋白「＋」／ログポイント＝塗りつぶしひし形。
    // 無効なものは塗らず赤の輪郭だけにする（VS の中抜き表現に倣う）。
    private static void DrawBreakpointShape(DrawingContext dc, double cx, double cy, double r, BreakpointGlyphKind kind, bool enabled)
    {
        var fill = enabled ? BreakpointBrush : null;
        var outline = enabled ? null : BreakpointOutlinePen;

        if (kind == BreakpointGlyphKind.Logpoint)
        {
            // ひし形（菱形）。
            var diamond = new StreamGeometry();
            using (var ctx = diamond.Open())
            {
                ctx.BeginFigure(new Point(cx, cy - r), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(cx + r, cy), true, false);
                ctx.LineTo(new Point(cx, cy + r), true, false);
                ctx.LineTo(new Point(cx - r, cy), true, false);
            }
            diamond.Freeze();
            dc.DrawGeometry(fill, outline, diamond);
            return;
        }

        dc.DrawEllipse(fill, outline, new Point(cx, cy), r, r);

        if (kind == BreakpointGlyphKind.Conditional)
        {
            // 中央に小さな白い「＋」を重ねて条件付きを示す。
            double h = r * 0.55;
            var pen = enabled ? BreakpointGlyphPen : BreakpointOutlinePen;
            dc.DrawLine(pen, new Point(cx - h, cy), new Point(cx + h, cy));
            dc.DrawLine(pen, new Point(cx, cy - h), new Point(cx, cy + h));
        }
    }

    // ───────────────────────── DataTip（デバッグ中のホバー値表示）─────────────────────────

    private bool _dataTipsEnabled;
    private int _dataTipLine = -1, _dataTipStart = -1, _dataTipEnd = -1;

    /// <summary>本文ホバーで <see cref="DataTipHoverChanged"/> を発火し、ホストが式の値ポップアップを出せるようにする。
    /// 通常はデバッガが停止したときに有効化し、再開・終了で無効化する。</summary>
    public event Action<DataTipHover>? DataTipHoverChanged;

    /// <summary>ホバーが式の上から外れた（または DataTip が無効化された）とき。ホストはポップアップを閉じる。</summary>
    public event Action? DataTipHoverEnded;

    /// <summary>DataTip ホバー連携の有効/無効を切り替える（停止時に有効化）。無効化時は進行中のホバーを終了させる。</summary>
    public void SetDataTipsEnabled(bool enabled)
    {
        if (_dataTipsEnabled == enabled) return;
        _dataTipsEnabled = enabled;
        if (!enabled) ClearDataTipHover();
    }

    /// <summary>本文上の点 <paramref name="point"/>（キャンバス相対）でホバーしている式を判定し、変化があれば通知する。</summary>
    private void UpdateDataTipHover(Point point)
    {
        if (!_dataTipsEnabled) return;
        var (line, col) = HitTest(point);
        var span = ExtractDataTipExpression(line, col);
        if (span is not var (expr, start, end) || expr is null)
        {
            ClearDataTipHover();
            return;
        }
        // 同じ式領域を指している間は再通知しない（評価の連打を防ぐ）。
        if (line == _dataTipLine && start == _dataTipStart && end == _dataTipEnd) return;
        _dataTipLine = line; _dataTipStart = start; _dataTipEnd = end;
        // ポップアップは式のすぐ下に出したいので、ホバー位置を行の高さ分だけ下げたアンカーを渡す。
        DataTipHoverChanged?.Invoke(new DataTipHover(line, start, expr, new Point(point.X, point.Y + _lineHeight)));
    }

    private void ClearDataTipHover()
    {
        if (_dataTipLine < 0 && _dataTipStart < 0) return;
        _dataTipLine = _dataTipStart = _dataTipEnd = -1;
        DataTipHoverEnded?.Invoke();
    }

    // (line, col) の周囲から識別子チェーン（a.b.c）を切り出す。式文字でなければ null。
    private (string Expr, int Start, int End)? ExtractDataTipExpression(int line, int col)
    {
        if (line < 0 || line >= _lines.Length) return null;
        var text = _lines[line];
        if (col < 0 || col >= text.Length || !IsExprChar(text[col])) return null;

        int start = col, end = col;
        while (start > 0 && IsExprChar(text[start - 1])) start--;
        while (end < text.Length - 1 && IsExprChar(text[end + 1])) end++;
        // 前後のドットを落とす（".x" や "x." を式として渡さない）。
        while (start <= end && text[start] == '.') start++;
        while (end >= start && text[end] == '.') end--;
        if (start > end) return null;

        var expr = text.Substring(start, end - start + 1);
        // 数値リテラル単体は評価しても意味がないので除外。
        if (expr.Length == 0 || char.IsDigit(expr[0])) return null;
        return (expr, start, end);
    }

    private static bool IsExprChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
}

/// <summary>ガターのブレークポイント・グリフ種別。</summary>
public enum BreakpointGlyphKind
{
    /// <summary>通常のブレークポイント（塗りつぶし丸）。</summary>
    Normal,
    /// <summary>条件付き／ヒット数ブレークポイント（丸＋白「＋」）。</summary>
    Conditional,
    /// <summary>ログポイント（塗りつぶしひし形）。</summary>
    Logpoint,
}

/// <summary>ガターに表示する 1 件のブレークポイント。<paramref name="Line"/> は 0 始まりのバッファ行。</summary>
public readonly record struct EditorBreakpoint(int Line, BreakpointGlyphKind Glyph = BreakpointGlyphKind.Normal, bool Enabled = true);

/// <summary>DataTip ホバーの通知。<paramref name="Anchor"/> はポップアップを出すキャンバス相対座標（当該行の下端）。</summary>
public readonly record struct DataTipHover(int Line, int Column, string Expression, Point Anchor);
