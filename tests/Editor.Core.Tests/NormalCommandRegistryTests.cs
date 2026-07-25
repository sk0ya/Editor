using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Extensibility;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class NormalCommandRegistryTests
{
    [Fact]
    public void RegisteredCommand_OverridesBuiltInDispatch()
    {
        var commands = new NormalCommandRegistry();
        commands.Register(
            new("test.x", ["x"]),
            _ => [VimEvent.StatusMessage("custom x")]);
        var engine = CreateEngine("abc", commands);

        var events = engine.ProcessKey("x");

        Assert.Equal("abc", engine.CurrentBuffer.Text.GetText());
        Assert.Equal("custom x", Assert.IsType<StatusMessageEvent>(events.Single()).Message);
    }

    [Fact]
    public void DisposedReplacement_RestoresPreviousCommand()
    {
        var commands = new NormalCommandRegistry();
        commands.Register(new("test.x", ["x"]),
            _ => [VimEvent.StatusMessage("first")]);
        var replacement = commands.Register(
            new("test.x", ["x"]),
            _ => [VimEvent.StatusMessage("second")],
            RegistrationPolicy.Replace);
        var engine = CreateEngine("abc", commands);

        Assert.Equal("second",
            Assert.IsType<StatusMessageEvent>(engine.ProcessKey("x").Single()).Message);
        replacement.Dispose();
        Assert.Equal("first",
            Assert.IsType<StatusMessageEvent>(engine.ProcessKey("x").Single()).Message);
    }

    [Fact]
    public void UnregisteredCommand_UsesBuiltInDispatcher()
    {
        var engine = CreateEngine("abc", new NormalCommandRegistry());

        engine.ProcessKey("x");

        Assert.Equal("bc", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void MotionBinding_DoesNotInterceptOperatorMotion()
    {
        var commands = new NormalCommandRegistry();
        commands.Register(new("test.w", ["w"]),
            _ => [VimEvent.StatusMessage("custom w")]);
        var engine = CreateEngine("one two", commands);

        engine.ProcessKey("d");
        engine.ProcessKey("w");

        Assert.Equal("two", engine.CurrentBuffer.Text.GetText());
    }

    private static VimEngine CreateEngine(string text, NormalCommandRegistry commands)
    {
        var engine = new VimEngine(new VimConfig(), normalCommands: commands);
        engine.SetText(text);
        return engine;
    }
}
