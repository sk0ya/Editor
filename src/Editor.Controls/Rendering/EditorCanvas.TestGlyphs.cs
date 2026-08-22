using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Editor.Controls.Themes;

namespace Editor.Controls.Rendering;

/// <summary>
/// EditorCanvas のテスト実行用ガター列（未実行の ▶ と実行結果のグリフ）。
/// テストの発見・実行・結果判定は **すべてホストの責務** で、エディタは「与えられた行にグリフを描き、
/// クリックを通知する」だけを担う。ブレークポイント列（<see cref="EditorCanvas.Breakpoints"/> 側）と
/// 同じ流儀で、<see cref="_testGlyphsEnabled"/> が false の間は <c>GetGutterMetrics</c> が列の幅を 0 にし、
/// 描画・ヒットテストとも一切作動しない＝既存のレイアウト・見た目は 1px も変わらない。
/// ホストは <see cref="SetTestGlyphsEnabled"/> で列を有効化し、<see cref="SetTestGlyphs"/> で表示を全置換し、
/// <see cref="TestGlyphClicked"/> を購読してその行のテストを実行する。
/// 操作はマウス専用（キーボード／スクリーンリーダー導線は持たない）＝ホスト側のコマンドで補う前提。
/// </summary>
public partial class EditorCanvas
{
    private bool _testGlyphsEnabled;
    // バッファ行（0始まり）→ グリフ。ホストが SetTestGlyphs で全置換する（差分更新はしない）。
    private readonly Dictionary<int, EditorTestGlyph> _testGlyphs = new();
    private int _hoveredTestGlyphLine = -1;
    private System.Windows.Controls.ToolTip? _testGlyphToolTip;
    // 最後に見たマウス位置（キャンバス相対）。null＝キャンバス外。SetTestGlyphs のホバー再評価に使う。
    private Point? _lastMousePoint;

    // ホバーリングは専用の半透明白。ガター背景（LineNumberBg）とカレント行背景（CurrentLineBg）は
    // テーマによってはほぼ同色なので、テーマ資源を借りると「見えないホバー」になる。
    // ブレークポイント列のホバー色と同じく、この用途専用の固定色として持つ。
    private static readonly Brush TestGlyphHoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    /// <summary>テスト列のグリフがクリックされたとき。引数はバッファ行（0始まり）。
    /// グリフの無い行では発火しない（列内のクリックは本文へは抜けない）。</summary>
    public event Action<int>? TestGlyphClicked;

