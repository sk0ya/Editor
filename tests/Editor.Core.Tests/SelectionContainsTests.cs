using Editor.Core.Models;

namespace Editor.Core.Tests;

/// <summary>選択の内側判定。用途は「選択の内側で右クリックされたか」——
/// 内側なら選択を壊さずメニューを出す。壊すと「選択して右クリック→メソッドの抽出」が成立しない。</summary>
public sealed class SelectionContainsTests
{
    private static Selection Character(int sl, int sc, int el, int ec) =>
        new(new CursorPosition(sl, sc), new CursorPosition(el, ec), SelectionType.Character);

    [Theory]
    [InlineData(1, 5, true)]    // 開始そのもの
    [InlineData(1, 4, false)]   // 開始の1つ手前
    [InlineData(2, 0, true)]    // 途中の行は桁を問わない
    [InlineData(3, 8, true)]    // 終端そのもの（含む）
    [InlineData(3, 9, false)]   // 終端の1つ先
    [InlineData(0, 99, false)]  // 手前の行
    [InlineData(4, 0, false)]   // 後ろの行
    public void Character_selection_spans_from_start_to_end_inclusive(int line, int column, bool expected)
        => Assert.Equal(expected, Character(1, 5, 3, 8).Contains(line, column));

    /// <summary>後ろから前へドラッグした選択でも同じ範囲を指す。</summary>
    [Fact]
    public void Reversed_selection_is_normalized()
        => Assert.True(Character(3, 8, 1, 5).Contains(2, 0));

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(2, 999, true)]   // 行選択は桁を問わない
    [InlineData(0, 0, false)]
    public void Line_selection_ignores_columns(int line, int column, bool expected)
        => Assert.Equal(expected,
            new Selection(new CursorPosition(1, 4), new CursorPosition(2, 2), SelectionType.Line)
                .Contains(line, column));

    [Theory]
    [InlineData(1, 4, true)]
    [InlineData(2, 8, true)]
    [InlineData(2, 3, false)]    // 矩形の左外
    [InlineData(2, 9, false)]    // 矩形の右外
    public void Block_selection_is_a_rectangle(int line, int column, bool expected)
        => Assert.Equal(expected,
            new Selection(new CursorPosition(1, 4), new CursorPosition(3, 8), SelectionType.Block)
                .Contains(line, column));
}
