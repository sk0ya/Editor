using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas のテスト実行用ガター列（未実行の ▶ と実行結果のグリフ）。
/// テストの発見・実行・結果判定は **すべてホストの責務** で、エディタは「与えられた行にグリフを描き、
/// クリックを通知する」だけを担う。ブレークポイント列（<see cref="EditorCanvas.Breakpoints"/> 側）と
/// 同じ流儀で、<see cref="_testGlyphsEnabled"/> が false の間は <c>GetGutterMetrics</c> が列の幅を 0 にし、
/// 描画・ヒットテストとも一切作動しない＝既存のレイアウト・見た目は 1px も変わらない。
/// ホストは <see cref="SetTestGlyphsEnabled"/> で列を有効化し、<see cref="SetTestGlyphs"/> で表示を全置換し、
/// <see cref="TestGlyphClicked"/> を購読してその行のテストを実行する。
/// </summary>
public partial class EditorCanvas
{
    private bool _testGlyphsEnabled;
    // バッファ行（0始まり）→ グリフ。ホストが SetTestGlyphs で全置換する（差分更新はしない）。
    private readonly Dictionary<int, EditorTestGlyph> _testGlyphs = new();
    private int _hoveredTestGlyphLine = -1;
    private System.Windows.Controls.ToolTip? _testGlyphToolTip;

    /// <summary>テスト列のグリフがクリックされたとき。引数はバッファ行（0始まり）。
    /// グリフの無い行では発火しない（列内のクリックは本文へは抜けない）。</summary>
    public event Action<int>? TestGlyphClicked;

    /// <summary>テスト列の有効/無効を切り替える。既定は無効（幅 0）。</summary>
    public void SetTestGlyphsEnabled(bool enabled)
    {
        if (_testGlyphsEnabled == enabled) return;
        _testGlyphsEnabled = enabled;
        if (!enabled)
        {
            _hoveredTestGlyphLine = -1;
            CloseTestGlyphToolTip();
        }
        // 列の分だけ本文の左端が動く（折り返し幅・可視桁数に影響）ので、再描画に加えて再レイアウトも要求する。
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>このドキュメントのテストグリフを全置換する。空リストを渡すと列は空になる（列自体は
    /// <see cref="SetTestGlyphsEnabled"/> が false になるまで残る）。</summary>
    public void SetTestGlyphs(IReadOnlyList<EditorTestGlyph> glyphs)
    {
        _testGlyphs.Clear();
        foreach (var g in glyphs) if (g.Line0 >= 0) _testGlyphs[g.Line0] = g;
        if (_hoveredTestGlyphLine >= 0 && !_testGlyphs.ContainsKey(_hoveredTestGlyphLine)) CloseTestGlyphToolTip();
        InvalidateVisual();
    }

    /// <summary>テスト列のクリック処理本体。マウスイベントから切り離してあるのは、座標だけで単体テストから
    /// 叩けるようにするため。列の外なら false（呼び出し側は後続の処理へ進む）。</summary>
    internal bool TryClickTestGlyphColumn(Point point)
    {
        if (!_testGlyphsEnabled) return false;
        if (!_gutterHitTester.TryHitTestGutter(point, CurrentGutterBoundaries(), out int line)) return false;
        if (line >= 0 && _testGlyphs.ContainsKey(line)) TestGlyphClicked?.Invoke(line);
        return true;
    }

    private void SetHoveredTestGlyphLine(int line)
    {
        int normalized = line >= 0 && _testGlyphs.ContainsKey(line) ? line : -1;
        if (_hoveredTestGlyphLine == normalized) return;
        _hoveredTestGlyphLine = normalized;
        UpdateTestGlyphToolTip(normalized);
        if (_testGlyphsEnabled) InvalidateVisual();
    }

    // ツールチップは blame カラムと同じ流儀（マウス位置に出し直す ToolTip）。
    private void UpdateTestGlyphToolTip(int hoveredLine)
    {
        if (hoveredLine < 0 || !_testGlyphs.TryGetValue(hoveredLine, out var glyph)
            || string.IsNullOrEmpty(glyph.Tooltip))
        {
            CloseTestGlyphToolTip();
            return;
        }

        _testGlyphToolTip ??= new System.Windows.Controls.ToolTip
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
        };
        _testGlyphToolTip.IsOpen = false;  // 行移動時にマウス位置へ出し直す
        _testGlyphToolTip.Content = glyph.Tooltip;
        _testGlyphToolTip.IsOpen = true;
    }

