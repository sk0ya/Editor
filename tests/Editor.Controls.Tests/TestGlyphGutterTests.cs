using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Controls.Git;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// ガターのテスト実行列（▶と結果グリフ）の幅・ヒットテスト・グリフ更新・ホバー・クリック通知。
/// 「既定 off で既存のレイアウトが 1px も変わらない」ことと、
/// 「blame / ブレークポイント / 行番号 / フォールドの各列と混線しない」ことを固定する。
/// </summary>
public class TestGlyphGutterTests
{
    // ── GutterHitTester（純ロジック。WPF の描画は不要）─────────────────────────────

    private static GutterHitTester NewHitTester(int lineToReturn = 7) => new(_ => lineToReturn);

    // 各列が幅を持つ状態： blame 30 | bp 16 | test 16 | 行番号 40 | フォールド 16 （合計 118）
    private static GutterHitTester.Boundaries AllColumns() => new(30, 16, 16, 40, 118);

    // テスト列が無効（幅 0）の状態： blame 0 | bp 16 | test 0 | 行番号 40 | フォールド 16 （合計 72）
    private static GutterHitTester.Boundaries TestColumnDisabled() => new(0, 16, 0, 40, 72);

    [Fact]
    public void TestGlyphGutter_WhenDisabled_NeverHits()
    {
        var tester = NewHitTester();
        var b = TestColumnDisabled();

        foreach (double x in new double[] { 0, 1, 8, 16, 20, 40, 55, 60, 71 })
            Assert.False(tester.TryHitTestGlyphGutter(new Point(x, 5), b, out _), $"x={x}");
    }

    [Fact]
    public void TestGlyphGutter_WhenDisabled_LeavesOtherColumnBoundariesUnchanged()
    {
        var tester = NewHitTester();
        var b = TestColumnDisabled();

        // ブレークポイント列は 0..16、行番号は 16..56、フォールドは 56..72 のまま。
        Assert.True(tester.TryHitBreakpointGutter(new Point(8, 5), b, out _));
        Assert.False(tester.TryHitBreakpointGutter(new Point(16, 5), b, out _));

        Assert.True(tester.TryHitLineNumberGutter(new Point(16, 5), b, out _));
        Assert.True(tester.TryHitLineNumberGutter(new Point(55, 5), b, out _));
        Assert.False(tester.TryHitLineNumberGutter(new Point(56, 5), b, out _));

        Assert.False(tester.TryHitFoldGutter(new Point(55, 5), b, out _));
        Assert.True(tester.TryHitFoldGutter(new Point(56, 5), b, out _));
        Assert.False(tester.TryHitFoldGutter(new Point(72, 5), b, out _));
    }

    [Fact]
    public void TestGlyphGutter_WhenEnabled_HitsOnlyItsOwnRange()
    {
        var tester = NewHitTester();
        var b = AllColumns();

        // テスト列は blame(30) + bp(16) = 46 から 62 まで。
        Assert.False(tester.TryHitTestGlyphGutter(new Point(45, 5), b, out _));
        Assert.True(tester.TryHitTestGlyphGutter(new Point(46, 5), b, out _));
        Assert.True(tester.TryHitTestGlyphGutter(new Point(61, 5), b, out _));
        Assert.False(tester.TryHitTestGlyphGutter(new Point(62, 5), b, out _));
    }

    [Fact]
    public void TestGlyphGutter_HitReturnsResolvedBufferLine()
    {
        var tester = NewHitTester(lineToReturn: 42);
        Assert.True(tester.TryHitTestGlyphGutter(new Point(50, 5), AllColumns(), out int line));
        Assert.Equal(42, line);
    }

