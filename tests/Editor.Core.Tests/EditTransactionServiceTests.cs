using Editor.Core.Buffer;
using Editor.Core.Editing;
using Editor.Core.Engine;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Core.Tests;

public class EditTransactionServiceTests
{
    [Fact]
    public void Execute_EmitsOneChangePairAndCreatesOneUndoStep()
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("abc");
        var cursor = CursorPosition.Zero;
        var service = CreateService(buffers, () => cursor, value => cursor = value);
        var events = new List<VimEvent> { VimEvent.CursorMoved(cursor) };

        var result = service.Execute(events, tx =>
        {
            tx.Buffer.InsertChar(0, 0, 'x');
            tx.Cursor = new CursorPosition(0, 1);
            return "repeat";
        });

        Assert.True(result.TextChanged);
        Assert.Equal("repeat", result.RepeatMetadata);
        Assert.Equal("xabc", buffers.Current.Text.GetText());
        Assert.Equal(new[] { VimEventType.TextChanged, VimEventType.CursorMoved }, events.Select(e => e.Type));
        Assert.NotNull(buffers.Current.Undo.Undo(buffers.Current.Text, cursor));
    }

    [Fact]
    public void Execute_WhenMutationThrows_RollsBackBufferAndCursor()
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("abc");
        var cursor = new CursorPosition(0, 1);
        var service = CreateService(buffers, () => cursor, value => cursor = value);

        Assert.Throws<InvalidOperationException>(() =>
            service.Execute([], tx =>
            {
                tx.Buffer.DeleteChar(0, 0);
                tx.Cursor = CursorPosition.Zero;
                throw new InvalidOperationException();
            }));

        Assert.Equal("abc", buffers.Current.Text.GetText());
        Assert.Equal(new CursorPosition(0, 1), cursor);
    }

    private static EditTransactionService CreateService(
        BufferManager buffers,
        Func<CursorPosition> getCursor,
        Action<CursorPosition> setCursor) =>
        new(buffers, new MarkManager(), new SyntaxEngine(), getCursor, setCursor,
            () => false, (events, message) => events.Add(VimEvent.StatusMessage(message)));
}

public class EditTransactionIntegrationTests
{
    [Fact]
    public void InsertCharacters_KeepSessionUndoAndEmitOneChangePairPerKey()
    {
        var engine = new VimEngine();
        engine.ProcessKey("i");

        var firstEvents = engine.ProcessKey("a");
        var secondEvents = engine.ProcessKey("b");
        engine.ProcessKey("Escape");
        engine.ProcessKey("u");

        Assert.Equal(new[] { VimEventType.TextChanged, VimEventType.CursorMoved },
            firstEvents.Select(e => e.Type));
        Assert.Equal(new[] { VimEventType.TextChanged, VimEventType.CursorMoved },
            secondEvents.Select(e => e.Type));
        Assert.Equal("", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void PasteText_UsesOneTransactionAndOneUndoStep()
    {
        var engine = new VimEngine();
        engine.SetText("abc");

        var events = engine.PasteText("XY");
        engine.ProcessKey("u");

        Assert.Equal(new[] { VimEventType.TextChanged, VimEventType.CursorMoved },
            events.Select(e => e.Type));
        Assert.Equal("abc", engine.CurrentBuffer.Text.GetText());
    }
}
