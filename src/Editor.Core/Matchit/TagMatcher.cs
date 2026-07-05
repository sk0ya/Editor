using System.Text.RegularExpressions;
using Editor.Core.Buffer;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Matchit;

/// <summary>
/// HTML/XML-style tag matching for `%`: cursor on an opening tag jumps to its matching
/// closing tag (respecting nesting of same-named tags) and vice versa. Self-closing tags
/// and tags inside `&lt;!-- --&gt;` comments are never match targets.
/// </summary>
public static class TagMatcher
{
    // Mirrors XmlFoldLanguage's extension list.
    private static readonly string[] _extensions = [".xml", ".xaml", ".html", ".htm", ".svg"];

    // The attribute portion alternates between quoted strings (where '>' doesn't end the tag)
    // and plain non-'>' text, so a '>' inside title="a > b" doesn't truncate the match early.
    private const string AttrPattern = @"(?:\s(?:""[^""]*""|'[^']*'|[^>])*)?";
    private static readonly Regex _openTag = new(@"<([\w:\.\-]+)" + AttrPattern + ">", RegexOptions.Compiled);
    private static readonly Regex _selfCloseTag = new(@"<[\w:\.\-]+" + AttrPattern + @"\s*/>", RegexOptions.Compiled);
    private static readonly Regex _closeTag = new(@"</([\w:\.\-]+)\s*>", RegexOptions.Compiled);
    private static readonly Regex _comment = new(@"<!--.*?-->", RegexOptions.Compiled);

    public static bool IsMarkupExtension(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && _extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static Motion? Match(TextBuffer buffer, CursorPosition cursor)
    {
        var line = buffer.GetLine(cursor.Line);
        if (HasUnterminatedComment(line, out var commentRanges)) return null;

        var selfCloseRanges = _selfCloseTag.Matches(line).Select(m => (m.Index, m.Index + m.Length)).ToList();

        foreach (Match m in _openTag.Matches(line))
        {
            if (InComment(commentRanges, m.Index)) continue;
            if (selfCloseRanges.Any(r => m.Index >= r.Item1 && m.Index < r.Item2)) continue;
            if (cursor.Column >= m.Index && cursor.Column < m.Index + m.Length)
                return FindMatchingClose(buffer, cursor.Line, m.Index + m.Length, m.Groups[1].Value);
        }

        foreach (Match m in _closeTag.Matches(line))
        {
            if (InComment(commentRanges, m.Index)) continue;
            if (cursor.Column >= m.Index && cursor.Column < m.Index + m.Length)
                return FindMatchingOpen(buffer, cursor.Line, m.Index, m.Groups[1].Value);
        }

        return null;
    }

    // Ranges of complete same-line <!-- ... --> comments. Returns true (and an incomplete/empty
    // list) when the line has a comment marker that isn't resolved on this line (an opener with
    // no closer, or a stray closer left over from a previous line) — callers conservatively skip
    // the whole line in that case, since we can't tell what's really inside a cross-line comment.
    private static bool HasUnterminatedComment(string line, out List<(int Start, int End)> ranges)
    {
        ranges = [];
        int i = 0;
        while (true)
        {
            int start = line.IndexOf("<!--", i, StringComparison.Ordinal);
            if (start < 0) break;
            int end = line.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0) return true;
            ranges.Add((start, end + 3));
            i = end + 3;
        }
        return line.IndexOf("-->", i, StringComparison.Ordinal) >= 0;
    }

    private static bool InComment(List<(int Start, int End)> ranges, int index) =>
        ranges.Any(r => index >= r.Start && index < r.End);

    private static Motion? FindMatchingClose(TextBuffer buffer, int fromLine, int fromCol, string tagName)
    {
        int depth = 1;
        foreach (var ev in EnumerateTagEvents(buffer, fromLine, fromCol, forward: true))
        {
            if (ev.Name != tagName) continue;
            if (ev.IsOpen) depth++;
            else depth--;
            if (depth == 0) return new Motion(new CursorPosition(ev.Line, ev.Col), MotionType.Inclusive);
        }
        return null;
    }

    private static Motion? FindMatchingOpen(TextBuffer buffer, int fromLine, int fromCol, string tagName)
    {
        int depth = 1;
        foreach (var ev in EnumerateTagEvents(buffer, fromLine, fromCol, forward: false))
        {
            if (ev.Name != tagName) continue;
            if (ev.IsOpen) depth--;
            else depth++;
            if (depth == 0) return new Motion(new CursorPosition(ev.Line, ev.Col), MotionType.Inclusive);
        }
        return null;
    }

    private readonly record struct TagEvent(int Line, int Col, bool IsOpen, string Name);

    private static IEnumerable<TagEvent> EnumerateTagEvents(TextBuffer buffer, int fromLine, int fromCol, bool forward)
    {
        if (forward)
        {
            for (int l = fromLine; l < buffer.LineCount; l++)
            {
                var line = buffer.GetLine(l);
                if (HasUnterminatedComment(line, out var commentRanges)) continue;

                int searchFrom = l == fromLine ? fromCol : 0;
                var selfCloseRanges = _selfCloseTag.Matches(line)
                    .Where(m => m.Index + m.Length > searchFrom)
                    .Select(m => (m.Index, m.Index + m.Length)).ToList();

                var events = new List<(int Index, TagEvent Ev)>();
                foreach (Match m in _openTag.Matches(line))
                {
                    if (m.Index < searchFrom) continue;
                    if (InComment(commentRanges, m.Index)) continue;
                    if (selfCloseRanges.Any(r => m.Index >= r.Item1 && m.Index < r.Item2)) continue;
                    events.Add((m.Index, new TagEvent(l, m.Index, true, m.Groups[1].Value)));
                }
                foreach (Match m in _closeTag.Matches(line))
                {
                    if (m.Index < searchFrom) continue;
                    if (InComment(commentRanges, m.Index)) continue;
                    events.Add((m.Index, new TagEvent(l, m.Index, false, m.Groups[1].Value)));
                }
                foreach (var (_, ev) in events.OrderBy(e => e.Index))
                    yield return ev;
            }
        }
        else
        {
            for (int l = fromLine; l >= 0; l--)
            {
                var line = buffer.GetLine(l);
                if (HasUnterminatedComment(line, out var commentRanges)) continue;

                int searchTo = l == fromLine ? fromCol : line.Length;
                var selfCloseRanges = _selfCloseTag.Matches(line)
                    .Where(m => m.Index < searchTo)
                    .Select(m => (m.Index, m.Index + m.Length)).ToList();

                var events = new List<(int Index, TagEvent Ev)>();
                foreach (Match m in _openTag.Matches(line))
                {
                    if (m.Index >= searchTo) continue;
                    if (InComment(commentRanges, m.Index)) continue;
                    if (selfCloseRanges.Any(r => m.Index >= r.Item1 && m.Index < r.Item2)) continue;
                    events.Add((m.Index, new TagEvent(l, m.Index, true, m.Groups[1].Value)));
                }
                foreach (Match m in _closeTag.Matches(line))
                {
                    if (m.Index >= searchTo) continue;
                    if (InComment(commentRanges, m.Index)) continue;
                    events.Add((m.Index, new TagEvent(l, m.Index, false, m.Groups[1].Value)));
                }
                foreach (var (_, ev) in events.OrderByDescending(e => e.Index))
                    yield return ev;
            }
        }
    }
}
