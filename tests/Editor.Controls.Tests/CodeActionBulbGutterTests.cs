using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Editor.Controls.Rendering;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

/// <summary>
/// クイックフィックスの電球列（ガター）と、スクロールバーの診断マーカー。
/// 「既定 off で既存のレイアウトが 1px も変わらない」「他の列と混線しない」
/// 「電球の無い行では何も起きない」「印の上のクリックはスクロール操作に食われない」を固定する。
/// </summary>
public class CodeActionBulbGutterTests
{
    // ── GutterHitTester（純ロジック）─────────────────────────────

    private static GutterHitTester NewHitTester(int lineToReturn = 7) => new(_ => lineToReturn);

    // blame 30 | bp 0 | 電球 16 | test 16 | 行番号 40 | フォールド 16 （合計 118）
    private static GutterHitTester.Boundaries AllColumns() => new(30, 0, 16, 16, 40, 118);

    // 電球列が無効（幅 0）： blame 0 | bp 0 | 電球 0 | test 16 | 行番号 40 | フォールド 16 （合計 72）
    private static GutterHitTester.Boundaries BulbColumnDisabled() => new(0, 0, 0, 16, 40, 72);

    [Fact]
    public void BulbGutter_WhenDisabled_NeverHits()
    {
        var tester = NewHitTester();
        var b = BulbColumnDisabled();

        foreach (double x in new double[] { 0, 1, 15, 16, 20, 31, 32, 60, 71 })
            Assert.False(tester.TryHitCodeActionBulbGutter(new Point(x, 5), b, out _), $"x={x}");
    }

    [Fact]
    public void BulbGutter_WhenDisabled_LeavesOtherColumnBoundariesUnchanged()
    {
        var tester = NewHitTester();
        var b = BulbColumnDisabled();

        // テスト 0..16、行番号 16..56、フォールド 56..72 のまま。
        Assert.True(tester.TryHitTestGlyphGutter(new Point(8, 5), b, out _));
        Assert.True(tester.TryHitLineNumberGutter(new Point(16, 5), b, out _));
        Assert.False(tester.TryHitLineNumberGutter(new Point(56, 5), b, out _));
        Assert.True(tester.TryHitFoldGutter(new Point(56, 5), b, out _));
        // ブレークポイントの帯は行番号列そのもの。
        Assert.True(tester.TryHitBreakpointGutter(new Point(20, 5), b, out _));
        Assert.False(tester.TryHitBreakpointGutter(new Point(8, 5), b, out _));
    }

    [Fact]
    public void BulbGutter_WhenEnabled_HitsOnlyItsOwnRange()
    {
        var tester = NewHitTester();
        var b = AllColumns();

        // 電球列は blame(30) の右＝30 から 46 まで（ブレークポイント専用列は無い）。
        Assert.False(tester.TryHitCodeActionBulbGutter(new Point(29, 5), b, out _));
        Assert.True(tester.TryHitCodeActionBulbGutter(new Point(30, 5), b, out _));
        Assert.True(tester.TryHitCodeActionBulbGutter(new Point(45, 5), b, out _));
        Assert.False(tester.TryHitCodeActionBulbGutter(new Point(46, 5), b, out _));

        // 隣の列は 1px も食われない。
        Assert.True(tester.TryHitBlameGutter(new Point(29, 5), b, out _));
        Assert.False(tester.TryHitBlameGutter(new Point(30, 5), b, out _));
        Assert.False(tester.TryHitTestGlyphGutter(new Point(45, 5), b, out _));
        Assert.True(tester.TryHitTestGlyphGutter(new Point(46, 5), b, out _));
    }

    // ── EditorCanvas（列の幅・クリック）─────────────────────

