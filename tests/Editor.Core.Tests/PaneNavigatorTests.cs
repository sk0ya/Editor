using Editor.Core.Models;
using Editor.Core.Panes;

namespace Editor.Core.Tests;

/// <summary>
/// Tests for PaneNavigator.FindNext.
///
/// Layout diagrams use (col, row) grid notation. Each pane is described as
/// PaneRect(left, top, right, bottom) in pixel coordinates.
///
/// Naming convention:  Layout_Direction_From_ExpectedTarget
/// </summary>
public class PaneNavigatorTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static int? Nav(PaneRect current, PaneRect[] all, WindowNavDir dir)
        => PaneNavigator.FindNext(current, all, dir);

    // index of 'current' inside 'all'
    private static int Idx(PaneRect[] all, PaneRect current) => Array.IndexOf(all, current);

    // Navigate from the pane at index 'from' and assert we arrive at index 'expected'.
    private static void Check(PaneRect[] panes, int from, WindowNavDir dir, int expected)
    {
        var result = PaneNavigator.FindNext(panes[from], panes, dir);
        Assert.True(result.HasValue,
            $"Expected to reach pane[{expected}] going {dir} from pane[{from}], but got null.");
        Assert.Equal(expected, result!.Value);
    }

    // Navigate from 'from' index and assert no target exists.
    private static void CheckNull(PaneRect[] panes, int from, WindowNavDir dir)
    {
        var result = PaneNavigator.FindNext(panes[from], panes, dir);
        Assert.Null(result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 1. TWO-PANE VERTICAL SPLIT  (side by side)
    //    [0: Left] | [1: Right]
    // ═════════════════════════════════════════════════════════════════════
    private static readonly PaneRect[] TwoPaneV =
    {
        new(  0, 0, 100, 200), // 0 Left
        new(100, 0, 200, 200), // 1 Right
    };

    [Fact] public void TwoPaneV_Right_From0_Reaches1() => Check(TwoPaneV, 0, WindowNavDir.Right, 1);
    [Fact] public void TwoPaneV_Left_From1_Reaches0()  => Check(TwoPaneV, 1, WindowNavDir.Left,  0);
    [Fact] public void TwoPaneV_Left_From0_IsNull()    => CheckNull(TwoPaneV, 0, WindowNavDir.Left);
    [Fact] public void TwoPaneV_Right_From1_IsNull()   => CheckNull(TwoPaneV, 1, WindowNavDir.Right);
    [Fact] public void TwoPaneV_Up_From0_IsNull()      => CheckNull(TwoPaneV, 0, WindowNavDir.Up);
    [Fact] public void TwoPaneV_Down_From1_IsNull()    => CheckNull(TwoPaneV, 1, WindowNavDir.Down);

    // ═════════════════════════════════════════════════════════════════════
    // 2. TWO-PANE HORIZONTAL SPLIT  (stacked)
    //    [0: Top]
    //    [1: Bottom]
    // ═════════════════════════════════════════════════════════════════════
    private static readonly PaneRect[] TwoPaneH =
    {
        new(0,   0, 200, 100), // 0 Top
        new(0, 100, 200, 200), // 1 Bottom
    };

    [Fact] public void TwoPaneH_Down_From0_Reaches1() => Check(TwoPaneH, 0, WindowNavDir.Down, 1);
    [Fact] public void TwoPaneH_Up_From1_Reaches0()   => Check(TwoPaneH, 1, WindowNavDir.Up,   0);
    [Fact] public void TwoPaneH_Up_From0_IsNull()     => CheckNull(TwoPaneH, 0, WindowNavDir.Up);
    [Fact] public void TwoPaneH_Down_From1_IsNull()   => CheckNull(TwoPaneH, 1, WindowNavDir.Down);

    // ═════════════════════════════════════════════════════════════════════
    // 3. THREE-PANE: one full column left, two rows right
    //    [0: Left] | [1: TopRight]
    //              | [2: BotRight]
    // ═════════════════════════════════════════════════════════════════════
    private static readonly PaneRect[] ThreePaneL =
    {
        new(  0,   0, 100, 200), // 0 Left
        new(100,   0, 200, 100), // 1 TopRight
        new(100, 100, 200, 200), // 2 BotRight
    };

    [Fact] public void ThreePaneL_Right_From0_ReachesTopRight()
        => Check(ThreePaneL, 0, WindowNavDir.Right, 1); // closest midpoint match

    [Fact] public void ThreePaneL_Left_From1_ReachesLeft() => Check(ThreePaneL, 1, WindowNavDir.Left, 0);
    [Fact] public void ThreePaneL_Left_From2_ReachesLeft() => Check(ThreePaneL, 2, WindowNavDir.Left, 0);
    [Fact] public void ThreePaneL_Down_From1_Reaches2()    => Check(ThreePaneL, 1, WindowNavDir.Down, 2);
    [Fact] public void ThreePaneL_Up_From2_Reaches1()      => Check(ThreePaneL, 2, WindowNavDir.Up,   1);

    // ═════════════════════════════════════════════════════════════════════
    // 4. 2×2 GRID
    //    [0: TL] | [1: TR]
    //    [2: BL] | [3: BR]
    // ═════════════════════════════════════════════════════════════════════
    private static readonly PaneRect[] Grid2x2 =
    {
        new(  0,   0, 100, 100), // 0 TopLeft
        new(100,   0, 200, 100), // 1 TopRight
        new(  0, 100, 100, 200), // 2 BotLeft
        new(100, 100, 200, 200), // 3 BotRight
    };

    [Fact] public void Grid2x2_Right_From0_Reaches1() => Check(Grid2x2, 0, WindowNavDir.Right, 1);
    [Fact] public void Grid2x2_Down_From0_Reaches2()  => Check(Grid2x2, 0, WindowNavDir.Down,  2);
    [Fact] public void Grid2x2_Left_From1_Reaches0()  => Check(Grid2x2, 1, WindowNavDir.Left,  0);
    [Fact] public void Grid2x2_Down_From1_Reaches3()  => Check(Grid2x2, 1, WindowNavDir.Down,  3);
    [Fact] public void Grid2x2_Up_From2_Reaches0()    => Check(Grid2x2, 2, WindowNavDir.Up,    0);
    [Fact] public void Grid2x2_Right_From2_Reaches3() => Check(Grid2x2, 2, WindowNavDir.Right, 3);
    [Fact] public void Grid2x2_Up_From3_Reaches1()    => Check(Grid2x2, 3, WindowNavDir.Up,    1);
    [Fact] public void Grid2x2_Left_From3_Reaches2()  => Check(Grid2x2, 3, WindowNavDir.Left,  2);
    // Corner nulls
    [Fact] public void Grid2x2_Left_From0_IsNull()  => CheckNull(Grid2x2, 0, WindowNavDir.Left);
    [Fact] public void Grid2x2_Up_From0_IsNull()    => CheckNull(Grid2x2, 0, WindowNavDir.Up);
    [Fact] public void Grid2x2_Right_From1_IsNull() => CheckNull(Grid2x2, 1, WindowNavDir.Right);
    [Fact] public void Grid2x2_Up_From1_IsNull()    => CheckNull(Grid2x2, 1, WindowNavDir.Up);
    [Fact] public void Grid2x2_Down_From2_IsNull()  => CheckNull(Grid2x2, 2, WindowNavDir.Down);
    [Fact] public void Grid2x2_Left_From2_IsNull()  => CheckNull(Grid2x2, 2, WindowNavDir.Left);
    [Fact] public void Grid2x2_Down_From3_IsNull()  => CheckNull(Grid2x2, 3, WindowNavDir.Down);
    [Fact] public void Grid2x2_Right_From3_IsNull() => CheckNull(Grid2x2, 3, WindowNavDir.Right);

    // ═════════════════════════════════════════════════════════════════════
    // 5. FOUR-PANE COLUMN STRIP  (4 vertical splits)
    //    [0] | [1] | [2] | [3]
    // ═════════════════════════════════════════════════════════════════════
    private static readonly PaneRect[] FourCol =
    {
        new(  0, 0, 100, 200), // 0
        new(100, 0, 200, 200), // 1
        new(200, 0, 300, 200), // 2
        new(300, 0, 400, 200), // 3
    };

    [Fact] public void FourCol_Right_0_1() => Check(FourCol, 0, WindowNavDir.Right, 1);
    [Fact] public void FourCol_Right_1_2() => Check(FourCol, 1, WindowNavDir.Right, 2);
    [Fact] public void FourCol_Right_2_3() => Check(FourCol, 2, WindowNavDir.Right, 3);
    [Fact] public void FourCol_Left_3_2()  => Check(FourCol, 3, WindowNavDir.Left,  2);
    [Fact] public void FourCol_Left_2_1()  => Check(FourCol, 2, WindowNavDir.Left,  1);
    [Fact] public void FourCol_Left_1_0()  => Check(FourCol, 1, WindowNavDir.Left,  0);
    [Fact] public void FourCol_Right_3_IsNull() => CheckNull(FourCol, 3, WindowNavDir.Right);
    [Fact] public void FourCol_Left_0_IsNull()  => CheckNull(FourCol, 0, WindowNavDir.Left);

    // ═════════════════════════════════════════════════════════════════════
    // 6. FOUR-PANE ROW STRIP  (4 horizontal splits)
    //    [0]
    //    [1]
    //    [2]
    //    [3]
    // ═════════════════════════════════════════════════════════════════════
    private static readonly PaneRect[] FourRow =
    {
        new(0,   0, 200, 100), // 0
        new(0, 100, 200, 200), // 1
        new(0, 200, 200, 300), // 2
        new(0, 300, 200, 400), // 3
    };

    [Fact] public void FourRow_Down_0_1() => Check(FourRow, 0, WindowNavDir.Down, 1);
    [Fact] public void FourRow_Down_1_2() => Check(FourRow, 1, WindowNavDir.Down, 2);
    [Fact] public void FourRow_Down_2_3() => Check(FourRow, 2, WindowNavDir.Down, 3);
    [Fact] public void FourRow_Up_3_2()   => Check(FourRow, 3, WindowNavDir.Up,   2);
    [Fact] public void FourRow_Up_2_1()   => Check(FourRow, 2, WindowNavDir.Up,   1);
    [Fact] public void FourRow_Up_1_0()   => Check(FourRow, 1, WindowNavDir.Up,   0);
    [Fact] public void FourRow_Down_3_IsNull() => CheckNull(FourRow, 3, WindowNavDir.Down);
    [Fact] public void FourRow_Up_0_IsNull()   => CheckNull(FourRow, 0, WindowNavDir.Up);

    // ═════════════════════════════════════════════════════════════════════
    // 7. 3×3 GRID
    //    [0: 0,0] | [1: 1,0] | [2: 2,0]
    //    [3: 0,1] | [4: 1,1] | [5: 2,1]
    //    [6: 0,2] | [7: 1,2] | [8: 2,2]
    // ═════════════════════════════════════════════════════════════════════
    private static PaneRect[] MakeGrid(int cols, int rows, double w = 100, double h = 100)
    {
        var p = new PaneRect[cols * rows];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
            p[r * cols + c] = new PaneRect(c * w, r * h, (c + 1) * w, (r + 1) * h);
        return p;
    }

    [Theory]
    [InlineData(0, WindowNavDir.Right, 1)]
    [InlineData(1, WindowNavDir.Right, 2)]
    [InlineData(2, WindowNavDir.Left,  1)]
    [InlineData(1, WindowNavDir.Left,  0)]
    [InlineData(0, WindowNavDir.Down,  3)]
    [InlineData(3, WindowNavDir.Down,  6)]
    [InlineData(6, WindowNavDir.Up,    3)]
    [InlineData(3, WindowNavDir.Up,    0)]
    [InlineData(4, WindowNavDir.Right, 5)]
    [InlineData(4, WindowNavDir.Left,  3)]
    [InlineData(4, WindowNavDir.Down,  7)]
    [InlineData(4, WindowNavDir.Up,    1)]
    [InlineData(8, WindowNavDir.Left,  7)]
    [InlineData(8, WindowNavDir.Up,    5)]
    public void Grid3x3_Navigation(int from, WindowNavDir dir, int expected)
        => Check(MakeGrid(3, 3), from, dir, expected);

    [Theory]
    [InlineData(2, WindowNavDir.Right)]
    [InlineData(0, WindowNavDir.Left)]
    [InlineData(0, WindowNavDir.Up)]
    [InlineData(6, WindowNavDir.Left)]
    [InlineData(8, WindowNavDir.Right)]
    [InlineData(8, WindowNavDir.Down)]
    [InlineData(6, WindowNavDir.Down)]
    public void Grid3x3_Boundary_IsNull(int from, WindowNavDir dir)
        => CheckNull(MakeGrid(3, 3), from, dir);

    // ═════════════════════════════════════════════════════════════════════
    // 8. 4×4 GRID  (16 panes)
    //    Indices: row*4 + col  →  row 0: 0..3, row 1: 4..7, etc.
    // ═════════════════════════════════════════════════════════════════════
    [Theory]
    // Horizontal navigation across every row
    [InlineData( 0, WindowNavDir.Right,  1)]
    [InlineData( 1, WindowNavDir.Right,  2)]
    [InlineData( 2, WindowNavDir.Right,  3)]
    [InlineData( 3, WindowNavDir.Left,   2)]
    [InlineData( 4, WindowNavDir.Right,  5)]
    [InlineData( 5, WindowNavDir.Right,  6)]
    [InlineData( 6, WindowNavDir.Right,  7)]
    [InlineData( 7, WindowNavDir.Left,   6)]
    [InlineData( 8, WindowNavDir.Right,  9)]
    [InlineData( 9, WindowNavDir.Right, 10)]
    [InlineData(10, WindowNavDir.Right, 11)]
    [InlineData(11, WindowNavDir.Left,  10)]
    [InlineData(12, WindowNavDir.Right, 13)]
    [InlineData(13, WindowNavDir.Right, 14)]
    [InlineData(14, WindowNavDir.Right, 15)]
    [InlineData(15, WindowNavDir.Left,  14)]
    // Vertical navigation down every column
    [InlineData( 0, WindowNavDir.Down,   4)]
    [InlineData( 4, WindowNavDir.Down,   8)]
    [InlineData( 8, WindowNavDir.Down,  12)]
    [InlineData( 1, WindowNavDir.Down,   5)]
    [InlineData( 5, WindowNavDir.Down,   9)]
    [InlineData( 9, WindowNavDir.Down,  13)]
    [InlineData( 2, WindowNavDir.Down,   6)]
    [InlineData( 6, WindowNavDir.Down,  10)]
    [InlineData(10, WindowNavDir.Down,  14)]
    [InlineData( 3, WindowNavDir.Down,   7)]
    [InlineData( 7, WindowNavDir.Down,  11)]
    [InlineData(11, WindowNavDir.Down,  15)]
    // Vertical navigation up every column
    [InlineData(12, WindowNavDir.Up,     8)]
    [InlineData( 8, WindowNavDir.Up,     4)]
    [InlineData( 4, WindowNavDir.Up,     0)]
    [InlineData(15, WindowNavDir.Up,    11)]
    [InlineData(11, WindowNavDir.Up,     7)]
    [InlineData( 7, WindowNavDir.Up,     3)]
    // Centre pane — directions not yet covered by the column/row passes above
    [InlineData( 5, WindowNavDir.Left,   4)]
    [InlineData( 5, WindowNavDir.Up,     1)]
    [InlineData(10, WindowNavDir.Left,   9)]
    [InlineData(10, WindowNavDir.Up,     6)]
    public void Grid4x4_Navigation(int from, WindowNavDir dir, int expected)
        => Check(MakeGrid(4, 4), from, dir, expected);

    [Theory]
    // Top-row: can't go up
    [InlineData(0, WindowNavDir.Up)]
    [InlineData(1, WindowNavDir.Up)]
    [InlineData(2, WindowNavDir.Up)]
    [InlineData(3, WindowNavDir.Up)]
    // Bottom-row: can't go down
    [InlineData(12, WindowNavDir.Down)]
    [InlineData(13, WindowNavDir.Down)]
    [InlineData(14, WindowNavDir.Down)]
    [InlineData(15, WindowNavDir.Down)]
    // Left-col: can't go left
    [InlineData( 0, WindowNavDir.Left)]
    [InlineData( 4, WindowNavDir.Left)]
    [InlineData( 8, WindowNavDir.Left)]
    [InlineData(12, WindowNavDir.Left)]
    // Right-col: can't go right
    [InlineData( 3, WindowNavDir.Right)]
    [InlineData( 7, WindowNavDir.Right)]
    [InlineData(11, WindowNavDir.Right)]
    [InlineData(15, WindowNavDir.Right)]
    public void Grid4x4_Boundary_IsNull(int from, WindowNavDir dir)
        => CheckNull(MakeGrid(4, 4), from, dir);

    // ═════════════════════════════════════════════════════════════════════
    // 9. ASYMMETRIC LAYOUT
    //    Wide left pane spanning full height beside two stacked right panes.
    //
    //    [0: Left (0,0,100,200)] | [1: TopRight (100,0,200,100)]
    //                            | [2: BotRight (100,100,200,200)]
    //
    //    Going Right from [0] should pick [1] or [2] based on midpoint:
    //    MidY of [0] = 100, MidY of [1] = 50, MidY of [2] = 150.
    //    Both overlap vertically. [2] is closer (|100-150|=50 < |100-50|=50),
    //    they are equidistant — navigator picks the first one found ([1]).
    //    (Vim itself picks based on cursor row, which we don't have here.)
    // ═════════════════════════════════════════════════════════════════════
    private static readonly PaneRect[] Asymmetric =
    {
        new(  0,   0, 100, 200), // 0 Left  (MidY=100)
        new(100,   0, 200, 100), // 1 TopRight (MidY=50)
        new(100, 100, 200, 200), // 2 BotRight (MidY=150)
    };

    [Fact]
    public void Asymmetric_Right_From0_PicksClosestMidpoint()
    {
        // [0].MidY = 100, [1].MidY = 50 → distance 50,  [2].MidY = 150 → distance 50.
        // Equal distance: navigator picks the one encountered first → index 1.
        var result = PaneNavigator.FindNext(Asymmetric[0], Asymmetric, WindowNavDir.Right);
        Assert.NotNull(result);
        // Either 1 or 2 is acceptable; what matters is it doesn't stay on 0.
        Assert.NotEqual(0, result!.Value);
    }

    [Fact]
    public void Asymmetric_Right_From0_BiasedTop_PicksTop()
    {
        // Shift [0] to top half: MidY becomes 50, should prefer [1] (MidY=50).
        var shifted = new PaneRect[]
        {
            new(  0,   0, 100, 100), // 0 Left (MidY=50)
            new(100,   0, 200, 100), // 1 TopRight (MidY=50)
            new(100, 100, 200, 200), // 2 BotRight (MidY=150)
        };
        Check(shifted, 0, WindowNavDir.Right, 1);
    }

    [Fact]
    public void Asymmetric_Right_From0_BiasedBot_PicksBot()
    {
        // Shift [0] to bottom half: MidY becomes 150, should prefer [2] (MidY=150).
        var shifted = new PaneRect[]
        {
            new(  0, 100, 100, 200), // 0 Left (MidY=150)
            new(100,   0, 200, 100), // 1 TopRight (MidY=50)
            new(100, 100, 200, 200), // 2 BotRight (MidY=150)
        };
        Check(shifted, 0, WindowNavDir.Right, 2);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 10. NEXT / PREV return null (handled by caller)
    // ═════════════════════════════════════════════════════════════════════
    [Fact]
    public void Next_ReturnsNull()
        => Assert.Null(Nav(Grid2x2[0], Grid2x2, WindowNavDir.Next));

    [Fact]
    public void Prev_ReturnsNull()
        => Assert.Null(Nav(Grid2x2[0], Grid2x2, WindowNavDir.Prev));

    // ═════════════════════════════════════════════════════════════════════
    // 11. SINGLE PANE — never returns itself
    // ═════════════════════════════════════════════════════════════════════
    [Fact]
    public void SinglePane_AllDirections_AreNull()
    {
        var panes = new[] { new PaneRect(0, 0, 200, 200) };
        foreach (var dir in new[] { WindowNavDir.Left, WindowNavDir.Right, WindowNavDir.Up, WindowNavDir.Down })
            Assert.Null(PaneNavigator.FindNext(panes[0], panes, dir));
    }
}
