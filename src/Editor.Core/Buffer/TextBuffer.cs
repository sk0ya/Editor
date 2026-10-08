using Editor.Core.Models;
using Editor.Core.Extensibility;

namespace Editor.Core.Buffer;

public class TextBuffer : INormalBufferView
{
    private List<string> _lines;
    // Set by every mutation, cleared at a save point. Only says "an edit happened since
    // then" — whether the contents actually differ is decided lazily by IsModified.
    private bool _touched;
    private long _modifiedComputedAt = -1;
    private bool _modifiedCache;
    // The joined document, computed at most once per Version (see GetText).
    private long _textComputedAt = -1;
    private string? _textCache;
    // Snapshot of the line contents as of the last load/save. Used to compute the
    // "changed since last save" gutter (see Editor.Core.Editing.SaveDiff), which is
    // independent of git (it tracks the on-disk baseline, not HEAD).
    private string[] _savedSnapshot;

    public TextBuffer()
    {
        _lines = [""];
        _savedSnapshot = [.. _lines];
    }

    public TextBuffer(string text)
    {
        _lines = SplitLines(text);
        _savedSnapshot = [.. _lines];
    }

    public int LineCount => _lines.Count;

    /// <summary>
    /// Whether the contents differ from the last load/save point. Undo/redo can return the
    /// buffer byte-for-byte to that point, so this is a relation to the save point, not a
    /// sticky record that an edit happened at some time in the past.
    /// </summary>
    /// <remarks>
    /// The comparison is O(lines), so it is done here — once per <see cref="Version"/>, only
    /// when someone asks — rather than on every mutation: a bulk edit runs one mutation per
    /// line, which would make the whole operation quadratic.
    /// </remarks>
    public bool IsModified
    {
        get
        {
            if (!_touched) return false;
            if (_modifiedComputedAt != Version)
            {
                _modifiedCache = !_lines.SequenceEqual(_savedSnapshot);
                _modifiedComputedAt = Version;
            }
            return _modifiedCache;
        }
    }

    public long Version { get; private set; }

    /// <summary>
    /// Raised after every mutation with exactly what changed (see <see cref="TextBufferChange"/>):
    /// applying the changes in order to a copy of the previous text reproduces the buffer. Hosts use
    /// it to keep other views, anchors and excerpts of the same document in step without comparing
    /// the whole text on every keystroke. Raised synchronously on the mutating thread, mid-operation
    /// for multi-step commands — a handler must not mutate this buffer. Nothing is computed while
    /// there is no subscriber, so the per-keystroke cost of an unobserved buffer is unchanged.
    /// </summary>
    public event Action<TextBufferChange>? Changed;

    /// <summary>
    /// The line contents as of the last load (<see cref="SetText"/>) or save
    /// (<see cref="MarkSaved"/>). Compared against the current lines to render the
    /// changed-since-save gutter.
    /// </summary>
    public IReadOnlyList<string> SavedLines => _savedSnapshot;

    public string GetLine(int index)
    {
        if (index < 0 || index >= _lines.Count) return "";
        return _lines[index];
    }

    public int GetLineLength(int index) => GetLine(index).Length;

    /// <summary>The whole document as a single string (lines joined with "\n").</summary>
    /// <remarks>
    /// Cached per <see cref="Version"/>, the same way <see cref="IsModified"/> is. The join is
    /// O(document) and allocates a copy of the entire file — 0.4ms and a Large Object Heap
    /// allocation for a 456KB source file — and hosts ask for it several times for the *same*
    /// buffer version (session capture, diagnostics, "does the file on disk still match",
    /// LSP didChange). Computing it once per version turns all but the first of those into a
    /// field read, and nothing is recomputed unless the buffer actually changed.
    /// </remarks>
    public string GetText()
    {
        if (_textComputedAt != Version || _textCache is null)
        {
            _textCache = string.Join("\n", _lines);
            _textComputedAt = Version;
        }
        return _textCache;
    }

    public void SetText(string text)
    {
        var notify = Changed is not null;
        var oldEnd = notify ? EndPosition() : default;
        _lines = SplitLines(text);
        _touched = false;
        _savedSnapshot = [.. _lines];
        Version++;
        if (notify)
            Raise(0, 0, oldEnd.Line, oldEnd.Column, string.Join("\n", _lines), TextBufferChangeKind.Reload);
    }