    private static (int Bp, int Bulb, int Test, int Num, double Fold, int Gutter) Metrics(EditorCanvas canvas)
    {
        var method = typeof(EditorCanvas).GetMethod("GetGutterMetrics", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tuple = (ITuple)method.Invoke(canvas, null)!;
        return ((int)tuple[0]!, (int)tuple[1]!, (int)tuple[2]!, (int)tuple[3]!, (double)tuple[4]!, (int)tuple[5]!);
    }

    private static double LineHeight(EditorCanvas canvas) =>
        (double)typeof(EditorCanvas).GetField("_lineHeight", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;

    private static EditorCanvas NewCanvas(int lineCount = 8)
    {
        var canvas = new EditorCanvas();
        canvas.UpdateFont("Consolas", 14);
        canvas.SetLines(Enumerable.Range(0, lineCount).Select(i => $"line {i}").ToArray());
        return canvas;
    }

    private static Point Row(EditorCanvas canvas, int line, double x) => new(x, LineHeight(canvas) * (line + 0.5));

    private static Point BulbPoint(EditorCanvas canvas, int line)
    {
        var m = Metrics(canvas);
        return new Point(m.Bp + m.Bulb / 2.0, LineHeight(canvas) * (line + 0.5));
    }

    [Fact]
    public void BulbColumn_DefaultsToDisabled_AndAddsNoWidth()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            var before = Metrics(canvas);
            Assert.Equal(0, before.Bulb);

            canvas.SetCodeActionBulbEnabled(true);
            var after = Metrics(canvas);
            Assert.True(after.Bulb > 0);
            Assert.Equal(before.Gutter + after.Bulb, after.Gutter);

            canvas.SetCodeActionBulbEnabled(false);
            Assert.Equal(before, Metrics(canvas));
        });
    }

    [Fact]
    public void BulbColumn_TakesTheGlyphMargin_AndBreakpointsMoveOntoTheLineNumbers()
    {
        // ユーザーが見る並びの固定：blame の右が電球、ブレークポイントは行番号の上（専用列なし）。
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetBreakpointsEnabled(true);
            canvas.SetCodeActionBulbEnabled(true);
            canvas.SetCodeActionBulbLine(1);

            var m = Metrics(canvas);
            Assert.Equal(0, m.Bp);
            Assert.True(m.Bulb > 0);

            int bulbClicks = -1, toggled = -1;
            canvas.CodeActionBulbClicked += line => bulbClicks = line;
            canvas.BreakpointToggled += line => toggled = line;

            // 最左（0..Bulb）は電球。
            Assert.True(canvas.TryClickCodeActionBulbColumn(Row(canvas, 1, m.Bulb / 2.0)));
            Assert.Equal(1, bulbClicks);
            Assert.Equal(-1, toggled);

            // 行番号の上はブレークポイント。電球は反応しない。
            Assert.False(canvas.TryClickCodeActionBulbColumn(Row(canvas, 1, m.Bulb + m.Num / 2.0)));
            Assert.True(canvas.TryClickBreakpointColumn(Row(canvas, 1, m.Bulb + m.Num / 2.0)));
            Assert.Equal(1, toggled);
        });
    }

    [Fact]
    public void BulbColumn_KeepsItsWidthWhileTheBulbComesAndGoes()
    {
        // 電球はキャレット行にだけ出る。点いた瞬間に本文が右へずれてはいけない。
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetCodeActionBulbEnabled(true);
            var empty = Metrics(canvas);

            canvas.SetCodeActionBulbLine(3);
            Assert.Equal(empty, Metrics(canvas));

            canvas.SetCodeActionBulbLine(-1);
            Assert.Equal(empty, Metrics(canvas));
        });
    }

    [Fact]
    public void BulbColumn_WhenDisabled_SwallowsNoClick()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            int clicked = -1;
            canvas.CodeActionBulbClicked += line => clicked = line;

            Assert.False(canvas.TryClickCodeActionBulbColumn(new Point(4, LineHeight(canvas) * 1.5)));
            Assert.Equal(-1, clicked);
        });
    }

    [Fact]
    public void BulbClick_ReportsZeroBasedBufferLine()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetCodeActionBulbEnabled(true);
            canvas.SetCodeActionBulbLine(2);

            int clicked = -1;
            canvas.CodeActionBulbClicked += line => clicked = line;

            Assert.True(canvas.TryClickCodeActionBulbColumn(BulbPoint(canvas, 2)));
            Assert.Equal(2, clicked);
        });
    }

    [Fact]
    public void BulbClick_OnLineWithoutBulb_RaisesNothingButStaysInTheColumn()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetCodeActionBulbEnabled(true);
            canvas.SetCodeActionBulbLine(2);

            int clicked = -1;
            canvas.CodeActionBulbClicked += line => clicked = line;

            // 列の中なので本文のクリックへは抜けない（true）が、電球が無い行では何も起きない。
            Assert.True(canvas.TryClickCodeActionBulbColumn(BulbPoint(canvas, 5)));
            Assert.Equal(-1, clicked);
        });
    }

    [Fact]
    public void SetCodeActionBulbLine_IsIgnoredWhileTheColumnIsDisabled()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = NewCanvas();
            canvas.SetCodeActionBulbLine(3);
            Assert.Equal(-1, canvas.CodeActionBulbLine);

            // 無効化は点いていた電球も消す（列が戻ったときに古い行が残らない）。
            canvas.SetCodeActionBulbEnabled(true);
            canvas.SetCodeActionBulbLine(3);
            Assert.Equal(3, canvas.CodeActionBulbLine);
            canvas.SetCodeActionBulbEnabled(false);
            Assert.Equal(-1, canvas.CodeActionBulbLine);
        });
    }

    // ── スクロールバーの診断マーカー ─────────────────────

    private static EditorCanvas ArrangedCanvas(int lineCount, double width = 400, double height = 120)
    {
        var canvas = NewCanvas(lineCount);
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        return canvas;
    }

    private static LspDiagnostic Diag(int line, DiagnosticSeverity severity) =>
        new(new LspRange(new LspPosition(line, 4), new LspPosition(line, 9)), $"問題 {line}", severity);

    [Fact]
    public void DiagnosticMarkClick_JumpsToThatDiagnostic()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = ArrangedCanvas(lineCount: 300);
            canvas.SetDiagnostics([Diag(10, DiagnosticSeverity.Error), Diag(250, DiagnosticSeverity.Warning)]);

            DiagnosticOverviewMark? clicked = null;
            canvas.DiagnosticMarkClicked += mark => clicked = mark;

            double y = DiagnosticOverviewMarks.TrackY(250, 300, canvas.RenderSize.Height);
            Assert.True(canvas.TryClickDiagnosticMark(new Point(canvas.RenderSize.Width - 3, y)));
            Assert.Equal(250, clicked!.Value.Line);
            Assert.Equal(4, clicked!.Value.Column);   // 診断の開始桁へ飛ぶ
        });
    }

    [Fact]
    public void DiagnosticMarkClick_AwayFromAMark_LeavesTheScrollbarAlone()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = ArrangedCanvas(lineCount: 300);
            canvas.SetDiagnostics([Diag(10, DiagnosticSeverity.Error)]);

            int clicked = -1;
            canvas.DiagnosticMarkClicked += mark => clicked = mark.Line;

            double y = DiagnosticOverviewMarks.TrackY(150, 300, canvas.RenderSize.Height);
            Assert.False(canvas.TryClickDiagnosticMark(new Point(canvas.RenderSize.Width - 3, y)));
            Assert.Equal(-1, clicked);
        });
    }

    [Fact]
    public void DiagnosticMarkClick_InTheTextArea_IsNotAMarkClick()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = ArrangedCanvas(lineCount: 300);
            canvas.SetDiagnostics([Diag(10, DiagnosticSeverity.Error)]);

            double y = DiagnosticOverviewMarks.TrackY(10, 300, canvas.RenderSize.Height);
            Assert.False(canvas.TryClickDiagnosticMark(new Point(canvas.RenderSize.Width / 2, y)));
        });
    }

    [Fact]
    public void DiagnosticMarks_AreNotOfferedWhenTheDocumentFitsOnScreen()
    {
        // 垂直スクロールバーが無い＝文書全体が見えている。印も当たり判定も出さない。
        WpfTestHost.Run(() =>
        {
            var canvas = ArrangedCanvas(lineCount: 3, height: 400);
            canvas.SetDiagnostics([Diag(1, DiagnosticSeverity.Error)]);

            for (double y = 0; y < 400; y += 10)
                Assert.False(canvas.TryClickDiagnosticMark(new Point(canvas.RenderSize.Width - 3, y)));
        });
    }
}
