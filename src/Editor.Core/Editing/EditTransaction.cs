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

/// <summary>
/// ホストが文書の一部を守るための判定。編集前と編集後の全行を受け取り、断るなら理由（状態行に出す）、
/// 通すなら null を返す。呼ばれるのは本文が実際に変わった編集トランザクションの終わりだけ。
/// </summary>
public delegate string? HostEditGuard(IReadOnlyList<string> before, IReadOnlyList<string> after);

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
/// <param name="captureGuardRollback">
/// Taken at the start of a transaction only while a host edit guard is set. It records the engine
/// state a refused edit must not leave behind besides the text — registers the command wrote, the
/// Insert mode <c>cc</c> entered — and returns how to put it back (given the events list to report
/// on). The text and the cursor are restored here.
/// </param>
public sealed class EditTransactionService(
    BufferManager buffers,
    MarkManager marks,
    SyntaxEngine syntax,
    Func<CursorPosition> getCursor,
    Action<CursorPosition> setCursor,
    Func<bool> suppressSnapshot,
    Action<List<VimEvent>, string> emitStatus,
    Func<bool>? hostReadOnly = null,
    Func<HostEditGuard?>? hostEditGuard = null,
    Func<Action<List<VimEvent>>>? captureGuardRollback = null) : IEditTransactionService
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
        bool readOnly = hostReadOnly?.Invoke() == true;
        if ((options?.EnforceReadOnly ?? true) && (current.IsBinary || readOnly))
        {
            emitStatus(events, current.IsBinary
                ? "E21: Cannot make changes (binary file is read-only)"
                : "E21: Cannot make changes (read-only)");
            return new EditTransactionResult(false, false, originalCursor);
        }

        var originalLines = current.Text.Snapshot();
        var guard = hostEditGuard?.Invoke();
        var restoreGuarded = guard is not null ? captureGuardRollback?.Invoke() : null;
        var transaction = new EditTransaction(current.Text, originalCursor);
        transaction.CreateUndoSnapshot = options?.CreateUndoSnapshot ?? true;
        object? repeatMetadata;
        var committed = false;
        var batchStart = current.Text.BeginChangeBatch();
        try
        {
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
            var result = Complete(events, current, originalCursor, originalLines, transaction,
                repeatMetadata, readOnly, guard, restoreGuarded, options);
            committed = result.Applied;
            return result;
        }
        finally
        {
            current.Text.EndChangeBatch(batchStart, committed);
        }
    }

    private EditTransactionResult Complete(
        List<VimEvent> events,
        VimBuffer current,
        CursorPosition originalCursor,
        string[] originalLines,
        EditTransaction transaction,
        object? repeatMetadata,
        bool readOnly,
        HostEditGuard? guard,
        Action<List<VimEvent>>? restoreGuarded,
        EditTransactionOptions? options)
    {
        if (transaction.RollbackRequested)
            return Reject(events, current, originalCursor, originalLines, repeatMetadata, status: null);

        // Buffer-navigation Ex commands may switch BufferManager.Current without editing the
        // buffer that opened this transaction. Cursor ownership then belongs to the new buffer.
        if (!ReferenceEquals(buffers.Current, current))
            return new EditTransactionResult(true, false, getCursor(), repeatMetadata);

        var after = current.Text.Snapshot();
        var changed = !originalLines.SequenceEqual(after);

        // ホストの編集ガード（抜粋ビューの見出し行など、文書の一部だけを守りたいとき）。読み取り専用と
        // 同じく「変わっていたら最後に判定して戻す」。差分の通知は確定まで溜めてあるので、断った編集は
        // ホストへ一度も流れない。入口で断れない分、コマンドが本文以外に残したもの（レジスタ・Insert
        // モード）も戻す——戻さないと、断られた dd の行が p で貼れ、cc は守られた行で Insert のまま残る。
        if (changed && guard?.Invoke(originalLines, after) is { } reason)
        {
            restoreGuarded?.Invoke(events);
            return Reject(events, current, originalCursor, originalLines, repeatMetadata, reason);
        }

        // ホストの読み取り専用は、入口で断らない経路（Ex コマンド・挿入の入力）も最後にここで止める。
        // 個々の :s / :d / :normal を洗い出すより、「変わっていたら戻す」一か所の方が漏れない。
        if (changed && readOnly)
            return Reject(events, current, originalCursor, originalLines, repeatMetadata,
                "E21: Cannot make changes (read-only)");

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
            e => e.Type is VimEventType.TextChanged or VimEventType.CursorMoved);
        if (insertionIndex < 0)
            insertionIndex = events.Count;
        RemoveTextAndCursorEvents(events);
        events.Insert(insertionIndex, VimEvent.TextChanged());
        events.Insert(insertionIndex + 1, VimEvent.CursorMoved(finalCursor));
        return new EditTransactionResult(true, true, finalCursor, repeatMetadata);
    }

    /// <summary>Undoes the transaction: text and cursor back, its text/cursor events dropped, and
    /// <paramref name="status"/> (when given) shown as the reason.</summary>
    private EditTransactionResult Reject(
        List<VimEvent> events,
        VimBuffer current,
        CursorPosition originalCursor,
        string[] originalLines,
        object? repeatMetadata,
        string? status)
    {
        current.Text.RestoreSnapshot(originalLines);
        setCursor(originalCursor);
        RemoveTextAndCursorEvents(events);
        if (status is not null)
            emitStatus(events, status);
        return new EditTransactionResult(false, false, originalCursor, repeatMetadata);
    }

    private static void RemoveTextAndCursorEvents(List<VimEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (events[i].Type is VimEventType.TextChanged or VimEventType.CursorMoved)
                events.RemoveAt(i);
    }
}
