using Editor.Core.Buffer;
using Editor.Core.Engine;
using Editor.Core.Extensibility;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Core.Tests;

public class ExtensibilityTests
{
    private sealed class TestLanguage(string name, string extension) : ISyntaxLanguage
    {
        public string Name => name;
        public string[] Extensions => [extension];
        public string? LineCommentPrefix => "--";
        public LineTokens[] Tokenize(string[] lines) => [new(0, [new(0, lines[0].Length, TokenKind.Keyword)])];
    }

    [Fact]
    public void SyntaxRegistration_IsVisibleAndDisposalRemovesIt()
    {
        var registry = new SyntaxLanguageRegistry();
        var engine = new SyntaxEngine(registry);
        using (registry.Register(new("Acme", [".acme"]), () => new TestLanguage("Acme", ".acme")))
        {
            engine.DetectLanguage("file.ACME");
            Assert.Equal("Acme", engine.LanguageName);
            Assert.Single(engine.Tokenize(["hello"]));
        }
        engine.DetectLanguage("file.acme");
        Assert.Null(engine.LanguageName);
    }

    [Fact]
    public void SyntaxReplace_DisposalRestoresPreviousRegistration()
    {
        var registry = new SyntaxLanguageRegistry();
        using var original = registry.Register(new("Acme", [".one"]), () => new TestLanguage("Acme", ".one"));
        using (registry.Register(new("Acme", [".two"]), () => new TestLanguage("Acme", ".two"), RegistrationPolicy.Replace))
            Assert.Equal(".two", registry.Languages.Single().Extensions[0]);
        Assert.Equal(".one", registry.Languages.Single().Extensions[0]);
    }

    [Fact]
    public void DuplicateRegistrations_AreRejectedWithoutPartialAliases()
    {
        var registry = new EditorCommandRegistry();
        using var first = registry.Register(new("one", Aliases: ["occupied"]), _ => EditorCommandResult.Ok());
        Assert.Throws<InvalidOperationException>(() => registry.Register(new("two", Aliases: ["occupied", "free"]), _ => EditorCommandResult.Ok()));
        Assert.Single(registry.Commands);
    }

    [Fact]
    public async Task CommandRegistration_SupportsAliasesArgumentsContextAndAsync()
    {
        var registry = new EditorCommandRegistry();
        EditorCommandContext? seen = null;
        using var registration = registry.RegisterAsync(new("greet", "Greet", Aliases: ["hi"]), async (context, token) =>
        {
            await Task.Yield(); token.ThrowIfCancellationRequested(); seen = context;
            return EditorCommandResult.Ok("hello " + context.Arguments);
        });
        var result = await registry.ExecuteAsync(":hi world", new CursorPosition(2, 3));
        Assert.True(result!.Success); Assert.Equal("hello world", result.Message);
        Assert.Equal("hi", seen!.Name); Assert.Equal(new CursorPosition(2, 3), seen.Cursor);
        registration.Dispose();
        Assert.Null(await registry.ExecuteAsync("hi world", new CursorPosition()));
    }

    [Fact]
    public void ExProcessor_ExecutesSynchronousExtensionCommand()
    {
        var registry = new EditorCommandRegistry();
        using var registration = registry.Register(new("hello"), context => EditorCommandResult.Ok(context.Arguments.ToUpperInvariant()));
        var processor = new ExCommandProcessor(new BufferManager(), new(), new MarkManager(), commandRegistry: registry);
        var result = processor.Execute("hello loomo", new CursorPosition());
        Assert.True(result.Success); Assert.Equal("LOOMO", result.Message);
    }

    [Fact]
    public async Task ReplaceCommand_DisposalRestoresPreviousHandler()
    {
        var registry = new EditorCommandRegistry();
        using var original = registry.Register(new("x", Aliases: ["old"]), _ => EditorCommandResult.Ok("one"));
        using (registry.Register(new("x", Aliases: ["new"]), _ => EditorCommandResult.Ok("two"), RegistrationPolicy.Replace))
        {
            Assert.Equal("two", (await registry.ExecuteAsync("x", new()))!.Message);
            Assert.Equal("two", (await registry.ExecuteAsync("old", new()))!.Message);
        }
        Assert.Equal("one", (await registry.ExecuteAsync("x", new()))!.Message);
        Assert.Equal("one", (await registry.ExecuteAsync("old", new()))!.Message);
        Assert.Null(await registry.ExecuteAsync("new", new()));
    }

