using System.Linq;
using Editor.Controls.Rendering;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Controls;

/// <summary>
/// Owns the extra-cursor list and Ctrl+D / Ctrl+Alt+Up/Down multi-cursor behaviour for a single
/// <see cref="VimEditorControl"/>. Composed the same way as <c>LspManager</c>/<c>GitDiffProvider</c>:
/// the control owns an instance and delegates key handling to it, keeping ProcessKey's
/// dispatch flow unchanged.
/// </summary>
public sealed class MultiCursorManager(VimEngine engine, EditorCanvas canvas, Action<string> updateStatus, Action refreshAll)
{
    private readonly List<(int Line, int Col)> _extraCursors = [];
    private bool _multiCursorMode;
    private string? _multiCursorWord;
    private (int Line, int Col) _multiCursorSearchFrom;

    public bool IsActive => _multiCursorMode;
    public int Count => _extraCursors.Count;

    /// <summary>
    /// Gets the word under the primary cursor. Returns null if the cursor is not on a word char.
    /// Also returns the start column of the word.
    /// </summary>
    private (string Word, int StartCol)? GetWordUnderCursor()
    {
        var buf = engine.CurrentBuffer.Text;
        var cursor = engine.Cursor;
        if (cursor.Line >= buf.LineCount) return null;
        var line = buf.GetLine(cursor.Line);
        int col = cursor.Column;
        if (col >= line.Length || !MotionEngine.IsWordChar(line[col])) return null;
        int start = col;
        while (start > 0 && MotionEngine.IsWordChar(line[start - 1])) start--;
        int end = col;
        while (end < line.Length && MotionEngine.IsWordChar(line[end])) end++;
        return (line[start..end], start);
    }

    /// <summary>
    /// Adds a cursor at the next occurrence of the word under the primary cursor (Ctrl+D).
    /// </summary>
    public void AddCursorAtNextOccurrence()
    {
        var buf = engine.CurrentBuffer.Text;

        // On first Ctrl+D: capture the word and start searching from primary cursor
        if (!_multiCursorMode || _multiCursorWord == null)
        {
            var wordInfo = GetWordUnderCursor();
            if (wordInfo == null)
            {
                updateStatus("No word under cursor");
                return;
            }
            _multiCursorWord = wordInfo.Value.Word;
            _multiCursorSearchFrom = (engine.Cursor.Line, engine.Cursor.Column);
        }

        // Search forward from last cursor position
        var searchFrom = new CursorPosition(_multiCursorSearchFrom.Line, _multiCursorSearchFrom.Col);
        var found = buf.FindNext(_multiCursorWord, searchFrom, forward: true, ignoreCase: false, wrapScan: true);
        if (found == null)
        {
            updateStatus($"No more occurrences of '{_multiCursorWord}'");
            return;
        }

        // Avoid adding a duplicate (including primary cursor)
        var primary = engine.Cursor;
        int foundLine = found.Value.Line;
        int foundCol  = found.Value.Column;
        if (foundLine == primary.Line && foundCol == primary.Column)
        {
            updateStatus($"No more occurrences of '{_multiCursorWord}'");
            return;
        }
        if (_extraCursors.Any(c => c.Line == foundLine && c.Col == foundCol))
        {
            updateStatus($"No more occurrences of '{_multiCursorWord}'");
            return;
        }

        _extraCursors.Add((foundLine, foundCol));
        _multiCursorSearchFrom = (foundLine, foundCol);
        _multiCursorMode = true;

        canvas.SetExtraCursors(_extraCursors);
        updateStatus($"Multi-cursor: {_extraCursors.Count + 1} cursors  (Esc to exit)");
    }

