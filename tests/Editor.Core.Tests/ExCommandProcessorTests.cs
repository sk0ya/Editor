using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Marks;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class ExCommandProcessorTests
{
    private static (ExCommandProcessor Processor, BufferManager Buffers) CreateProcessor()
    {
        var buffers = new BufferManager();
        return (new ExCommandProcessor(buffers, new VimOptions(), new MarkManager()), buffers);
    }

    [Fact]
    public void QuitAlias_ProducesWindowCloseRequestedEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("quit", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<WindowCloseRequestedEvent>(result.Event);
        Assert.False(evt.Force);
    }

    [Fact]
    public void ForceQuitAlias_ProducesForceWindowCloseRequestedEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("quit!", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<WindowCloseRequestedEvent>(result.Event);
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

    [Theory]
    [InlineData("w")]
    [InlineData("write")]
    public void WriteWithoutPath_OnUnnamedBuffer_ProducesSaveRequestedEvent(string command)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<SaveRequestedEvent>(result.Event);
        Assert.Null(evt.FilePath);
    }

    [Theory]
    [InlineData("w!")]
    [InlineData("write!")]
    public void ForceWriteWithoutPath_OnUnnamedBuffer_ProducesSaveRequestedEvent(string command)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<SaveRequestedEvent>(result.Event);
        Assert.Null(evt.FilePath);
    }

    // ── :e! tests ──────────────────────────────────────────────────────────

    [Fact]
    public void Edit_WithNoArgAndNoFilePath_ReturnsError()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("e!", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Equal("No file name", result.Message);
    }

    [Theory]
    [InlineData("e!")]
    [InlineData("edit!")]
    public void ForceEdit_WithFilePath_ProducesReloadFileRequestedEvent(string command)
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.FilePath = "test.txt";

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<ReloadFileRequestedEvent>(result.Event);
        Assert.True(evt.Force);
    }

    [Fact]
    public void Edit_WithModifiedBufferAndNoForce_ReturnsError()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.FilePath = "test.txt";
        // InsertChar marks the buffer as modified (SetText does not)
        buffers.Current.Text.InsertChar(0, 0, 'x');

        var result = processor.Execute("e", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Contains("No write since last change", result.Message);
    }

    [Fact]
    public void Edit_WithFileArgument_ProducesOpenFileRequestedEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("e other.txt", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<OpenFileRequestedEvent>(result.Event);
        Assert.Equal("other.txt", evt.FilePath);
    }

    // ── :global tests ──────────────────────────────────────────────────────

    [Fact]
    public void Global_Delete_RemovesMatchingLines()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("foo\nbar\nbaz\nfoo2");

        var result = processor.Execute("g/foo/d", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Contains("2", result.Message); // "2 line(s) deleted"
        var lines = buffers.Current.Text.GetText().Split('\n');
        Assert.DoesNotContain("foo", lines);
        Assert.Contains("bar", lines);
        Assert.Contains("baz", lines);
    }

    [Fact]
    public void Global_Inverse_DeletesNonMatchingLines()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("keep\nremove\nkeep2");

        var result = processor.Execute("v/keep/d", CursorPosition.Zero);

        Assert.True(result.Success);
        var text = buffers.Current.Text.GetText();
        Assert.Contains("keep", text);
        Assert.DoesNotContain("remove", text);
    }

    [Fact]
    public void Global_Substitute_AppliesOnMatchingLines()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("hello world\ngoodbye world\nhello again");

        var result = processor.Execute("g/hello/s/world/earth/", CursorPosition.Zero);

        Assert.True(result.Success);
        var lines = buffers.Current.Text.GetText().Split('\n');
        Assert.Equal("hello earth", lines[0]);
        Assert.Equal("goodbye world", lines[1]); // unchanged — doesn't match "hello"
        Assert.Equal("hello again", lines[2]);   // "world" not present, no change
    }

    [Fact]
    public void Global_Print_ReturnsMatchingLineContent()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("alpha\nbeta\nalpha2");

        var result = processor.Execute("g/alpha/p", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Contains("alpha", result.Message);
    }

    [Fact]
    public void Global_NoMatch_ReturnsFailure()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("foo\nbar");

        var result = processor.Execute("g/xyz/d", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vglobal_Delete_RemovesNonMatchingLines()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("keep\ndelete me\nkeep too");

        var result = processor.Execute("vglobal/keep/d", CursorPosition.Zero);

        Assert.True(result.Success);
        var text = buffers.Current.Text.GetText();
        Assert.Contains("keep", text);
        Assert.DoesNotContain("delete me", text);
    }

    [Fact]
    public void GlobalBang_Delete_RemovesNonMatchingLines()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("keep\nremove\nkeep2");

        var result = processor.Execute("g!/keep/d", CursorPosition.Zero);

        Assert.True(result.Success);
        var text = buffers.Current.Text.GetText();
        Assert.Contains("keep", text);
        Assert.DoesNotContain("remove", text);
    }
}
