using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas の注記の印（行に付いた会話・メモの吹き出し。例：PR のレビューのコメント）。
/// テストの印と同じく<b>中身はすべてホストの責務</b>で、エディタは「与えられた行に吹き出しを描き、乗れば説明を出し、
/// 押されたら行を知らせる」だけ。<see cref="_noteMarkersEnabled"/> が false の間は列の幅 0 で、見た目は 1px も変わらない。
/// <para>置き場はテストの列の右隣。列を1本足すとガターの寸法の受け渡し（GetGutterMetrics・当たり判定の境界・行番号の描画）
/// が全部広がるので、テストの列の幅に吹き出しの幅を足し、その中を左右に分けて使う——行番号から右の位置は
/// 「左の列の幅の合計」で決まるので、それだけで正しくずれる。</para>
/// <para>印は行番号でしか結び付いていない（編集で行がずれても追わない）。ずれた行の付け直しはホストが行い、
/// <see cref="SetNoteMarkers"/> で全部置き換える。</para>
/// </summary>
public partial class EditorCanvas
{
    private bool _noteMarkersEnabled;
    // バッファ行（0始まり）→ 印。ホストが SetNoteMarkers で全置換する。
    private readonly Dictionary<int, EditorNoteMarker> _noteMarkers = new();
    private int _hoveredNoteLine = -1;
    private System.Windows.Controls.ToolTip? _noteToolTip;

    /// <summary>乗ったときに出す中身をホストが組み立てる口（画像入りの会話など、文字列で足りないもの）。
    /// 引数はバッファ行、戻り値が null ならその行の <see cref="EditorNoteMarker.Tooltip"/> の文字列を出す。</summary>
    public Func<int, object?>? NoteToolTipContentProvider { get; set; }

    /// <summary>印が押されたとき。引数はバッファ行（0始まり）。印の無い行では発火しない。</summary>
    public event Action<int>? NoteMarkerClicked;

    /// <summary>注記の列の有効/無効。既定は無効（幅 0）。</summary>
    public void SetNoteMarkersEnabled(bool enabled)
    {
        if (_noteMarkersEnabled == enabled) return;
        _noteMarkersEnabled = enabled;
        SetHoveredNoteLine(-1);
        InvalidateArrange();   // 列のぶん本文の左端が動く
        InvalidateVisual();
    }

    /// <summary>このドキュメントの印を全部置き換える（空なら列は空になる）。<c>Line0</c> が負のものは捨て、同じ行は後勝ち。
    /// 中身が今と同じなら何もしない（編集のたびに付け直すホストが、毎回全面の描き直しを積まないように）。</summary>
    public void SetNoteMarkers(IReadOnlyList<EditorNoteMarker> markers)
    {
        if (NoteMarkersUnchanged(markers)) return;
        _noteMarkers.Clear();
        foreach (var m in markers) if (m.Line0 >= 0) _noteMarkers[m.Line0] = m;
        ReevaluateNoteHover();
        UpdateNoteToolTip(_hoveredNoteLine);
        InvalidateVisual();
    }

    internal bool NoteMarkersUnchanged(IReadOnlyList<EditorNoteMarker> markers)
    {
        int valid = 0;
        foreach (var m in markers)
        {
            if (m.Line0 < 0) continue;
            valid++;
            if (!_noteMarkers.TryGetValue(m.Line0, out var current) || current != m) return false;
        }
        return valid == _noteMarkers.Count;
    }

    /// <summary>いま出している印（テスト用のシーム）。</summary>
    internal IReadOnlyDictionary<int, EditorNoteMarker> NoteMarkers => _noteMarkers;

    // ── 寸法 ─────────────────────────────────────────────────────

    private int GlyphColumnWidth() => (int)Math.Max(14.0, _charWidth + 2);

    /// <summary>テストの列（テストの印・カバレッジ）のうち、テストの印が使う左側の幅。</summary>
    private int TestGlyphSubWidth() => _testGlyphsEnabled || _coverageMarkersEnabled ? GlyphColumnWidth() : 0;

    /// <summary>テストの列のうち、注記の印が使う右側の幅。</summary>
    private int NoteMarkerSubWidth() => _noteMarkersEnabled ? GlyphColumnWidth() : 0;

    /// <summary>点が注記の印の場所（テストの列の右側）にあるか。</summary>
    internal bool TryHitNoteMarkerColumn(Point point, out int line)
    {
        line = -1;
        if (!_noteMarkersEnabled) return false;
        var b = CurrentGutterBoundaries();
        if (!_gutterHitTester.TryHitTestGlyphGutter(point, b, out line)) return false;
        double left = b.BlameColWidth + b.BpColWidth + b.BulbColWidth + TestGlyphSubWidth();
        if (point.X >= left) return true;
        line = -1;
        return false;
    }

    /// <summary>注記の印のクリック。列の外なら false（呼び出し側は後続の処理へ）。</summary>
    internal bool TryClickNoteMarkerColumn(Point point)
    {
        if (!TryHitNoteMarkerColumn(point, out int line)) return false;
        if (line >= 0 && _noteMarkers.ContainsKey(line)) NoteMarkerClicked?.Invoke(line);
        return true;
    }

    // ── ホバー ───────────────────────────────────────────────────

