using System.Text.Json.Nodes;
using Editor.Core.Buffer;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class UndoPersistenceTests
{
    [Fact]
    public void ExportImport_RoundTripsLinearAndBranchingHistory()
    {
        var buffer = new TextBuffer("one");
        var original = new UndoManager();
        original.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        original.Snapshot(buffer, new(0, 7));
        buffer.InsertText(0, 7, " old");
        original.Undo(buffer, new(0, 11));
        original.Snapshot(buffer, new(0, 7)); // archives old future before the new edit
        buffer.InsertText(0, 7, " new");

        var restored = new UndoManager();
        var result = restored.ImportHistory(original.ExportHistory());

        Assert.True(result.Success, result.Error);
        Assert.Single(restored.ListBranches());
        Assert.Equal(original.GetHistory(), restored.GetHistory());

        restored.Undo(buffer, new(0, 11));
        Assert.Equal("one two", buffer.GetText());
        Assert.True(restored.SwitchToBranch(0, buffer, new(0, 7)));
        restored.Redo(buffer, new(0, 7));
        Assert.Equal("one two old", buffer.GetText());
    }

    [Fact]
    public void SaveLoad_RoundTripsAndReplacesExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"editor-undo-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "old");
            var source = new UndoManager();
            source.Snapshot(new[] { "hello" }, new(0, 5));
            source.SaveHistory(path);
            var target = new UndoManager();
            var result = target.LoadHistory(path);
            Assert.True(result.Success, result.Error);
            Assert.Single(target.GetHistory());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_InvalidDocument_DoesNotMutateExistingHistory()
    {
        var undo = new UndoManager();
        undo.Snapshot(new[] { "safe" }, CursorPosition.Zero);
        var json = JsonNode.Parse(undo.ExportHistory())!.AsObject();
        json["version"] = 999;

        var result = undo.ImportHistory(json.ToJsonString());

        Assert.False(result.Success);
        Assert.Single(undo.GetHistory());
    }

    [Fact]
    public void Import_RejectsInvalidCursorAndResourceLimit()
    {
        var source = new UndoManager();
        source.Snapshot(new[] { "hello" }, CursorPosition.Zero);
        var json = JsonNode.Parse(source.ExportHistory())!.AsObject();
        json["undo"]![0]!["cursor"]!["line"] = 10;

        Assert.False(new UndoManager().ImportHistory(json.ToJsonString()).Success);
        Assert.False(new UndoManager().ImportHistory(source.ExportHistory(),
            new UndoPersistenceLimits(MaxStates: 0)).Success);
    }

    [Fact]
    public void Import_PreservesMonotonicChangeNumbers()
    {
        var source = new UndoManager();
        source.Snapshot(new[] { "a" }, CursorPosition.Zero);
        var target = new UndoManager();
        Assert.True(target.ImportHistory(source.ExportHistory()).Success);
        target.Snapshot(new[] { "b" }, CursorPosition.Zero);
        Assert.Equal(2, target.GetHistory().Last().Number);
    }

    [Fact]
    public void BoundImport_RejectsWrongIdentityAndChangedContentsWithoutMutation()
    {
        var buffer = new TextBuffer("same revision");
        var source = new UndoManager();
        source.Snapshot(buffer, CursorPosition.Zero);
        var json = source.ExportHistory(buffer, "document-a");
        var target = new UndoManager();
        target.Snapshot(new[] { "keep" }, CursorPosition.Zero);

        Assert.False(target.ImportHistory(json, buffer, "document-b").Success);
        buffer.InsertText(0, 0, "changed ");
        Assert.False(target.ImportHistory(json, buffer, "document-a").Success);
        Assert.Single(target.GetHistory());
        Assert.Equal("keep", target.GetHistory().Single().Number == 1 ? "keep" : "bad");
    }

    [Fact]
    public void BoundImport_MatchingIdentityAndRevisionSucceeds()
    {
        var buffer = new TextBuffer("revision");
        var source = new UndoManager();
        source.Snapshot(buffer, CursorPosition.Zero);
        var target = new UndoManager();
        var result = target.ImportHistory(source.ExportHistory(buffer, "doc"), buffer, "doc");
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void VimBufferLifecycle_BindsSidecarToFileAndContents()
    {
        var file = Path.Combine(Path.GetTempPath(), $"editor-{Guid.NewGuid():N}.txt");
        var sidecar = file + ".undo";
        try
        {
            File.WriteAllText(file, "hello");
            var source = new VimBuffer(file);
            source.Undo.Snapshot(source.Text, CursorPosition.Zero);
            source.SaveUndoHistory(sidecar);
            var matching = new VimBuffer(file);
            Assert.True(matching.LoadUndoHistory(sidecar).Success);
            matching.Text.InsertText(0, 0, "x");
            Assert.False(matching.LoadUndoHistory(sidecar).Success);
        }
        finally { File.Delete(file); File.Delete(sidecar); }
    }

    [Fact]
    public void GetTree_ReportsArchivedBranchParentAtFork()
    {
        var buffer = new TextBuffer("a");
        var undo = new UndoManager();
        undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 1, "b");
        undo.Snapshot(buffer, new(0, 2));
        buffer.InsertText(0, 2, "c");
        undo.Undo(buffer, new(0, 3));
        undo.Snapshot(buffer, new(0, 2));
        var tree = undo.GetTree();
        var archived = Assert.Single(tree.Nodes, n => n.Location == "archived");
        Assert.Equal(3, archived.ParentChangeNumber);
        Assert.Equal(0, archived.BranchIndex);
    }

    [Fact]
    public void Import_RejectsDuplicateAndOutOfOrderStackIds()
    {
        var source = new UndoManager();
        source.Snapshot(new[] { "a" }, CursorPosition.Zero);
        source.Snapshot(new[] { "b" }, CursorPosition.Zero);
        var json = JsonNode.Parse(source.ExportHistory())!.AsObject();
        json["undo"]![1]!["changeNumber"] = 2;
        Assert.False(new UndoManager().ImportHistory(json.ToJsonString()).Success);
    }

    [Fact]
    public void BoundRoundTrip_AfterMultipleUndo_PreservesRedoTopFirstOrder()
    {
        var buffer = new TextBuffer("a");
        var source = new UndoManager();
        source.Snapshot(buffer, new(0, 1));
        buffer.InsertText(0, 1, "b");
        source.Snapshot(buffer, new(0, 2));
        buffer.InsertText(0, 2, "c");
        source.Snapshot(buffer, new(0, 3));
        buffer.InsertText(0, 3, "d");
        source.Undo(buffer, new(0, 4));
        source.Undo(buffer, new(0, 3));
        Assert.Equal("ab", buffer.GetText());

        var restored = new UndoManager();
        var result = restored.ImportHistory(source.ExportHistory(buffer, "multi"), buffer, "multi");
        Assert.True(result.Success, result.Error);
        restored.Redo(buffer, new(0, 2));
        Assert.Equal("abc", buffer.GetText());
        restored.Redo(buffer, new(0, 3));
        Assert.Equal("abcd", buffer.GetText());
    }

    [Fact]
    public void Import_CorruptCurrentState_IsRejectedWithoutMutation()
    {
        var buffer = new TextBuffer("source");
        var source = new UndoManager();
        source.Snapshot(buffer, CursorPosition.Zero);
        var json = JsonNode.Parse(source.ExportHistory(buffer, "doc"))!.AsObject();
        json["currentState"]!["lines"] = new JsonArray();
        var target = new UndoManager();
        target.Snapshot(new[] { "preserved" }, CursorPosition.Zero);

        var result = target.ImportHistory(json.ToJsonString(), buffer, "doc");

        Assert.False(result.Success);
        Assert.Single(target.GetHistory());
    }

    [Fact]
    public void BoundImport_RejectsCurrentStateLinesEvenWhenStoredHashMatchesBuffer()
    {
        var buffer = new TextBuffer("actual");
        var source = new UndoManager();
        source.Snapshot(buffer, CursorPosition.Zero);
        var json = JsonNode.Parse(source.ExportHistory(buffer, "doc"))!.AsObject();
        json["currentState"]!["lines"]![0] = "other"; // leave currentContentHash untouched
        var target = new UndoManager();

        var result = target.ImportHistory(json.ToJsonString(), buffer, "doc");

        Assert.False(result.Success);
        Assert.Empty(target.GetHistory());
    }
}
