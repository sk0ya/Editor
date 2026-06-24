using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Marks;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class LspExCommandTests
{
    private static (ExCommandProcessor Processor, LspServerRegistry Registry) Create()
    {
        var registry = new LspServerRegistry();   // in-memory, isolated from %APPDATA%
        var processor = new ExCommandProcessor(
            new BufferManager(), new VimOptions(), new MarkManager(), lspRegistry: registry);
        return (processor, registry);
    }

    [Fact]
    public void LspList_ShowsBuiltInServers()
    {
        var (processor, _) = Create();

        var result = processor.Execute("LspList", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Contains(".cs", result.Message);
        Assert.Contains("csharp-ls", result.Message);
    }

    [Fact]
    public void LspAdd_RegistersServer()
    {
        var (processor, registry) = Create();

        var result = processor.Execute("LspAdd .zig zls --stdio", CursorPosition.Zero);

        Assert.True(result.Success);
        var def = registry.GetForExtension(".zig");
        Assert.NotNull(def);
        Assert.Equal("zls", def!.Executable);
        Assert.Equal(["--stdio"], def.Args);
    }

    [Fact]
    public void LspAdd_NormalizesBareExtension()
    {
        var (processor, registry) = Create();

        processor.Execute("LspAdd zig zls", CursorPosition.Zero);

        Assert.Equal("zls", registry.GetForExtension(".zig")!.Executable);
    }

    [Fact]
    public void LspAdd_MissingExecutable_Fails()
    {
        var (processor, _) = Create();

        var result = processor.Execute("LspAdd .zig", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Contains("Usage", result.Message);
    }

    [Fact]
    public void LspRemove_HidesBuiltIn()
    {
        var (processor, registry) = Create();

        var result = processor.Execute("LspRemove .cs", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Null(registry.GetForExtension(".cs"));
    }

    [Fact]
    public void LspRemove_Unknown_Fails()
    {
        var (processor, _) = Create();

        var result = processor.Execute("LspRemove .nope", CursorPosition.Zero);

        Assert.False(result.Success);
    }

    [Fact]
    public void LspReset_RestoresBuiltIn()
    {
        var (processor, registry) = Create();
        processor.Execute("LspRemove .cs", CursorPosition.Zero);

        var result = processor.Execute("LspReset .cs", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("csharp-ls", registry.GetForExtension(".cs")!.Executable);
    }
}
