using System;
using System.Windows;

namespace Editor.Controls.Rendering;

// EditorCanvas の OnMouseMove/OnMouseLeftButtonDown が個別に計算していた
// 「blame | ブレークポイント | テスト | 行番号 | フォールド」列の境界チェックを1箇所にまとめたもの。
// バッファ行への変換（fold-aware な Y→行番号変換）は EditorCanvas 側の HitTestGutterLine に残し、
// コンストラクタでデリゲートとして受け取る。
internal sealed class GutterHitTester
{
    // 各列の幅。フィールドの並びは実際の列の並び（左→右）と同じ。GetGutterMetrics() の戻り値 +
    // _blameColWidth をそのまま渡す。無効な列は幅 0 で来るので、その列のヒットテストは必ず false になる。
    public readonly record struct Boundaries(
        double BlameColWidth, double BpColWidth, double TestColWidth, double LineNumWidth, double GutterWidth);

    private readonly Func<Point, int> _lineResolver;

    public GutterHitTester(Func<Point, int> lineResolver)
    {
        _lineResolver = lineResolver;
    }

    public bool TryHitBlameGutter(Point point, Boundaries b, out int line)
    {
        if (b.BlameColWidth > 0 && point.X < b.BlameColWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    public bool TryHitBreakpointGutter(Point point, Boundaries b, out int line)
    {
        if (b.BpColWidth > 0 && point.X >= b.BlameColWidth && point.X < b.BlameColWidth + b.BpColWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    public bool TryHitTestGlyphGutter(Point point, Boundaries b, out int line)
    {
        double left = b.BlameColWidth + b.BpColWidth;
        if (b.TestColWidth > 0 && point.X >= left && point.X < left + b.TestColWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    public bool TryHitFoldGutter(Point point, Boundaries b, out int line)
    {
        if (point.X >= b.BlameColWidth + b.BpColWidth + b.TestColWidth + b.LineNumWidth && point.X < b.GutterWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    public bool TryHitLineNumberGutter(Point point, Boundaries b, out int line)
    {
        // 行番号列そのものの範囲だけを見る。左端を左隣の列の右端に合わせておかないと、
        // 列を1つ足すたびにこの範囲が黙って広がる（左の列とヒットが二重になる）。
        double left = b.BlameColWidth + b.BpColWidth + b.TestColWidth;
        if (point.X >= left && point.X < left + b.LineNumWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }
}
