using Editor.Core.Lsp;

namespace Editor.Core.Tests;

/// <summary>
/// スクロールバーの診断マーカー：行への畳み込み（1行1印・重い方が勝つ・重大度では捨てない）と、
/// トラック座標の対応付け・当たり判定。どちらも「見えない印」「押しても違う問題へ飛ぶ」の再発防止。
/// </summary>
public class DiagnosticOverviewMarksTests
{
    private static LspDiagnostic Diag(int line, DiagnosticSeverity severity, int character = 0, string? message = null)
        => new(new LspRange(new LspPosition(line, character), new LspPosition(line, character + 3)),
               message ?? $"line {line}", severity);

    [Fact]
    public void Build_OnePerLine_WorstSeverityWins()
    {
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(5, DiagnosticSeverity.Warning), Diag(5, DiagnosticSeverity.Error), Diag(5, DiagnosticSeverity.Information)],
            lineCount: 100);

        var mark = Assert.Single(marks);
        Assert.Equal(5, mark.Line);
        Assert.Equal(DiagnosticSeverity.Error, mark.Severity);
    }

    [Fact]
    public void Build_KeepsTheColumnOfTheDiagnosticItChose()
    {
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(5, DiagnosticSeverity.Warning, character: 2), Diag(5, DiagnosticSeverity.Error, character: 11)],
            lineCount: 100);

        Assert.Equal(11, Assert.Single(marks).Column);
    }

    [Fact]
    public void Build_KeepsWarningsAndHints()
    {
        // 未使用の using（Hint）は本文では薄字にするだけなので、画面外に行くと存在が判らない。
        // スクロールバーには出す——重大度で捨てると「警告と不要な using が見えない」になる。
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(1, DiagnosticSeverity.Hint), Diag(2, DiagnosticSeverity.Information),
             Diag(3, DiagnosticSeverity.Warning), Diag(4, DiagnosticSeverity.Error)], lineCount: 100);

        Assert.Equal([1, 2, 3, 4], marks.Select(static m => m.Line));
        Assert.Equal(DiagnosticSeverity.Hint, marks[0].Severity);
    }

    [Fact]
    public void Build_OnALineWithBothAHintAndAnError_TheErrorWins()
    {
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(7, DiagnosticSeverity.Hint), Diag(7, DiagnosticSeverity.Error)], lineCount: 100);

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(marks).Severity);
    }

    [Fact]
    public void Build_DropsLinesOutsideTheDocument()
    {
        // サーバーの応答が本文より古い／新しいときに来る。クランプして残すと存在しない行へ飛ぶ。
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(-1, DiagnosticSeverity.Error), Diag(10, DiagnosticSeverity.Error), Diag(3, DiagnosticSeverity.Error)],
            lineCount: 10);

        Assert.Equal([3], marks.Select(static m => m.Line));
    }

    [Fact]
    public void Build_ReturnsMarksInLineOrder()
    {
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(9, DiagnosticSeverity.Error), Diag(2, DiagnosticSeverity.Warning), Diag(5, DiagnosticSeverity.Error)],
            lineCount: 20);

        Assert.Equal([2, 5, 9], marks.Select(static m => m.Line));
    }

    [Fact]
    public void Build_EmptyInputs()
    {
        Assert.Empty(DiagnosticOverviewMarks.Build([], lineCount: 10));
        Assert.Empty(DiagnosticOverviewMarks.Build([Diag(0, DiagnosticSeverity.Error)], lineCount: 0));
    }

    [Fact]
    public void TrackY_SpansTheWholeTrack_AndIsIndependentOfScrollPosition()
    {
        // 文書を等分する射影。先頭行は上端側、末尾行は下端側に来る。
        Assert.Equal(1.0, DiagnosticOverviewMarks.TrackY(0, lineCount: 100, trackHeight: 200), 3);
        Assert.Equal(199.0, DiagnosticOverviewMarks.TrackY(99, lineCount: 100, trackHeight: 200), 3);
        Assert.Equal(101.0, DiagnosticOverviewMarks.TrackY(50, lineCount: 100, trackHeight: 200), 3);
    }

    [Fact]
    public void HitTest_PicksTheNearestMarkWithinTolerance()
    {
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(10, DiagnosticSeverity.Error), Diag(80, DiagnosticSeverity.Warning)], lineCount: 100);

        var near80 = DiagnosticOverviewMarks.TrackY(80, 100, 200);
        var hit = DiagnosticOverviewMarks.HitTest(marks, near80 + 2, 100, 200, tolerance: 5);
        Assert.Equal(80, hit!.Value.Line);

        // 離れた位置は当たらない（＝スクロールバーの通常のトラック操作を邪魔しない）。
        Assert.Null(DiagnosticOverviewMarks.HitTest(marks, near80 + 20, 100, 200, tolerance: 5));
    }

    [Fact]
    public void HitTest_AtEqualDistance_PrefersTheHeavierSeverity()
    {
        // 密集した行では 1px の差で別の問題へ飛ぶ。同距離ならエラーを選ぶ。
        var marks = DiagnosticOverviewMarks.Build(
            [Diag(40, DiagnosticSeverity.Warning), Diag(42, DiagnosticSeverity.Error)], lineCount: 100);

        double between = (DiagnosticOverviewMarks.TrackY(40, 100, 200) + DiagnosticOverviewMarks.TrackY(42, 100, 200)) / 2;
        var hit = DiagnosticOverviewMarks.HitTest(marks, between, 100, 200, tolerance: 10);
        Assert.Equal(42, hit!.Value.Line);
    }

    [Fact]
    public void HitTest_NoMarks_ReturnsNull()
        => Assert.Null(DiagnosticOverviewMarks.HitTest([], 10, 100, 200, tolerance: 5));
}
