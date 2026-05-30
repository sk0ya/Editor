using Editor.Core.Models;
using System.Text;

namespace Editor.Core.Buffer;

public record UndoState(string[] Lines, CursorPosition Cursor, int ChangeNumber, DateTimeOffset Timestamp)
{
    public UndoState(string[] lines, CursorPosition cursor)
        : this(lines, cursor, 0, DateTimeOffset.Now)
    {
    }
}

public record UndoHistoryEntry(int Number, string State, DateTimeOffset Timestamp);
public record UndoTraversalResult(int Count, UndoState? State);

public class UndoManager
{
    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();
    private readonly Func<DateTimeOffset> _clock;
    private const int MaxHistory = 1000;
    private int _nextChangeNumber;

    public UndoManager(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Snapshot(TextBuffer buffer, CursorPosition cursor)
    {
        Snapshot(buffer.Snapshot(), cursor);
    }

    public void Snapshot(string[] lines, CursorPosition cursor)
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
        _undoStack.Push(new UndoState([.. lines], cursor, ++_nextChangeNumber, _clock()));
    }

    public UndoState? Undo(TextBuffer buffer, CursorPosition currentCursor)
    {
        if (!CanUndo) return null;
        var state = _undoStack.Pop();
        _redoStack.Push(new UndoState(buffer.Snapshot(), currentCursor, state.ChangeNumber, state.Timestamp));
        buffer.RestoreSnapshot(state.Lines);
        return state;
    }

    public UndoState? Redo(TextBuffer buffer, CursorPosition currentCursor)
    {
        if (!CanRedo) return null;
        var state = _redoStack.Pop();
        _undoStack.Push(new UndoState(buffer.Snapshot(), currentCursor, state.ChangeNumber, state.Timestamp));
        buffer.RestoreSnapshot(state.Lines);
        return state;
    }

    public UndoTraversalResult Earlier(TextBuffer buffer, CursorPosition currentCursor, int count)
    {
        return MoveEarlier(buffer, currentCursor, () => count-- > 0);
    }

    public UndoTraversalResult Later(TextBuffer buffer, CursorPosition currentCursor, int count)
    {
        return MoveLater(buffer, currentCursor, () => count-- > 0);
    }

    public UndoTraversalResult Earlier(TextBuffer buffer, CursorPosition currentCursor, TimeSpan age)
    {
        if (_undoStack.Count == 0) return new UndoTraversalResult(0, null);
        var target = _undoStack.Peek().Timestamp - age;
        return MoveEarlier(buffer, currentCursor, () => _undoStack.Count > 0 && _undoStack.Peek().Timestamp > target);
    }

    public UndoTraversalResult Later(TextBuffer buffer, CursorPosition currentCursor, TimeSpan age)
    {
        if (_redoStack.Count == 0) return new UndoTraversalResult(0, null);
        var currentTimestamp = _undoStack.Count > 0
            ? _undoStack.Peek().Timestamp
            : _redoStack.Peek().Timestamp - TimeSpan.FromTicks(1);
        var target = currentTimestamp + age;
        return MoveLater(buffer, currentCursor, () => _redoStack.Count > 0 && _redoStack.Peek().Timestamp <= target);
    }

    private UndoTraversalResult MoveEarlier(TextBuffer buffer, CursorPosition currentCursor, Func<bool> shouldContinue)
    {
        var count = 0;
        UndoState? last = null;
        var cursor = currentCursor;

        while (CanUndo && shouldContinue())
        {
            last = Undo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }

        return new UndoTraversalResult(count, last);
    }

    private UndoTraversalResult MoveLater(TextBuffer buffer, CursorPosition currentCursor, Func<bool> shouldContinue)
    {
        var count = 0;
        UndoState? last = null;
        var cursor = currentCursor;

        while (CanRedo && shouldContinue())
        {
            last = Redo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }

        return new UndoTraversalResult(count, last);
    }

    public IReadOnlyList<UndoHistoryEntry> GetHistory()
    {
        var undoEntries = _undoStack.Reverse().ToArray();
        var redoEntries = _redoStack.ToArray();
        var history = new List<UndoHistoryEntry>(undoEntries.Length + redoEntries.Length);

        for (int i = 0; i < undoEntries.Length; i++)
        {
            var state = i == undoEntries.Length - 1 ? "current" : "done";
            history.Add(new UndoHistoryEntry(undoEntries[i].ChangeNumber, state, undoEntries[i].Timestamp));
        }

        foreach (var state in redoEntries)
            history.Add(new UndoHistoryEntry(state.ChangeNumber, "redo", state.Timestamp));

        return history;
    }

    public string FormatUndoList()
    {
        var history = GetHistory();
        if (history.Count == 0) return "undo list is empty";

        var sb = new StringBuilder();
        sb.AppendLine("number  state    time");
        foreach (var entry in history)
        {
            var marker = entry.State == "current" ? '>' : ' ';
            sb.AppendLine($"{marker} {entry.Number,5}  {entry.State,-7}  {entry.Timestamp:HH:mm:ss}");
        }

        return sb.ToString().TrimEnd();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _nextChangeNumber = 0;
    }
}