    [Theory]
    // x座標 → 期待する列（blame / bp / test / linenum / fold）。どの x でもちょうど1列だけが true。
    [InlineData(10, true, false, false, false, false)]   // blame 列
    [InlineData(29, true, false, false, false, false)]   // blame 列の右端
    [InlineData(30, false, true, false, false, false)]   // 境界ちょうど＝ブレークポイント列の左端
    [InlineData(40, false, true, false, false, false)]   // ブレークポイント列
    [InlineData(46, false, false, true, false, false)]   // テスト列の左端
    [InlineData(50, false, false, true, false, false)]   // テスト列
    [InlineData(62, false, false, false, true, false)]   // 行番号列の左端
    [InlineData(70, false, false, false, true, false)]   // 行番号列
    [InlineData(102, false, false, false, false, true)]  // フォールド列の左端
    [InlineData(110, false, false, false, false, true)]  // フォールド列
    [InlineData(118, false, false, false, false, false)] // GutterWidth ちょうど＝ガターの外（本文）
    public void GutterColumns_DoNotOverlap(double x, bool blame, bool bp, bool test, bool lineNum, bool fold)
    {
        var tester = NewHitTester();
        var b = AllColumns();
        var p = new Point(x, 5);

        Assert.Equal(blame, tester.TryHitBlameGutter(p, b, out _));
        Assert.Equal(bp, tester.TryHitBreakpointGutter(p, b, out _));
        Assert.Equal(test, tester.TryHitTestGlyphGutter(p, b, out _));
        Assert.Equal(lineNum, tester.TryHitLineNumberGutter(p, b, out _));
        Assert.Equal(fold, tester.TryHitFoldGutter(p, b, out _));
    }

    // ── EditorCanvas（列の幅・クリック・ホバー・ツールチップ）─────────────────────

    private static (int Bp, int Test, int Num, double Fold, int Gutter) Metrics(EditorCanvas canvas)
    {
        var method = typeof(EditorCanvas).GetMethod("GetGutterMetrics", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tuple = (ITuple)method.Invoke(canvas, null)!;
        return ((int)tuple[0]!, (int)tuple[1]!, (int)tuple[2]!, (double)tuple[3]!, (int)tuple[4]!);
    }

    private static T Field<T>(EditorCanvas canvas, string name) =>
        (T)typeof(EditorCanvas).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;

    private static double LineHeight(EditorCanvas canvas) => Field<double>(canvas, "_lineHeight");
    private static double BlameColWidth(EditorCanvas canvas) => Field<double>(canvas, "_blameColWidth");
    private static int HoveredTestGlyphLine(EditorCanvas canvas) => Field<int>(canvas, "_hoveredTestGlyphLine");

    private static System.Windows.Controls.ToolTip? TestGlyphToolTip(EditorCanvas canvas) =>
        (System.Windows.Controls.ToolTip?)typeof(EditorCanvas)
            .GetField("_testGlyphToolTip", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas);

    private static EditorCanvas NewCanvas(int lineCount = 4)
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(Enumerable.Range(0, lineCount).Select(i => $"line {i}").ToArray());
        return canvas;
    }

    // テスト列の中央 x（blame・ブレークポイント列の幅を踏まえた実座標）。
    private static double TestColumnCenterX(EditorCanvas canvas)
    {
        var m = Metrics(canvas);
        return BlameColWidth(canvas) + m.Bp + m.Test / 2.0;
    }

    private static Point Row(EditorCanvas canvas, int line, double x) => new(x, LineHeight(canvas) * (line + 0.5));

    [Fact]
    public void TestGlyphColumn_DefaultsToDisabled_AndAddsNoWidth()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            var before = Metrics(canvas);

            Assert.Equal(0, before.Test);

            // 有効化してはじめて列が現れ、ガター幅がその分だけ広がる。
            canvas.SetTestGlyphsEnabled(true);
            var after = Metrics(canvas);
            Assert.True(after.Test > 0);
            Assert.Equal(before.Gutter + after.Test, after.Gutter);