    /// <summary>
    /// Adds a cursor one line below (delta=+1) or above (delta=-1) the lowest/highest extra cursor.
    /// </summary>
    public void AddCursorVertical(int delta)
    {
        var buf = engine.CurrentBuffer.Text;
        var primary = engine.Cursor;

        // Determine the reference cursor to extend from
        int refLine, refCol;
        if (_extraCursors.Count == 0)
        {
            refLine = primary.Line;
            refCol = primary.Column;
        }
        else if (delta > 0)
        {
            // Extend downward from the bottommost cursor
            var bottom = _extraCursors.MaxBy(c => c.Line);
            refLine = bottom.Line;
            refCol = bottom.Col;
        }
        else
        {
            // Extend upward from the topmost cursor
            var top = _extraCursors.MinBy(c => c.Line);
            refLine = top.Line;
            refCol = top.Col;
        }

        int newLine = refLine + delta;
        if (newLine < 0 || newLine >= buf.LineCount) return;
        int newCol = Math.Min(refCol, buf.GetLine(newLine).Length);

        // Don't add duplicate
        if (_extraCursors.Any(c => c.Line == newLine && c.Col == newCol)) return;
        if (newLine == primary.Line && newCol == primary.Column) return;

        _extraCursors.Add((newLine, newCol));
        _multiCursorMode = true;
        _multiCursorWord = null; // vertical add resets word tracking

        canvas.SetExtraCursors(_extraCursors);
        updateStatus($"Multi-cursor: {_extraCursors.Count + 1} cursors  (Esc to exit)");
    }

    /// <summary>
    /// Clears all extra cursors and exits multi-cursor mode.
    /// </summary>
    public void Exit()
    {
        _extraCursors.Clear();
        _multiCursorMode = false;
        _multiCursorWord = null;
        canvas.SetExtraCursors(_extraCursors);
        updateStatus("");
    }

    /// <summary>
    /// Applies a single Insert-mode key (character, Backspace, Delete, Return) to all extra cursors.
    /// Extra cursors are sorted in reverse line/col order so that earlier edits don't shift later positions.
    /// </summary>
    public void ApplyKeyToExtraCursors(string key)
    {
        var buf = engine.CurrentBuffer;

        // Work on a sorted copy (reverse order) to keep positions stable
        var sorted = _extraCursors.OrderByDescending(c => c.Line).ThenByDescending(c => c.Col).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (line, col) = sorted[i];
            var lineText = buf.Text.GetLine(line);

            if (key.Length == 1)
            {
                // Insert character at position
                var newLine = lineText[..col] + key + lineText[col..];
                buf.Text.ReplaceLine(line, newLine);
                sorted[i] = (line, col + 1);
            }
            else if (key == "Return")
            {
                var before = lineText[..col];
                var after = lineText[col..];
                buf.Text.ReplaceLine(line, before);
                // InsertLines(afterLine, ...) inserts after `line`, i.e. at line+1
                buf.Text.InsertLines(line, [after]);
                sorted[i] = (line + 1, 0);
                // Shift cursors below this line down by 1
                for (int j = i + 1; j < sorted.Count; j++)
                    if (sorted[j].Line > line)
                        sorted[j] = (sorted[j].Line + 1, sorted[j].Col);
            }
            else if (key == "Back" && col > 0)
            {
                var newLine = lineText[..(col - 1)] + lineText[col..];
                buf.Text.ReplaceLine(line, newLine);
                sorted[i] = (line, col - 1);
            }
            else if (key == "Back" && col == 0 && line > 0)
            {
                var prevLine = buf.Text.GetLine(line - 1);
                int joinCol = prevLine.Length;
                // JoinLines merges line-1 with line and removes line
                buf.Text.JoinLines(line - 1);
                sorted[i] = (line - 1, joinCol);
                // Shift cursors below this line up by 1
                for (int j = i + 1; j < sorted.Count; j++)
                    if (sorted[j].Line >= line)
                        sorted[j] = (sorted[j].Line - 1, sorted[j].Col);
            }
            else if (key == "Delete" && col < lineText.Length)
            {
                var newLine = lineText[..col] + lineText[(col + 1)..];
                buf.Text.ReplaceLine(line, newLine);
                // col stays the same
            }
        }

        // Update the stored extra cursor positions
        _extraCursors.Clear();
        _extraCursors.AddRange(sorted);
        canvas.SetExtraCursors(_extraCursors);

        // Refresh canvas after all edits
        refreshAll();
    }
}
