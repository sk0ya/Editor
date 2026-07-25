using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Extensibility;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class VimKeyBindingRegistryTests
{
    [Fact]
    public void RegisteredBinding_OverridesBuiltInKey()
    {
        var bindings = new VimKeyBindingRegistry();
        bindings.Register(
            new("test.noop-x", VimModeSet.Normal, "x"),
            _ => [VimEvent.StatusMessage("custom")]);
        var engine = CreateEngine("abc", bindings);

        var events = engine.ProcessKey("x");

        Assert.Equal("abc", engine.CurrentBuffer.Text.GetText());
        Assert.Contains(events, e => e.Type == VimEventType.StatusMessage);
    }

    [Fact]
    public void MultiStrokeBinding_WaitsForCompleteSequence()
    {
        var bindings = new VimKeyBindingRegistry();
        bindings.Register(
            new("test.go", VimModeSet.Normal, "qq"),
            context =>
            {
                context.Engine.SetText("handled");
                return Array.Empty<VimEvent>();
            });
        var engine = CreateEngine("one\ntwo", bindings);

        var first = engine.ProcessKey("q");
        Assert.Empty(first);
        Assert.Equal("one\ntwo", engine.CurrentBuffer.Text.GetText());

        engine.ProcessKey("q");
        Assert.Equal("handled", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Binding_IsScopedToConfiguredMode()
    {
        var bindings = new VimKeyBindingRegistry();
        bindings.Register(
            new("test.insert-j", VimModeSet.Insert, "j"),
            _ => [VimEvent.StatusMessage("handled")]);
        var engine = CreateEngine("", bindings);

        engine.ProcessKey("j");
        Assert.Equal(CursorPosition.Zero, engine.Cursor);

        engine.ProcessKey("i");
        engine.ProcessKey("j");
        Assert.Equal("", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void DisposingReplacement_RestoresPreviousBinding()
    {
        var bindings = new VimKeyBindingRegistry();
        bindings.Register(new("test.binding", VimModeSet.Normal, "x"),
            _ => [VimEvent.StatusMessage("first")]);
        var replacement = bindings.Register(
            new("test.binding", VimModeSet.Normal, "x"),
            _ => [VimEvent.StatusMessage("second")],
            RegistrationPolicy.Replace);
        var engine = CreateEngine("abc", bindings);

        Assert.Equal("second", Assert.IsType<StatusMessageEvent>(engine.ProcessKey("x").Single()).Message);
        replacement.Dispose();
        Assert.Equal("first", Assert.IsType<StatusMessageEvent>(engine.ProcessKey("x").Single()).Message);
    }

    [Fact]
    public void NewerDistinctBinding_WinsSharedKeyAndReportsShadowing()
    {
        var bindings = new VimKeyBindingRegistry();
        bindings.Register(new("test.first", VimModeSet.Normal, "x"),
            _ => [VimEvent.StatusMessage("first")]);
        bindings.Register(new("test.second", VimModeSet.Normal, "x"),
            _ => [VimEvent.StatusMessage("second")]);
        var engine = CreateEngine("abc", bindings);

        Assert.Equal("second",
            Assert.IsType<StatusMessageEvent>(engine.ProcessKey("x").Single()).Message);
        Assert.Contains(bindings.Diagnostics,
            diagnostic => diagnostic.Id.StartsWith("test.first", StringComparison.Ordinal) &&
                diagnostic.IsUnreachable);
    }

    [Fact]
    public void FlushPendingMappings_ReplaysIncompleteBindingLiterally()
    {
        var bindings = new VimKeyBindingRegistry();
        bindings.Register(new("test.sequence", VimModeSet.Normal, "jk"),
            _ => Array.Empty<VimEvent>());
        var engine = CreateEngine("abc", bindings);

        engine.ProcessKey("j");
        engine.FlushPendingMappings();

        Assert.Equal(CursorPosition.Zero, engine.Cursor);
    }

    private static VimEngine CreateEngine(string text, VimKeyBindingRegistry bindings)
    {
        var engine = new VimEngine(new VimConfig(), keyBindings: bindings);
        engine.SetText(text);
        return engine;
    }
}