    /// <summary>
    /// Replaces the document as an edit rather than as a load: the saved baseline is left
    /// alone, so the result participates in an outer undo transaction and stays dirty unless
    /// the replacement happens to equal the save point.
    /// </summary>
    public void ReplaceAll(string text)
    {
        var replacement = SplitLines(text);
        if (_lines.SequenceEqual(replacement))
            return;
        ReplaceLinesWithDiff(replacement);
    }

    // Insert a character at position
    public void InsertChar(int line, int col, char ch)
    {
        var growth = EnsureLine(line);
        var s = _lines[line];
        col = Math.Clamp(col, 0, s.Length);
        _lines[line] = s[..col] + ch + s[col..];
        MarkModified();
        RaiseGrowth(growth);
        if (Changed is not null) Raise(line, col, line, col, ch.ToString());
    }

    // Insert text at position
    public void InsertText(int line, int col, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var growth = EnsureLine(line);
        var s = _lines[line];
        col = Math.Clamp(col, 0, s.Length);
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var parts = text.Split('\n');
        if (parts.Length == 1)
        {
            _lines[line] = s[..col] + text + s[col..];
        }
        else
        {
            var before = s[..col];
            var after = s[col..];
            _lines[line] = before + parts[0];
            for (int i = 1; i < parts.Length; i++)
                _lines.Insert(line + i, parts[i]);
            _lines[line + parts.Length - 1] += after;
        }
        MarkModified();
        RaiseGrowth(growth);
        if (Changed is not null) Raise(line, col, line, col, text);
    }

    // Break line at position (Enter key)
    public void BreakLine(int line, int col)
    {
        var growth = EnsureLine(line);
        var s = _lines[line];
        col = Math.Clamp(col, 0, s.Length);
        var before = s[..col];
        var after = s[col..];

        // Carry over leading whitespace for auto-indent
        _lines[line] = before;
        _lines.Insert(line + 1, after);
        MarkModified();
        RaiseGrowth(growth);
        if (Changed is not null) Raise(line, col, line, col, "\n");
    }

    // Delete character at position
    public void DeleteChar(int line, int col)
    {
        if (line < 0 || line >= _lines.Count) return;
        var s = _lines[line];
        if (col < 0 || col >= s.Length) return;
        _lines[line] = s.Remove(col, 1);
        MarkModified();
        if (Changed is not null) Raise(line, col, line, col + 1, "");
    }

    // Delete from col to end of line range [col, endCol)
    public void DeleteRange(int line, int startCol, int endCol)
    {
        if (line < 0 || line >= _lines.Count) return;
        var s = _lines[line];
        startCol = Math.Clamp(startCol, 0, s.Length);
        endCol = Math.Clamp(endCol, startCol, s.Length);
        if (startCol == endCol) return;
        _lines[line] = s[..startCol] + s[endCol..];
        MarkModified();
        if (Changed is not null) Raise(line, startCol, line, endCol, "");
    }

    // Join line with next line
    public void JoinLines(int line)
    {
        if (line < 0 || line >= _lines.Count - 1) return;
        var joinColumn = _lines[line].Length;
        _lines[line] = _lines[line] + _lines[line + 1];
        _lines.RemoveAt(line + 1);
        MarkModified();
        if (Changed is not null) Raise(line, joinColumn, line + 1, 0, "");
    }

    // Delete entire line(s)
    public void DeleteLines(int startLine, int endLine)
    {
        startLine = Math.Clamp(startLine, 0, _lines.Count - 1);
        endLine = Math.Clamp(endLine, startLine, _lines.Count - 1);
        TextBufferChange? change = null;
        if (Changed is not null)
        {
            // The removed lines take one line break with them: the one after them, or — when they
            // run to the end of the document — the one before them.
            if (endLine < _lines.Count - 1)
                change = Change(startLine, 0, endLine + 1, 0, "");
            else if (startLine > 0)
                change = Change(startLine - 1, _lines[startLine - 1].Length, endLine, _lines[endLine].Length, "");
            else
                change = Change(0, 0, endLine, _lines[endLine].Length, "");
        }
        _lines.RemoveRange(startLine, endLine - startLine + 1);
        if (_lines.Count == 0) _lines.Add("");
        MarkModified();
        if (change is not null) Raise(change);
    }

