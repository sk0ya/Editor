using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Engine.Ops;

/// <summary>
/// Owns the Ctrl+N/P (keyword), Ctrl+X Ctrl+F (file path), and Ctrl+X Ctrl+L (whole
/// line) Insert-mode completion cycling state and apply step. Candidates themselves
/// come from the stateless <see cref="CompletionCollector"/>; this class only tracks
/// the in-progress cycle (candidates/index/prefix/applied-length) and applies the
/// selected candidate to the buffer.
/// </summary>
public sealed class KeywordCompletionOps(
    BufferManager bufferManager,
    Action<List<VimEvent>, CursorPosition> emitTextAt,
    Action<List<VimEvent>, string> emitStatus)
{
    private string[] _completions = [];
    private int _index = -1;
    private string _prefix = "";
    private int _applied;

    public bool HasCandidates => _completions.Length > 0;

    public void Reset()
    {
        _completions = [];
        _index = -1;
        _applied = 0;
        _prefix = "";
    }

    public CursorPosition CycleKeyword(CursorPosition cursor, int dir, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        if (_completions.Length == 0)
        {
            // Start a new completion session: extract word prefix before cursor
            var line = buf.GetLine(cursor.Line);
            int col = cursor.Column;
            int start = col;
            while (start > 0 && MotionEngine.IsWordChar(line[start - 1]))
                start--;
            _prefix = line[start..col];
            _completions = CompletionCollector.CollectBufferKeywords(bufferManager, _prefix);
            _index = -1;
            _applied = _prefix.Length;
            if (_completions.Length == 0)
            {
                emitStatus(events, "Pattern not found");
                return cursor;
            }
        }

        return Apply(cursor, dir, events);
    }

    // Ctrl+X Ctrl+F — file path completion
    public CursorPosition CycleFilePath(CursorPosition cursor, int dir, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        if (_completions.Length == 0)
        {
            // Extract the partial path before cursor (back to first space or line start)
            var line = buf.GetLine(cursor.Line);
            int col = cursor.Column;
            int start = col;
            while (start > 0 && line[start - 1] != ' ' && line[start - 1] != '\t')
                start--;
            _prefix = line[start..col];
            _completions = CompletionCollector.CollectFilePathCompletions(bufferManager, _prefix);
            _index = -1;
            _applied = _prefix.Length;
            if (_completions.Length == 0)
            {
                emitStatus(events, "No matching files");
                return cursor;
            }
        }

        return Apply(cursor, dir, events);
    }

    // Ctrl+X Ctrl+L — whole-line completion
    public CursorPosition CycleLine(CursorPosition cursor, int dir, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        if (_completions.Length == 0)
        {
            // Get text from start of current line up to cursor (trimmed leading whitespace)
            var currentLine = buf.GetLine(cursor.Line);
            _prefix = currentLine[..cursor.Column].TrimStart();
            _completions = CompletionCollector.CollectLineCompletions(bufferManager, _prefix, cursor.Line);
            _index = -1;
            // Track how many chars we already have (full line content from col 0 to cursor)
            _applied = cursor.Column;
            if (_completions.Length == 0)
            {
                emitStatus(events, "Pattern not found");
                return cursor;
            }
        }

        return Apply(cursor, dir, events);
    }

    // Shared apply step for all completion modes
    private CursorPosition Apply(CursorPosition cursor, int dir, List<VimEvent> events)
    {
        var buf = bufferManager.Current.Text;
        _index = dir > 0
            ? (_index + 1) % _completions.Length
            : (_index - 1 + _completions.Length) % _completions.Length;

        var completion = _completions[_index];
        int delStart = cursor.Column - _applied;
        if (_applied > 0)
            buf.DeleteRange(cursor.Line, delStart, delStart + _applied - 1);
        buf.InsertText(cursor.Line, delStart, completion);
        cursor = cursor with { Column = delStart + completion.Length };
        _applied = completion.Length;
        emitTextAt(events, cursor);
        emitStatus(events, $"\"{completion}\"");
        return cursor;
    }
}
