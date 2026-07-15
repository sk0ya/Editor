namespace Editor.Core.Formatting;

/// <summary>
/// Text slicing for range formatting: pull a run of lines out of a document, hand it to a formatter,
/// and splice the result back. A CLI formatter is stdin→stdout over a whole document, so a range
/// format feeds it only the selected lines — dedented to column 0 first, because most formatters treat
/// their input as top-level and would otherwise flatten a nested block's indentation.
/// </summary>
/// <remarks>
/// Every method splits on '\n' only, leaving any '\r' at the end of each line, so a CRLF document
/// round-trips through extract/replace unchanged.
/// </remarks>
public static class LineRangeText
{
    /// <summary>Order and clamp a 0-based inclusive line range to the lines <paramref name="text"/> actually has.</summary>
    public static (int Start, int End) Clamp(string text, int start, int end)
    {
        int last = text.Split('\n').Length - 1;
        int s = Math.Clamp(Math.Min(start, end), 0, last);
        int e = Math.Clamp(Math.Max(start, end), s, last);
        return (s, e);
    }

    /// <summary>The text of lines <paramref name="start"/>..<paramref name="end"/> (0-based, inclusive).</summary>
    public static string Extract(string text, int start, int end) =>
        string.Join('\n', text.Split('\n')[start..(end + 1)]);

    /// <summary>Replace lines <paramref name="start"/>..<paramref name="end"/> with <paramref name="replacement"/>.</summary>
    public static string Replace(string text, int start, int end, string replacement)
    {
        var lines = text.Split('\n').ToList();
        lines.RemoveRange(start, end - start + 1);
        lines.InsertRange(start, replacement.Split('\n'));
        return string.Join('\n', lines);
    }

    /// <summary>The length of the last line in the range, ignoring its newline — the end column of a whole-line range.</summary>
    public static int LineLength(string text, int line) =>
        text.Split('\n')[line].TrimEnd('\r').Length;

    /// <summary>The longest leading whitespace shared by every non-blank line in the range.</summary>
    public static string CommonIndent(string text, int start, int end)
    {
        string? common = null;
        foreach (var raw in text.Split('\n')[start..(end + 1)])
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            if (common is null) { common = indent; continue; }
            int i = 0;
            while (i < common.Length && i < indent.Length && common[i] == indent[i]) i++;
            common = common[..i];
            if (common.Length == 0) break;
        }
        return common ?? "";
    }

    /// <summary>Strip <paramref name="indent"/> from the front of every line that has it.</summary>
    public static string Dedent(string text, string indent) =>
        indent.Length == 0
            ? text
            : string.Join('\n', text.Split('\n')
                .Select(l => l.StartsWith(indent, StringComparison.Ordinal) ? l[indent.Length..] : l));

    /// <summary>Prefix every non-blank line with <paramref name="indent"/>.</summary>
    public static string Indent(string text, string indent) =>
        indent.Length == 0
            ? text
            : string.Join('\n', text.Split('\n')
                .Select(l => l.TrimEnd('\r').Length == 0 ? l : indent + l));
}