    private void ReevaluateNoteHover()
    {
        int line = -1;
        bool inColumn = _lastMousePoint is { } point && TryHitNoteMarkerColumn(point, out line);
        SetHoveredNoteLine(inColumn ? line : -1);
    }

    /// <summary>乗っている行を更新する（印のある行だけ。ツールチップの開閉も）。</summary>
    internal void SetHoveredNoteLine(int line)
    {
        int normalized = _noteMarkersEnabled && line >= 0 && _noteMarkers.ContainsKey(line) ? line : -1;
        if (_hoveredNoteLine == normalized) return;
        _hoveredNoteLine = normalized;
        UpdateNoteToolTip(normalized);
        InvalidateVisual();
    }

    internal bool IsNoteMarkerHovered => _hoveredNoteLine >= 0;

    private void UpdateNoteToolTip(int hoveredLine)
    {
        var custom = hoveredLine >= 0 && _noteMarkers.ContainsKey(hoveredLine) ? NoteToolTipContentProvider?.Invoke(hoveredLine) : null;
        if (custom is null && (hoveredLine < 0 || !_noteMarkers.TryGetValue(hoveredLine, out var plain) || string.IsNullOrEmpty(plain.Tooltip)))
        {
            if (_noteToolTip is { } open) open.IsOpen = false;
            return;
        }
        _noteToolTip ??= new System.Windows.Controls.ToolTip
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            MaxWidth = 560,
        };
        _noteToolTip.IsOpen = false;   // 行が変わったらマウスの位置へ出し直す
        _noteToolTip.Content = custom
            ?? new System.Windows.Controls.TextBlock { Text = _noteMarkers[hoveredLine].Tooltip, TextWrapping = TextWrapping.Wrap };
        _noteToolTip.IsOpen = true;
    }

    // ── 描画 ─────────────────────────────────────────────────────

    /// <summary>その行の吹き出しを描く。<paramref name="x"/> は注記の場所の左端。</summary>
    private void DrawNoteMarker(DrawingContext dc, int line, double y, double x, int width)
    {
        if (!_noteMarkers.TryGetValue(line, out var marker)) return;
        double w = Math.Min(width - 3, _lineHeight * 0.95);
        double h = Math.Max(7, _lineHeight * 0.58);
        double left = x + (width - w) / 2.0;
        double top = y + (_lineHeight - h) / 2.0 - 1;
        var brush = marker.Kind == NoteMarkerKind.Resolved ? Theme.DiagnosticHint : Theme.DiagnosticInfo;

        if (_hoveredNoteLine == line)
            dc.DrawRoundedRectangle(TestGlyphHoverBrush, null, new Rect(x + 1, y + 1, width - 2, _lineHeight - 2), 3, 3);

        // 吹き出し：角の丸い箱と、左下の尾。未解決は塗り、解決済みは輪郭だけ（目に入る強さを分ける）。
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double r = Math.Min(3, h / 3);
            ctx.BeginFigure(new Point(left + r, top), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(left + w - r, top), true, false);
            ctx.ArcTo(new Point(left + w, top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(left + w, top + h - r), true, false);
            ctx.ArcTo(new Point(left + w - r, top + h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(left + w * 0.45, top + h), true, false);
            ctx.LineTo(new Point(left + w * 0.22, top + h + 3), true, false);   // 尾
            ctx.LineTo(new Point(left + w * 0.28, top + h), true, false);
            ctx.LineTo(new Point(left + r, top + h), true, false);
            ctx.ArcTo(new Point(left, top + h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(left, top + r), true, false);
            ctx.ArcTo(new Point(left + r, top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        bool open = marker.Kind == NoteMarkerKind.Open;
        dc.DrawGeometry(open ? brush : null, open ? null : NotePen(brush), geometry);

        // 2つ以上の会話がある行は数を添える（吹き出しの中に小さく）。
        if (marker.Count > 1)
        {
            var text = new FormattedText(marker.Count > 9 ? "9+" : marker.Count.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(7, h * 0.85),
                open ? Theme.LineNumberBg : brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(left + (w - text.Width) / 2, top + (h - text.Height) / 2));
        }
    }

    private Pen? _notePen;
    private Brush? _notePenBrush;

    private Pen NotePen(Brush brush)
    {
        if (_notePen is null || !ReferenceEquals(_notePenBrush, brush))
        {
            _notePen = new Pen(brush, 1.2);
            _notePenBrush = brush;
        }
        return _notePen;
    }
}

/// <summary>注記の印の種類。未解決は塗り、解決済みは淡い輪郭。</summary>
public enum NoteMarkerKind
{
    /// <summary>まだ片付いていない会話（目立たせる）。</summary>
    Open,
    /// <summary>解決済み（淡く）。</summary>
    Resolved,
}

/// <summary>ガターに出す注記の印1つ。<paramref name="Line0"/> は 0 始まりのバッファ行、<paramref name="Count"/> はその行の会話の数
/// （2つ以上で数を添える）、<paramref name="Tooltip"/> は乗ったときに出す中身（会話の本文など。null/空なら出さない）。</summary>
public readonly record struct EditorNoteMarker(int Line0, NoteMarkerKind Kind, int Count = 1, string? Tooltip = null);
