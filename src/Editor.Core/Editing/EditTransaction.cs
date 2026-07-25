using Editor.Core.Buffer;
using Editor.Core.Engine;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Core.Editing;

public sealed class EditTransaction(TextBuffer buffer, CursorPosition cursor)
{
    public TextBuffer Buffer { get; } = buffer;
    public CursorPosition Cursor { get; set; } = cursor;
    public bool CreateUndoSnapshot { get; set; } = true;
    public bool RollbackRequested { get; private set; }

    public void Rollback() => RollbackRequested = true;
}

public sealed record EditTransactionResult(
    bool Applied,
    bool TextChanged,
    CursorPosition Cursor,
    object? RepeatMetadata = null);

public sealed record EditTransactionOptions(
    bool CreateUndoSnapshot = true,
    bool AllowCursorAtEndOfLine = false,
    bool EnforceReadOnly = true);

public interface IEditTransactionService
{
    EditTransactionResult Execute(
        List<VimEvent> events,
        Func<EditTransaction, object?> mutation,
        EditTransactionOptions? options = null);
}

/// <summary>
/// Provides the invariant boundary around a single buffer mutation.
/// Low-level mutation code receives a buffer and cursor but cannot snapshot or emit events.
/// </summary>
public sealed class EditTransactionService(
    BufferManager buffers,
    MarkManager marks,
    SyntaxEngine syntax,
    Func<CursorPosition> getCursor,
    Action<CursorPosition> setCursor,
    Func<bool> suppressSnapshot,
    Action<List<VimEvent>, string> emitStatus) : IEditTransactionService
{
    public EditTransactionResult Execute(
        List<VimEvent> events,
        Func<EditTransaction, object?> mutation,
        EditTransactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(mutation);

        var current = buffers.Current;
        var originalCursor = getCursor();
        if ((options?.EnforceReadOnly ?? true) && current.IsBinary)
        {
            emitStatus(events, "E21: Cannot make changes (binary file is read-only)");
            return new EditTransactionResult(false, false, originalCursor);
        }

        var originalLines = current.Text.Snapshot();
        const int eventStart = 0;
        var transaction = new EditTransaction(current.Text, originalCursor);
        transaction.CreateUndoSnapshot = options?.CreateUndoSnapshot ?? true;
        object? repeatMetadata;
        try
        {
            repeatMetadata = mutation(transaction);
        }
        catch
        {
            current.Text.RestoreSnapshot(originalLines);
            setCursor(originalCursor);
            throw;
        }

        if (transaction.RollbackRequested)
        {
            current.Text.RestoreSnapshot(originalLines);
            setCursor(originalCursor);
            for (var i = events.Count - 1; i >= eventStart; i--)
                if (events[i].Type is VimEventType.TextChanged or VimEventType.CursorMoved)
                    events.RemoveAt(i);
            return new EditTransactionResult(false, false, originalCursor, repeatMetadata);
        }

        // Buffer-navigation Ex commands may switch BufferManager.Current without editing the
        // buffer that opened this transaction. Cursor ownership then belongs to the new buffer.
        if (!ReferenceEquals(buffers.Current, current))
            return new EditTransactionResult(true, false, getCursor(), repeatMetadata);

        var changed = !originalLines.SequenceEqual(current.Text.Snapshot());
        var finalCursor = current.Text.ClampCursor(
            transaction.Cursor,
            options?.AllowCursorAtEndOfLine ?? false);
        setCursor(finalCursor);
        if (!changed)
            return new EditTransactionResult(true, false, finalCursor, repeatMetadata);

        if (transaction.CreateUndoSnapshot && !suppressSnapshot())
        {
            current.Undo.Snapshot(originalLines, originalCursor);
            marks.AddChange(originalCursor);
            marks.SetMark('.', originalCursor);
        }

        syntax.Invalidate();
        var insertionIndex = events.FindIndex(
            eventStart,
            e => e.Type is VimEventType.TextChanged or VimEventType.CursorMoved);
        if (insertionIndex < 0)
            insertionIndex = events.Count;
        for (var i = events.Count - 1; i >= eventStart; i--)
            if (events[i].Type is VimEventType.TextChanged or VimEventType.CursorMoved)
                events.RemoveAt(i);
        events.Insert(insertionIndex, VimEvent.TextChanged());
        events.Insert(insertionIndex + 1, VimEvent.CursorMoved(finalCursor));
        return new EditTransactionResult(true, true, finalCursor, repeatMetadata);
    }
}
