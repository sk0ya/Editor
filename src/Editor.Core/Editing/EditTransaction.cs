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

public interface IEditTransactionService
{
    EditTransactionResult Execute(
        List<VimEvent> events,
        Func<EditTransaction, object?> mutation);
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
        Func<EditTransaction, object?> mutation)
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
        var finalCursor = current.Text.ClampCursor(transaction.Cursor);
        setCursor(finalCursor);
        if (!changed)
            return new EditTransactionResult(true, false, finalCursor, repeatMetadata);

        if (!suppressSnapshot())
        {
            current.Undo.Snapshot(originalLines, originalCursor);
            marks.AddChange(originalCursor);
            marks.SetMark('.', originalCursor);
        }

        syntax.Invalidate();
        events.RemoveAll(e => e.Type is VimEventType.TextChanged or VimEventType.CursorMoved);
        events.Add(VimEvent.TextChanged());
        events.Add(VimEvent.CursorMoved(finalCursor));
        return new EditTransactionResult(true, true, finalCursor, repeatMetadata);
    }
}
