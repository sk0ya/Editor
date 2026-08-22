using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// ガターのテスト実行列（▶と結果グリフ）の幅・ヒットテスト・グリフ更新・クリック通知。
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
    public void TestGutter_WhenDisabled_NeverHits()
    {
        var tester = NewHitTester();
        var b = TestColumnDisabled();

        foreach (double x in new double[] { 0, 1, 8, 16, 20, 40, 55, 60, 71 })
            Assert.False(tester.TryHitTestGutter(new Point(x, 5), b, out _), $"x={x}");
    }

    [Fact]
    public void TestGutter_WhenDisabled_LeavesOtherColumnBoundariesUnchanged()
    {
        var tester = NewHitTester();
        var b = TestColumnDisabled();

        // ブレークポイント列は 0..16、行番号は 16..56、フォールドは 56..72 のまま。
        Assert.True(tester.TryHitBreakpointGutter(new Point(8, 5), b, out _));
        Assert.False(tester.TryHitBreakpointGutter(new Point(16, 5), b, out _));

        Assert.True(tester.TryHitLineNumberGutter(new Point(55, 5), b, out _));
        Assert.False(tester.TryHitLineNumberGutter(new Point(56, 5), b, out _));

        Assert.False(tester.TryHitFoldGutter(new Point(55, 5), b, out _));
        Assert.True(tester.TryHitFoldGutter(new Point(56, 5), b, out _));
        Assert.False(tester.TryHitFoldGutter(new Point(72, 5), b, out _));
    }

    [Fact]
    public void TestGutter_WhenEnabled_HitsOnlyItsOwnRange()
    {
        var tester = NewHitTester();
        var b = AllColumns();

        // テスト列は blame(30) + bp(16) = 46 から 62 まで。
        Assert.False(tester.TryHitTestGutter(new Point(45, 5), b, out _));
        Assert.True(tester.TryHitTestGutter(new Point(46, 5), b, out _));
        Assert.True(tester.TryHitTestGutter(new Point(61, 5), b, out _));
        Assert.False(tester.TryHitTestGutter(new Point(62, 5), b, out _));
    }

    [Fact]
    public void TestGutter_HitReturnsResolvedBufferLine()
    {
        var tester = NewHitTester(lineToReturn: 42);
        Assert.True(tester.TryHitTestGutter(new Point(50, 5), AllColumns(), out int line));
        Assert.Equal(42, line);
    }

    [Theory]
    // x座標 → 期待する列（blame / bp / test / linenum / fold）
    [InlineData(10, true, false, false, true, false)]   // blame 列（行番号判定は blame も含む既存仕様のまま）
    [InlineData(40, false, true, false, true, false)]   // ブレークポイント列
    [InlineData(50, false, false, true, true, false)]   // テスト列
    [InlineData(70, false, false, false, true, false)]  // 行番号列
    [InlineData(110, false, false, false, false, true)] // フォールド列
    public void GutterColumns_DoNotOverlap(double x, bool blame, bool bp, bool test, bool lineNum, bool fold)
    {
        var tester = NewHitTester();
        var b = AllColumns();
        var p = new Point(x, 5);

        Assert.Equal(blame, tester.TryHitBlameGutter(p, b, out _));
        Assert.Equal(bp, tester.TryHitBreakpointGutter(p, b, out _));
        Assert.Equal(test, tester.TryHitTestGutter(p, b, out _));
        Assert.Equal(lineNum, tester.TryHitLineNumberGutter(p, b, out _));
        Assert.Equal(fold, tester.TryHitFoldGutter(p, b, out _));
    }

    // ── EditorCanvas（列の幅とクリック）───────────────────────────────────────────

    private static (int Bp, int Test, int Num, double Fold, int Gutter) Metrics(EditorCanvas canvas)
    {
        var method = typeof(EditorCanvas).GetMethod("GetGutterMetrics", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tuple = (ITuple)method.Invoke(canvas, null)!;
        return ((int)tuple[0]!, (int)tuple[1]!, (int)tuple[2]!, (double)tuple[3]!, (int)tuple[4]!);
    }

    private static double LineHeight(EditorCanvas canvas) =>
        (double)typeof(EditorCanvas).GetField("_lineHeight", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;

    private static EditorCanvas NewCanvas()
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(["one", "two", "three", "four"]);
        return canvas;
    }

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

            var m = Metrics(canvas);
            double x = m.Bp + m.Test / 2.0;              // blame 非表示なので bp 列の右＝テスト列
            double y = LineHeight(canvas) * 2.5;         // 3行目＝バッファ行 2 の中央

            Assert.True(canvas.TryClickTestGlyphColumn(new Point(x, y)));
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

            var m = Metrics(canvas);
            double x = m.Bp + m.Test / 2.0;

            // 列内のクリックなので true（本文へは抜けない）が、グリフの無い行では通知しない。
            Assert.True(canvas.TryClickTestGlyphColumn(new Point(x, LineHeight(canvas) * 0.5)));
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

            var m = Metrics(canvas);
            double x = m.Bp + m.Test / 2.0;
            Point Row(int line) => new(x, LineHeight(canvas) * (line + 0.5));

            canvas.SetTestGlyphs([new EditorTestGlyph(0, TestGlyphKind.Passed), new EditorTestGlyph(1, TestGlyphKind.Failed)]);
            canvas.TryClickTestGlyphColumn(Row(0));
            canvas.TryClickTestGlyphColumn(Row(1));
            Assert.Equal(new[] { 0, 1 }, clicks);

            // 全置換：行 0 のグリフは消え、行 3 に現れる。
            clicks.Clear();
            canvas.SetTestGlyphs([new EditorTestGlyph(3, TestGlyphKind.Running)]);
            canvas.TryClickTestGlyphColumn(Row(0));
            canvas.TryClickTestGlyphColumn(Row(1));
            canvas.TryClickTestGlyphColumn(Row(3));
            Assert.Equal(new[] { 3 }, clicks);

            // 空リストで全消去（列自体は残るのでクリックは吸うが、通知は出ない）。
            clicks.Clear();
            canvas.SetTestGlyphs([]);
            canvas.TryClickTestGlyphColumn(Row(3));
            Assert.Empty(clicks);
        });
    }

    [Fact]
    public void Render_WithEveryGlyphKind_DoesNotThrow()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = new EditorCanvas();
            canvas.UpdateFont("Consolas", 14);
            canvas.SetLines(["one", "two", "three", "four", "five"]);
            canvas.SetTestGlyphsEnabled(true);
            canvas.SetTestGlyphs([
                new EditorTestGlyph(0, TestGlyphKind.Run, "実行する"),
                new EditorTestGlyph(1, TestGlyphKind.Passed, "12ms"),
                new EditorTestGlyph(2, TestGlyphKind.Failed, "Assert.Equal() Failure"),
                new EditorTestGlyph(3, TestGlyphKind.Skipped, "スキップ"),
                new EditorTestGlyph(4, TestGlyphKind.Running),
            ]);

            var window = WpfTestHost.Load(canvas);
            try
            {
                var bmp = new RenderTargetBitmap(300, 200, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(canvas);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
