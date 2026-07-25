using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class PendingInputControllerTests
{
    [Fact]
    public void Begin_ReplacesCurrentExclusiveState()
    {
        var controller = new PendingInputController();

        controller.Begin(new PendingInputState.InsertRegister());
        controller.Begin(new PendingInputState.Digraph(null));

        Assert.IsType<PendingInputState.Digraph>(controller.Current);
    }

    [Fact]
    public void Cancel_ClearsCurrentState()
    {
        var controller = new PendingInputController();
        controller.Begin(new PendingInputState.ExpressionRegister("1+2"));

        controller.Cancel();

        Assert.IsType<PendingInputState.None>(controller.Current);
        Assert.False(controller.HasPendingInput);
    }

    [Fact]
    public void Begin_None_IsRejected()
    {
        var controller = new PendingInputController();

        Assert.Throws<ArgumentException>(() => controller.Begin(new PendingInputState.None()));
    }
}

public class VimEnginePendingInputTests
{
    [Fact]
    public void InsertExpressionRegister_TracksStateAndEmitsStablePromptEvents()
    {
        var engine = CreateEngine("value:");
        engine.ProcessKey("A");
        engine.ProcessKey("r", ctrl: true);

        var beginEvents = engine.ProcessKey("=");
        var inputEvents = engine.ProcessKey("1");
        var finishEvents = engine.ProcessKey("Return");

        Assert.Collection(beginEvents,
            e => Assert.Equal(VimEventType.CommandLineChanged, e.Type));
        Assert.Collection(inputEvents,
            e => Assert.Equal(VimEventType.CommandLineChanged, e.Type));
        Assert.Equal(new[] { VimEventType.CommandLineChanged, VimEventType.TextChanged, VimEventType.CursorMoved },
            finishEvents.Select(e => e.Type));
        Assert.Equal("value:1", engine.CurrentBuffer.Text.GetText());
        Assert.IsType<PendingInputState.None>(engine.PendingInput);
    }

    [Fact]
    public void InsertExpressionRegister_EscapeCancelsWithoutEditing()
    {
        var engine = CreateEngine("unchanged");
        engine.ProcessKey("i");
        engine.ProcessKey("r", ctrl: true);
        engine.ProcessKey("=");
        engine.ProcessKey("4");

        var events = engine.ProcessKey("Escape");

        Assert.Equal("unchanged", engine.CurrentBuffer.Text.GetText());
        Assert.IsType<PendingInputState.None>(engine.PendingInput);
        Assert.Collection(events,
            e => Assert.Equal(VimEventType.CommandLineChanged, e.Type));
    }

    [Theory]
    [InlineData("r")]
    [InlineData("k")]
    [InlineData("x")]
    public void SetVimEnabled_CancelsInsertPendingState(string controlKey)
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        engine.ProcessKey(controlKey, ctrl: true);
        Assert.IsNotType<PendingInputState.None>(engine.PendingInput);

        engine.SetVimEnabled(false);

        Assert.IsType<PendingInputState.None>(engine.PendingInput);
    }

    [Theory]
    [InlineData("expression")]
    [InlineData("digraph")]
    public void SetVimEnabled_ClearsPendingInputPrompt(string pendingKind)
    {
        var engine = CreateEngine();
        engine.ProcessKey("i");
        if (pendingKind == "expression")
        {
            engine.ProcessKey("r", ctrl: true);
            engine.ProcessKey("=");
            engine.ProcessKey("1");
        }
        else
        {
            engine.ProcessKey("k", ctrl: true);
            engine.ProcessKey("a");
        }

        var events = engine.SetVimEnabled(false);

        Assert.Contains(events,
            e => e is CommandLineChangedEvent { Text: "" });
        Assert.IsType<PendingInputState.None>(engine.PendingInput);
    }

    [Theory]
    [InlineData("r", typeof(PendingInputState.ReplaceCharacter))]
    [InlineData("m", typeof(PendingInputState.SetMark))]
    [InlineData("`", typeof(PendingInputState.JumpToMark))]
    [InlineData("'", typeof(PendingInputState.JumpToMark))]
    [InlineData("\"", typeof(PendingInputState.NormalRegister))]
    [InlineData("f", typeof(PendingInputState.FindCharacter))]
    public void NormalPrefix_ExposesExclusivePendingState(string key, Type stateType)
    {
        var engine = CreateEngine("abc");

        engine.ProcessKey(key);

        Assert.IsType(stateType, engine.PendingInput);
    }

    [Theory]
    [InlineData("r", "x")]
    [InlineData("m", "a")]
    [InlineData("`", "a")]
    [InlineData("'", "a")]
    [InlineData("\"", "a")]
    [InlineData("f", "b")]
    public void NormalPrefix_CompletionClearsPendingState(string prefix, string completion)
    {
        var engine = CreateEngine("abc");
        engine.ProcessKey(prefix);

        engine.ProcessKey(completion);

        Assert.IsType<PendingInputState.None>(engine.PendingInput);
    }

    [Fact]
    public void VisualPendingStates_AreCancelledWhenVisualModeExits()
    {
        var engine = CreateEngine("word");
        engine.ProcessKey("v");
        engine.ProcessKey("i");
        Assert.IsType<PendingInputState.VisualTextObject>(engine.PendingInput);

        engine.ProcessKey("Escape");

        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.IsType<PendingInputState.None>(engine.PendingInput);
    }

    private static VimEngine CreateEngine(string text = "")
    {
        var engine = new VimEngine();
        if (text.Length > 0)
            engine.SetText(text);
        return engine;
    }
}
