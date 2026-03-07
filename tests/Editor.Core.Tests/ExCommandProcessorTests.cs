using System.IO;
using System.Text.RegularExpressions;
using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Registers;

namespace Editor.Core.Tests;

public class ExCommandProcessorTests
{
    private static (ExCommandProcessor Processor, BufferManager Buffers) CreateProcessor()
    {
        var buffers = new BufferManager();
        return (new ExCommandProcessor(buffers, new VimOptions(), new MarkManager()), buffers);
    }

    private static (ExCommandProcessor Processor, BufferManager Buffers, RegisterManager Registers) CreateProcessorWithRegisters()
    {
        var buffers = new BufferManager();
        var options = new VimOptions();
        var registers = new RegisterManager(options);
        return (new ExCommandProcessor(buffers, options, new MarkManager(), registerManager: registers), buffers, registers);
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

    // ── Additional ExCommand tests ────────────────────────────────────────

    [Fact]
    public void Noh_ClearsSearchHighlight()
    {
        var options = new VimOptions();
        options.HlSearch = true;
        var buffers = new BufferManager();
        var processor = new ExCommandProcessor(buffers, options, new MarkManager());

        var result = processor.Execute("noh", CursorPosition.Zero);

        Assert.True(result.Success);
        // HlSearch must stay true — :noh only clears the visual highlight temporarily;
        // n/N will re-enable highlights on the next search move.
        Assert.True(options.HlSearch);
        // A SearchResultChanged event with an empty pattern is emitted to clear the canvas.
        Assert.NotNull(result.Event);
        Assert.Equal(VimEventType.SearchResultChanged, result.Event.Type);
        var srce = Assert.IsType<SearchResultChangedEvent>(result.Event);
        Assert.Equal("", srce.Pattern);
    }

    [Fact]
    public void Nohlsearch_ClearsSearchHighlight()
    {
        var options = new VimOptions();
        options.HlSearch = true;
        var buffers = new BufferManager();
        var processor = new ExCommandProcessor(buffers, options, new MarkManager());

        var result = processor.Execute("nohlsearch", CursorPosition.Zero);

        Assert.True(result.Success);
        // HlSearch must stay true — :nohlsearch only clears the visual highlight temporarily;
        // n/N will re-enable highlights on the next search move.
        Assert.True(options.HlSearch);
        // A SearchResultChanged event with an empty pattern is emitted to clear the canvas.
        Assert.NotNull(result.Event);
        Assert.Equal(VimEventType.SearchResultChanged, result.Event.Type);
        var srce = Assert.IsType<SearchResultChangedEvent>(result.Event);
        Assert.Equal("", srce.Pattern);
    }

    [Fact]
    public void Pwd_ShowsCurrentDirectory()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("pwd", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public void Echo_PrintsMessage()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("echo hello", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Message);
    }

    [Fact]
    public void Echo_PrintsQuotedMessage()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("echo \"hello world\"", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("hello world", result.Message);
    }

    [Fact]
    public void Cd_ChangesCurrentDirectory()
    {
        var (processor, _) = CreateProcessor();
        var original = Directory.GetCurrentDirectory();
        var target = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            var result = processor.Execute($"cd {target}", CursorPosition.Zero);

            Assert.True(result.Success);
            Assert.Equal(target, Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void Lcd_ChangesCurrentDirectory()
    {
        var (processor, _) = CreateProcessor();
        var original = Directory.GetCurrentDirectory();
        var target = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            var result = processor.Execute($"lcd {target}", CursorPosition.Zero);

            Assert.True(result.Success);
            Assert.Equal(target, Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void Cd_NoArg_ChangesToHomeDirectory()
    {
        var (processor, _) = CreateProcessor();
        var original = Directory.GetCurrentDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var result = processor.Execute("cd", CursorPosition.Zero);

            Assert.True(result.Success);
            Assert.Equal(home, Directory.GetCurrentDirectory());
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void Echomsg_PrintsMessage()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("echomsg \"hello world\"", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("hello world", result.Message);
    }

    [Fact]
    public void Echo_ExpandPercent_ReturnsFilePath()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.FilePath = "/some/path/file.txt";

        var result = processor.Execute("echo expand('%')", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("/some/path/file.txt", result.Message);
    }

    [Fact]
    public void Echo_ExpandPercentT_ReturnsFileName()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.FilePath = "/some/path/file.txt";

        var result = processor.Execute("echo expand('%:t')", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("file.txt", result.Message);
    }

    [Fact]
    public void Echo_ExpandPercentH_ReturnsDirectory()
    {
        var (processor, buffers) = CreateProcessor();
        var sep = Path.DirectorySeparatorChar;
        buffers.Current.FilePath = $"{sep}some{sep}path{sep}file.txt";

        var result = processor.Execute("echo expand('%:h')", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal($"{sep}some{sep}path", result.Message);
    }

    [Fact]
    public void Echo_Strftime_ReturnsFormattedDate()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("echo strftime('%Y-%m-%d')", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        // Should look like a date: 4 digits, dash, 2 digits, dash, 2 digits
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", result.Message);
    }

    [Fact]
    public void Execute_RunsEchoCommand()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("execute \"echo hello\"", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Message);
    }

    [Fact]
    public void Execute_NoArg_ReturnsError()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("execute", CursorPosition.Zero);

        Assert.False(result.Success);
    }

    [Fact]
    public void RangeYank_YanksLinesToRegister()
    {
        var (processor, buffers, registers) = CreateProcessorWithRegisters();
        buffers.Current.Text.SetText("line1\nline2\nline3");

        // Yank lines 1-2 (1-indexed) to register a
        var result = processor.Execute("1,2yank a", new CursorPosition(0, 0));

        Assert.True(result.Success);
        var reg = registers.Get('a');
        Assert.Equal("line1\nline2", reg.Text);
        Assert.Equal(RegisterType.Line, reg.Type);
    }

    [Fact]
    public void Put_CommandPastesFromRegister()
    {
        var (processor, buffers, registers) = CreateProcessorWithRegisters();
        buffers.Current.Text.SetText("aaa\nbbb");
        // Pre-fill register a
        registers.SetYank('a', new Register("inserted", RegisterType.Line));

        // :put a — paste after current line (line 0)
        var result = processor.Execute("put a", new CursorPosition(0, 0));

        Assert.True(result.Success);
        var text = buffers.Current.Text.GetText();
        Assert.Contains("inserted", text);
    }

    // ── :registers tests ────────────────────────────────────────────────────

    [Fact]
    public void Registers_DisplaysNonEmptyRegisters()
    {
        var (processor, _, registers) = CreateProcessorWithRegisters();
        registers.SetYank('a', new Register("hello world", RegisterType.Character));
        registers.SetYank('b', new Register("foo\nbar", RegisterType.Line));

        var result = processor.Execute("registers", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("\"a", result.Message);
        Assert.Contains("hello world", result.Message);
        // Newlines in register content shown as ^J
        Assert.Contains("foo^Jbar", result.Message);
        Assert.Contains("--- Registers ---", result.Message);
    }

    [Fact]
    public void Reg_Alias_WorksLikeRegisters()
    {
        var (processor, _, registers) = CreateProcessorWithRegisters();
        registers.SetYank('c', new Register("content", RegisterType.Character));

        var result = processor.Execute("reg", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("\"c", result.Message);
        Assert.Contains("content", result.Message);
    }

    [Fact]
    public void Reg_WithArgs_DisplaysOnlySpecifiedRegisters()
    {
        var (processor, _, registers) = CreateProcessorWithRegisters();
        registers.SetYank('a', new Register("alpha", RegisterType.Character));
        registers.SetYank('b', new Register("beta", RegisterType.Character));
        registers.SetYank('c', new Register("gamma", RegisterType.Character));

        var result = processor.Execute("reg ab", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("\"a", result.Message);
        Assert.Contains("alpha", result.Message);
        Assert.Contains("\"b", result.Message);
        Assert.Contains("beta", result.Message);
        // Register c should NOT appear
        Assert.DoesNotContain("\"c", result.Message);
        Assert.DoesNotContain("gamma", result.Message);
    }

    [Fact]
    public void Registers_EmptyRegisters_ReturnsEmptyMessage()
    {
        var (processor, _, _) = CreateProcessorWithRegisters();

        var result = processor.Execute("registers", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Contains("(empty)", result.Message);
    }

    // ── :marks tests ─────────────────────────────────────────────────────────

    private static (ExCommandProcessor Processor, BufferManager Buffers, MarkManager Marks) CreateProcessorWithMarks()
    {
        var buffers = new BufferManager();
        var options = new VimOptions();
        var marks = new MarkManager();
        return (new ExCommandProcessor(buffers, options, marks), buffers, marks);
    }

    [Fact]
    public void Marks_DisplaysAllMarks()
    {
        var (processor, buffers, marks) = CreateProcessorWithMarks();
        buffers.Current.Text.SetText("first line\nsecond line\nthird line");
        marks.SetMark('a', new CursorPosition(0, 0));
        marks.SetMark('b', new CursorPosition(2, 5));

        var result = processor.Execute("marks", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("mark  line  col  text", result.Message);
        Assert.Contains(" a ", result.Message);
        Assert.Contains(" b ", result.Message);
        // Lines are 1-based
        Assert.Contains("1", result.Message);
        Assert.Contains("3", result.Message);
        Assert.Contains("first line", result.Message);
        Assert.Contains("third line", result.Message);
    }

    [Fact]
    public void Marks_WithArgs_DisplaysOnlySpecifiedMarks()
    {
        var (processor, buffers, marks) = CreateProcessorWithMarks();
        buffers.Current.Text.SetText("aaa\nbbb\nccc");
        marks.SetMark('a', new CursorPosition(0, 0));
        marks.SetMark('b', new CursorPosition(1, 0));
        marks.SetMark('c', new CursorPosition(2, 0));

        var result = processor.Execute("marks ac", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains(" a ", result.Message);
        Assert.Contains(" c ", result.Message);
        // Mark b should NOT appear
        Assert.DoesNotContain(" b ", result.Message);
    }

    [Fact]
    public void Marks_NoMarksSet_ReturnsEmptyMessage()
    {
        var (processor, _, _) = CreateProcessorWithMarks();

        var result = processor.Execute("marks", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Contains("(no marks set)", result.Message);
    }
}
