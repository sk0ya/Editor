namespace Editor.Core.Editing;

/// <summary>
/// Per-line change state of the current buffer relative to the last-saved baseline.
/// <see cref="Deleted"/> marks the current line that immediately follows one or more
/// removed lines (there is no line in the buffer for the removed content itself).
/// </summary>
public enum SaveLineState
{
    None,
    Added,
    Modified,
    Deleted
}

/// <summary>
/// Computes a git-style, per-line diff between a saved baseline and the current buffer
/// contents for the "changed since last save" gutter. Pure .NET (no WPF, no git).
///
/// The algorithm trims the common prefix/suffix (so unchanged regions cost O(n)), then
/// runs an LCS diff only over the changed middle. Very large changed regions fall back
/// to a coarse line-by-line marking to bound the O(n*m) cost.
/// </summary>
public static class SaveDiff
{
    // Upper bound on the changed-middle size for which we run the full LCS diff.
    private const int LcsCap = 2000;

    public static Dictionary<int, SaveLineState> Compute(
        IReadOnlyList<string> baseline, IReadOnlyList<string> current)
    {
        var result = new Dictionary<int, SaveLineState>();
        if (baseline.Count == 0) return result; // no baseline captured yet

        int n = baseline.Count, m = current.Count;

        // Common prefix.
        int p = 0;
        while (p < n && p < m && baseline[p] == current[p]) p++;
        // Common suffix (not overlapping the prefix).
        int s = 0;
        while (s < n - p && s < m - p && baseline[n - 1 - s] == current[m - 1 - s]) s++;

        int aLen = n - p - s; // baseline lines in the changed middle
        int bLen = m - p - s; // current lines in the changed middle
        if (aLen == 0 && bLen == 0) return result; // identical

        if (aLen == 0)
        {
            // Pure insertion of bLen lines starting at current line p.
            for (int i = 0; i < bLen; i++) result[p + i] = SaveLineState.Added;
            return result;
        }
        if (bLen == 0)
        {
            // Pure deletion of aLen lines; flag the current line at the boundary.
            MarkDeleted(result, p, m);
            return result;
        }

        if (aLen > LcsCap || bLen > LcsCap)
        {
            // Coarse fallback for huge changed regions: overlap = Modified, extra
            // current lines = Added, and a deletion marker if the baseline was longer.
            int common = Math.Min(aLen, bLen);
            for (int i = 0; i < common; i++) result[p + i] = SaveLineState.Modified;
            for (int i = common; i < bLen; i++) result[p + i] = SaveLineState.Added;
            if (aLen > bLen) MarkDeleted(result, p + bLen, m);
            return result;
        }

        ApplyLcsDiff(baseline, current, p, aLen, bLen, m, result);
        return result;
    }

    private static void ApplyLcsDiff(
        IReadOnlyList<string> baseline, IReadOnlyList<string> current,
        int p, int aLen, int bLen, int m, Dictionary<int, SaveLineState> result)
    {
        // LCS DP table over the changed middle slices.
        // a = baseline[p .. p+aLen), b = current[p .. p+bLen)
        var dp = new int[aLen + 1, bLen + 1];
        for (int i = aLen - 1; i >= 0; i--)
            for (int j = bLen - 1; j >= 0; j--)
                dp[i, j] = baseline[p + i] == current[p + j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        // Walk the alignment from the start, running the same state machine the git
        // gutter uses: '-' accumulates deletes, '+' becomes Modified when it absorbs a
        // pending delete (else Added), and an equal line flushes pending deletes as a
        // Deleted marker at the current line.
        int ai = 0, bj = 0;
        int curLine = p;
        int pendingDeletes = 0;
        while (ai < aLen && bj < bLen)
        {
            if (baseline[p + ai] == current[p + bj])
            {
                Flush(result, curLine, ref pendingDeletes);
                ai++; bj++; curLine++;
            }
            else if (dp[ai + 1, bj] >= dp[ai, bj + 1])
            {
                pendingDeletes++; // baseline-only line (deletion)
                ai++;
            }
            else
            {
                result[curLine] = pendingDeletes > 0 ? SaveLineState.Modified : SaveLineState.Added;
                if (pendingDeletes > 0) pendingDeletes--;
                bj++; curLine++;
            }
        }
        while (ai < aLen) { pendingDeletes++; ai++; }
        while (bj < bLen)
        {
            result[curLine] = pendingDeletes > 0 ? SaveLineState.Modified : SaveLineState.Added;
            if (pendingDeletes > 0) pendingDeletes--;
            bj++; curLine++;
        }
        Flush(result, curLine, ref pendingDeletes);

        void Flush(Dictionary<int, SaveLineState> r, int line, ref int pending)
        {
            if (pending <= 0) return;
            MarkDeleted(r, line, m);
            pending = 0;
        }
    }

    /// <summary>Marks a deletion boundary at <paramref name="line"/> without clobbering an
    /// existing add/modify state there. <paramref name="lineCount"/> clamps the marker to a
    /// valid current line (deletions at EOF attach to the last line).</summary>
    private static void MarkDeleted(Dictionary<int, SaveLineState> result, int line, int lineCount)
    {
        int target = line;
        if (lineCount != int.MaxValue && target >= lineCount)
            target = Math.Max(0, lineCount - 1);
        result.TryAdd(target, SaveLineState.Deleted);
    }
}
