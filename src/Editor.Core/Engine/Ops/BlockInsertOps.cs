using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Models;

namespace Editor.Core.Engine.Ops;

/// <summary>
/// Owns the Visual Block insert/append/change session (I/A/c after Ctrl+V) and the
/// per-keystroke replay of typed text across every selected line while it's active.
/// </summary>
public sealed class BlockInsertOps(
    BufferManager bufferManager,
    VimOptions options,
    Action<List<VimEvent>, CursorPosition> emitTextAt)
{
    private sealed record State(int StartLine, int EndLine, int Column, IReadOnlyDictionary<int, int> LineColumns);

    private State? _state;

    public bool IsActive => _state != null;

    public void Begin(int startLine, int endLine, int column, IReadOnlyDictionary<int, int> lineColumns) =>
        _state = new State(startLine, endLine, column, lineColumns);

    public void Clear() => _state = null;

    public bool TryHandleKey(string key, ref CursorPosition cursorRef, List<VimEvent> events)
    {
        if (_state == null) return false;
        var state = _state;
        var buf = bufferManager.Current.Text;
        var cursor = cursorRef;
        int Offset() => Math.Max(0, cursor.Column - state.Column);
        int EditColumn(int line)
        {
            var baseColumn = state.LineColumns.TryGetValue(line, out var col) ? col : state.Column;
            return Math.Min(baseColumn + Offset(), buf.GetLineLength(line));
        }

        switch (key)
        {
            case "Back":
                if (cursor.Column <= state.Column)
                    return true;
                for (int line = state.StartLine; line <= state.EndLine; line++)
                {
                    var lineLen = buf.GetLineLength(line);
                    var baseColumn = state.LineColumns.TryGetValue(line, out var col) ? col : state.Column;
                    var deleteCol = Math.Min(baseColumn + Offset() - 1, lineLen - 1);
                    if (deleteCol >= baseColumn && deleteCol >= 0)
                        buf.DeleteChar(line, deleteCol);
                }
                cursorRef = cursor with { Column = cursor.Column - 1 };
                emitTextAt(events, cursorRef);
                return true;
            case "Delete":
                for (int line = state.StartLine; line <= state.EndLine; line++)
                {
                    var deleteCol = EditColumn(line);
                    if (deleteCol < buf.GetLineLength(line))
                        buf.DeleteChar(line, deleteCol);
                }
                emitTextAt(events, cursorRef);
                return true;
            case "Tab":
                var insert = options.ExpandTab
                    ? new string(' ', options.TabStop)
                    : "\t";
                for (int line = state.StartLine; line <= state.EndLine; line++)
                {
                    buf.InsertText(line, EditColumn(line), insert);
                }
                cursorRef = cursor with { Column = cursor.Column + insert.Length };
                emitTextAt(events, cursorRef);
                return true;
            default:
                if (key.Length == 1)
                {
                    for (int line = state.StartLine; line <= state.EndLine; line++)
                    {
                        buf.InsertChar(line, EditColumn(line), key[0]);
                    }
                    cursorRef = cursor with { Column = cursor.Column + 1 };
                    emitTextAt(events, cursorRef);
                    return true;
                }
                return false;
        }
    }
}
