namespace Editor.Core.Editing;

/// <summary>Kind of whitespace worth flagging — both are easy to miss visually and a common
/// source of bugs in Japanese text (full-width space) or diffs (trailing whitespace).</summary>
public enum WhitespaceIssueKind
{
    FullWidthSpace,
    TrailingWhitespace
}

public readonly record struct WhitespaceIssue(int Start, int End, WhitespaceIssueKind Kind);

/// <summary>
/// Scans buffer lines for full-width (ideographic, U+3000) spaces and trailing whitespace.
/// Pure .NET, no WPF — the result is handed to the renderer for highlighting.
/// </summary>
public static class WhitespaceIssueDetector
{
    private const char IdeographicSpace = '　';

    public static Dictionary<int, List<WhitespaceIssue>> Detect(IReadOnlyList<string> lines)
        => Detect(lines, 0, lines.Count - 1);

    /// <summary>
    /// <paramref name="firstLine"/>〜<paramref name="lastLine"/>（両端を含む）だけを走査する。
    /// 結果のキーは絶対行番号のまま。
    ///
    /// <para>この目印は可視行の描画時にしか引かれないので、打鍵のたびに全行を走らせる理由がない
    /// （7000 行の .cs で実測 1.26ms/打鍵）。スクロールとサイズ変更で再計算する呼び出し側と
    /// 組にして使う。</para>
    /// </summary>
    public static Dictionary<int, List<WhitespaceIssue>> Detect(
        IReadOnlyList<string> lines, int firstLine, int lastLine)
    {
        var result = new Dictionary<int, List<WhitespaceIssue>>();
        firstLine = Math.Max(0, firstLine);
        lastLine = Math.Min(lastLine, lines.Count - 1);
        for (int i = firstLine; i <= lastLine; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;

            List<WhitespaceIssue>? issues = null;

            int trailStart = line.Length;
            while (trailStart > 0 && IsWhitespace(line[trailStart - 1])) trailStart--;
            if (trailStart < line.Length)
            {
                issues = [new WhitespaceIssue(trailStart, line.Length, WhitespaceIssueKind.TrailingWhitespace)];
            }

            // Full-width spaces before the trailing run get their own marker (the trailing
            // run, if any, is already flagged above).
            for (int c = 0; c < trailStart; c++)
            {
                if (line[c] != IdeographicSpace) continue;
                issues ??= [];
                issues.Add(new WhitespaceIssue(c, c + 1, WhitespaceIssueKind.FullWidthSpace));
            }

            if (issues != null) result[i] = issues;
        }
        return result;
    }

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or IdeographicSpace;
}