    [Fact]
    public void AsyncCommand_ExProbeHasNoSideEffects()
    {
        var calls = 0; var registry = new EditorCommandRegistry();
        using var r = registry.RegisterAsync(new("slow"), async (_, _) => { calls++; await Task.Yield(); return EditorCommandResult.Ok(); });
        var processor = new ExCommandProcessor(new BufferManager(), new(), new MarkManager(), commandRegistry: registry);
        Assert.False(processor.Execute("slow", new()).Success); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AsyncRawExPath_ParsesRangeTabsAndPreservesRawInput()
    {
        EditorCommandContext? seen = null; var registry = new EditorCommandRegistry();
        using var r = registry.RegisterAsync(new("async-host"), async (context, token) =>
        {
            await Task.Yield(); token.ThrowIfCancellationRequested(); seen = context; return EditorCommandResult.Ok("done");
        });
        var services = new TestServices();
        var processor = new ExCommandProcessor(new BufferManager(), new(), new MarkManager(), commandRegistry: registry, services: services);
        const string raw = "  2,5async-host\t alpha beta  ";
        var result = await processor.ExecuteExtensionAsync(raw, new(4, 7));
        Assert.Equal("done", result!.Message); Assert.Equal(raw, seen!.RawCommand); Assert.Equal("2,5", seen.Range);
        Assert.Equal("alpha beta", seen.Arguments); Assert.Equal(new CursorPosition(4, 7), seen.Cursor); Assert.Same(services, seen.Services);
    }

    [Fact]
    public void ExContext_PreservesRawRangeAndTabArguments()
    {
        EditorCommandContext? seen = null; var registry = new EditorCommandRegistry();
        using var r = registry.Register(new("custom"), c => { seen = c; return EditorCommandResult.Ok(); });
        var processor = new ExCommandProcessor(new BufferManager(), new(), new MarkManager(), commandRegistry: registry);
        processor.Execute("  1,3custom\t alpha beta  ", new());
        Assert.Equal("  1,3custom\t alpha beta  ", seen!.RawCommand); Assert.Equal("1,3", seen.Range); Assert.Equal("alpha beta", seen.Arguments);
    }

    [Fact]
    public void SyntaxFactory_IsolatesStateBetweenEngines()
    {
        var registry = new SyntaxLanguageRegistry(); var created = 0;
        using var r = registry.Register(new("Acme", [".a"]), () => { created++; return new TestLanguage("Acme", ".a"); });
        new SyntaxEngine(registry).SetLanguage("Acme"); new SyntaxEngine(registry).SetLanguage("Acme");
        Assert.Equal(2, created);
    }

    [Fact]
    public void VimEngine_UsesInjectedRegistriesAndServices()
    {
        var commands = new EditorCommandRegistry(); var service = new TestServices();
        using var r = commands.Register(new("host"), c => EditorCommandResult.Ok(ReferenceEquals(service, c.Services) ? "yes" : "no"));
        var engine = new VimEngine(commands: commands, services: service);
        Assert.Equal("yes", engine.ExProcessor.Execute("host", new()).Message);
    }
    private sealed class TestServices : IServiceProvider { public object? GetService(Type serviceType) => null; }

    [Fact]
    public async Task ConcurrentSnapshotsExecutionAndDispose_AreSafe()
    {
        var registry = new EditorCommandRegistry(); var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var tasks = Enumerable.Range(0, 8).Select(async worker =>
        {
            for (var i = 0; i < 100; i++) try
            {
                using var r = registry.Register(new($"c{worker}_{i}"), _ => EditorCommandResult.Ok());
                _ = registry.Commands;
                Assert.True((await registry.ExecuteAsync($"c{worker}_{i}", new()))!.Success);
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });
        await Task.WhenAll(tasks); Assert.Empty(errors); Assert.Empty(registry.Commands);
    }
}
