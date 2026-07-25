using Editor.Core.Config;
using Editor.Core.Editing;
using Editor.Core.Engine;
using Editor.Core.Extensibility;
using Editor.Core.Models;
using Editor.Core.Registers;

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

    [Fact]
    public void RegisteredCommand_EditCapabilityPreservesUndoAndEvents()
    {
        var commands = new NormalCommandRegistry();
        commands.Register(new("test.edit", ["w"]), context =>
        {
            context.Edit(edit =>
            {
                edit.Buffer.ReplaceLine(0, "changed");
                edit.Cursor = CursorPosition.Zero;
            });
            return [];
        });
        var engine = CreateEngine("original", commands);

        var events = engine.ProcessKey("w");
        engine.ProcessKey("u");

        Assert.Equal(new[] { VimEventType.TextChanged, VimEventType.CursorMoved },
            events.Select(e => e.Type));
        Assert.Equal("original", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Handler_CanBeUnitTestedWithoutVimEngine()
    {
        var context = new TestNormalCommandContext("lower");
        NormalCommandHandler handler = command =>
        {
            command.Edit(edit => edit.Buffer.ReplaceLine(0, command.Buffer.GetLine(0).ToUpperInvariant()));
            return [VimEvent.StatusMessage("done")];
        };

        var events = handler(context);

        Assert.Equal("LOWER", context.Buffer.GetText());
        Assert.Equal("done", Assert.IsType<StatusMessageEvent>(events.Single()).Message);
    }

    [Fact]
    public void PublicCommandContext_DoesNotExposeVimEngine()
    {
        Assert.DoesNotContain(
            typeof(INormalCommandContext).GetProperties(),
            property => property.PropertyType == typeof(VimEngine));
    }

    private static VimEngine CreateEngine(string text, NormalCommandRegistry commands)
    {
        var engine = new VimEngine(new VimConfig(), normalCommands: commands);
        engine.SetText(text);
        return engine;
    }

    private sealed class TestNormalCommandContext(string text) : INormalCommandContext
    {
        private readonly Editor.Core.Buffer.TextBuffer _buffer = new(text);

        public ParsedCommand Command { get; } = new(1, null, "test", null, null, false);
        public INormalBufferView Buffer => _buffer;
        public CursorPosition Cursor { get; private set; }
        public Selection? Selection => null;
        public VimMode Mode => VimMode.Normal;
        public string? FilePath => null;
        public Motion? CalculateMotion(string motion, int count = 1) => null;
        public EditTransactionResult Edit(Action<EditTransaction> mutation)
        {
            var edit = new EditTransaction(_buffer, Cursor);
            mutation(edit);
            Cursor = edit.Cursor;
            return new EditTransactionResult(true, true, Cursor);
        }
        public void MoveCursor(CursorPosition cursor) => Cursor = cursor;
        public Register GetRegister(char name) => Register.Empty;
        public void SetRegister(char name, Register value) { }
    }
}