    private void CloseTestGlyphToolTip()
    {
        if (_testGlyphToolTip is { } tip) tip.IsOpen = false;
    }

    /// <summary>テスト列（ブレークポイント列の右、行番号列の左）に、その行のグリフを描く。
    /// <paramref name="x"/> は列の左端。</summary>
    private void DrawTestGlyph(DrawingContext dc, int line, double y, double x, int colWidth)
    {
        if (!_testGlyphs.TryGetValue(line, out var glyph)) return;

        double cx = x + colWidth / 2.0;
        double cy = y + _lineHeight / 2.0;
        double r = Math.Max(3.0, Math.Min(colWidth, _lineHeight) / 2.0 - 3.0);

        // ホバー中はうっすら背景を敷いて押せることを示す（色はテーマ資源から）。
        if (_hoveredTestGlyphLine == line && Theme.CurrentLineBg is { } hoverBg)
            dc.DrawEllipse(hoverBg, null, new Point(cx, cy), r + 2, r + 2);

        var brush = TestGlyphBrush(glyph.Kind);
        var pen = new Pen(brush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        switch (glyph.Kind)
        {
            case TestGlyphKind.Run:      // 未実行＝中抜きの ▷
                dc.DrawGeometry(null, pen, PlayTriangle(cx, cy, r));
                break;
            case TestGlyphKind.Running:  // 実行中＝塗りつぶしの ▶
                dc.DrawGeometry(brush, null, PlayTriangle(cx, cy, r));
                break;
            case TestGlyphKind.Passed:   // 成功＝チェック
                dc.DrawLine(pen, new Point(cx - r * 0.75, cy), new Point(cx - r * 0.15, cy + r * 0.6));
                dc.DrawLine(pen, new Point(cx - r * 0.15, cy + r * 0.6), new Point(cx + r * 0.8, cy - r * 0.7));
                break;
            case TestGlyphKind.Failed:   // 失敗＝×
                dc.DrawLine(pen, new Point(cx - r * 0.7, cy - r * 0.7), new Point(cx + r * 0.7, cy + r * 0.7));
                dc.DrawLine(pen, new Point(cx + r * 0.7, cy - r * 0.7), new Point(cx - r * 0.7, cy + r * 0.7));
                break;
            case TestGlyphKind.Skipped:  // スキップ＝⊘（中抜きの丸＋斜線）
                dc.DrawEllipse(null, pen, new Point(cx, cy), r * 0.8, r * 0.8);
                dc.DrawLine(pen, new Point(cx - r * 0.55, cy + r * 0.55), new Point(cx + r * 0.55, cy - r * 0.55));
                break;
        }
    }

    // 配色はテーマ資源から取る（新しいハードコード色を増やさない）。
    private Brush TestGlyphBrush(TestGlyphKind kind) => kind switch
    {
        TestGlyphKind.Passed  => Theme.GitAdded,
        TestGlyphKind.Failed  => Theme.DiagnosticError,
        TestGlyphKind.Skipped => Theme.DiagnosticHint,
        TestGlyphKind.Running => Theme.DiagnosticWarning,
        _                     => Theme.LineNumberFg,
    };

    private static StreamGeometry PlayTriangle(double cx, double cy, double r)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cx - r * 0.6, cy - r), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(cx + r * 0.85, cy), true, false);
            ctx.LineTo(new Point(cx - r * 0.6, cy + r), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>ガターのテストグリフ種別。判定はホストが行い、エディタは描き分けるだけ。</summary>
public enum TestGlyphKind
{
    /// <summary>未実行／実行可能（中抜きの ▷）。</summary>
    Run,
    /// <summary>成功（緑のチェック）。</summary>
    Passed,
    /// <summary>失敗（赤の ×）。</summary>
    Failed,
    /// <summary>スキップ（灰色の ⊘）。</summary>
    Skipped,
    /// <summary>実行中（塗りつぶしの ▶）。</summary>
    Running,
}

/// <summary>ガターに表示する 1 件のテストグリフ。<paramref name="Line0"/> はブレークポイントと同じ
/// 0 始まりのバッファ行。<paramref name="Tooltip"/> はホバー時に出す説明（結果メッセージや所要時間など。
/// null/空ならツールチップを出さない）。</summary>
public readonly record struct EditorTestGlyph(int Line0, TestGlyphKind Kind, string? Tooltip = null);
