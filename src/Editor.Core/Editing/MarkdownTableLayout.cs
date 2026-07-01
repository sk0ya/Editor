namespace Editor.Core.Editing;

/// <summary>
/// Pure detection of GFM-style markdown table blocks and per-row cell boundaries.
/// Used to visually realign table columns (e.g. when full-width characters throw off
/// space-padded alignment) without touching the underlying buffer text.
/// </summary>
public static class MarkdownTableLayout
{
    public readonly record struct CellSpan(int Start, int End);

    public readonly record struct TableBlock(int StartLine, int EndLine);

    /// <summary>True if the line contains at least one unescaped '|' (a candidate table row).</summary>
    public static bool LooksLikeRow(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '|') return true;
        }
        return false;
    }

    /// <summary>True if, split on unescaped '|', every cell is a GFM separator cell (e.g. "---", ":--:", "--:").</summary>
    public static bool IsSeparatorRow(string line)
    {
        var spans = SplitCellSpans(line);
        if (spans.Count == 0) return false;

        foreach (var span in spans)
        {
            int start = span.Start, end = span.End;
            while (start < end && line[start] == ' ') start++;
            while (end > start && line[end - 1] == ' ') end--;
            if (end <= start) return false;

            bool leftColon = line[start] == ':';
            bool rightColon = line[end - 1] == ':';
            int dashStart = leftColon ? start + 1 : start;
            int dashEnd = rightColon ? end - 1 : end;
            if (dashEnd <= dashStart) return false;
            for (int i = dashStart; i < dashEnd; i++)
                if (line[i] != '-') return false;
        }
        return true;
    }

    /// <summary>
    /// Splits a row into cell spans using unescaped '|' as the delimiter. A leading/trailing
    /// empty span caused by a leading/trailing pipe is dropped, matching GFM conventions
    /// (<c>| a | b |</c> and <c>a | b</c> produce the same two cells).
    /// </summary>
    public static List<CellSpan> SplitCellSpans(string line)
    {
        var pipeCols = new List<int>();
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '|') { i++; continue; }
            if (line[i] == '|') pipeCols.Add(i);
        }

        var bounds = new List<int>(pipeCols.Count + 2) { -1 };
        bounds.AddRange(pipeCols);
        bounds.Add(line.Length);

        var spans = new List<CellSpan>(bounds.Count - 1);
        for (int i = 0; i < bounds.Count - 1; i++)
            spans.Add(new CellSpan(bounds[i] + 1, bounds[i + 1]));

        if (spans.Count > 0 && IsBlank(line, spans[0])) spans.RemoveAt(0);
        if (spans.Count > 0 && IsBlank(line, spans[^1])) spans.RemoveAt(spans.Count - 1);
        return spans;
    }

    private static bool IsBlank(string line, CellSpan span)
    {
        for (int i = span.Start; i < span.End; i++)
            if (line[i] != ' ') return false;
        return true;
    }

    /// <summary>
    /// Finds contiguous GFM table blocks: a header row followed by a matching separator row,
    /// followed by zero or more further row-like lines (until a line with no '|', or EOF).
    /// </summary>
    public static IReadOnlyList<TableBlock> FindBlocks(string[] lines)
    {
        var blocks = new List<TableBlock>();
        int i = 0;
        while (i < lines.Length - 1)
        {
            if (LooksLikeRow(lines[i]) && IsSeparatorRow(lines[i + 1])
                && SplitCellSpans(lines[i]).Count == SplitCellSpans(lines[i + 1]).Count)
            {
                int end = i + 1;
                while (end + 1 < lines.Length && LooksLikeRow(lines[end + 1]))
                    end++;
                blocks.Add(new TableBlock(i, end));
                i = end + 1;
            }
            else
            {
                i++;
            }
        }
        return blocks;
    }
}
