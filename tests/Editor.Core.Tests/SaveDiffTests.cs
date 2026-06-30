using Editor.Core.Editing;

namespace Editor.Core.Tests;

public class SaveDiffTests
{
    private static Dictionary<int, SaveLineState> Diff(string[] baseline, string[] current)
        => SaveDiff.Compute(baseline, current);

    [Fact]
    public void NoBaseline_ReturnsEmpty()
        => Assert.Empty(Diff([], ["a", "b"]));

    [Fact]
    public void Identical_ReturnsEmpty()
        => Assert.Empty(Diff(["a", "b", "c"], ["a", "b", "c"]));

    [Fact]
    public void ModifiedLine_MarkedModified()
    {
        var d = Diff(["a", "b", "c"], ["a", "B", "c"]);
        Assert.Equal(SaveLineState.Modified, d[1]);
        Assert.Single(d);
    }

    [Fact]
    public void InsertedLines_MarkedAdded()
    {
        var d = Diff(["a", "b"], ["a", "x", "y", "b"]);
        Assert.Equal(SaveLineState.Added, d[1]);
        Assert.Equal(SaveLineState.Added, d[2]);
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void DeletedLines_MarkDeletionBoundary()
    {
        // Remove "b" and "c"; the boundary line is now "d" at index 1.
        var d = Diff(["a", "b", "c", "d"], ["a", "d"]);
        Assert.Equal(SaveLineState.Deleted, d[1]);
        Assert.Single(d);
    }

    [Fact]
    public void DeletionAtEof_ClampedToLastLine()
    {
        var d = Diff(["a", "b", "c"], ["a"]);
        Assert.Equal(SaveLineState.Deleted, d[0]);
    }

    [Fact]
    public void AppendedLines_MarkedAdded()
    {
        var d = Diff(["a"], ["a", "b", "c"]);
        Assert.Equal(SaveLineState.Added, d[1]);
        Assert.Equal(SaveLineState.Added, d[2]);
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void ReplaceBlock_FewerLines_ModifiesThenDeletes()
    {
        // 3 lines replaced by 1 → first replacement is Modified, remaining removal a deletion notch.
        var d = Diff(["a", "1", "2", "3", "z"], ["a", "X", "z"]);
        Assert.Equal(SaveLineState.Modified, d[1]);
        Assert.Equal(SaveLineState.Deleted, d[2]);
    }

    [Fact]
    public void ScatteredEdits_AreReportedIndependently()
    {
        var d = Diff(["a", "b", "c", "d", "e"], ["a", "B", "c", "d", "E"]);
        Assert.Equal(SaveLineState.Modified, d[1]);
        Assert.Equal(SaveLineState.Modified, d[4]);
        Assert.Equal(2, d.Count);
    }
}
