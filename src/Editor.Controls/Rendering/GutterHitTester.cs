using System;
using System.Windows;

namespace Editor.Controls.Rendering;

// EditorCanvas の OnMouseMove/OnMouseLeftButtonDown が個別に計算していた
// 「blame | ブレークポイント | 行番号 | フォールド」列の境界チェックを1箇所にまとめたもの。
// バッファ行への変換（fold-aware な Y→行番号変換）は EditorCanvas 側の HitTestGutterLine に残し、
// コンストラクタでデリゲートとして受け取る。
internal sealed class GutterHitTester
{
    // 各列の左端からの幅。GetGutterMetrics() の戻り値 + _blameColWidth をそのまま渡す。
    public readonly record struct Boundaries(double BlameColWidth, double BpColWidth, double LineNumWidth, double GutterWidth);

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

    public bool TryHitFoldGutter(Point point, Boundaries b, out int line)
    {
        if (point.X >= b.BlameColWidth + b.BpColWidth + b.LineNumWidth && point.X < b.GutterWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    public bool TryHitLineNumberGutter(Point point, Boundaries b, out int line)
    {
        if (point.X < b.BlameColWidth + b.BpColWidth + b.LineNumWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }
}