            // 無効に戻すと元の幅に戻る（1px も残らない）。
            canvas.SetTestGlyphsEnabled(false);
            Assert.Equal(before, Metrics(canvas));
        });
    }

    [Fact]
    public void TestGlyphColumn_WhenDisabled_SwallowsNoClick()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Run)]);

            int clicked = -1;
            canvas.TestGlyphClicked += line => clicked = line;

            Assert.False(canvas.TryClickTestGlyphColumn(new Point(4, LineHeight(canvas) * 1.5)));
            Assert.Equal(-1, clicked);
        });
    }

    [Fact]
    public void TestGlyphClick_ReportsZeroBasedBufferLine()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetTestGlyphs([new EditorTestGlyph(2, TestGlyphKind.Run, "まだ実行していません")]);

            int clicked = -1;
            canvas.TestGlyphClicked += line => clicked = line;

            // 3行目＝バッファ行 2 の中央。
            Assert.True(canvas.TryClickTestGlyphColumn(Row(canvas, 2, TestColumnCenterX(canvas))));
            Assert.Equal(2, clicked);
        });
    }

    [Fact]
    public void TestGlyphClick_OnLineWithoutGlyph_RaisesNothingButStaysInTheColumn()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetTestGlyphs([new EditorTestGlyph(2, TestGlyphKind.Run)]);

            int raised = 0;
            canvas.TestGlyphClicked += _ => raised++;

            // 列内のクリックなので true（本文へは抜けない）が、グリフの無い行では通知しない。
            Assert.True(canvas.TryClickTestGlyphColumn(Row(canvas, 0, TestColumnCenterX(canvas))));
            Assert.Equal(0, raised);
        });
    }

    [Fact]
    public void SetTestGlyphs_ReplacesAll_AndEmptyListClears()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);

            var clicks = new List<int>();
            canvas.TestGlyphClicked += clicks.Add;
            double x = TestColumnCenterX(canvas);

            canvas.SetTestGlyphs([new EditorTestGlyph(0, TestGlyphKind.Passed), new EditorTestGlyph(1, TestGlyphKind.Failed)]);
            canvas.TryClickTestGlyphColumn(Row(canvas, 0, x));
            canvas.TryClickTestGlyphColumn(Row(canvas, 1, x));
            Assert.Equal(new[] { 0, 1 }, clicks);

            // 全置換：行 0 のグリフは消え、行 3 に現れる。
            clicks.Clear();
            canvas.SetTestGlyphs([new EditorTestGlyph(3, TestGlyphKind.Running)]);
            canvas.TryClickTestGlyphColumn(Row(canvas, 0, x));
            canvas.TryClickTestGlyphColumn(Row(canvas, 1, x));
            canvas.TryClickTestGlyphColumn(Row(canvas, 3, x));
            Assert.Equal(new[] { 3 }, clicks);

            // 空リストで全消去（列自体は残るのでクリックは吸うが、通知は出ない）。
            clicks.Clear();
            canvas.SetTestGlyphs([]);
            canvas.TryClickTestGlyphColumn(Row(canvas, 3, x));
            Assert.Empty(clicks);
        });
    }

    [Fact]
    public void SetTestGlyphs_DropsNegativeLines_IgnoresOutOfRange_AndLastDuplicateWins()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas(lineCount: 3);
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetTestGlyphs([
                new EditorTestGlyph(-1, TestGlyphKind.Run, "捨てられる"),
                new EditorTestGlyph(1, TestGlyphKind.Run, "先に来たほう"),
                new EditorTestGlyph(1, TestGlyphKind.Failed, "後勝ち"),
                new EditorTestGlyph(99, TestGlyphKind.Passed, "範囲外"),
            ]);

            var glyphs = Field<Dictionary<int, EditorTestGlyph>>(canvas, "_testGlyphs");
            Assert.False(glyphs.ContainsKey(-1));
            Assert.Equal(new EditorTestGlyph(1, TestGlyphKind.Failed, "後勝ち"), glyphs[1]);
            // 範囲外の行は保持はされるが（ホストが行を追加する前に届くことがある）、可視行に無いので描かれない。
            Assert.True(glyphs.ContainsKey(99));

            // 範囲外の行を持ったまま描いても落ちない。
            var window = WpfTestHost.Load(canvas);
            try
            {
                new RenderTargetBitmap(200, 120, 96, 96, PixelFormats.Pbgra32).Render(canvas);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SetTestGlyphs_WithIdenticalContent_IsANoOp()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetTestGlyphs([
                new EditorTestGlyph(0, TestGlyphKind.Passed, "12ms"),
                new EditorTestGlyph(1, TestGlyphKind.Run),
            ]);

            // 同じ内容なら差し替えも再描画もしない（1件ずつ結果を流し込むホストが毎回全面再描画を積まないため）。
            Assert.True(canvas.TestGlyphsUnchanged([
                new EditorTestGlyph(0, TestGlyphKind.Passed, "12ms"),
                new EditorTestGlyph(1, TestGlyphKind.Run),
            ]));
            // 負値は元から捨てられているので、混ざっていても「変わっていない」。
            Assert.True(canvas.TestGlyphsUnchanged([
                new EditorTestGlyph(-3, TestGlyphKind.Failed),
                new EditorTestGlyph(0, TestGlyphKind.Passed, "12ms"),
                new EditorTestGlyph(1, TestGlyphKind.Run),
            ]));

            // 種別・ツールチップ・件数のどれが違っても「変わった」。
            Assert.False(canvas.TestGlyphsUnchanged([
                new EditorTestGlyph(0, TestGlyphKind.Failed, "12ms"),
                new EditorTestGlyph(1, TestGlyphKind.Run),
            ]));
            Assert.False(canvas.TestGlyphsUnchanged([
                new EditorTestGlyph(0, TestGlyphKind.Passed, "13ms"),
                new EditorTestGlyph(1, TestGlyphKind.Run),
            ]));
            Assert.False(canvas.TestGlyphsUnchanged([new EditorTestGlyph(0, TestGlyphKind.Passed, "12ms")]));
            Assert.False(canvas.TestGlyphsUnchanged([]));
        });
    }

    [Fact]
    public void BreakpointAndTestColumns_BothEnabled_DoNotCrossTalk()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetBreakpointsEnabled(true);
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Run)]);

            var m = Metrics(canvas);
            Assert.True(m.Bp > 0);
            Assert.True(m.Test > 0);

            var toggled = new List<int>();
            var testClicks = new List<int>();
            canvas.BreakpointToggled += toggled.Add;
            canvas.TestGlyphClicked += testClicks.Add;

            double bpX = m.Bp / 2.0;              // blame 非表示なのでブレークポイント列は 0..Bp
            double testX = m.Bp + m.Test / 2.0;   // その右がテスト列

            // ブレークポイント列のクリック — トグルだけが上がり、テスト列は反応しない。
            Assert.False(canvas.TryClickTestGlyphColumn(Row(canvas, 1, bpX)));
            Assert.True(canvas.TryClickBreakpointColumn(Row(canvas, 1, bpX)));
            Assert.Equal(new[] { 1 }, toggled);
            Assert.Empty(testClicks);

            // テスト列のクリック — テストだけが上がり、ブレークポイントは反応しない。
            toggled.Clear();
            Assert.False(canvas.TryClickBreakpointColumn(Row(canvas, 1, testX)));
            Assert.True(canvas.TryClickTestGlyphColumn(Row(canvas, 1, testX)));
            Assert.Equal(new[] { 1 }, testClicks);
            Assert.Empty(toggled);
        });
    }

    [Fact]
    public void TestColumn_ShiftsRightOfTheBlameMargin()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Run)]);
            canvas.SetBlameLines(new Dictionary<int, EditorBlameLine>
            {
                [0] = new EditorBlameLine("abc1234", "koya", "2026-08-22", "初期実装"),
                [1] = new EditorBlameLine("abc1234", "koya", "2026-08-22", "初期実装"),
            });

            double blameWidth = BlameColWidth(canvas);
            Assert.True(blameWidth > 0);

            var m = Metrics(canvas);
            var clicks = new List<int>();
            canvas.TestGlyphClicked += clicks.Add;

            // blame カラムの中はテスト列ではない。
            Assert.False(canvas.TryClickTestGlyphColumn(Row(canvas, 1, blameWidth / 2)));
            // テスト列は blame の右（bp 幅 0）。
            Assert.True(canvas.TryClickTestGlyphColumn(Row(canvas, 1, blameWidth + m.Test / 2.0)));
            Assert.Equal(new[] { 1 }, clicks);
        });
    }

    [Fact]
    public void WrappedContinuationRow_NeitherClicksNorHovers()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = new EditorCanvas();
            canvas.UpdateFont("Consolas", 14);
            canvas.SetLines([new string('a', 400), "short"]);
            canvas.SetTestGlyphsEnabled(true);
            // 行 1 にもグリフを置く。継続行を「バッファ行 1」と誤認していたら、下のクリックが 1 を上げてしまう。
            canvas.SetTestGlyphs([
                new EditorTestGlyph(0, TestGlyphKind.Run, "実行する"),
                new EditorTestGlyph(1, TestGlyphKind.Run, "こちらは呼ばれない"),
            ]);

            var window = WpfTestHost.Load(canvas, width: 320, height: 240);
            try
            {
                canvas.WrapLines = true;
                canvas.UpdateLayout();

                var clicks = new List<int>();
                canvas.TestGlyphClicked += clicks.Add;
                double x = TestColumnCenterX(canvas);

                // 1行目（＝バッファ行 0 の先頭ビジュアル行）は効く。
                Assert.True(canvas.TryClickTestGlyphColumn(Row(canvas, 0, x)));
                Assert.Equal(new[] { 0 }, clicks);

                // 2行目は折り返しの継続行 — HitTestGutterLine が -1 を返すので通知しない。
                clicks.Clear();
                Assert.True(canvas.TryClickTestGlyphColumn(Row(canvas, 1, x)));
                Assert.Empty(clicks);

                // ホバーも継続行では付かない。
                canvas.TrackMousePoint(Row(canvas, 1, x));
                canvas.SetTestGlyphs([
                    new EditorTestGlyph(0, TestGlyphKind.Passed, "12ms"),
                    new EditorTestGlyph(1, TestGlyphKind.Passed, "12ms"),
                ]);
                Assert.Equal(-1, HoveredTestGlyphLine(canvas));
            }
            finally { window.Close(); }
        });
    }

    // ── ホバーとツールチップ ─────────────────────────────────────────────────────

    [Fact]
    public void SetTestGlyphs_WhileHovering_RefreshesTooltipContent()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            var window = WpfTestHost.Load(canvas);
            try
            {
                canvas.TrackMousePoint(Row(canvas, 1, TestColumnCenterX(canvas)));
                canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Run, "まだ実行していません")]);

                Assert.Equal(1, HoveredTestGlyphLine(canvas));
                var tip = TestGlyphToolTip(canvas);
                Assert.NotNull(tip);
                Assert.True(tip!.IsOpen);
                Assert.Equal("まだ実行していません", tip.Content);

                // マウスを動かさないまま結果が届いても、古い文言が残らない。
                canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Failed, "Assert.Equal() Failure")]);
                Assert.Equal("Assert.Equal() Failure", TestGlyphToolTip(canvas)!.Content);
                Assert.True(TestGlyphToolTip(canvas)!.IsOpen);

                // Tooltip の無いグリフに置き換わったら閉じる。
                canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Running)]);
                Assert.False(TestGlyphToolTip(canvas)!.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SetTestGlyphs_WhileHovering_PicksUpAGlyphThatJustAppeared()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            var window = WpfTestHost.Load(canvas);
            try
            {
                // マウスは列上にあるが、その行にはまだグリフが無い。
                canvas.TrackMousePoint(Row(canvas, 2, TestColumnCenterX(canvas)));
                canvas.SetTestGlyphs([new EditorTestGlyph(0, TestGlyphKind.Run)]);
                Assert.Equal(-1, HoveredTestGlyphLine(canvas));

                // 検出完了で▶が付いた瞬間、マウスを動かさなくてもホバー扱いになる。
                canvas.SetTestGlyphs([new EditorTestGlyph(0, TestGlyphKind.Run), new EditorTestGlyph(2, TestGlyphKind.Run, "実行する")]);
                Assert.Equal(2, HoveredTestGlyphLine(canvas));
                Assert.True(TestGlyphToolTip(canvas)!.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SetTestGlyphs_ThatRemovesTheHoveredGlyph_ClearsHoverAndTooltip()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            var window = WpfTestHost.Load(canvas);
            try
            {
                canvas.TrackMousePoint(Row(canvas, 1, TestColumnCenterX(canvas)));
                canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Run, "実行する")]);
                Assert.Equal(1, HoveredTestGlyphLine(canvas));

                canvas.SetTestGlyphs([]);
                Assert.Equal(-1, HoveredTestGlyphLine(canvas));
                Assert.False(TestGlyphToolTip(canvas)!.IsOpen);

                // 同じ行にあとからグリフが戻っても、マウス位置が変わっていない限りは正しくホバーに戻る。
                canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Passed, "12ms")]);
                Assert.Equal(1, HoveredTestGlyphLine(canvas));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DisablingTheColumn_ClearsHoverAndTooltip()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetTestGlyphsEnabled(true);
            var window = WpfTestHost.Load(canvas);
            try
            {
                canvas.TrackMousePoint(Row(canvas, 1, TestColumnCenterX(canvas)));
                canvas.SetTestGlyphs([new EditorTestGlyph(1, TestGlyphKind.Run, "実行する")]);
                Assert.Equal(1, HoveredTestGlyphLine(canvas));

                canvas.SetTestGlyphsEnabled(false);
                Assert.Equal(-1, HoveredTestGlyphLine(canvas));
                Assert.False(TestGlyphToolTip(canvas)!.IsOpen);
            }
            finally { window.Close(); }
        });
    }

    // ── 描画（無効時にガターが 1px も変わらない）─────────────────────────────────

    [Fact]
    public void Render_WhenDisabled_LeavesTheGutterPixelsUntouched()
    {
        WpfTestHost.Run(() =>
        {
            var glyphs = new[]
            {
                new EditorTestGlyph(0, TestGlyphKind.Run, "実行する"),
                new EditorTestGlyph(1, TestGlyphKind.Passed, "12ms"),
                new EditorTestGlyph(2, TestGlyphKind.Failed, "Assert.Equal() Failure"),
                new EditorTestGlyph(3, TestGlyphKind.Skipped, "スキップ"),
                new EditorTestGlyph(4, TestGlyphKind.Running),
            };

            // 無効のまま（グリフだけ渡す）と、そもそも何も渡さない場合とでピクセルが一致する。
            byte[] baseline = RenderPixels(canvas => { });
            byte[] withGlyphsButDisabled = RenderPixels(canvas => canvas.SetTestGlyphs(glyphs));
            Assert.Equal(baseline, withGlyphsButDisabled);

            // 有効化すると（当然）変わる＝この比較が本当に効いていることの確認。
            byte[] enabled = RenderPixels(canvas =>
            {
                canvas.SetTestGlyphsEnabled(true);
                canvas.SetTestGlyphs(glyphs);
            });
            Assert.NotEqual(baseline, enabled);
        });
    }

    private static byte[] RenderPixels(Action<EditorCanvas> configure)
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(["one", "two", "three", "four", "five"]);
        configure(canvas);

        var window = WpfTestHost.Load(canvas, width: 300, height: 200);
        try
        {
            canvas.UpdateLayout();
            canvas.ResetCursorBlink();   // 点滅位相で 2 回のレンダリングがずれないように揃える
            var bmp = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(canvas);
            int stride = 300 * 4;
            var pixels = new byte[stride * 200];
            bmp.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        finally { window.Close(); }
    }
}
