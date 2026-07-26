using Editor.Core.Engine;
using Editor.Core.Editing;
using Editor.Core.Extensibility;
using Editor.Core.Formatting;
using Editor.Core.Lsp;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Core.Tests;

public class VimEngineServicesTests
{
    [Fact]
    public void DefaultEngines_DoNotLeakRegistrations()
    {
        var first = new VimEngine();
        first.KeyBindings.Register(
            new("test.shared-x", VimModeSet.Normal, "x"),
            _ => [VimEvent.StatusMessage("custom")]);
        var second = new VimEngine();
        second.SetText("abc");

        second.ProcessKey("x");

        Assert.Equal("bc", second.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void ExplicitlySharedServices_ShareRegistrations()
    {
        var services = VimEngineServices.CreateIsolated();
        services.KeyBindings.Register(
            new("test.shared-x", VimModeSet.Normal, "x"),
            _ => [VimEvent.StatusMessage("custom")]);
        var first = new VimEngine(engineServices: services);
        var second = new VimEngine(engineServices: services);

        Assert.Equal("custom",
            Assert.IsType<StatusMessageEvent>(first.ProcessKey("x").Single()).Message);
        Assert.Equal("custom",
            Assert.IsType<StatusMessageEvent>(second.ProcessKey("x").Single()).Message);
    }

    [Fact]
    public void CompatibilityDefaults_AreFactories()
    {
        Assert.NotSame(VimKeyBindingRegistry.Default, VimKeyBindingRegistry.Default);
        Assert.NotSame(NormalCommandRegistry.Default, NormalCommandRegistry.Default);
        Assert.NotSame(EditorCommandRegistry.Default, EditorCommandRegistry.Default);
        Assert.NotSame(EditAssistRegistry.Default, EditAssistRegistry.Default);
        Assert.NotSame(SyntaxLanguageRegistry.Default, SyntaxLanguageRegistry.Default);
        Assert.NotSame(FormatterRegistry.Default, FormatterRegistry.Default);
    }
}
