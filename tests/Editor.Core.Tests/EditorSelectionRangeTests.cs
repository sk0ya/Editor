using Editor.Core.Lsp;
using Editor.Core.Models;

namespace Editor.Core.Tests;

/// <summary>選択 → LSP range の変換。ここを間違えると「メソッドの抽出が出ない」になる
/// （Roslyn は完全な文の並びを要求するため）。</summary>
public sealed class EditorSelectionRangeTests
{
    // 0行目=20文字、1行目=30文字、2行目=0文字（空行）
    private static int Length(int line) => line switch { 0 => 20, 1 => 30, _ => 0 };

    private static LspRange Convert(Selection selection) =>
        EditorSelectionRange.FromSelection(selection, Length)!;

    private static Selection Make(int sl, int sc, int el, int ec, SelectionType type) =>
        new(new CursorPosition(sl, sc), new CursorPosition(el, ec), type);

    [Fact]
    public void Empty_selection_has_no_range()
        => Assert.Null(EditorSelectionRange.FromSelection(
            Make(1, 5, 1, 5, SelectionType.Character), Length));

    /// <summary>選択の終端は「含む」セル、LSP は「含まない」終端。+1 しないと末尾1文字が落ちる。</summary>
    [Fact]
    public void Character_selection_end_becomes_exclusive()
    {
        var range = Convert(Make(0, 4, 1, 9, SelectionType.Character));

        Assert.Equal(new LspPosition(0, 4), range.Start);
        Assert.Equal(new LspPosition(1, 10), range.End);
    }

    [Fact]
    public void Character_selection_end_never_passes_the_end_of_the_line()
    {
        var range = Convert(Make(0, 0, 0, 19, SelectionType.Character));

        Assert.Equal(new LspPosition(0, 20), range.End);
    }

    /// <summary>行選択は「行まるごと」。<see cref="Selection.Start"/> に残っている
    /// キャレット桁をそのまま使うと、文の途中から始まる範囲になり抽出系が1件も出なくなる
    /// （実測: 30,27-31,0 で 0 件）。</summary>
    [Fact]
    public void Line_selection_starts_at_column_zero_and_ends_at_the_end_of_the_last_line()
    {
        var range = Convert(Make(0, 27, 1, 3, SelectionType.Line));

        Assert.Equal(new LspPosition(0, 0), range.Start);
        Assert.Equal(new LspPosition(1, 30), range.End);
    }

    /// <summary><c>V</c> を押しただけ（Start == End）でも「その1行まるごと」。
    /// ここを IsEmpty で弾くと、行選択が「選択なし」に化けてキャレット1点で問い合わせてしまう。</summary>
    [Fact]
    public void Line_selection_of_a_single_line_covers_that_whole_line()
    {
        var range = Convert(Make(0, 9, 0, 9, SelectionType.Line));

        Assert.Equal(new LspPosition(0, 0), range.Start);
        Assert.Equal(new LspPosition(0, 20), range.End);
    }

    /// <summary>後ろから前へ選択しても同じ範囲になる。</summary>
    [Fact]
    public void Reversed_selection_is_normalized()
        => Assert.Equal(
            Convert(Make(0, 4, 1, 9, SelectionType.Character)),
            Convert(Make(1, 9, 0, 4, SelectionType.Character)));

    /// <summary>矩形選択は外接矩形として渡す（LSP に矩形を表す手段が無い）。</summary>
    [Fact]
    public void Block_selection_becomes_its_bounding_box()
    {
        var range = Convert(Make(0, 12, 1, 4, SelectionType.Block));

        Assert.Equal(new LspPosition(0, 4), range.Start);
        Assert.Equal(new LspPosition(1, 13), range.End);
    }
}
