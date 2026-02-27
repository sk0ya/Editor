using WVim.Core.Models;

namespace WVim.Core.Buffer;

public record UndoState(string[] Lines, CursorPosition Cursor);

public class UndoManager
{
    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();
    private const int MaxHistory = 1000;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Snapshot(TextBuffer buffer, CursorPosition cursor)
    {
        _redoStack.Clear();
        if (_undoStack.Count >= MaxHistory)
        {
            // Remove oldest
            var arr = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = arr.Length - 1; i > 0; i--)
                _undoStack.Push(arr[i]);
        }
        _undoStack.Push(new UndoState(buffer.Snapshot(), cursor));
    }

    public UndoState? Undo(TextBuffer buffer, CursorPosition currentCursor)
    {
        if (!CanUndo) return null;
        _redoStack.Push(new UndoState(buffer.Snapshot(), currentCursor));
        var state = _undoStack.Pop();
        buffer.RestoreSnapshot(state.Lines);
        return state;
    }

    public UndoState? Redo(TextBuffer buffer, CursorPosition currentCursor)
    {
        if (!CanRedo) return null;
        _undoStack.Push(new UndoState(buffer.Snapshot(), currentCursor));
        var state = _redoStack.Pop();
        buffer.RestoreSnapshot(state.Lines);
        return state;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
