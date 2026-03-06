namespace Editor.Core.Folds;

/// <summary>
/// Language-agnostic indentation-based fold detection (foldmethod=indent).
/// Consecutive lines with greater indentation than the first line form a fold region.
/// </summary>
public static class IndentFoldDetector
{
    public static IReadOnlyList<(int StartLine, int EndLine)> Detect(string[] lines, int tabStop = 4)
    {
        var result = new List<(int, int)>();
        int n = lines.Length;

        // Build indent levels (tab counts as tabStop spaces)
        var levels = new int[n];
        for (int i = 0; i < n; i++)
            levels[i] = GetIndentLevel(lines[i], tabStop);

        for (int i = 0; i < n - 1; i++)
        {
            // Skip blank lines as fold starters
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            int baseLevel = levels[i];
            // Look for a body that has greater indentation
            int j = i + 1;
            while (j < n && string.IsNullOrWhiteSpace(lines[j])) j++;
            if (j >= n || levels[j] <= baseLevel) continue;

            // Extend the fold to the last line that has greater indent than baseLevel
            int end = j;
            for (int k = j + 1; k < n; k++)
            {
                if (string.IsNullOrWhiteSpace(lines[k])) continue;
                if (levels[k] <= baseLevel) break;
                end = k;
            }

            // Trim trailing blank lines
            while (end > i && string.IsNullOrWhiteSpace(lines[end])) end--;

            if (end > i)
                result.Add((i, end));
        }

        return result;
    }

    private static int GetIndentLevel(string line, int tabStop)
    {
        int level = 0;
        foreach (char c in line)
        {
            if (c == ' ') level++;
            else if (c == '\t') level = ((level / tabStop) + 1) * tabStop;
            else break;
        }
        return level;
    }
}
