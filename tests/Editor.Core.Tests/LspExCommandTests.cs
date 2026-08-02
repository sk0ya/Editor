using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Lsp;
using Editor.Core.Marks;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class LspExCommandTests
{
    /// <summary>
    /// Stand-in for the host's extension→server table. The editor no longer owns one, so these tests
    /// verify only that the ex commands are a faithful frontend onto whatever table was injected.
    /// </summary>
    private sealed class FakeLspServerAdmin : ILspServerAdmin
    {
        private static readonly Dictionary<string, LspServerDef> Builtins =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { ".cs", new LspServerDef("csharp-ls", [], "csharp") },
            };

        private readonly Dictionary<string, LspServerDef> _overrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<LspServerEntry> List()
        {
            var rows = new Dictionary<string, LspServerEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ext, def) in Builtins)
                rows[ext] = new LspServerEntry(ext, def,
                    _removed.Contains(ext) ? LspServerOrigin.Removed : LspServerOrigin.BuiltIn);
            foreach (var (ext, def) in _overrides)
                rows[ext] = new LspServerEntry(ext, def, LspServerOrigin.Custom);
            return rows.Values.OrderBy(e => e.Extension, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public LspServerDef? GetForExtension(string extension)
        {
            var ext = LspExtensions.NormalizeExt(extension);
            if (_overrides.TryGetValue(ext, out var def)) return def;
            if (_removed.Contains(ext)) return null;
            return Builtins.GetValueOrDefault(ext);
        }

        public void Set(string extension, LspServerDef def)
        {
            var ext = LspExtensions.NormalizeExt(extension);
            _overrides[ext] = def;
            _removed.Remove(ext);
        }

        public bool Remove(string extension)
        {
            var ext = LspExtensions.NormalizeExt(extension);
            bool changed = _overrides.Remove(ext);
            if (Builtins.ContainsKey(ext) && _removed.Add(ext)) changed = true;
            return changed;
        }

        public bool Reset(string extension)
        {
            var ext = LspExtensions.NormalizeExt(extension);
            return _overrides.Remove(ext) | _removed.Remove(ext);
        }
    }

    private static (ExCommandProcessor Processor, FakeLspServerAdmin Admin) Create()
    {
        var admin = new FakeLspServerAdmin();
        var processor = new ExCommandProcessor(
            new BufferManager(), new VimOptions(), new MarkManager(), lspServerAdmin: admin);
        return (processor, admin);
    }

    private static ExCommandProcessor CreateWithoutAdmin() =>
        new(new BufferManager(), new VimOptions(), new MarkManager());

    [Theory]
    [InlineData("CodeAction")]
    [InlineData("codeaction")]
    [InlineData("CodeActions")]
    public void CodeAction_RequestsTheSameEventAsGa(string command)
    {
        var (processor, _) = Create();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal(VimEventType.CodeActionRequested, result.Event?.Type);
    }

    [Fact]
    public void CodeAction_BothSpellingsComplete()
    {
        var (processor, _) = Create();

        var completions = processor.GetCompletions("Code");

        Assert.Contains("CodeAction", completions);
        Assert.Contains("CodeActions", completions);
    }

    [Fact]
    public void LspList_ShowsTheInjectedTable()
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
        var (processor, admin) = Create();

        var result = processor.Execute("LspAdd .zig zls --stdio", CursorPosition.Zero);

        Assert.True(result.Success);
        var def = admin.GetForExtension(".zig");
        Assert.NotNull(def);
        Assert.Equal("zls", def!.Executable);
        Assert.Equal(["--stdio"], def.Args);
    }

    [Fact]
    public void LspAdd_NormalizesBareExtension()
    {
        var (processor, admin) = Create();

        processor.Execute("LspAdd zig zls", CursorPosition.Zero);

        Assert.Equal("zls", admin.GetForExtension(".zig")!.Executable);
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
        var (processor, admin) = Create();

        var result = processor.Execute("LspRemove .cs", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Null(admin.GetForExtension(".cs"));
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
        var (processor, admin) = Create();
        processor.Execute("LspRemove .cs", CursorPosition.Zero);

        var result = processor.Execute("LspReset .cs", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("csharp-ls", admin.GetForExtension(".cs")!.Executable);
    }

    // The whole point of routing through ILspServerAdmin: with no host table injected the commands
    // must say so, not quietly write into a private table nobody reads (the old LspServerRegistry
    // failure mode — see docs §30.2.1 in the Loomo design docs).
    [Theory]
    [InlineData("LspList")]
    [InlineData("LspAdd .zig zls")]
    [InlineData("LspRemove .cs")]
    [InlineData("LspReset .cs")]
    public void LspCommands_WithoutHostTable_ReportUnavailable(string command)
    {
        var processor = CreateWithoutAdmin();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.Contains("not available", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
