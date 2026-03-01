using Editor.Core.Models;

namespace Editor.Core.Buffer;

public class TextBuffer
{
    private List<string> _lines;
    private bool _modified;

    public TextBuffer() => _lines = [""];

    public TextBuffer(string text) => _lines = SplitLines(text);

    public int LineCount => _lines.Count;
    public bool IsModified => _modified;

    public string GetLine(int index)
    {
        if (index < 0 || index >= _lines.Count) return "";
        return _lines[index];
    }

    public int GetLineLength(int index) => GetLine(index).Length;

    public string GetText() => string.Join("\n", _lines);

    public void SetText(string text)
    {
        _lines = SplitLines(text);
        _modified = false;
    }

    // Insert a character at position
    public void InsertChar(int line, int col, char ch)
    {
        EnsureLine(line);
        var s = _lines[line];
        col = Math.Clamp(col, 0, s.Length);
        _lines[line] = s[..col] + ch + s[col..];
        _modified = true;
    }

    // Insert text at position
    public void InsertText(int line, int col, string text)
    {
        EnsureLine(line);
        var s = _lines[line];
        col = Math.Clamp(col, 0, s.Length);
        _lines[line] = s[..col] + text + s[col..];
        _modified = true;
    }

    // Break line at position (Enter key)
    public void BreakLine(int line, int col)
    {
        EnsureLine(line);
        var s = _lines[line];
        col = Math.Clamp(col, 0, s.Length);
        var before = s[..col];
        var after = s[col..];

        // Carry over leading whitespace for auto-indent
        _lines[line] = before;
        _lines.Insert(line + 1, after);
        _modified = true;
    }

    // Delete character at position
    public void DeleteChar(int line, int col)
    {
        EnsureLine(line);
        var s = _lines[line];
        if (col < 0 || col >= s.Length) return;
        _lines[line] = s.Remove(col, 1);
        _modified = true;
    }

    // Delete from col to end of line range [col, endCol)
    public void DeleteRange(int line, int startCol, int endCol)
    {
        EnsureLine(line);
        var s = _lines[line];
        startCol = Math.Clamp(startCol, 0, s.Length);
        endCol = Math.Clamp(endCol, startCol, s.Length);
        _lines[line] = s[..startCol] + s[endCol..];
        _modified = true;
    }

    // Join line with next line
    public void JoinLines(int line)
    {
        if (line >= _lines.Count - 1) return;
        _lines[line] = _lines[line] + _lines[line + 1];
        _lines.RemoveAt(line + 1);
        _modified = true;
    }

    // Delete entire line(s)
    public void DeleteLines(int startLine, int endLine)
    {
        startLine = Math.Clamp(startLine, 0, _lines.Count - 1);
        endLine = Math.Clamp(endLine, startLine, _lines.Count - 1);
        _lines.RemoveRange(startLine, endLine - startLine + 1);
        if (_lines.Count == 0) _lines.Add("");
        _modified = true;
    }

    // Insert lines at position
    public void InsertLines(int afterLine, IEnumerable<string> lines)
    {
        afterLine = Math.Clamp(afterLine + 1, 0, _lines.Count);
        _lines.InsertRange(afterLine, lines);
        _modified = true;
    }

    public void InsertLineAbove(int line, string text = "")
    {
        line = Math.Clamp(line, 0, _lines.Count);
        _lines.Insert(line, text);
        _modified = true;
    }

    // Replace line content
    public void ReplaceLine(int line, string text)
    {
        EnsureLine(line);
        _lines[line] = text;
        _modified = true;
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
        _lines = [.. snapshot];
        _modified = true;
    }

    public void MarkSaved() => _modified = false;

    private void EnsureLine(int line)
    {
        while (_lines.Count <= line)
            _lines.Add("");
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return [""];
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
        return lines;
    }
}
