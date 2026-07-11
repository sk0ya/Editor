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

    [Fact]
    public void EarlierAndLater_ByCount_RestoresTextAndCursor()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");

        var earlier = undo.Earlier(buffer, new CursorPosition(0, 13), 2);

        Assert.Equal(2, earlier.Count);
        Assert.Equal("one", buffer.GetText());
        Assert.Equal(CursorPosition.Zero, earlier.State?.Cursor);

        var later = undo.Later(buffer, CursorPosition.Zero, 1);

        Assert.Equal(1, later.Count);
        Assert.Equal("one two", buffer.GetText());
        Assert.Equal(new CursorPosition(0, 6), later.State?.Cursor);
    }

    [Fact]
    public void EarlierAndLater_ByCount_StopAtBoundaries()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");

        var earlier = undo.Earlier(buffer, new CursorPosition(0, 13), 99);

        Assert.Equal(2, earlier.Count);
        Assert.Equal("one", buffer.GetText());

        var tooEarly = undo.Earlier(buffer, CursorPosition.Zero, 1);
        Assert.Equal(0, tooEarly.Count);
        Assert.Equal("one", buffer.GetText());

        var later = undo.Later(buffer, CursorPosition.Zero, 99);

        Assert.Equal(2, later.Count);
        Assert.Equal("one two three", buffer.GetText());

        var tooLate = undo.Later(buffer, new CursorPosition(0, 13), 1);
        Assert.Equal(0, tooLate.Count);
        Assert.Equal("one two three", buffer.GetText());
    }

    [Fact]
    public void EarlierAndLater_ByTime_UseSnapshotTimestamps()
    {
        var timestamps = new Queue<DateTimeOffset>(
        [
            new DateTimeOffset(2026, 5, 30, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 30, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 30, 12, 3, 0, TimeSpan.Zero),
        ]);
        var undo = new UndoManager(() => timestamps.Dequeue());
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        undo.Snapshot(buffer, new CursorPosition(0, 13));
        buffer.InsertText(0, 13, " four");

        var earlier = undo.Earlier(buffer, new CursorPosition(0, 18), TimeSpan.FromSeconds(90));

        Assert.Equal(1, earlier.Count);
        Assert.Equal("one two three", buffer.GetText());

        var shortLater = undo.Later(buffer, new CursorPosition(0, 13), TimeSpan.FromMinutes(1));
        Assert.Equal(0, shortLater.Count);
        Assert.Equal("one two three", buffer.GetText());

        var later = undo.Later(buffer, new CursorPosition(0, 13), TimeSpan.FromMinutes(2));
        Assert.Equal(1, later.Count);
        Assert.Equal("one two three four", buffer.GetText());
    }

    [Fact]
    public void SnapshotAfterEarlier_ClearsRedoAndKeepsChangeNumbersMonotonic()
    {
        var nextSecond = 0;
        var undo = new UndoManager(() => new DateTimeOffset(2026, 5, 30, 12, 0, ++nextSecond, TimeSpan.Zero));
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");

        undo.Earlier(buffer, new CursorPosition(0, 13), 1);
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " changed");

        Assert.False(undo.CanRedo);
        AssertHistory(
            undo.GetHistory(),
            (1, "done"),
            (3, "current"));
    }

    [Fact]
    public void EditAfterUndo_ArchivesAbandonedRedoBranchInsteadOfDiscardingIt()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);          // change 1: "one"
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));     // change 2: "one two"
        buffer.InsertText(0, 7, " three");
        undo.Snapshot(buffer, new CursorPosition(0, 13));    // change 3: "one two three"
        buffer.InsertText(0, 13, " four");                   // live: "one two three four"

        undo.Undo(buffer, new CursorPosition(0, 18));        // -> "one two three", redo has change-3-tagged entry (live-before-undo)
        undo.Undo(buffer, new CursorPosition(0, 13));        // -> "one two", redo now has 2 entries

        Assert.True(undo.CanRedo);
        Assert.Empty(undo.ListBranches());

        // A brand new edit here abandons the 2-entry redo branch.
        buffer.InsertText(0, 7, " five");
        undo.Snapshot(buffer, new CursorPosition(0, 12));

        var branches = undo.ListBranches();
        Assert.Single(branches);
        Assert.Equal(0, branches[0].Index);
        Assert.Equal(4, branches[0].ForkChangeNumber); // checkpoint naming the exact fork contents
        Assert.Equal(2, branches[0].StateCount);

        // The abandoned branch is not auto-redoable (matches real vim: redo dies after a new edit).
        Assert.False(undo.CanRedo);
        Assert.Null(undo.Redo(buffer, new CursorPosition(0, 12)));
        Assert.Equal("one two five", buffer.GetText());
    }

    [Fact]
    public void SwitchToBranch_SucceedsAtForkPointAndRestoresRedoOrder()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        undo.Snapshot(buffer, new CursorPosition(0, 13));
        buffer.InsertText(0, 13, " four");

        undo.Undo(buffer, new CursorPosition(0, 18));
        undo.Undo(buffer, new CursorPosition(0, 13));

        buffer.InsertText(0, 7, " five");
        undo.Snapshot(buffer, new CursorPosition(0, 12)); // archives the "three"/"four" branch
        buffer.InsertText(0, 12, "!");

        Assert.Equal("one two five!", buffer.GetText());

        // Wrong position: haven't undone back to the fork point yet.
        Assert.False(undo.SwitchToBranch(0, buffer, new CursorPosition(0, 12)));

        undo.Undo(buffer, new CursorPosition(0, 13));

        Assert.True(undo.SwitchToBranch(0, buffer, new CursorPosition(0, 7)));
        // The single pending redo entry we had (from the Undo above) is itself archived as a
        // new sibling branch rather than discarded, so one branch remains after the switch.
        Assert.Single(undo.ListBranches());
        Assert.True(undo.CanRedo);

        var redo1 = undo.Redo(buffer, new CursorPosition(0, 7));
        Assert.Equal("one two three", buffer.GetText());
        var redo2 = undo.Redo(buffer, new CursorPosition(0, 13));
        Assert.Equal("one two three four", buffer.GetText());
        Assert.NotNull(redo1);
        Assert.NotNull(redo2);
    }

    [Fact]
    public void SwitchToBranch_InvalidIndex_ReturnsFalse()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("one");

        Assert.False(undo.SwitchToBranch(0, buffer, CursorPosition.Zero));
    }

    [Fact]
    public void SwitchToBranch_AtCapacity_DoesNotEvictTheBranchBeingSwitchedInto()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("base");
        var cursor = CursorPosition.Zero;

        // Repeatedly checkpoint "base", edit away, then undo straight back to "base" — each
        // cycle's abandoned redo entry gets archived (as a fork@0 branch) at the start of the
        // *next* cycle's Snapshot call. Loop until the 50-branch archive cap has been reached.
        int guard = 0;
        while (undo.ListBranches().Count < 50 && guard++ < 200)
        {
            undo.Snapshot(buffer, cursor);
            buffer.SetText($"edit{guard}");
            undo.Undo(buffer, cursor);
        }

        Assert.Equal(50, undo.ListBranches().Count);
        Assert.True(undo.ListBranches()[0].ForkChangeNumber > 0);
        Assert.True(undo.CanRedo); // the last cycle's Undo left one entry pending, not yet archived

        // Switching into the oldest archived branch (index 0) has to archive that pending redo
        // entry first — which used to evict branch 0 itself via the 50-cap FIFO, since it was
        // the oldest, making the switch spuriously fail.
        Assert.True(undo.SwitchToBranch(undo.ListBranches().Count - 1, buffer, cursor));
    }

    [Fact]
    public void JumpToChangeNumber_FindsStateInUndoStackRedoStackAndArchivedBranch()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("one");

        undo.Snapshot(buffer, CursorPosition.Zero);          // change 1: "one"
        buffer.InsertText(0, 3, " two");
        undo.Snapshot(buffer, new CursorPosition(0, 7));     // change 2: "one two"
        buffer.InsertText(0, 7, " three");
        undo.Snapshot(buffer, new CursorPosition(0, 13));    // change 3: "one two three"
        buffer.InsertText(0, 13, " four");                   // live: "one two three four"

        // Jump into the redo stack (nothing undone yet): change 3's snapshot is on the undo stack;
        // change number to look for the current live state after one undo is 3 (top of undo stack).
        var toChange3 = undo.JumpToChangeNumber(3, buffer, new CursorPosition(0, 18));
        Assert.NotNull(toChange3);
        Assert.Equal("one two three", buffer.GetText());

        // Now the redo stack has one entry tagged with change 3 (the state we just left).
        var backToChange3 = undo.JumpToChangeNumber(3, buffer, new CursorPosition(0, 13));
        Assert.NotNull(backToChange3);
        Assert.Equal("one two three four", buffer.GetText());

        // Jump back into the undo-stack ancestry (change 1).
        var toChange1 = undo.JumpToChangeNumber(1, buffer, new CursorPosition(0, 18));
        Assert.NotNull(toChange1);
        Assert.Equal("one", buffer.GetText());

        // Redo back to the live edit, then create a fresh edit to archive the abandoned branch.
        undo.Later(buffer, new CursorPosition(0, 3), 99);
        Assert.Equal("one two three four", buffer.GetText());

        undo.Undo(buffer, new CursorPosition(0, 18));        // -> "one two three"
        undo.Undo(buffer, new CursorPosition(0, 13));        // -> "one two"
        buffer.InsertText(0, 7, " five");
        undo.Snapshot(buffer, new CursorPosition(0, 12));    // archives change 2/3 redo branch

        Assert.Single(undo.ListBranches());

        // Position back at the branch's fork point, then jump into the branch.
        undo.Undo(buffer, new CursorPosition(0, 12));

        var intoBranch = undo.JumpToChangeNumber(3, buffer, new CursorPosition(0, 7));
        Assert.NotNull(intoBranch);
        Assert.Equal(3, intoBranch!.State?.ChangeNumber);
        // The single pending redo left behind by the Undo above becomes its own sibling branch.
        Assert.Single(undo.ListBranches());

        Assert.Null(undo.JumpToChangeNumber(9999, buffer, new CursorPosition(0, 13)));
    }

    [Fact]
    public void JumpToChangeNumber_Zero_OnFreshBuffer_ReportsNoChangesInsteadOfFakeSuccess()
    {
        var undo = new UndoManager();
        var buffer = new TextBuffer("one");

        var result = undo.JumpToChangeNumber(0, buffer, CursorPosition.Zero);
        Assert.NotNull(result);
        Assert.Equal(0, result!.Count);
        Assert.Null(result.State);
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
