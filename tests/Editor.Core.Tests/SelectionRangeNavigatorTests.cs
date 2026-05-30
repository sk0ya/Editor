using Editor.Core.Buffer;
using Editor.Core.Lsp;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class SelectionRangeNavigatorTests
{
    [Fact]
    public void ToSelection_ConvertsSingleLineHalfOpenRangeToInclusiveSelection()
    {
        var range = new LspRange(new LspPosition(0, 4), new LspPosition(0, 7));

        var selection = SelectionRangeNavigator.ToSelection(range, lineCount: 1, _ => 12);

        Assert.Equal(new CursorPosition(0, 4), selection.Start);
        Assert.Equal(new CursorPosition(0, 6), selection.End);
        Assert.Equal(SelectionType.Character, selection.Type);
    }

    [Fact]
    public void ToSelection_EndAtColumnZeroSelectsPreviousLineEnd()
    {
        var range = new LspRange(new LspPosition(0, 0), new LspPosition(2, 0));

        var selection = SelectionRangeNavigator.ToSelection(range, lineCount: 3, line => line switch
        {
            0 => 5,
            1 => 8,
            _ => 3
        });

        Assert.Equal(new CursorPosition(0, 0), selection.Start);
        Assert.Equal(new CursorPosition(1, 7), selection.End);
    }

    [Fact]
    public void BuildSelections_FlattensParentsAndDropsDuplicateConvertedRanges()
    {
        var root = new LspSelectionRange(
            new LspRange(new LspPosition(0, 2), new LspPosition(0, 5)),
            new LspSelectionRange(
                new LspRange(new LspPosition(0, 2), new LspPosition(0, 5)),
                new LspSelectionRange(
                    new LspRange(new LspPosition(0, 0), new LspPosition(0, 10)))));

        var selections = SelectionRangeNavigator.BuildSelections(root, lineCount: 1, _ => 10);

        Assert.Equal(2, selections.Count);
        Assert.Equal(new CursorPosition(0, 2), selections[0].NormalizedStart);
        Assert.Equal(new CursorPosition(0, 4), selections[0].NormalizedEnd);
        Assert.Equal(new CursorPosition(0, 0), selections[1].NormalizedStart);
        Assert.Equal(new CursorPosition(0, 9), selections[1].NormalizedEnd);
    }

    [Fact]
    public void BuildSelections_DropsEmptyRanges()
    {
        var root = new LspSelectionRange(
            new LspRange(new LspPosition(0, 3), new LspPosition(0, 3)),
            new LspSelectionRange(
                new LspRange(new LspPosition(0, 0), new LspPosition(0, 6))));

        var selections = SelectionRangeNavigator.BuildSelections(root, lineCount: 1, _ => 6);

        var selection = Assert.Single(selections);
        Assert.Equal(new CursorPosition(0, 0), selection.NormalizedStart);
        Assert.Equal(new CursorPosition(0, 5), selection.NormalizedEnd);
    }

    [Fact]
    public void ToSelection_UsesUtf16Columns()
    {
        const string line = "a\ud83d\ude00b";
        var range = new LspRange(new LspPosition(0, 1), new LspPosition(0, 3));

        var selection = SelectionRangeNavigator.ToSelection(range, lineCount: 1, _ => line.Length);

        Assert.Equal(new CursorPosition(0, 1), selection.Start);
        Assert.Equal(new CursorPosition(0, 2), selection.End);
    }

    [Fact]
    public void ToSelection_UsesNormalizedLineLengthsForCrlfText()
    {
        var buffer = new TextBuffer("abc\r\ndef");
        var range = new LspRange(new LspPosition(0, 0), new LspPosition(1, 0));

        var selection = SelectionRangeNavigator.ToSelection(
            range,
            buffer.LineCount,
            buffer.GetLineLength);

        Assert.Equal(new CursorPosition(0, 0), selection.Start);
        Assert.Equal(new CursorPosition(0, 2), selection.End);
    }

    [Fact]
    public void GetExpansionIndex_AdvancesFromCurrentSelectionToParent()
    {
        var selections = new[]
        {
            new Selection(new CursorPosition(0, 4), new CursorPosition(0, 6), SelectionType.Character),
            new Selection(new CursorPosition(0, 0), new CursorPosition(0, 10), SelectionType.Character)
        };

        var next = SelectionRangeNavigator.GetExpansionIndex(selections, selections[0], currentIndex: 0);

        Assert.Equal(1, next);
    }

    [Fact]
    public void GetExpansionIndex_IgnoresStaleCurrentIndex()
    {
        var selections = new[]
        {
            new Selection(new CursorPosition(0, 4), new CursorPosition(0, 6), SelectionType.Character),
            new Selection(new CursorPosition(0, 0), new CursorPosition(0, 10), SelectionType.Character)
        };

        var next = SelectionRangeNavigator.GetExpansionIndex(
            selections,
            currentSelection: null,
            currentIndex: 1);

        Assert.Equal(0, next);
    }

    [Fact]
    public void GetShrinkIndex_ReturnsPreviousSelection()
    {
        var selections = new[]
        {
            new Selection(new CursorPosition(0, 4), new CursorPosition(0, 6), SelectionType.Character),
            new Selection(new CursorPosition(0, 0), new CursorPosition(0, 10), SelectionType.Character)
        };

        var next = SelectionRangeNavigator.GetShrinkIndex(selections, selections[1], currentIndex: 1);

        Assert.Equal(0, next);
    }

    [Fact]
    public void GetShrinkIndex_IgnoresStaleCurrentIndex()
    {
        var selections = new[]
        {
            new Selection(new CursorPosition(0, 4), new CursorPosition(0, 6), SelectionType.Character),
            new Selection(new CursorPosition(0, 0), new CursorPosition(0, 10), SelectionType.Character)
        };

        var next = SelectionRangeNavigator.GetShrinkIndex(
            selections,
            currentSelection: null,
            currentIndex: 1);

        Assert.Null(next);
    }
}
