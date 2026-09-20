using System;
using System.Windows;

namespace Editor.Controls.Rendering;

// EditorCanvas の OnMouseMove/OnMouseLeftButtonDown が個別に計算していた
// 「blame | 電球 | テスト | 行番号 | フォールド」列の境界チェックを1箇所にまとめたもの。
// ブレークポイントは専用列を持たず**行番号の上**に出す（行番号を消しているときだけ、
// 置き場が無くなるので最左の専用列へ逃がす＝そのときだけ BpColWidth > 0）。
// バッファ行への変換（fold-aware な Y→行番号変換）は EditorCanvas 側の HitTestGutterLine に残し、
// コンストラクタでデリゲートとして受け取る。
internal sealed class GutterHitTester
{
    // 各列の幅。フィールドの並びは実際の列の並び（左→右）と同じ。GetGutterMetrics() の戻り値 +
    // _blameColWidth をそのまま渡す。無効な列は幅 0 で来るので、その列のヒットテストは必ず false になる。
    public readonly record struct Boundaries(
        double BlameColWidth, double BpColWidth, double BulbColWidth, double TestColWidth,
        double LineNumWidth, double GutterWidth);

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

    /// <summary>ブレークポイントを置く帯。<b>ふつうは行番号の上</b>で、行番号を消しているときだけ
    /// 最左の専用列（<see cref="Boundaries.BpColWidth"/> > 0）。呼び出し側は
    /// ブレークポイントが有効なときにだけ問うこと——無効なら行番号のクリックは従来どおり素通りさせる。</summary>
    public bool TryHitBreakpointGutter(Point point, Boundaries b, out int line)
    {
        var (left, width) = b.BpColWidth > 0
            ? (b.BlameColWidth, b.BpColWidth)
            : (LineNumberLeft(b), b.LineNumWidth);
        if (width > 0 && point.X >= left && point.X < left + width)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    public bool TryHitCodeActionBulbGutter(Point point, Boundaries b, out int line)
    {
        double left = b.BlameColWidth + b.BpColWidth;
        if (b.BulbColWidth > 0 && point.X >= left && point.X < left + b.BulbColWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    public bool TryHitTestGlyphGutter(Point point, Boundaries b, out int line)
    {
        double left = b.BlameColWidth + b.BpColWidth + b.BulbColWidth;
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
        if (point.X >= LineNumberLeft(b) + b.LineNumWidth && point.X < b.GutterWidth)
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
        double left = LineNumberLeft(b);
        if (b.LineNumWidth > 0 && point.X >= left && point.X < left + b.LineNumWidth)
        {
            line = _lineResolver(point);
            return true;
        }
        line = -1;
        return false;
    }

    /// <summary>行番号列の左端＝その左にある全列の幅の合計。</summary>
    public static double LineNumberLeft(Boundaries b) =>
        b.BlameColWidth + b.BpColWidth + b.BulbColWidth + b.TestColWidth;
}
