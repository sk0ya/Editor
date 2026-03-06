namespace Editor.Core.Folds;

/// <summary>
/// Marker-based fold detection (foldmethod=marker).
/// Scans for {{{ / }}} markers (optionally followed by a level number like {{{1).
/// Nested markers are matched in LIFO order.
/// </summary>
public static class MarkerFoldDetector
{
    private const string OpenMarker  = "{{{";
    private const string CloseMarker = "}}}";

    public static IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines)
    {
        var result = new List<(int, int)>();
        var stack  = new Stack<int>(); // start-line indices

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int openIdx  = line.IndexOf(OpenMarker,  StringComparison.Ordinal);
            int closeIdx = line.IndexOf(CloseMarker, StringComparison.Ordinal);

            // A line can contain both (unusual, but handle open-before-close)
            if (openIdx >= 0 && (closeIdx < 0 || openIdx < closeIdx))
            {
                stack.Push(i);
                // Also close on the same line if }}} appears after {{{
                if (closeIdx >= 0 && closeIdx > openIdx)
                {
                    int start = stack.Pop();
                    if (i > start) result.Add((start, i));
                }
            }
            else if (closeIdx >= 0 && stack.Count > 0)
            {
                int start = stack.Pop();
                if (i > start) result.Add((start, i));
            }
        }

        // Unclosed markers: fold to end of file
        while (stack.Count > 0)
        {
            int start = stack.Pop();
            int end   = lines.Length - 1;
            if (end > start) result.Add((start, end));
        }

        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }
}
