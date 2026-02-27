using WVim.Core.Buffer;
using WVim.Core.Config;
using WVim.Core.Engine;
using WVim.Core.Models;

namespace WVim.Core.Tests;

public class ExCommandProcessorTests
{
    private static (ExCommandProcessor Processor, BufferManager Buffers) CreateProcessor()
    {
        var buffers = new BufferManager();
        return (new ExCommandProcessor(buffers, new VimOptions()), buffers);
    }

    [Fact]
    public void QuitAlias_ProducesQuitRequestedEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("quit", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<QuitRequestedEvent>(result.Event);
        Assert.False(evt.Force);
    }

    [Fact]
    public void ForceQuitAlias_ProducesForceQuitRequestedEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("quit!", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<QuitRequestedEvent>(result.Event);
        Assert.True(evt.Force);
    }

    [Fact]
    public void TabCommands_ProduceExpectedEvents()
    {
        var (processor, _) = CreateProcessor();

        var next = processor.Execute("tabnext", CursorPosition.Zero);
        var prev = processor.Execute("tabprevious", CursorPosition.Zero);
        var close = processor.Execute("tabclose!", CursorPosition.Zero);

        Assert.True(next.Success);
        Assert.IsType<NextTabRequestedEvent>(next.Event);

        Assert.True(prev.Success);
        Assert.IsType<PrevTabRequestedEvent>(prev.Event);

        Assert.True(close.Success);
        var closeEvt = Assert.IsType<CloseTabRequestedEvent>(close.Event);
        Assert.True(closeEvt.Force);
    }

    [Fact]
    public void TabEditAlias_ProducesNewTabRequestedEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("tabe notes.txt", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<NewTabRequestedEvent>(result.Event);
        Assert.Equal("notes.txt", evt.FilePath);
    }

    [Fact]
    public void NewAndVnew_ProduceSplitEvents()
    {
        var (processor, _) = CreateProcessor();

        var horizontal = processor.Execute("new", CursorPosition.Zero);
        var vertical = processor.Execute("vnew", CursorPosition.Zero);

        Assert.True(horizontal.Success);
        var hEvt = Assert.IsType<SplitRequestedEvent>(horizontal.Event);
        Assert.False(hEvt.Vertical);

        Assert.True(vertical.Success);
        var vEvt = Assert.IsType<SplitRequestedEvent>(vertical.Event);
        Assert.True(vEvt.Vertical);
    }
}
