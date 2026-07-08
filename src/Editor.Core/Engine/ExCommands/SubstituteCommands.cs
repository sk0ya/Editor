using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Models;

namespace Editor.Core.Engine.ExCommands;

/// <summary>
/// Handles :s (substitute), :g/:v (global), :sort, and :retab.
/// Search-history read/write is delegated back to the owning ExCommandProcessor since it's
/// shared with other commands (e.g. the incremental substitute preview).
/// </summary>
public class SubstituteCommands(
    BufferManager bufferManager,
    VimOptions options,
    RangeResolver rangeResolver,
    Func<string> getLastSearchPattern,
    Action<string> addSearchHistory)
{
    private static readonly System.Text.RegularExpressions.Regex NumericSortRegex =
        new(@"-?\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public bool TryHandle(string cmd, string range, CursorPosition cursor, out ExResult result)
    {
        // :s/pattern/replace/flags (substitute)
        if (cmd.StartsWith("s/") || cmd.StartsWith("s!"))
        {
            result = ExecuteSubstitute(cmd, range, cursor);
            return true;
        }

        // :g/pattern/cmd  :g!/pattern/cmd  :v/pattern/cmd  :global  :vglobal
        if (IsGlobalCommand(cmd, out bool globalInverse, out string globalRest))
        {
            result = ExecuteGlobal(globalRest, globalInverse, range, cursor);
            return true;
        }

        // :[range]sort[!] [i] [n] [r] [/pat/]
        if (cmd == "sort" || (cmd.StartsWith("sort") && cmd.Length > 4 && cmd[4] is ' ' or '!'))
        {
            result = ExecuteSort(cmd, range, cursor);
            return true;
        }

        // :[range]retab[!] [N]
        if (cmd == "retab" || cmd.StartsWith("retab ") || cmd.StartsWith("retab!"))
        {
            result = ExecuteRetab(cmd, range);
            return true;
        }

        result = default!;
        return false;
    }

    private static bool IsGlobalCommand(string cmd, out bool inverse, out string rest)
    {
        inverse = false;
        rest = "";
        if (cmd.StartsWith("global!", StringComparison.Ordinal))      { inverse = true;  rest = cmd[7..]; return true; }
        if (cmd.StartsWith("global", StringComparison.Ordinal))        { inverse = false; rest = cmd[6..]; return true; }
        if (cmd.StartsWith("vglobal", StringComparison.Ordinal))       { inverse = true;  rest = cmd[7..]; return true; }
        if (cmd.Length >= 2 && cmd[0] == 'g' && cmd[1] == '!')        { inverse = true;  rest = cmd[2..]; return true; }
        if (cmd.Length >= 2 && cmd[0] == 'g' && !char.IsLetterOrDigit(cmd[1])) { inverse = false; rest = cmd[1..]; return true; }
        if (cmd.Length >= 2 && cmd[0] == 'v' && !char.IsLetterOrDigit(cmd[1])) { inverse = true;  rest = cmd[1..]; return true; }
        return false;
    }

    private ExResult ExecuteGlobal(string rest, bool inverse, string range, CursorPosition cursor)
    {
        if (rest.Length == 0)
            return new ExResult(false, "E148: Regular expression missing from global");

        char delim = rest[0];
        var secondDelim = rest.IndexOf(delim, 1);
        if (secondDelim < 1)
            return new ExResult(false, "E148: Regular expression missing from global");

        string pattern = rest[1..secondDelim];
        string subCmd = secondDelim < rest.Length - 1 ? rest[(secondDelim + 1)..].Trim() : "";
        if (string.IsNullOrEmpty(subCmd)) subCmd = "p";

        var regex = RangeResolver.TryBuildRegex(pattern, options.IgnoreCase, out var patternError);
        if (regex == null) return new ExResult(false, patternError);

        var buf = bufferManager.Current.Text;
        int startLine = 0, endLine = buf.LineCount - 1;
        // :g defaults to whole file; only restrict when an explicit range was given
        if (!string.IsNullOrEmpty(range))
            rangeResolver.ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);

        // Collect matching lines (indices snapshot before any mutation)
        var matchingLines = new List<int>();
        for (int l = startLine; l <= endLine && l < buf.LineCount; l++)
        {
            bool matches = regex.IsMatch(buf.GetLine(l));
            if (matches != inverse)
                matchingLines.Add(l);
        }

        if (matchingLines.Count == 0)
            return new ExResult(false, "Pattern not found");

        // ── delete ──────────────────────────────────────────────────────────
        if (subCmd is "d" or "delete" or "d!" or "delete!")
        {
            // Batch contiguous groups into single DeleteLines calls (from bottom up)
            for (int i = matchingLines.Count - 1; i >= 0; )
            {
                int hi = i;
                while (i > 0 && matchingLines[i - 1] == matchingLines[i] - 1) i--;
                buf.DeleteLines(matchingLines[i], matchingLines[hi]);
                i--;
            }
            return new ExResult(true, $"{matchingLines.Count} line(s) deleted", TextModified: true);
        }

        // ── print ────────────────────────────────────────────────────────────
        if (subCmd is "p" or "print")
        {
            var lines = matchingLines.Select(l => buf.GetLine(l));
            return new ExResult(true, string.Join("  |  ", lines));
        }

        // ── substitute ───────────────────────────────────────────────────────
        if (subCmd.StartsWith("s/", StringComparison.Ordinal) ||
            subCmd.Length >= 2 && subCmd[0] == 's' && !char.IsLetterOrDigit(subCmd[1]))
        {
            int totalSubs = 0;
            // Process top-to-bottom; substitute doesn't change line count
            foreach (int l in matchingLines)
            {
                var lineRange = $"{l + 1},{l + 1}";
                var res = ExecuteSubstitute(subCmd, lineRange, new CursorPosition(l, 0));
                if (res.Success && res.Message?.Contains("substitution") == true)
                    totalSubs++;
            }
            return new ExResult(true, totalSubs > 0 ? $"{totalSubs} substitution(s) made" : "No matches", TextModified: totalSubs > 0);
        }

        return new ExResult(false, $"Not supported in :global: {subCmd}");
    }

    private ExResult ExecuteSubstitute(string cmd, string range, CursorPosition cursor)
    {
        char sep = cmd[1];
        var parts = cmd[2..].Split(sep);
        if (parts.Length < 2) return new ExResult(false, "Invalid substitution");

        string pattern = parts[0];
        string replacement = parts.Length > 1 ? parts[1] : "";
        string flags = parts.Length > 2 ? parts[2] : "";

        // An empty pattern (:s//replacement/) reuses the last search pattern —
        // either from a previous /pattern<CR> search or a previous non-empty :s pattern.
        if (string.IsNullOrEmpty(pattern))
            pattern = getLastSearchPattern();
        if (string.IsNullOrEmpty(pattern))
            return new ExResult(false, "E35: No previous regular expression");

        bool global = flags.Contains('g');
        bool ignoreCase = flags.Contains('i') || (!flags.Contains('I') && options.IgnoreCase);
        bool confirm = flags.Contains('c');

        var regex = RangeResolver.TryBuildRegex(pattern, ignoreCase, out var patternError);
        if (regex == null) return new ExResult(false, patternError);
        addSearchHistory(pattern);

        var buf = bufferManager.Current.Text;
        int count = 0;

        int startLine = 0, endLine = buf.LineCount - 1;
        rangeResolver.ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);

        for (int l = startLine; l <= endLine && l < buf.LineCount; l++)
        {
            var line = buf.GetLine(l);
            var newLine = global
                ? regex.Replace(line, replacement)
                : regex.Replace(line, replacement, 1);
            if (newLine != line)
            {
                buf.ReplaceLine(l, newLine);
                count++;
            }
        }

        return new ExResult(true, count > 0 ? $"{count} substitution(s) made" : "No matches", TextModified: count > 0);
    }

    private ExResult ExecuteSort(string cmd, string range, CursorPosition cursor)
    {
        // Extract "!" from the command verb itself (like :grep! does), not from the flag list
        var arg = cmd.Length > 4 ? cmd[4..].TrimStart() : "";
        bool reverse = arg.StartsWith('!');
        if (reverse) arg = arg[1..].TrimStart();

        bool ignoreCase = false, numeric = false, sortOnMatch = false, unique = false;
        while (arg.Length > 0 && arg[0] is 'i' or 'n' or 'r' or 'u')
        {
            switch (arg[0])
            {
                case 'i': ignoreCase = true; break;
                case 'n': numeric = true; break;
                case 'r': sortOnMatch = true; break;
                case 'u': unique = true; break;
            }
            arg = arg[1..].TrimStart();
        }

        System.Text.RegularExpressions.Regex? keyRegex = null;
        if (arg.StartsWith('/'))
        {
            var closeSlash = arg.IndexOf('/', 1);
            var pat = closeSlash > 0 ? arg[1..closeSlash] : arg[1..];
            if (!string.IsNullOrEmpty(pat))
            {
                keyRegex = RangeResolver.TryBuildRegex(pat, ignoreCase, out var patErr);
                if (keyRegex == null) return new ExResult(false, patErr);
            }
        }

        var buf = bufferManager.Current.Text;
        int startLine = 0, endLine = buf.LineCount - 1;
        if (!string.IsNullOrEmpty(range))
            rangeResolver.ResolveRange(range, cursor, buf.LineCount, ref startLine, ref endLine);
        if (startLine > endLine) return new ExResult(false, "Invalid range");

        // GetLines clamps indices internally
        var lines = buf.GetLines(startLine, endLine);

        string SortKey(string line)
        {
            if (keyRegex == null) return line;
            var m = keyRegex.Match(line);
            if (!m.Success) return "";
            return sortOnMatch ? m.Value : line[(m.Index + m.Length)..];
        }

        long NumKey(string line) { var m = NumericSortRegex.Match(SortKey(line)); return m.Success ? long.Parse(m.Value) : 0L; }

        string[] result;
        if (numeric)
        {
            result = reverse
                ? [.. lines.OrderByDescending(NumKey)]
                : [.. lines.OrderBy(NumKey)];
        }
        else
        {
            var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            result = reverse
                ? [.. lines.OrderByDescending(SortKey, comparer)]
                : [.. lines.OrderBy(SortKey, comparer)];
        }

        if (unique)
        {
            var keyComparer = ignoreCase && !numeric ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var deduped = new List<string>(result.Length);
            string? prevKey = null;
            for (int i = 0; i < result.Length; i++)
            {
                var key = numeric ? NumKey(result[i]).ToString() : SortKey(result[i]);
                if (i > 0 && keyComparer.Equals(key, prevKey))
                    continue;
                deduped.Add(result[i]);
                prevKey = key;
            }
            result = [.. deduped];
        }

        for (int i = 0; i < result.Length; i++)
            buf.ReplaceLine(startLine + i, result[i]);
        if (result.Length < lines.Length)
            buf.DeleteLines(startLine + result.Length, endLine);

        return new ExResult(true, $"{result.Length} line(s) sorted", TextModified: true);
    }

    private ExResult ExecuteRetab(string cmd, string range)
    {
        // retab[!] [N]: convert tabs<->spaces
        // retab  → expand tabs to spaces using tabstop
        // retab! → compress spaces to tabs where possible
        bool toTabs = cmd.StartsWith("retab!");
        string rest = cmd[(toTabs ? 6 : 5)..].Trim();
        int tabWidth = rest.Length > 0 && int.TryParse(rest, out int n) && n > 0 ? n : options.TabStop;

        var buf = bufferManager.Current.Text;
        int startLine = 0, endLine = buf.LineCount - 1;
        rangeResolver.ResolveRange(range, new CursorPosition(0, 0), buf.LineCount, ref startLine, ref endLine);

        for (int i = startLine; i <= endLine; i++)
        {
            var line = buf.GetLine(i);
            string newLine;
            if (toTabs)
            {
                // Replace runs of spaces with tabs
                var sb = new System.Text.StringBuilder();
                int col = 0;
                int j = 0;
                while (j < line.Length)
                {
                    if (line[j] == ' ')
                    {
                        int spaceStart = j;
                        while (j < line.Length && line[j] == ' ') { j++; col++; }
                        int spaces = j - spaceStart;
                        int tabs = spaces / tabWidth;
                        int rem = spaces % tabWidth;
                        sb.Append('\t', tabs);
                        sb.Append(' ', rem);
                    }
                    else if (line[j] == '\t')
                    {
                        sb.Append('\t');
                        col = (col / tabWidth + 1) * tabWidth;
                        j++;
                    }
                    else
                    {
                        sb.Append(line[j]);
                        col++;
                        j++;
                    }
                }
                newLine = sb.ToString();
            }
            else
            {
                // Expand tabs to spaces
                var sb = new System.Text.StringBuilder();
                int col = 0;
                foreach (char ch in line)
                {
                    if (ch == '\t')
                    {
                        int spaces = tabWidth - (col % tabWidth);
                        sb.Append(' ', spaces);
                        col += spaces;
                    }
                    else
                    {
                        sb.Append(ch);
                        col++;
                    }
                }
                newLine = sb.ToString();
            }
            buf.ReplaceLine(i, newLine);
        }

        int count = endLine - startLine + 1;
        return new ExResult(true, $"Retabbed {count} line(s)", TextModified: true);
    }
}
