using System.Text.RegularExpressions;
using Editor.Core.Marks;
using Editor.Core.Models;

namespace Editor.Core.Engine.ExCommands;

/// <summary>
/// Shared range-prefix scanning, range-to-line resolution, and regex construction used by
/// most ex commands (:s, :g/:v, :sort, shell filters, quickfix navigation, etc).
/// </summary>
public class RangeResolver(MarkManager markManager)
{
    // Scans a leading range prefix off `cmd`: %, ., a digit run, or a mark reference ('x),
    // optionally followed by a comma and a second address of the same kinds
    // (e.g. '<,'>  'a,'b  'a,5  .,'b). Returns false (range="", cmdStart=0) when `cmd`
    // doesn't start with a range at all, matching the previous no-range behaviour.
    public static bool TryScanRangePrefix(string cmd, out string range, out int cmdStart)
    {
        range = "";
        cmdStart = 0;
        if (cmd.Length == 0) return false;

        if (cmd[0] == '%') { range = "%"; cmdStart = 1; return true; }

        int pos = 0;
        if (!TryScanRangeAddress(cmd, ref pos))
            return false;

        if (pos < cmd.Length && cmd[pos] == ',')
        {
            int afterComma = pos + 1;
            if (TryScanRangeAddress(cmd, ref afterComma))
                pos = afterComma;
        }

        range = cmd[..pos];
        cmdStart = pos;
        return true;
    }

    // Scans a single range address (., a digit run, or 'x) starting at pos, advancing pos past it.
    private static bool TryScanRangeAddress(string cmd, ref int pos)
    {
        if (pos >= cmd.Length) return false;
        char c = cmd[pos];
        if (c == '.') { pos++; return true; }
        if (c == '\'' && pos + 1 < cmd.Length && (char.IsLetter(cmd[pos + 1]) || cmd[pos + 1] is '<' or '>'))
        {
            pos += 2;
            return true;
        }
        if (char.IsDigit(c))
        {
            while (pos < cmd.Length && char.IsDigit(cmd[pos])) pos++;
            return true;
        }
        return false;
    }

    public void ResolveRange(string range, CursorPosition cursor, int lineCount, ref int startLine, ref int endLine)
    {
        if (range == "%") { startLine = 0; endLine = lineCount - 1; }
        else if (range == "." || range == "") { startLine = endLine = cursor.Line; }
        else
        {
            var parts = range.Split(',');
            if (parts.Length == 2 &&
                TryResolveRangeAddress(parts[0], cursor, out var s) &&
                TryResolveRangeAddress(parts[1], cursor, out var e))
            { startLine = s; endLine = e; }
            else if (parts.Length == 1 && TryResolveRangeAddress(parts[0], cursor, out var n))
            { startLine = endLine = n; }
        }
    }

    // Resolves a single range token (digit run, ., or 'x) to a 0-based line number.
    // Returns false for an unrecognized or undefined mark, leaving the caller's
    // existing startLine/endLine untouched (same fallback as any other unparseable range).
    private bool TryResolveRangeAddress(string token, CursorPosition cursor, out int line)
    {
        token = token.Trim();
        line = 0;
        if (token.Length == 0) return false;
        if (token == ".") { line = cursor.Line; return true; }
        if (token[0] == '\'' && token.Length == 2)
        {
            var mark = markManager.GetMark(token[1]);
            if (mark == null) return false;
            line = mark.Value.Line;
            return true;
        }
        if (int.TryParse(token, out var n)) { line = n - 1; return true; }
        return false;
    }

    // Extract the optional argument after a command verb: "reg ab" → "ab", "marks" → ""
    public static string GetCommandArg(string cmd)
    {
        var idx = cmd.IndexOf(' ');
        return idx >= 0 ? cmd[(idx + 1)..].Trim() : "";
    }

    public static Regex? TryBuildRegex(string pattern, bool ignoreCase, out string? error)
    {
        error = null;
        var opts = ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None;
        try { return new Regex(VimRegex.ToDotNetPattern(pattern), opts); }
        catch (Exception ex) { error = $"Invalid pattern: {ex.Message}"; return null; }
    }
}
