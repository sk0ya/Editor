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

    private static ExCommandProcessor CreateProcessorWithScriptNames(IReadOnlyList<string> scriptNames)
    {
        return new ExCommandProcessor(new BufferManager(), new VimOptions(), new MarkManager(), scriptNames: scriptNames);
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
    public void TerminalOpenCommands_ProduceTerminalRequestedEvent()
    {
        var (processor, _) = CreateProcessor();

        var defaultTerm = processor.Execute("term", CursorPosition.Zero);
        var customTerm = processor.Execute("terminal pwsh -NoLogo", CursorPosition.Zero);
        var customTermAlias = processor.Execute("term pwsh -NoLogo", CursorPosition.Zero);

        Assert.True(defaultTerm.Success);
        var defaultEvent = Assert.IsType<TerminalRequestedEvent>(defaultTerm.Event);
        Assert.Null(defaultEvent.ShellCmd);

        Assert.True(customTerm.Success);
        var customEvent = Assert.IsType<TerminalRequestedEvent>(customTerm.Event);
        Assert.Equal("pwsh -NoLogo", customEvent.ShellCmd);

        Assert.True(customTermAlias.Success);
        var customAliasEvent = Assert.IsType<TerminalRequestedEvent>(customTermAlias.Event);
        Assert.Equal("pwsh -NoLogo", customAliasEvent.ShellCmd);
    }

    [Theory]
    [InlineData("terms", TerminalCommandKind.List, null, false)]
    [InlineData("termnext", TerminalCommandKind.Next, null, false)]
    [InlineData("termprev", TerminalCommandKind.Previous, null, false)]
    [InlineData("termselect 2", TerminalCommandKind.Select, 2, false)]
    [InlineData("termclose", TerminalCommandKind.Close, null, false)]
    [InlineData("termclose!", TerminalCommandKind.Close, null, true)]
    [InlineData("termclose 3", TerminalCommandKind.Close, 3, false)]
    [InlineData("termclose! 4", TerminalCommandKind.Close, 4, true)]
    public void TerminalManagementCommands_ProduceTerminalCommandRequestedEvent(
        string command,
        TerminalCommandKind expectedKind,
        int? expectedTerminalNumber,
        bool expectedForce)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<TerminalCommandRequestedEvent>(result.Event);
        Assert.Equal(expectedKind, evt.Kind);
        Assert.Equal(expectedTerminalNumber, evt.TerminalNumber);
        Assert.Equal(expectedForce, evt.Force);
    }

    [Theory]
    [InlineData("termselect")]
    [InlineData("termselect 0")]
    [InlineData("termselect +1")]
    [InlineData("termselect 1.5")]
    [InlineData("termselect abc")]
    [InlineData("termclose abc")]
    [InlineData("termclose! +1")]
    public void TerminalManagementCommands_RejectInvalidTerminalNumbers(string command)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Equal("Invalid terminal number", result.Message);
    }

    [Fact]
    public void Scriptnames_ListsLoadedScriptsInOrder()
    {
        var processor = CreateProcessorWithScriptNames(["C:\\vim\\vimrc", "C:\\vim\\plugin.vim"]);

        var result = processor.Execute("scriptnames", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("1: C:\\vim\\vimrc", result.Message);
        Assert.Contains("2: C:\\vim\\plugin.vim", result.Message);
    }

    [Fact]
    public void Scriptnames_WithNoScripts_ReturnsEmptyMessage()
    {
        var processor = CreateProcessorWithScriptNames([]);

        var result = processor.Execute("scriptnames", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("(no scripts sourced)", result.Message);
    }

    [Theory]
    [InlineData("Git stage", true)]
    [InlineData("Gstage", true)]
    [InlineData("Git unstage", false)]
    [InlineData("Gunstage", false)]
    public void GitHunkStageCommands_ProduceHunkStageRequestedEvent(string command, bool stage)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<HunkStageRequestedEvent>(result.Event);
        Assert.Equal(stage, evt.Stage);
    }

    [Theory]
    [InlineData("Git status")]
    [InlineData("Gstatus")]
    [InlineData("gs")]
    public void GitStatusCommands_ProduceGitStatusRequestedEvent(string command)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Event);
        Assert.Equal(VimEventType.GitStatusRequested, result.Event.Type);
    }

    [Theory]
    [InlineData("Git push", VimEventType.GitPushRequested)]
    [InlineData("Gpush", VimEventType.GitPushRequested)]
    [InlineData("Git pull", VimEventType.GitPullRequested)]
    [InlineData("Gpull", VimEventType.GitPullRequested)]
    public void GitRemoteCommands_ProduceExpectedEvents(string command, VimEventType eventType)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Event);
        Assert.Equal(eventType, result.Event.Type);
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
    public void SubstitutePreview_ReturnsChangedLinesWithoutMutatingBuffer()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("foo one\nbar two\nfoo three");

        var preview = processor.GetSubstitutePreview("%s/foo/baz/", CursorPosition.Zero);

        Assert.Equal("foo one\nbar two\nfoo three", buffers.Current.Text.GetText());
        Assert.Equal("baz one", preview[0]);
        Assert.Equal("baz three", preview[2]);
        Assert.False(preview.ContainsKey(1));
    }

    [Fact]
    public void SubstitutePreview_UsesCurrentLineWhenRangeIsOmitted()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("foo one\nfoo two");

        var preview = processor.GetSubstitutePreview("s/foo/baz/", new CursorPosition(1, 0));

        Assert.Single(preview);
        Assert.Equal("baz two", preview[1]);
    }

    [Fact]
    public void Substitute_VeryMagic_UsesUnescapedGroupsAndAlternation()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("cat dog\nbird");

        var result = processor.Execute("%s/\\v(cat|dog)/pet/g", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("pet pet\nbird", buffers.Current.Text.GetText());
    }

    [Fact]
    public void Substitute_VeryNomagic_TreatsRegexMetacharactersAsLiteralText()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("a.b axb");

        var result = processor.Execute("%s/\\Va.b/X/g", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("X axb", buffers.Current.Text.GetText());
    }

    [Fact]
    public void Substitute_VeryNomagic_PreservesEscapedBackslashAsLiteralBackslash()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText(@"path\name 123");

        var result = processor.Execute("%s/\\V\\\\/slash/g", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("pathslashname 123", buffers.Current.Text.GetText());
    }

    [Fact]
    public void Substitute_VeryNomagic_BackslashBeforeDDoesNotBecomeDigitClass()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText(@"x\d x5");

        var result = processor.Execute("%s/\\V\\\\d/TOKEN/g", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("xTOKEN x5", buffers.Current.Text.GetText());
    }

    [Fact]
    public void SubstitutePreview_VeryNomagic_TreatsRegexMetacharactersAsLiteralText()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("a.b axb");

        var preview = processor.GetSubstitutePreview("%s/\\Va.b/X/g", CursorPosition.Zero);

        Assert.Single(preview);
        Assert.Equal("X axb", preview[0]);
        Assert.Equal("a.b axb", buffers.Current.Text.GetText());
    }

    [Fact]
    public void Global_VeryNomagic_TreatsRegexMetacharactersAsLiteralText()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("a.b\naxb\na-b");

        var result = processor.Execute("g/\\Va.b/d", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("axb\na-b", buffers.Current.Text.GetText());
    }

    [Fact]
    public void SubstitutePreview_IgnoresSeparatorsThatExecuteDoesNotSupport()
    {
        var (processor, buffers) = CreateProcessor();
        buffers.Current.Text.SetText("foo");

        var preview = processor.GetSubstitutePreview("s#foo#baz#", CursorPosition.Zero);

        Assert.Empty(preview);
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
    public void GrepReplace_ParsesProjectReplaceEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("grepreplace /foo/bar/i **/*.cs", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<ProjectReplaceRequestedEvent>(result.Event);
        Assert.Equal("foo", evt.Pattern);
        Assert.Equal("bar", evt.Replacement);
        Assert.Equal("**/*.cs", evt.FileGlob);
        Assert.True(evt.IgnoreCase);
    }

    [Fact]
    public void GrepReplace_ParsesGlobWithoutFlagsAndEmptyReplacement()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("grepreplace /foo// **/*.cs", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<ProjectReplaceRequestedEvent>(result.Event);
        Assert.Equal("foo", evt.Pattern);
        Assert.Equal("", evt.Replacement);
        Assert.Equal("**/*.cs", evt.FileGlob);
        Assert.False(evt.IgnoreCase);
    }

    [Fact]
    public void Grep_ParsesDelimitedGlobWithoutFlags()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("grep /foo/ **/*.cs", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<GrepRequestedEvent>(result.Event);
        Assert.Equal("foo", evt.Pattern);
        Assert.Equal("**/*.cs", evt.FileGlob);
        Assert.False(evt.IgnoreCase);
    }

    [Fact]
    public void Vimgrep_ParsesDelimitedGlobWithoutFlags()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("vimgrep /foo/ **/*.cs", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<GrepRequestedEvent>(result.Event);
        Assert.Equal("foo", evt.Pattern);
        Assert.Equal("**/*.cs", evt.FileGlob);
        Assert.False(evt.IgnoreCase);
    }

    [Fact]
    public void CReplace_ParsesQuickfixReplaceEvent()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("creplace replacement text", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<QuickfixReplaceRequestedEvent>(result.Event);
        Assert.Equal("replacement text", evt.Replacement);
    }

    [Fact]
    public void CReplace_AllowsEmptyReplacement()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("creplace", CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<QuickfixReplaceRequestedEvent>(result.Event);
        Assert.Equal("", evt.Replacement);
    }

    [Theory]
    [InlineData("cnext", 1)]
    [InlineData("3cnext", 3)]
    [InlineData("cprev", -1)]
    [InlineData("cprevious", -1)]
    [InlineData("2cprev", -2)]
    public void QuickfixNavigationCommands_ParseCount(string command, int signedCount)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        if (signedCount > 0)
        {
            var evt = Assert.IsType<QuickfixNextEvent>(result.Event);
            Assert.Equal(signedCount, evt.Count);
        }
        else
        {
            var evt = Assert.IsType<QuickfixPrevEvent>(result.Event);
            Assert.Equal(-signedCount, evt.Count);
        }
    }

    [Theory]
    [InlineData("lopen")]
    [InlineData("lope")]
    [InlineData("llist")]
    [InlineData("lli")]
    public void LocationListOpenCommands_ProduceOpenEvent(string command)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Event);
        Assert.Equal(VimEventType.LocationListOpenRequested, result.Event.Type);
    }

    [Theory]
    [InlineData("diagnostics")]
    [InlineData("diag")]
    public void DiagnosticsCommands_ProduceWorkspaceDiagnosticsEvent(string command)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Event);
        Assert.Equal(VimEventType.WorkspaceDiagnosticsRequested, result.Event.Type);
    }

    [Theory]
    [InlineData("lclose")]
    [InlineData("lcl")]
    public void LocationListCloseCommands_ProduceCloseEvent(string command)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Event);
        Assert.Equal(VimEventType.LocationListCloseRequested, result.Event.Type);
    }

    [Theory]
    [InlineData("lnext", 1)]
    [InlineData("ln", 1)]
    [InlineData("3lnext", 3)]
    public void LocationListNextCommands_ParseCount(string command, int count)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<LocationListNextEvent>(result.Event);
        Assert.Equal(count, evt.Count);
    }

    [Theory]
    [InlineData("lprev", 1)]
    [InlineData("lprevious", 1)]
    [InlineData("lp", 1)]
    [InlineData("2lprev", 2)]
    public void LocationListPrevCommands_ParseCount(string command, int count)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<LocationListPrevEvent>(result.Event);
        Assert.Equal(count, evt.Count);
    }

    [Theory]
    [InlineData("ll", -1)]
    [InlineData("ll 4", 3)]
    public void LocationListGotoCommands_ParseIndex(string command, int index)
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.True(result.Success);
        var evt = Assert.IsType<LocationListGotoEvent>(result.Event);
        Assert.Equal(index, evt.Index);
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
    public void Let_AssignsVariableForEcho()
    {
        var (processor, _) = CreateProcessor();

        var set = processor.Execute("let greeting = \"hello world\"", CursorPosition.Zero);
        var echo = processor.Execute("echo greeting", CursorPosition.Zero);

        Assert.True(set.Success);
        Assert.True(echo.Success);
        Assert.Equal("hello world", echo.Message);
    }

    [Fact]
    public void Let_EvaluatesArithmeticExpression()
    {
        var (processor, _) = CreateProcessor();

        var set = processor.Execute("let answer = 40 + 2", CursorPosition.Zero);
        var echo = processor.Execute("echo answer", CursorPosition.Zero);

        Assert.True(set.Success);
        Assert.True(echo.Success);
        Assert.Equal("42", echo.Message);
    }

    [Fact]
    public void Let_ListShowsAssignedVariables()
    {
        var (processor, _) = CreateProcessor();
        processor.Execute("let g:name = 'editor'", CursorPosition.Zero);

        var result = processor.Execute("let", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Contains("g:name = \"editor\"", result.Message);
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
    public void If_InteractiveCommandReturnsMissingEndifError()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("if 1", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Equal("E580: :endif missing", result.Message);
    }

    [Fact]
    public void If_InteractiveErrorDoesNotAffectNextCommand()
    {
        var (processor, _) = CreateProcessor();

        var ifResult = processor.Execute("if 1", CursorPosition.Zero);
        var echoResult = processor.Execute("echo after", CursorPosition.Zero);

        Assert.False(ifResult.Success);
        Assert.True(echoResult.Success);
        Assert.Equal("after", echoResult.Message);
    }

    [Fact]
    public void For_InteractiveCommandReturnsMissingEndforError()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("for item in [1, 2, 3]", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Equal("E170: Missing :endfor", result.Message);
    }

    [Fact]
    public void Endfor_InteractiveCommandReturnsWithoutForError()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("endfor", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Equal("E588: :endfor without :for", result.Message);
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

    // ── :undolist tests ─────────────────────────────────────────────────────

    [Fact]
    public void Undolist_WithNoHistory_ReturnsEmptyMessage()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("undolist", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Equal("undo list is empty", result.Message);
    }

    [Fact]
    public void Undolist_DisplaysLinearUndoAndRedoHistory()
    {
        var (processor, buffers) = CreateProcessor();
        var buffer = buffers.Current.Text;
        buffer.SetText("one");

        buffers.Current.Undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        buffers.Current.Undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");
        buffers.Current.Undo.Undo(buffer, new CursorPosition(0, 13));

        var result = processor.Execute("undolist", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("number  state    time", result.Message);
        Assert.Matches(@"(?m)^>\s+1\s+current\s+\d{2}:\d{2}:\d{2}\r?$", result.Message);
        Assert.Matches(@"(?m)^\s+2\s+redo\s+\d{2}:\d{2}:\d{2}\r?$", result.Message);
    }

    [Fact]
    public void EarlierAndLater_ByCount_RestoreBufferAndReturnCursor()
    {
        var (processor, buffers) = CreateProcessor();
        var buffer = buffers.Current.Text;
        buffer.SetText("one");

        buffers.Current.Undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        buffers.Current.Undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");

        var earlier = processor.Execute("earlier 2", new CursorPosition(0, 13));

        Assert.True(earlier.Success);
        Assert.True(earlier.BufferRestored);
        Assert.Equal("2 changes undone", earlier.Message);
        Assert.Equal(CursorPosition.Zero, earlier.RestoredCursor);
        Assert.Equal("one", buffer.GetText());

        var later = processor.Execute("later 1", CursorPosition.Zero);

        Assert.True(later.Success);
        Assert.True(later.BufferRestored);
        Assert.Equal("1 change redone", later.Message);
        Assert.Equal(new CursorPosition(0, 6), later.RestoredCursor);
        Assert.Equal("one two", buffer.GetText());
    }

    [Fact]
    public void Earlier_WithTimeSuffix_IsAccepted()
    {
        var (processor, buffers) = CreateProcessor();
        var buffer = buffers.Current.Text;
        buffer.SetText("one");

        buffers.Current.Undo.Snapshot(buffer, CursorPosition.Zero);
        buffer.InsertText(0, 3, " two");
        buffers.Current.Undo.Snapshot(buffer, new CursorPosition(0, 7));
        buffer.InsertText(0, 7, " three");

        var result = processor.Execute("earlier 1h", new CursorPosition(0, 13));

        Assert.True(result.Success);
        Assert.True(result.BufferRestored);
        Assert.Equal("one", buffer.GetText());
    }

    [Fact]
    public void Earlier_RequiresPositiveNumber()
    {
        var (processor, _) = CreateProcessor();

        var result = processor.Execute("earlier 0", CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Equal("Invalid argument", result.Message);
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

    [Fact]
    public void Delmarks_WithNames_DeletesSpecifiedMarks()
    {
        var (processor, _, marks) = CreateProcessorWithMarks();
        marks.SetMark('a', new CursorPosition(0, 0));
        marks.SetMark('b', new CursorPosition(1, 0));
        marks.SetMark('c', new CursorPosition(2, 0));

        var result = processor.Execute("delmarks ac", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Null(marks.GetMark('a'));
        Assert.NotNull(marks.GetMark('b'));
        Assert.Null(marks.GetMark('c'));
    }

    [Fact]
    public void Delmarks_WithRange_DeletesMarksInRange()
    {
        var (processor, _, marks) = CreateProcessorWithMarks();
        marks.SetMark('a', new CursorPosition(0, 0));
        marks.SetMark('b', new CursorPosition(1, 0));
        marks.SetMark('c', new CursorPosition(2, 0));
        marks.SetMark('d', new CursorPosition(3, 0));

        var result = processor.Execute("delmarks b-c", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(marks.GetMark('a'));
        Assert.Null(marks.GetMark('b'));
        Assert.Null(marks.GetMark('c'));
        Assert.NotNull(marks.GetMark('d'));
    }

    [Fact]
    public void DelmarksBang_ClearsAllMarks()
    {
        var (processor, _, marks) = CreateProcessorWithMarks();
        marks.SetMark('a', new CursorPosition(0, 0));
        marks.SetMark('b', new CursorPosition(1, 0));

        var result = processor.Execute("delmarks!", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Empty(marks.GetAllMarks());
    }

    [Theory]
    [InlineData("delmarks")]
    [InlineData("delm")]
    public void Delmarks_WithoutArgs_ReturnsArgumentRequired(string command)
    {
        var (processor, _, _) = CreateProcessorWithMarks();

        var result = processor.Execute(command, CursorPosition.Zero);

        Assert.False(result.Success);
        Assert.Equal("E471: Argument required", result.Message);
    }

    // ── :unmap / :nunmap / :iunmap / :vunmap tests ───────────────────────────

    private static ExCommandProcessor CreateProcessorWithMaps(
        Dictionary<string, string>? normalMaps = null,
        Dictionary<string, string>? insertMaps = null,
        Dictionary<string, string>? visualMaps = null)
    {
        var buffers = new BufferManager();
        var options = new VimOptions();
        return new ExCommandProcessor(buffers, options, new MarkManager(),
            normalMaps: normalMaps, insertMaps: insertMaps, visualMaps: visualMaps);
    }

    [Fact]
    public void Nunmap_RemovesNormalMapping()
    {
        var normalMaps = new Dictionary<string, string> { ["<Leader>w"] = ":w<CR>" };
        var processor = CreateProcessorWithMaps(normalMaps: normalMaps);

        var result = processor.Execute("nunmap <Leader>w", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.False(normalMaps.ContainsKey("<Leader>w"));
    }

    [Fact]
    public void Iunmap_RemovesInsertMapping()
    {
        var insertMaps = new Dictionary<string, string> { ["jk"] = "<Esc>" };
        var processor = CreateProcessorWithMaps(insertMaps: insertMaps);

        var result = processor.Execute("iunmap jk", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.False(insertMaps.ContainsKey("jk"));
    }

    [Fact]
    public void Vunmap_RemovesVisualMapping()
    {
        var visualMaps = new Dictionary<string, string> { ["<Leader>y"] = "\"+y" };
        var processor = CreateProcessorWithMaps(visualMaps: visualMaps);

        var result = processor.Execute("vunmap <Leader>y", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.False(visualMaps.ContainsKey("<Leader>y"));
    }

    [Fact]
    public void Unmap_RemovesFromAllModes()
    {
        var normalMaps = new Dictionary<string, string> { ["<Leader>t"] = ":tabnew<CR>" };
        var insertMaps = new Dictionary<string, string> { ["<Leader>t"] = "<Esc>:tabnew<CR>" };
        var visualMaps = new Dictionary<string, string> { ["<Leader>t"] = ":tabnew<CR>" };
        var processor = CreateProcessorWithMaps(normalMaps, insertMaps, visualMaps);

        var result = processor.Execute("unmap <Leader>t", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.False(normalMaps.ContainsKey("<Leader>t"));
        Assert.False(insertMaps.ContainsKey("<Leader>t"));
        Assert.False(visualMaps.ContainsKey("<Leader>t"));
    }

    [Fact]
    public void Nunmap_NonExistentKey_StillSucceeds()
    {
        var processor = CreateProcessorWithMaps();

        var result = processor.Execute("nunmap nonexistent", CursorPosition.Zero);

        Assert.True(result.Success);
    }

    // ── :nmap/:imap/:vmap/:map no-args listing tests ─────────────────────────

    [Fact]
    public void Nmap_NoArgs_ListsNormalMappings()
    {
        var normalMaps = new Dictionary<string, string> { ["\\w"] = ":w<CR>" };
        var processor = CreateProcessorWithMaps(normalMaps: normalMaps);

        var result = processor.Execute("nmap", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("\\w", result.Message);
        Assert.Contains(":w<CR>", result.Message);
        Assert.Contains("n  ", result.Message);
    }

    [Fact]
    public void Imap_NoArgs_ListsInsertMappings()
    {
        var insertMaps = new Dictionary<string, string> { ["jk"] = "<Esc>" };
        var processor = CreateProcessorWithMaps(insertMaps: insertMaps);

        var result = processor.Execute("imap", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("jk", result.Message);
        Assert.Contains("<Esc>", result.Message);
        Assert.Contains("i  ", result.Message);
    }

    [Fact]
    public void Vmap_NoArgs_ListsVisualMappings()
    {
        var visualMaps = new Dictionary<string, string> { ["<Leader>y"] = "\"+y" };
        var processor = CreateProcessorWithMaps(visualMaps: visualMaps);

        var result = processor.Execute("vmap", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("<Leader>y", result.Message);
        Assert.Contains("\"+y", result.Message);
        Assert.Contains("v  ", result.Message);
    }

    [Fact]
    public void Nmap_NoArgs_NoMappings_ReturnsNoMappingsMessage()
    {
        var processor = CreateProcessorWithMaps();

        var result = processor.Execute("nmap", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.Contains("(no mappings)", result.Message);
    }

    [Fact]
    public void Map_NoArgs_ListsAllModes()
    {
        var normalMaps = new Dictionary<string, string> { ["\\w"] = ":w<CR>" };
        var insertMaps = new Dictionary<string, string> { ["jk"] = "<Esc>" };
        var visualMaps = new Dictionary<string, string> { ["\\y"] = "\"+y" };
        var processor = CreateProcessorWithMaps(normalMaps, insertMaps, visualMaps);

        var result = processor.Execute("map", CursorPosition.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.Message);
        Assert.Contains("n  \\w", result.Message);
        Assert.Contains("i  jk", result.Message);
        Assert.Contains("v  \\y", result.Message);
    }
}