    /// <summary>テスト列の有効/無効を切り替える。既定は無効（幅 0）。</summary>
    public void SetTestGlyphsEnabled(bool enabled)
    {
        if (_testGlyphsEnabled == enabled) return;
        _testGlyphsEnabled = enabled;
        SetHoveredTestGlyphLine(-1);
        // 列の分だけ本文の左端が動く（折り返し幅・可視桁数に影響）ので、再描画に加えて再レイアウトも要求する。
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>このドキュメントのテストグリフを全置換する。空リストを渡すと列は空になる（列自体は
    /// <see cref="SetTestGlyphsEnabled"/> が false になるまで残る）。<c>Line0</c> が負の要素は捨て、
    /// 同じ行が複数あれば後勝ち。内容が現在と同じなら何もしない（1件ずつ結果を流し込むホストが
    /// 毎回全面再描画を積まないように）。</summary>
    public void SetTestGlyphs(IReadOnlyList<EditorTestGlyph> glyphs)
    {
        if (TestGlyphsUnchanged(glyphs)) return;

        _testGlyphs.Clear();
        foreach (var g in glyphs) if (g.Line0 >= 0) _testGlyphs[g.Line0] = g;

        // ホバーを付け直す。マウスが列上にいるなら「今この瞬間グリフが付いた行」もホバー扱いになり
        // （テスト検出完了で ▶ が一斉に出る瞬間に「押せない列」に見えるのを防ぐ）、逆にグリフが消えた
        // 行のホバーは解除される＝_hoveredTestGlyphLine が古い行を指したまま残らない。
        ReevaluateTestGlyphHover();
        // ツールチップは無条件に貼り直す。ホバー行が変わらなくても中身は変わり得る
        // （▶「まだ実行していません」→ × 「Assert.Equal() Failure」）。
        UpdateTestGlyphToolTip(_hoveredTestGlyphLine);

        InvalidateVisual();
    }

    // 与えられた一覧が現在の内容と一致するか。重複 Line0 があると件数が食い違うので「変わった」と見なす（安全側）。
    internal bool TestGlyphsUnchanged(IReadOnlyList<EditorTestGlyph> glyphs)
    {
        int valid = 0;
        foreach (var g in glyphs)
        {
            if (g.Line0 < 0) continue;
            valid++;
            if (!_testGlyphs.TryGetValue(g.Line0, out var current) || current != g) return false;
        }
        return valid == _testGlyphs.Count;
    }

    /// <summary>テスト列のクリック処理本体。マウスイベントから切り離してあるのは、座標だけで単体テストから
    /// 叩けるようにするため。列の外なら false（呼び出し側は後続の処理へ進む）。</summary>
    internal bool TryClickTestGlyphColumn(Point point)
    {
        if (!_testGlyphsEnabled) return false;
        if (!_gutterHitTester.TryHitTestGlyphGutter(point, CurrentGutterBoundaries(), out int line)) return false;
        if (line >= 0 && _testGlyphs.ContainsKey(line)) TestGlyphClicked?.Invoke(line);
        return true;
    }

    /// <summary>マウス位置を控える（OnMouseMove の先頭・OnMouseLeave から呼ぶ。null＝キャンバス外）。
    /// 控えた位置は <see cref="SetTestGlyphs"/> のホバー再評価に使う。</summary>
    internal void TrackMousePoint(Point? point) => _lastMousePoint = point;

    // 控えたマウス位置からテスト列のホバーを判定し直す。位置が分からなければ解除する。
    private void ReevaluateTestGlyphHover()
    {
        int line = -1;
        bool inColumn = _testGlyphsEnabled && _lastMousePoint is { } point
            && _gutterHitTester.TryHitTestGlyphGutter(point, CurrentGutterBoundaries(), out line);
        SetHoveredTestGlyphLine(inColumn ? line : -1);
    }

    private void SetHoveredTestGlyphLine(int line)
    {
        int normalized = _testGlyphsEnabled && line >= 0 && _testGlyphs.ContainsKey(line) ? line : -1;
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
        _testGlyphToolTip.IsOpen = false;  // 行移動・内容更新時はマウス位置へ出し直す
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

        // ホバー中はうっすら背景を敷いて押せることを示す。
        if (_hoveredTestGlyphLine == line)
            dc.DrawEllipse(TestGlyphHoverBrush, null, new Point(cx, cy), r + 2, r + 2);

        switch (glyph.Kind)
        {
            case TestGlyphKind.Run:      // 未実行＝中抜きの ▷
                DrawPlayTriangle(dc, null, TestGlyphPen(glyph.Kind), cx, cy, r);
                break;
            case TestGlyphKind.Running:  // 実行中＝塗りつぶしの ▶（輪郭は描かないので Pen を作らない）
                DrawPlayTriangle(dc, TestGlyphBrush(glyph.Kind), null, cx, cy, r);
                break;
            case TestGlyphKind.Passed:   // 成功＝チェック
            {
                var pen = TestGlyphPen(glyph.Kind);
                dc.DrawLine(pen, new Point(cx - r * 0.75, cy), new Point(cx - r * 0.15, cy + r * 0.6));
                dc.DrawLine(pen, new Point(cx - r * 0.15, cy + r * 0.6), new Point(cx + r * 0.8, cy - r * 0.7));
                break;
            }
            case TestGlyphKind.Failed:   // 失敗＝×
            {
                var pen = TestGlyphPen(glyph.Kind);
                dc.DrawLine(pen, new Point(cx - r * 0.7, cy - r * 0.7), new Point(cx + r * 0.7, cy + r * 0.7));
                dc.DrawLine(pen, new Point(cx + r * 0.7, cy - r * 0.7), new Point(cx - r * 0.7, cy + r * 0.7));
                break;
            }
            case TestGlyphKind.Skipped:  // スキップ＝⊘（中抜きの丸＋斜線）
            {
                var pen = TestGlyphPen(glyph.Kind);
                dc.DrawEllipse(null, pen, new Point(cx, cy), r * 0.8, r * 0.8);
                dc.DrawLine(pen, new Point(cx - r * 0.55, cy + r * 0.55), new Point(cx + r * 0.55, cy - r * 0.55));
                break;
            }
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

    // 種別ごとの Pen。テーマ依存なので static にはできないが、可視行ごとに作り直すのは無駄なので
    // テーマが差し替わったときだけ捨てて作り直す。Freeze しないのは、Pen を凍らせるとテーマが持つ
    // Brush まで巻き添えで凍るため（ブラシはテーマの持ち物で、この列の持ち物ではない）。
    private readonly Dictionary<TestGlyphKind, Pen> _testGlyphPens = new();
    private EditorTheme? _testGlyphPenTheme;

    private Pen TestGlyphPen(TestGlyphKind kind)
    {
        if (!ReferenceEquals(_testGlyphPenTheme, Theme))
        {
            _testGlyphPens.Clear();
            _testGlyphPenTheme = Theme;
        }
        if (!_testGlyphPens.TryGetValue(kind, out var pen))
        {
            pen = new Pen(TestGlyphBrush(kind), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _testGlyphPens[kind] = pen;
        }
        return pen;
    }

    // ▶ の形は原点中心で組んでおき、行ごとの位置は平行移動で当てる。こうすると半径
    // （＝行高・列幅）が変わったときだけ StreamGeometry を作り直せばよい。
    private StreamGeometry? _playTriangle;
    private double _playTriangleRadius = double.NaN;

    private void DrawPlayTriangle(DrawingContext dc, Brush? fill, Pen? pen, double cx, double cy, double r)
    {
        if (_playTriangle is null || _playTriangleRadius != r)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(-r * 0.6, -r), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(r * 0.85, 0), true, false);
                ctx.LineTo(new Point(-r * 0.6, r), true, false);
            }
            geometry.Freeze();
            _playTriangle = geometry;
            _playTriangleRadius = r;
        }

        // DrawingContext は Transform を参照で保持するので、行ごとに凍らせた別インスタンスを渡す
        // （1つを使い回して座標を書き換えると、同じ描画パス内の全グリフが最後の位置にずれる）。
        var offset = new TranslateTransform(cx, cy);
        offset.Freeze();
        dc.PushTransform(offset);
        dc.DrawGeometry(fill, pen, _playTriangle);
        dc.Pop();
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
