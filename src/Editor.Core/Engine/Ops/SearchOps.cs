using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Marks;
using Editor.Core.Models;

namespace Editor.Core.Engine.Ops;

/// <summary>
/// Owns the active search pattern/direction/pre-search cursor and the `/`,`?`,`n`,`N`,
/// `*`,`#`,`;`,`,` search-execution logic. Same shape as <see cref="RepeatTracker"/>:
/// owns its state fully, takes the VimEngine callbacks it needs (cursor motion, status).
/// <c>EnterSearchMode</c>/the command-line key handler still live on VimEngine (they're
/// entangled with unrelated command-line bookkeeping) and write <see cref="Pattern"/>/
/// <see cref="Forward"/>/<see cref="PreSearchCursor"/> directly.
/// </summary>
public sealed class SearchOps(
    BufferManager bufferManager,
    MarkManager markManager,
    VimOptions options,
    CommandParser commandParser,
    Action<CursorPosition, List<VimEvent>> moveCursor,
    Action<List<VimEvent>, string> emitStatus)
{
    public string Pattern { get; set; } = "";
    public bool Forward { get; set; } = true;
    public CursorPosition PreSearchCursor { get; set; }

    public bool GetIgnoreCase(string pattern) =>
        options.SmartCase ? !pattern.Any(char.IsUpper) : options.IgnoreCase;

    public void DoSearch(CursorPosition cursor, bool forward, List<VimEvent> events)
    {
        if (string.IsNullOrEmpty(Pattern)) return;
        var buf = bufferManager.Current.Text;
        var ignoreCase = GetIgnoreCase(Pattern);

        var found = buf.FindNext(Pattern, cursor, forward, ignoreCase, options.WrapScan);
        if (found.HasValue)
        {
            markManager.AddJump(cursor);
            markManager.SetMark('\'', cursor);
            moveCursor(found.Value, events);
            var all = buf.FindAll(Pattern, ignoreCase);
            events.Add(VimEvent.SearchChanged(Pattern, all.Count));
        }
        else
        {
            var msg = options.WrapScan
                ? $"Pattern not found: {Pattern}"
                : $"Search hit {(forward ? "BOTTOM" : "TOP")}, continuing at {(forward ? "TOP" : "BOTTOM")} not done (no wrapscan)";
            emitStatus(events, msg);
        }
    }

    public void DoIncrSearch(string cmdLine, bool forward, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        if (string.IsNullOrEmpty(cmdLine))
        {
            moveCursor(PreSearchCursor, events);
            events.Add(VimEvent.SearchChanged("", 0));
            return;
        }
        var ignoreCase = GetIgnoreCase(cmdLine);
        var found = buf.FindNext(cmdLine, PreSearchCursor, forward, ignoreCase);
        moveCursor(found ?? PreSearchCursor, events);
        var all = buf.FindAll(cmdLine, ignoreCase);
        events.Add(VimEvent.SearchChanged(cmdLine, all.Count));
    }

    public (CursorPosition Start, CursorPosition End)? FindGnMatch(CursorPosition cursor, bool forward)
    {
        if (string.IsNullOrEmpty(Pattern)) return null;
        var buf = bufferManager.Current.Text;
        var ignoreCase = GetIgnoreCase(Pattern);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (forward)
        {
            // If cursor is at the start of a match, select that match directly.
            var curLine = buf.GetLine(cursor.Line);
            if (cursor.Column + Pattern.Length <= curLine.Length)
            {
                var onMatchIdx = curLine.IndexOf(Pattern, cursor.Column, comparison);
                if (onMatchIdx == cursor.Column)
                {
                    var matchEnd = new CursorPosition(cursor.Line, cursor.Column + Pattern.Length - 1);
                    return (cursor, buf.ClampCursor(matchEnd));
                }
            }
            // FindNext skips current column (+1 internally), so pass column - 1 to find matches starting at cursor.Column
            var searchFrom = new CursorPosition(cursor.Line, Math.Max(0, cursor.Column - 1));
            var found = buf.FindNext(Pattern, searchFrom, true, ignoreCase, options.WrapScan);
            if (!found.HasValue) return null;
            var start = found.Value;
            var end = new CursorPosition(start.Line, start.Column + Pattern.Length - 1);
            return (start, buf.ClampCursor(end));
        }
        else
        {
            // Backward: search from a position shifted right by (patternLength - 1) so that a match
            // whose end is at or before cursor is discovered (FindNext backward uses LastIndexOf with
            // startIndex = endCol - 1, which limits matches to those starting at ≤ endCol - 1).
            // We want matches starting at ≤ cursor.Column, so we need endCol - 1 ≥ cursor.Column,
            // i.e. endCol ≥ cursor.Column + 1, i.e. pass column = cursor.Column + patternLength - 1.
            var curLineLen = buf.GetLine(cursor.Line).Length;
            int shiftedCol = Math.Min(cursor.Column + Pattern.Length - 1, curLineLen);
            var searchFrom = new CursorPosition(cursor.Line, shiftedCol);
            var found = buf.FindNext(Pattern, searchFrom, false, ignoreCase, options.WrapScan);
            if (!found.HasValue) return null;
            var start = found.Value;
            var end = new CursorPosition(start.Line, start.Column + Pattern.Length - 1);
            return (start, buf.ClampCursor(end));
        }
    }

    public void RepeatFind(CursorPosition cursor, bool reverse, List<VimEvent> events)
    {
        if (commandParser.LastFindChar == null) return;
        var motion = new MotionEngine(bufferManager.Current.Text, bufferManager.Current.FilePath);
        bool fwd = reverse ? !commandParser.LastFindForward : commandParser.LastFindForward;
        var pos = motion.FindChar(cursor, commandParser.LastFindChar.Value, fwd, commandParser.LastFindBefore);
        moveCursor(pos, events);
    }

    public void SearchNext(CursorPosition cursor, bool sameDir, List<VimEvent> events)
    {
        var forward = sameDir ? Forward : !Forward;
        DoSearch(cursor, forward, events);
    }

    public void SearchWordUnderCursor(CursorPosition cursor, bool forward, List<VimEvent> events)
    {
        var line = bufferManager.Current.Text.GetLine(cursor.Line);
        int start = cursor.Column;
        while (start > 0 && MotionEngine.IsWordChar(line[start - 1])) start--;
        int end = cursor.Column;
        while (end < line.Length && MotionEngine.IsWordChar(line[end])) end++;
        if (end > start)
        {
            Pattern = line[start..end];
            Forward = forward;
            DoSearch(cursor, forward, events);
        }
    }
}
