using Editor.Core.Editing;

namespace Editor.Core.Tests;

/// <summary>
/// 差分の変更位置をスクロールバーへ写す計算（<see cref="DiffOverviewMarks"/>）と、行内差分を運ぶ
/// <see cref="DiffDecorations.InlineChanges"/>。印が「消える」「左右でずれる」「トラックからはみ出す」の再発防止。
/// </summary>
public class DiffOverviewMarksTests
{
    private const DiffDecorationKind A = DiffDecorationKind.Added;
    private const DiffDecorationKind R = DiffDecorationKind.Removed;

    [Fact]
    public void Build_merges_consecutive_rows_of_the_same_kind()
    {
        var marks = DiffOverviewMarks.Build([null, A, A, R, R, R, null, A]);

        Assert.Equal(
            [new DiffOverviewMark(1, 2, A), new DiffOverviewMark(3, 3, R), new DiffOverviewMark(7, 1, A)],
            marks);
    }

    [Fact]
    public void Build_without_changes_is_empty()
    {
        Assert.Empty(DiffOverviewMarks.Build([]));
        Assert.Empty(DiffOverviewMarks.Build([null, null, null]));
    }

    [Fact]
    public void TrackRect_maps_rows_proportionally()
    {
        // 100 行のうち 50〜59 行目 → トラック 200px の 100〜120px。
        var (y, height) = DiffOverviewMarks.TrackRect(new DiffOverviewMark(50, 10, A), 100, 200, 3);

        Assert.Equal(100, y, 6);
        Assert.Equal(20, height, 6);
    }

    [Fact]
    public void TrackRect_never_shrinks_below_the_minimum_height()
    {
        // 1万行の中の1行は 0.02px——そのままでは見えない。
        var (y, height) = DiffOverviewMarks.TrackRect(new DiffOverviewMark(5000, 1, R), 10_000, 200, 3);

        Assert.Equal(3, height, 6);
        Assert.InRange(y + height / 2, 99.9, 100.1);   // 中心は元の位置のまま
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9_999)]
    public void TrackRect_stays_inside_the_track_at_both_ends(int row)
    {
        var (y, height) = DiffOverviewMarks.TrackRect(new DiffOverviewMark(row, 1, A), 10_000, 200, 3);

        Assert.True(y >= 0);
        Assert.True(y + height <= 200 + 1e-9);
    }

    [Fact]
    public void TrackRect_of_a_degenerate_track_is_empty()
    {
        Assert.Equal((0d, 0d), DiffOverviewMarks.TrackRect(new DiffOverviewMark(0, 1, A), 0, 200, 3));
        Assert.Equal((0d, 0d), DiffOverviewMarks.TrackRect(new DiffOverviewMark(0, 1, A), 10, 0, 3));
    }

    [Fact]
    public void Decorations_without_inline_changes_report_none()
    {
        var decorations = new DiffDecorations(
            new Dictionary<int, DiffDecorationKind> { [0] = A }, new Dictionary<int, int>());

        Assert.Empty(decorations.InlineChanges);
        Assert.Empty(DiffDecorations.Empty.InlineChanges);
        Assert.True(DiffDecorations.Empty.IsEmpty);
    }

    [Fact]
    public void Decorations_carry_inline_changes()
    {
        var decorations = new DiffDecorations(
            new Dictionary<int, DiffDecorationKind> { [2] = R },
            new Dictionary<int, int>(),
            new Dictionary<int, IReadOnlyList<DiffInlineRange>> { [2] = [new DiffInlineRange(4, 3)] });

        var range = Assert.Single(decorations.InlineChanges[2]);
        Assert.Equal(7, range.End);
        Assert.False(decorations.IsEmpty);
    }
}
