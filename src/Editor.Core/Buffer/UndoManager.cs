using Editor.Core.Models;
using System.Linq;
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

/// <summary>
/// A branch of history that was abandoned when a new edit was made after undoing.
/// <see cref="ForkChangeNumber"/> is the change number at the top of the undo stack
/// at the moment the branch split off (0 if the undo stack was empty then).
/// </summary>
public record UndoBranch(int Index, int ForkChangeNumber, DateTimeOffset ArchivedAt, int StateCount);

public class UndoManager
{
    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();
    private readonly List<ArchivedBranch> _archivedBranches = new();
    private readonly Func<DateTimeOffset> _clock;
    private const int MaxHistory = 1000;
    private const int MaxArchivedBranches = 50;
    private int _nextChangeNumber;

    private record ArchivedBranch(int ForkChangeNumber, DateTimeOffset ArchivedAt, List<UndoState> States);

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
        if (_redoStack.Count > 0)
            ArchiveRedoStack();
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

    // Preserves an abandoned "future" (the redo stack) instead of discarding it, so it
    // can later be rediscovered via ListBranches/SwitchToBranch/JumpToChangeNumber.
    private void ArchiveRedoStack()
    {
        var forkChangeNumber = _undoStack.Count > 0 ? _undoStack.Peek().ChangeNumber : 0;
        _archivedBranches.Add(new ArchivedBranch(forkChangeNumber, _clock(), _redoStack.ToList()));
        if (_archivedBranches.Count > MaxArchivedBranches)
            _archivedBranches.RemoveAt(0);
        _redoStack.Clear();
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
        var sb = new StringBuilder();

        if (history.Count == 0)
        {
            sb.Append("undo list is empty");
        }
        else
        {
            sb.AppendLine("number  state    time");
            foreach (var entry in history)
            {
                var marker = entry.State == "current" ? '>' : ' ';
                sb.AppendLine($"{marker} {entry.Number,5}  {entry.State,-7}  {entry.Timestamp:HH:mm:ss}");
            }
        }

        if (_archivedBranches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("branches:");
            for (int i = 0; i < _archivedBranches.Count; i++)
            {
                var branch = _archivedBranches[i];
                var numbers = branch.States.Select(s => s.ChangeNumber).ToList();
                var range = numbers.Min() == numbers.Max() ? $"{numbers.Min()}" : $"{numbers.Min()}-{numbers.Max()}";
                sb.AppendLine($"  [{i}] fork@{branch.ForkChangeNumber}  changes {range}  archived {branch.ArchivedAt:HH:mm:ss}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Archived branches abandoned by editing after an undo, oldest first.</summary>
    public IReadOnlyList<UndoBranch> ListBranches()
    {
        var list = new List<UndoBranch>(_archivedBranches.Count);
        for (int i = 0; i < _archivedBranches.Count; i++)
        {
            var branch = _archivedBranches[i];
            list.Add(new UndoBranch(i, branch.ForkChangeNumber, branch.ArchivedAt, branch.States.Count));
        }
        return list;
    }

    /// <summary>
    /// Restores an archived branch's states into the redo stack, so <see cref="Redo"/> can walk
    /// forward into it again. Only valid when currently sitting at exactly the branch's fork point
    /// (undo stack top — or "fully undone" for fork 0 — matches the fork change number). Anything
    /// already pending in the redo stack at that moment is itself archived first (it is a sibling
    /// of the branch being switched into, not discarded) rather than blocking the switch.
    /// </summary>
    public bool SwitchToBranch(int index, TextBuffer buffer, CursorPosition currentCursor)
    {
        if (index < 0 || index >= _archivedBranches.Count) return false;

        var branch = _archivedBranches[index];
        var forkPoint = _undoStack.Count > 0 ? _undoStack.Peek().ChangeNumber : 0;
        if (forkPoint != branch.ForkChangeNumber) return false;

        // Remove the target branch before archiving any pending redo stack, so the archive-cap
        // eviction below (which only ever drops the oldest entry) can never evict the very
        // branch we just chose to switch into.
        _archivedBranches.RemoveAt(index);

        if (_redoStack.Count > 0)
            ArchiveRedoStack();

        foreach (var state in Enumerable.Reverse(branch.States))
            _redoStack.Push(state);
        return true;
    }

    /// <summary>
    /// Jumps directly to the state tagged with <paramref name="changeNumber"/>, searching the
    /// current undo stack, the current redo stack, and (if reachable) archived branches, in that
    /// order. Returns null if the change number cannot be found or cannot be cleanly reached.
    /// </summary>
    public UndoTraversalResult? JumpToChangeNumber(int changeNumber, TextBuffer buffer, CursorPosition currentCursor)
    {
        if (changeNumber == 0)
        {
            var cursor = currentCursor;
            UndoState? last = null;
            var count = 0;
            while (CanUndo)
            {
                last = Undo(buffer, cursor);
                if (last == null) break;
                cursor = buffer.ClampCursor(last.Cursor);
                count++;
            }
            return new UndoTraversalResult(count, last);
        }

        if (_undoStack.Any(s => s.ChangeNumber == changeNumber))
            return UndoUntil(buffer, currentCursor, changeNumber);

        if (_redoStack.Any(s => s.ChangeNumber == changeNumber))
            return RedoUntil(buffer, currentCursor, changeNumber);

        for (int i = 0; i < _archivedBranches.Count; i++)
        {
            var branch = _archivedBranches[i];
            if (!branch.States.Any(s => s.ChangeNumber == changeNumber)) continue;

            // Only support the simple case: we can reach this branch's exact fork point by
            // walking the stacks we already have (no rewriting of unrelated branches).
            var cursor = currentCursor;
            if (!TryReachForkPoint(branch.ForkChangeNumber, buffer, ref cursor)) return null;
            if (!SwitchToBranch(i, buffer, cursor)) return null;

            var result = RedoUntil(buffer, cursor, changeNumber);
            return result.State != null && result.State.ChangeNumber == changeNumber ? result : null;
        }

        return null;
    }

    private bool TryReachForkPoint(int forkChangeNumber, TextBuffer buffer, ref CursorPosition cursor)
    {
        bool AtFork() => forkChangeNumber == 0
            ? _undoStack.Count == 0
            : _undoStack.Count > 0 && _undoStack.Peek().ChangeNumber == forkChangeNumber;

        if (AtFork()) return true;

        if (forkChangeNumber == 0 || _undoStack.Any(s => s.ChangeNumber == forkChangeNumber))
        {
            while (CanUndo && !AtFork())
            {
                var state = Undo(buffer, cursor);
                if (state == null) break;
                cursor = buffer.ClampCursor(state.Cursor);
            }
        }
        else if (_redoStack.Any(s => s.ChangeNumber == forkChangeNumber))
        {
            while (CanRedo && !AtFork())
            {
                var state = Redo(buffer, cursor);
                if (state == null) break;
                cursor = buffer.ClampCursor(state.Cursor);
            }
        }

        return AtFork();
    }

    private UndoTraversalResult UndoUntil(TextBuffer buffer, CursorPosition currentCursor, int changeNumber)
    {
        var cursor = currentCursor;
        UndoState? last = null;
        var count = 0;
        while (CanUndo && (last == null || last.ChangeNumber != changeNumber))
        {
            last = Undo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }
        return new UndoTraversalResult(count, last);
    }

    private UndoTraversalResult RedoUntil(TextBuffer buffer, CursorPosition currentCursor, int changeNumber)
    {
        var cursor = currentCursor;
        UndoState? last = null;
        var count = 0;
        while (CanRedo && (last == null || last.ChangeNumber != changeNumber))
        {
            last = Redo(buffer, cursor);
            if (last == null) break;
            cursor = buffer.ClampCursor(last.Cursor);
            count++;
        }
        return new UndoTraversalResult(count, last);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _archivedBranches.Clear();
        _nextChangeNumber = 0;
    }
}
