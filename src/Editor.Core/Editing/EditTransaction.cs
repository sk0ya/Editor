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
}

public sealed record EditTransactionResult(
    bool Applied,
    bool TextChanged,
    CursorPosition Cursor,
    object? RepeatMetadata = null);

public sealed record EditTransactionOptions(
    bool CreateUndoSnapshot = true,
    bool AllowCursorAtEndOfLine = false);

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
        if (current.IsBinary)
        {
            emitStatus(events, "E21: Cannot make changes (binary file is read-only)");
            return new EditTransactionResult(false, false, originalCursor);
        }

        var originalLines = current.Text.Snapshot();
        const int eventStart = 0;
        var transaction = new EditTransaction(current.Text, originalCursor);
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

        var changed = !originalLines.SequenceEqual(current.Text.Snapshot());
        var finalCursor = current.Text.ClampCursor(
            transaction.Cursor,
            options?.AllowCursorAtEndOfLine ?? false);
        setCursor(finalCursor);
        if (!changed)
            return new EditTransactionResult(true, false, finalCursor, repeatMetadata);

        if ((options?.CreateUndoSnapshot ?? true) && !suppressSnapshot())
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
