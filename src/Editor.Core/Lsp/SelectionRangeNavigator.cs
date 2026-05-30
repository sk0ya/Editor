using Editor.Core.Models;

namespace Editor.Core.Lsp;

public static class SelectionRangeNavigator
{
    public static IReadOnlyList<LspRange> Flatten(LspSelectionRange? selectionRange)
    {
        var ranges = new List<LspRange>();
        for (var current = selectionRange; current is not null; current = current.Parent)
            ranges.Add(current.Range);
        return ranges;
    }

    public static IReadOnlyList<Selection> BuildSelections(
        LspSelectionRange? selectionRange,
        int lineCount,
        Func<int, int> getLineLength)
    {
        var selections = new List<Selection>();
        Selection? previous = null;

        foreach (var range in Flatten(selectionRange))
        {
            if (IsEmpty(range))
                continue;

            var selection = ToSelection(range, lineCount, getLineLength);
            if (previous is null || !SameRange(previous.Value, selection))
            {
                selections.Add(selection);
                previous = selection;
            }
        }

        return selections;
    }

    public static Selection ToSelection(
        LspRange range,
        int lineCount,
        Func<int, int> getLineLength)
    {
        if (lineCount <= 0)
            return new Selection(CursorPosition.Zero, CursorPosition.Zero, SelectionType.Character);

        var start = ClampPosition(range.Start.Line, range.Start.Character, lineCount, getLineLength);
        var end = ToInclusiveEnd(range, start, lineCount, getLineLength);

        if (Compare(end, start) < 0)
            end = start;

        return new Selection(start, end, SelectionType.Character);
    }

    public static int? GetExpansionIndex(
        IReadOnlyList<Selection> selections,
        Selection? currentSelection,
        int currentIndex)
    {
        if (selections.Count == 0)
            return null;

        if (IsCurrentIndexValid(selections, currentSelection, currentIndex))
            return currentIndex + 1 < selections.Count ? currentIndex + 1 : null;

        if (currentSelection is null)
            return 0;

        var current = currentSelection.Value;

        for (int i = 0; i < selections.Count; i++)
        {
            if (SameRange(selections[i], current))
                return i + 1 < selections.Count ? i + 1 : null;

            if (Contains(selections[i], current))
                return i;
        }

        return 0;
    }

    public static int? GetShrinkIndex(
        IReadOnlyList<Selection> selections,
        Selection? currentSelection,
        int currentIndex)
    {
        if (selections.Count == 0)
            return null;

        if (IsCurrentIndexValid(selections, currentSelection, currentIndex))
            return currentIndex > 0 ? currentIndex - 1 : null;

        if (currentSelection is null)
            return null;

        var current = currentSelection.Value;

        for (int i = 0; i < selections.Count; i++)
        {
            if (SameRange(selections[i], current))
                return i > 0 ? i - 1 : null;
        }

        return null;
    }

    private static CursorPosition ToInclusiveEnd(
        LspRange range,
        CursorPosition start,
        int lineCount,
        Func<int, int> getLineLength)
    {
        if (range.End.Line == range.Start.Line && range.End.Character <= range.Start.Character)
            return start;

        if (range.End.Character > 0)
            return ClampPosition(range.End.Line, range.End.Character - 1, lineCount, getLineLength);

        if (range.End.Line > range.Start.Line)
        {
            int line = Math.Clamp(range.End.Line - 1, 0, lineCount - 1);
            int len = Math.Max(0, getLineLength(line));
            return new CursorPosition(line, Math.Max(0, len - 1));
        }

        return start;
    }

    private static CursorPosition ClampPosition(
        int line,
        int character,
        int lineCount,
        Func<int, int> getLineLength)
    {
        int clampedLine = Math.Clamp(line, 0, lineCount - 1);
        int len = Math.Max(0, getLineLength(clampedLine));
        int maxCol = Math.Max(0, len - 1);
        return new CursorPosition(clampedLine, Math.Clamp(character, 0, maxCol));
    }

    private static bool Contains(Selection outer, Selection inner) =>
        Compare(outer.NormalizedStart, inner.NormalizedStart) <= 0 &&
        Compare(outer.NormalizedEnd, inner.NormalizedEnd) >= 0 &&
        !SameRange(outer, inner);

    private static bool IsCurrentIndexValid(
        IReadOnlyList<Selection> selections,
        Selection? currentSelection,
        int currentIndex) =>
        currentSelection is Selection current &&
        currentIndex >= 0 &&
        currentIndex < selections.Count &&
        SameRange(selections[currentIndex], current);

    private static bool IsEmpty(LspRange range) =>
        range.Start.Line == range.End.Line &&
        range.Start.Character == range.End.Character;

    private static bool SameRange(Selection left, Selection right) =>
        left.Type == right.Type &&
        left.NormalizedStart == right.NormalizedStart &&
        left.NormalizedEnd == right.NormalizedEnd;

    private static int Compare(CursorPosition left, CursorPosition right)
    {
        int line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }
}
