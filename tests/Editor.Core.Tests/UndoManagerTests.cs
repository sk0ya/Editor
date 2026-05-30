using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class UndoManagerTests
{
    [Fact]
    public void FormatUndoList_UsesSnapshotNumbersAndClock()
    {
        var timestamps = new Queue<DateTimeOffset>(
        [
            new DateTimeOffset(2026, 5, 30, 12, 0, 1, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 30, 12, 0, 2, TimeSpan.Zero),
        ]);
        var undo = new UndoManager(() => timestamps.Dequeue());
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));

        var output = undo.FormatUndoList();

        Assert.Equal(
            string.Join(Environment.NewLine,
                "number  state    time",
                "      1  done     12:00:01",
                ">     2  current  12:00:02"),
            output);
    }

    [Fact]
    public void GetHistory_MovesCurrentAndRedoEntriesAcrossUndoRedo()
    {
        var nextSecond = 0;
        var undo = new UndoManager(() => new DateTimeOffset(2026, 5, 30, 12, 0, ++nextSecond, TimeSpan.Zero));
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        undo.Snapshot(buffer, new CursorPosition(0, 13));
        buffer.InsertText(0, 13, " four");

        undo.Undo(buffer, new CursorPosition(0, 18));
        undo.Undo(buffer, new CursorPosition(0, 13));

        AssertHistory(
            undo.GetHistory(),
            (1, "current"),
            (2, "redo"),
            (3, "redo"));

        undo.Redo(buffer, new CursorPosition(0, 7));

        AssertHistory(
            undo.GetHistory(),
            (1, "done"),
            (2, "current"),
            (3, "redo"));
    }

    [Fact]
    public void SnapshotAfterUndo_ClearsRedoAndKeepsChangeNumbersMonotonic()
    {
        var nextSecond = 0;
        var undo = new UndoManager(() => new DateTimeOffset(2026, 5, 30, 12, 0, ++nextSecond, TimeSpan.Zero));
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        undo.Undo(buffer, new CursorPosition(0, 13));

        buffer.InsertText(0, 7, " changed");
        undo.Snapshot(buffer, new CursorPosition(0, 15));

        AssertHistory(
            undo.GetHistory(),
            (1, "done"),
            (3, "current"));
    }

    private static void AssertHistory(IReadOnlyList<UndoHistoryEntry> actual, params (int Number, string State)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Number, actual[i].Number);
            Assert.Equal(expected[i].State, actual[i].State);
        }
    }
}
