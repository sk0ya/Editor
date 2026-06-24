using System.IO;
using Editor.Core.Lsp;

namespace Editor.Core.Tests;

public class LspServerRegistryTests
{
    private static string TempStore() =>
        Path.Combine(Path.GetTempPath(), "lsp-reg-test-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void GetForExtension_ReturnsBuiltInDefault()
    {
        var reg = new LspServerRegistry();   // in-memory, built-ins only

        var def = reg.GetForExtension(".cs");

        Assert.NotNull(def);
        Assert.Equal("csharp-ls", def!.Executable);
    }

    [Fact]
    public void GetForExtension_NormalizesExtension()
    {
        var reg = new LspServerRegistry();

        Assert.Equal("csharp-ls", reg.GetForExtension("CS")!.Executable);
        Assert.Equal("csharp-ls", reg.GetForExtension(".CS")!.Executable);
    }

    [Fact]
    public void GetForExtension_UnknownReturnsNull()
    {
        var reg = new LspServerRegistry();

        Assert.Null(reg.GetForExtension(".nope"));
    }

    [Fact]
    public void Set_AddsCustomServer()
    {
        var reg = new LspServerRegistry();

        reg.Set(".zig", new LspServerDef("zls", [], "zig"));

        var def = reg.GetForExtension(".zig");
        Assert.NotNull(def);
        Assert.Equal("zls", def!.Executable);
    }

    [Fact]
    public void Set_ReplacesBuiltIn()
    {
        var reg = new LspServerRegistry();

        reg.Set(".cs", new LspServerDef("OmniSharp", ["-lsp"], "csharp"));

        var def = reg.GetForExtension(".cs");
        Assert.Equal("OmniSharp", def!.Executable);
        Assert.Equal(["-lsp"], def.Args);
        Assert.Contains(reg.List(), e => e.Extension == ".cs" && e.Origin == LspServerOrigin.Custom);
    }

    [Fact]
    public void Remove_HidesBuiltIn()
    {
        var reg = new LspServerRegistry();

        Assert.True(reg.Remove(".cs"));

        Assert.Null(reg.GetForExtension(".cs"));
        Assert.Contains(reg.List(), e => e.Extension == ".cs" && e.Origin == LspServerOrigin.Removed);
    }

    [Fact]
    public void Remove_DropsCustomServer()
    {
        var reg = new LspServerRegistry();
        reg.Set(".zig", new LspServerDef("zls", [], "zig"));

        Assert.True(reg.Remove(".zig"));

        Assert.Null(reg.GetForExtension(".zig"));
        Assert.DoesNotContain(reg.List(), e => e.Extension == ".zig");
    }

    [Fact]
    public void Remove_UnknownReturnsFalse()
    {
        var reg = new LspServerRegistry();

        Assert.False(reg.Remove(".nope"));
    }

    [Fact]
    public void Reset_RestoresHiddenBuiltIn()
    {
        var reg = new LspServerRegistry();
        reg.Remove(".cs");

        Assert.True(reg.Reset(".cs"));

        Assert.Equal("csharp-ls", reg.GetForExtension(".cs")!.Executable);
    }

    [Fact]
    public void Reset_RestoresOverriddenBuiltIn()
    {
        var reg = new LspServerRegistry();
        reg.Set(".cs", new LspServerDef("OmniSharp", [], "csharp"));

        Assert.True(reg.Reset(".cs"));

        Assert.Equal("csharp-ls", reg.GetForExtension(".cs")!.Executable);
    }

    [Fact]
    public void Persistence_RoundTripsOverridesAndRemovals()
    {
        var path = TempStore();
        try
        {
            var reg = new LspServerRegistry(path);
            reg.Set(".zig", new LspServerDef("zls", ["--stdio"], "zig"));
            reg.Remove(".cs");

            // A fresh registry pointed at the same file must see the same state.
            var reloaded = new LspServerRegistry(path);

            Assert.Equal("zls", reloaded.GetForExtension(".zig")!.Executable);
            Assert.Equal(["--stdio"], reloaded.GetForExtension(".zig")!.Args);
            Assert.Null(reloaded.GetForExtension(".cs"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InMemoryRegistry_DoesNotWriteFile()
    {
        // A null store path means no persistence — Set must not throw or create files.
        var reg = new LspServerRegistry();
        reg.Set(".zig", new LspServerDef("zls", [], "zig"));
        Assert.Equal("zls", reg.GetForExtension(".zig")!.Executable);
    }
}