    // Insert lines at position
    public void InsertLines(int afterLine, IEnumerable<string> lines)
    {
        var newLines = lines.ToArray();
        if (newLines.Length == 0) return;
        afterLine = Math.Clamp(afterLine + 1, 0, _lines.Count);
        var change = Changed is not null ? LineInsertion(afterLine, newLines) : null;
        _lines.InsertRange(afterLine, newLines);
        MarkModified();
        if (change is not null) Raise(change);
    }

    public void InsertLineAbove(int line, string text = "")
    {
        line = Math.Clamp(line, 0, _lines.Count);
        var change = Changed is not null ? LineInsertion(line, [text]) : null;
        _lines.Insert(line, text);
        MarkModified();
        if (change is not null) Raise(change);
    }

    // Replace line content
    public void ReplaceLine(int line, string text)
    {
        var growth = EnsureLine(line);
        if (!_grewLastCall && _lines[line] == text) return;
        var oldLength = _lines[line].Length;
        _lines[line] = text;
        MarkModified();
        RaiseGrowth(growth);
        if (Changed is not null) Raise(line, 0, line, oldLength, text);
    }

    // Get lines as array (for yank/delete)
    public string[] GetLines(int startLine, int endLine)
    {
        startLine = Math.Clamp(startLine, 0, _lines.Count - 1);
        endLine = Math.Clamp(endLine, startLine, _lines.Count - 1);
        return _lines.GetRange(startLine, endLine - startLine + 1).ToArray();
    }

    // Clamp cursor to valid position
    public CursorPosition ClampCursor(CursorPosition pos, bool insertMode = false)
    {
        var line = Math.Clamp(pos.Line, 0, Math.Max(0, _lines.Count - 1));
        var lineLen = _lines[line].Length;
        var maxCol = insertMode ? lineLen : Math.Max(0, lineLen - 1);
        var col = Math.Clamp(pos.Column, 0, maxCol);
        return new CursorPosition(line, col);
    }

