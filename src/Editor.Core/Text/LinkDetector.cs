using System.Text.RegularExpressions;

namespace Editor.Core.Text;

/// <summary>The kind of clickable target detected in a line of text.</summary>
public enum LinkKind
{
    /// <summary>An http/https/ftp URL.</summary>
    Url,
    /// <summary>A candidate file path (relative or absolute). Existence is not verified here.</summary>
    FilePath,
}

/// <summary>A clickable span within a line: a URL or a file-path candidate.</summary>
/// <param name="Start">Start column (inclusive).</param>
/// <param name="End">End column (exclusive).</param>
/// <param name="Text">The matched text.</param>
/// <param name="Kind">Whether the span is a URL or a file-path candidate.</param>
public readonly record struct DetectedLink(int Start, int End, string Text, LinkKind Kind);

/// <summary>
/// Detects clickable spans within a line of text: URLs (http/https/ftp) and file-path
/// candidates (relative or absolute). Path detection is heuristic and performs no I/O —
/// callers are expected to verify that a <see cref="LinkKind.FilePath"/> span actually
/// exists on disk before treating it as clickable.
/// </summary>
public static class LinkDetector
{
    private static readonly Regex UrlRegex = new(
        @"(?:https?|ftp)://(?:\[[0-9a-fA-F:]+\]|[^\s<>""'\)\]\}])[^\s<>""'\)\]\}]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns all link spans found in <paramref name="line"/>, ordered by start column.
    /// Includes URLs and file-path candidates; the latter are unverified.
    /// </summary>
    public static IReadOnlyList<DetectedLink> FindLinks(string line)
    {
        if (string.IsNullOrEmpty(line)) return [];

        var results = new List<DetectedLink>();

        // URLs first so path scanning can skip spans already claimed by a URL.
        foreach (Match m in UrlRegex.Matches(line))
        {
            int start = m.Index;
            int end = m.Index + m.Length;

            // Trailing punctuation is usually sentence/markup, not part of the URL.
            while (end > start && IsTrailingPunctuation(line[end - 1]))
                end--;

            string url = line[start..end];

            // Trimming can reduce a match down to just "scheme://" (e.g. "https://..."
            // becomes "https://" after stripping the trailing dots) — not a usable link.
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0 || schemeEnd + 3 >= url.Length)
                continue;

            results.Add(new DetectedLink(start, end, url, LinkKind.Url));
        }

        // File-path candidates: tokens delimited by whitespace/markup that look path-like.
        int i = 0;
        while (i < line.Length)
        {
            if (!IsPathChar(line[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < line.Length && IsPathChar(line[i])) i++;
            int end = i; // exclusive

            while (end > start && IsTrailingPunctuation(line[end - 1]))
                end--;

            if (end > start && !OverlapsUrl(results, start, end))
            {
                string token = line[start..end];
                if (LooksLikePath(token))
                    results.Add(new DetectedLink(start, end, token, LinkKind.FilePath));
            }
        }

        results.Sort((a, b) => a.Start.CompareTo(b.Start));
        return results;
    }

    /// <summary>Returns the link span containing column <paramref name="col"/>, or null if none.</summary>
    public static DetectedLink? FindLinkAt(string line, int col)
    {
        foreach (var link in FindLinks(line))
        {
            if (col >= link.Start && col < link.End)
                return link;
        }
        return null;
    }

    /// <summary>
    /// Heuristic test for whether a bare token looks like a file path. Detects absolute paths
    /// (POSIX <c>/...</c>, home <c>~/...</c>, Windows drive <c>C:\...</c>/<c>C:/...</c>, UNC
    /// <c>\\host\...</c>), relative paths containing a separator (<c>./</c>, <c>../</c>,
    /// <c>src/Main.cs</c>), and bare filenames with an alphabetic extension (<c>README.md</c>).
    /// </summary>
    public static bool LooksLikePath(string token)
    {
        if (token.Length < 2) return false;

        // Absolute / rooted forms.
        if (token[0] is '/' or '~') return true;
        if (token.StartsWith(@"\\", StringComparison.Ordinal)) return true; // UNC
        if (token.Length >= 3 && char.IsLetter(token[0]) && token[1] == ':' &&
            (token[2] == '\\' || token[2] == '/'))
            return true; // C:\ or C:/

        // Relative path with an explicit separator.
        if (token.Contains('/') || token.Contains('\\')) return true;

        // Bare filename with an extension whose suffix contains at least one letter
        // (rules out version/number tokens like "1.5" or "3.14").
        int dot = token.LastIndexOf('.');
        if (dot > 0 && dot < token.Length - 1)
        {
            var ext = token.AsSpan(dot + 1);
            if (ext.Length <= 8)
            {
                bool hasLetter = false, allAlnum = true;
                foreach (var c in ext)
                {
                    if (char.IsLetter(c)) hasLetter = true;
                    else if (!char.IsDigit(c)) { allAlnum = false; break; }
                }
                if (allAlnum && hasLetter) return true;
            }
        }

        return false;
    }

    // Path chars: anything except whitespace, quotes, and common markup delimiters.
    // Mirrors the token boundary used by the gf/gx normal-mode commands.
    private static bool IsPathChar(char c) =>
        !char.IsWhiteSpace(c) && c is not ('"' or '\'' or '<' or '>' or '(' or ')' or
            '[' or ']' or '{' or '}' or ',' or ';');

    private static bool OverlapsUrl(List<DetectedLink> links, int start, int end)
    {
        foreach (var l in links)
        {
            if (l.Kind == LinkKind.Url && start < l.End && end > l.Start)
                return true;
        }
        return false;
    }

    private static bool IsTrailingPunctuation(char c) =>
        c is '.' or ',' or ';' or ':' or '!' or '?';
}
