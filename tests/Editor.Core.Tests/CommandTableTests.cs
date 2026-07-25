using Editor.Core.Extensibility;

namespace Editor.Core.Tests;

public class CommandTableTests
{
    [Fact]
    public void HigherLayerWinsRegardlessOfRegistrationOrder()
    {
        var table = new CommandTable<string, string, string>(StringComparer.Ordinal);
        table.RegisterExact("user", "x", _ => "user", CommandLayer.User);
        table.RegisterExact("builtin", "x", _ => "builtin", CommandLayer.BuiltIn);

        var resolved = table.TryResolve("x", "", out var handler);

        Assert.True(resolved);
        Assert.Equal("user", handler(""));
    }

    [Fact]
    public void ExactWinsBeforePatternAtSameLayerAndPriority()
    {
        var table = new CommandTable<string, string, string>();
        table.RegisterPattern("pattern", (key, _) => key.StartsWith("g"), _ => "pattern");
        table.RegisterExact("exact", "gx", _ => "exact");

        table.TryResolve("gx", "", out var handler);

        Assert.Equal("exact", handler(""));
    }

    [Fact]
    public void DisposalRestoresShadowedRegistration()
    {
        var table = new CommandTable<string, string, string>();
        table.RegisterExact("command", "x", _ => "first");
        var replacement = table.RegisterExact(
            "command", "x", _ => "second", priority: 1);
        table.TryResolve("x", "", out var before);

        replacement.Dispose();
        table.TryResolve("x", "", out var after);

        Assert.Equal("second", before(""));
        Assert.Equal("first", after(""));
    }

    [Fact]
    public void SnapshotReportsShadowedExactRegistration()
    {
        var table = new CommandTable<string, string, string>();
        table.RegisterExact("builtin", "x", _ => "builtin", CommandLayer.BuiltIn);
        table.RegisterExact("user", "x", _ => "user", CommandLayer.User);

        Assert.Contains(table.Snapshot.Diagnostics,
            diagnostic => diagnostic.Id == "builtin" && diagnostic.IsUnreachable);
    }
}