    // Find next occurrence of pattern from position
    public CursorPosition? FindNext(string pattern, CursorPosition from, bool forward, bool ignoreCase = false, bool wrapScan = true)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (forward)
        {
            // Search from current position forward
            for (int l = from.Line; l < _lines.Count; l++)
            {
                var line = _lines[l];
                var startCol = l == from.Line ? from.Column + 1 : 0;
                // string.IndexOf throws when startIndex > Length (e.g. cursor on an empty line:
                // Column 0, Length 0 → startCol 1). Nothing can match past the line end, so skip.
                if (startCol > line.Length) continue;
                var idx = line.IndexOf(pattern, startCol, comparison);
                if (idx >= 0) return new CursorPosition(l, idx);
            }
            if (!wrapScan) return null;
            // Wrap around
            for (int l = 0; l <= from.Line; l++)
            {
                var line = _lines[l];
                var endCol = l == from.Line ? from.Column : line.Length;
                var idx = line.IndexOf(pattern, 0, comparison);
                if (idx >= 0 && idx < endCol) return new CursorPosition(l, idx);
            }
        }
        else
        {
            // Search backward
            for (int l = from.Line; l >= 0; l--)
            {
                var line = _lines[l];
                var endCol = l == from.Line ? from.Column : line.Length;
                var idx = line.LastIndexOf(pattern, Math.Max(0, endCol - 1), comparison);
                if (idx >= 0) return new CursorPosition(l, idx);
            }
            if (!wrapScan) return null;
            // Wrap
            for (int l = _lines.Count - 1; l >= from.Line; l--)
            {
                var line = _lines[l];
                var idx = line.LastIndexOf(pattern, comparison);
                if (idx >= 0) return new CursorPosition(l, idx);
            }
        }
        return null;
    }

    public List<CursorPosition> FindAll(string pattern, bool ignoreCase = false)
    {
        var results = new List<CursorPosition>();
        if (string.IsNullOrEmpty(pattern)) return results;
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        for (int l = 0; l < _lines.Count; l++)
        {
            var line = _lines[l];
            int idx = 0;
            while ((idx = line.IndexOf(pattern, idx, comparison)) >= 0)
            {
                results.Add(new CursorPosition(l, idx));
                idx += pattern.Length;
            }
        }
        return results;
    }

    // Snapshot for undo
    public string[] Snapshot() => [.. _lines];

    public void RestoreSnapshot(string[] snapshot)
    {
        if (Changed is null)
        {
            _lines = [.. snapshot];
            MarkModified();
            return;
        }
        ReplaceLinesWithDiff([.. snapshot]);
    }

    public void MarkSaved()
    {
        _touched = false;
        _savedSnapshot = [.. _lines];
    }

    // Whether the last EnsureLine call padded the document (kept separately from its returned
    // change, which is null whenever nobody is subscribed).
    private bool _grewLastCall;

    /// <summary>Pads the document with empty lines up to <paramref name="line"/>. Returns the
    /// insertion that padding amounts to (null when nothing was added or nobody listens), to be
    /// raised before the change of the mutation that needed it.</summary>
    private TextBufferChange? EnsureLine(int line)
    {
        var originalCount = _lines.Count;
        _grewLastCall = originalCount <= line;
        if (!_grewLastCall) return null;
        var lastLength = _lines[originalCount - 1].Length;
        while (_lines.Count <= line)
            _lines.Add("");
        return Changed is null
            ? null
            : Change(originalCount - 1, lastLength, originalCount - 1, lastLength,
                new string('\n', _lines.Count - originalCount));
    }

    private void RaiseGrowth(TextBufferChange? growth)
    {
        if (growth is not null) Raise(growth);
    }

    /// <summary>Inserting whole lines before <paramref name="index"/> (or after the last line).</summary>
    private TextBufferChange LineInsertion(int index, string[] lines)
    {
        var joined = string.Join("\n", lines);
        if (index < _lines.Count)
            return Change(index, 0, index, 0, joined + "\n");
        var last = _lines.Count - 1;
        return Change(last, _lines[last].Length, last, _lines[last].Length, "\n" + joined);
    }

    /// <summary>
    /// Replaces the whole document (an edit, not a load) and reports only the lines that differ:
    /// the unchanged head and tail are kept out of the range, so positions there survive an undo or a
    /// host-side whole-text replacement instead of all collapsing to the document start.
    /// </summary>
    private void ReplaceLinesWithDiff(List<string> replacement)
    {
        var old = _lines;
        TextBufferChange? change = null;
        if (Changed is not null)
        {
            var min = Math.Min(old.Count, replacement.Count);
            var prefix = 0;
            while (prefix < min && old[prefix] == replacement[prefix]) prefix++;
            var suffix = 0;
            while (suffix < min - prefix && old[old.Count - 1 - suffix] == replacement[replacement.Count - 1 - suffix])
                suffix++;
            var insertedCount = replacement.Count - prefix - suffix;
            var inserted = string.Join("\n", replacement.Skip(prefix).Take(insertedCount));
            if (insertedCount == 0 && prefix + suffix == old.Count)
                change = null;   // identical (an undo back to the same lines): nothing to report
            else if (suffix > 0)
                change = Change(prefix, 0, old.Count - suffix, 0, insertedCount > 0 ? inserted + "\n" : "");
            else if (prefix < old.Count && insertedCount > 0)
                change = Change(prefix, 0, old.Count - 1, old[^1].Length, inserted);
            else if (prefix < old.Count)
                change = Change(prefix - 1, old[prefix - 1].Length, old.Count - 1, old[^1].Length, "");
            else
                change = Change(old.Count - 1, old[^1].Length, old.Count - 1, old[^1].Length, "\n" + inserted);
        }
        _lines = replacement;
        MarkModified();
        if (change is not null) Raise(change);
    }

    private (int Line, int Column) EndPosition() => (_lines.Count - 1, _lines[^1].Length);

    private TextBufferChange Change(int startLine, int startColumn, int endLine, int endColumn, string newText)
        => new(startLine, startColumn, endLine, endColumn, newText, Version);

    private void Raise(int startLine, int startColumn, int endLine, int endColumn, string newText,
        TextBufferChangeKind kind = TextBufferChangeKind.Edit)
        => Changed?.Invoke(new TextBufferChange(startLine, startColumn, endLine, endColumn, newText, Version, kind));

    // Changes are built before the mutation (their range is in the old coordinates) but carry the
    // version the buffer has once the mutation is done.
    private void Raise(TextBufferChange change) => Changed?.Invoke(change with { Version = Version });

    // Kept O(1): a bulk edit calls this once per line (and block insert once per typed
    // character per line). Whether the buffer really differs from the save point is
    // settled in IsModified, at most once per Version.
    private void MarkModified()
    {
        _touched = true;
        Version++;
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
        return lines;
    }
}
