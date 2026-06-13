using System.Text.RegularExpressions;

namespace Editor.Core.Text;

/// <summary>Detects clickable URLs (http/https/ftp) within a line of text.</summary>
public static class LinkDetector
{
    private static readonly Regex UrlRegex = new(
        @"(?:https?|ftp)://(?:\[[0-9a-fA-F:]+\]|[^\s<>""'\)\]\}])[^\s<>""'\)\]\}]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns all URL spans found in <paramref name="line"/> as (Start, End, Url), where End is exclusive.</summary>
    public static IReadOnlyList<(int Start, int End, string Url)> FindLinks(string line)
    {
        if (string.IsNullOrEmpty(line)) return [];

        var results = new List<(int Start, int End, string Url)>();
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

            results.Add((start, end, url));
        }
        return results;
    }

    /// <summary>Returns the URL span containing column <paramref name="col"/>, or null if none.</summary>
    public static (int Start, int End, string Url)? FindLinkAt(string line, int col)
    {
        foreach (var link in FindLinks(line))
        {
            if (col >= link.Start && col < link.End)
                return link;
        }
        return null;
    }

    private static bool IsTrailingPunctuation(char c) =>
        c is '.' or ',' or ';' or ':' or '!' or '?';
}
