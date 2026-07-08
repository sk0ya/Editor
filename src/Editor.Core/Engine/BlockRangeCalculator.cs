using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Engine;

public readonly record struct BlockLineRange(int Line, int StartColumn, int EndColumn);

/// <summary>
/// Pure coordinate arithmetic for Visual Block ($<see cref="VimMode.VisualBlock"/>)
/// selections: bounds, per-line column ranges, and per-line edit-column maps used by
/// block insert/append/change/replace. Stateless — mirrors <see cref="MotionEngine"/>.
/// </summary>
public static class BlockRangeCalculator
{
    public static (int StartLine, int EndLine, int LeftColumn, int RightColumn) GetBounds(Selection selection)
    {
        var startLine = Math.Min(selection.Start.Line, selection.End.Line);
        var endLine = Math.Max(selection.Start.Line, selection.End.Line);
        var leftColumn = Math.Min(selection.Start.Column, selection.End.Column);
        var rightColumn = Math.Max(selection.Start.Column, selection.End.Column);
        return (startLine, endLine, leftColumn, rightColumn);
    }

    public static int GetLeftColumn(Selection selection, bool blockToLineEnd, int lineEndStartColumn)
    {
        return blockToLineEnd
            ? lineEndStartColumn
            : Math.Min(selection.Start.Column, selection.End.Column);
    }

    public static IEnumerable<BlockLineRange> GetLineRanges(
        Selection selection, TextBuffer buf, bool blockToLineEnd, int lineEndStartColumn)
    {
        var (startLine, endLine, leftColumn, rightColumn) = GetBounds(selection);
        if (blockToLineEnd)
            leftColumn = lineEndStartColumn;

        for (int line = startLine; line <= endLine; line++)
        {
            var lineEnd = buf.GetLineLength(line) - 1;
            var endColumn = blockToLineEnd ? lineEnd : rightColumn;
            yield return new BlockLineRange(line, leftColumn, endColumn);
        }
    }

    public static Dictionary<int, int> BuildEditColumns(TextBuffer buf, int startLine, int endLine, int column)
    {
        var columns = new Dictionary<int, int>();
        for (int line = startLine; line <= endLine; line++)
            columns[line] = Math.Min(column, buf.GetLineLength(line));
        return columns;
    }

    public static Dictionary<int, int> BuildAppendToLineEndColumns(TextBuffer buf, int startLine, int endLine)
    {
        var columns = new Dictionary<int, int>();
        for (int line = startLine; line <= endLine; line++)
            columns[line] = buf.GetLineLength(line);
        return columns;
    }
}
