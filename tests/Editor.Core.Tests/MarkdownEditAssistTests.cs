using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Editing;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class MarkdownEditAssistTests
{
    private static (MarkdownEditAssist assist, TextBuffer buf) Setup(string text)
    {
        var buf = new TextBuffer();
        buf.SetText(text);
        return (new MarkdownEditAssist(), buf);
    }

    private static EditContext Ctx(TextBuffer buf, int line, int col, bool expandTab = true, int shiftWidth = 2)
        => new(buf, new CursorPosition(line, col), "notes.md", shiftWidth, expandTab);

    // ── AppliesTo ──

    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("README.markdown", true)]
    [InlineData("code.cs", false)]
    [InlineData(null, false)]
    public void AppliesTo_OnlyMarkdownExtensions(string? path, bool expected)
        => Assert.Equal(expected, new MarkdownEditAssist().AppliesTo(path));

    // ── OnEnter: continuation ──

    [Fact]
    public void OnEnter_ContinuesUnorderedList()
    {
        var (assist, buf) = Setup("- item");
        var r = assist.OnEnter(Ctx(buf, 0, 6));
        Assert.True(r.Handled);
        Assert.Equal("- item\n- ", buf.GetText());
        Assert.Equal(new CursorPosition(1, 2), r.Cursor);
    }

    [Fact]
    public void OnEnter_IncrementsOrderedList()
    {
        var (assist, buf) = Setup("1. first");
        var r = assist.OnEnter(Ctx(buf, 0, 8));
        Assert.True(r.Handled);
        Assert.Equal("1. first\n2. ", buf.GetText());
        Assert.Equal(new CursorPosition(1, 3), r.Cursor);
    }

    [Fact]
    public void OnEnter_PreservesIndentAndMarkerStyle()
    {
        var (assist, buf) = Setup("    * nested");
        var r = assist.OnEnter(Ctx(buf, 0, 12));
        Assert.True(r.Handled);
        Assert.Equal("    * nested\n    * ", buf.GetText());
    }

    [Fact]
    public void OnEnter_OrderedWithParenDelimiter()
    {
        var (assist, buf) = Setup("3) item");
        var r = assist.OnEnter(Ctx(buf, 0, 7));
        Assert.Equal("3) item\n4) ", buf.GetText());
    }

    [Fact]
    public void OnEnter_SplitsContentAtCursor()
    {
        var (assist, buf) = Setup("- abcdef");
        var r = assist.OnEnter(Ctx(buf, 0, 5)); // between "abc" and "def"
        Assert.Equal("- abc\n- def", buf.GetText());
        Assert.Equal(new CursorPosition(1, 2), r.Cursor);
    }

    // ── OnEnter: exit on empty item ──

    [Fact]
    public void OnEnter_EmptyItem_ClearsMarkerAndExits()
    {
        var (assist, buf) = Setup("- ");
        var r = assist.OnEnter(Ctx(buf, 0, 2));
        Assert.True(r.Handled);
        Assert.Equal("", buf.GetText());
        Assert.Equal(new CursorPosition(0, 0), r.Cursor);
    }

    [Fact]
    public void OnEnter_EmptyOrderedItem_ClearsMarkerAndExits()
    {
        var (assist, buf) = Setup("2. ");
        var r = assist.OnEnter(Ctx(buf, 0, 3));
        Assert.True(r.Handled);
        Assert.Equal("", buf.GetText());
    }

    // ── OnEnter: declines ──

    [Fact]
    public void OnEnter_NonListLine_NotHandled()
    {
        var (assist, buf) = Setup("plain text");
        Assert.False(assist.OnEnter(Ctx(buf, 0, 10)).Handled);
    }

    [Fact]
    public void OnEnter_CursorInsideMarker_NotHandled()
    {
        var (assist, buf) = Setup("- item");
        Assert.False(assist.OnEnter(Ctx(buf, 0, 1)).Handled); // caret on the dash
    }

    // ── OnTab: indent / outdent ──

    [Fact]
    public void OnTab_IndentsEmptyItemWithSpaces()
    {
        var (assist, buf) = Setup("- ");
        var r = assist.OnTab(Ctx(buf, 0, 2, expandTab: true, shiftWidth: 2), shift: false);
        Assert.True(r.Handled);
        Assert.Equal("  - ", buf.GetText());
        Assert.Equal(new CursorPosition(0, 4), r.Cursor);
    }

    [Fact]
    public void OnTab_IndentsWithTabWhenNoExpandTab()
    {
        var (assist, buf) = Setup("- item");
        var r = assist.OnTab(Ctx(buf, 0, 6, expandTab: false), shift: false);
        Assert.Equal("\t- item", buf.GetText());
    }

    [Fact]
    public void OnTab_ShiftOutdentsOneLevel()
    {
        var (assist, buf) = Setup("    - item");
        var r = assist.OnTab(Ctx(buf, 0, 6, shiftWidth: 2), shift: true);
        Assert.True(r.Handled);
        Assert.Equal("  - item", buf.GetText());
        Assert.Equal(new CursorPosition(0, 4), r.Cursor);
    }

    [Fact]
    public void OnTab_ShiftAtZeroIndent_HandledButNoChange()
    {
        var (assist, buf) = Setup("- item");
        var r = assist.OnTab(Ctx(buf, 0, 4), shift: true);
        Assert.True(r.Handled);
        Assert.Equal("- item", buf.GetText());
    }

    [Fact]
    public void OnTab_NonListLine_NotHandled()
    {
        var (assist, buf) = Setup("plain");
        Assert.False(assist.OnTab(Ctx(buf, 0, 5), shift: false).Handled);
    }

    // ── OpenLinePrefix: o / O ──

    [Fact]
    public void OpenLinePrefix_Below_RepeatsUnorderedMarker()
    {
        var (assist, buf) = Setup("  - item");
        Assert.Equal("  - ", assist.OpenLinePrefix(Ctx(buf, 0, 8), above: false));
    }

    [Fact]
    public void OpenLinePrefix_Below_AdvancesOrdinal()
    {
        var (assist, buf) = Setup("1. first");
        Assert.Equal("2. ", assist.OpenLinePrefix(Ctx(buf, 0, 8), above: false));
    }

    [Fact]
    public void OpenLinePrefix_Above_KeepsOrdinal()
    {
        var (assist, buf) = Setup("1. first");
        Assert.Equal("1. ", assist.OpenLinePrefix(Ctx(buf, 0, 8), above: true));
    }

    [Fact]
    public void OpenLinePrefix_NonListLine_ReturnsNull()
    {
        var (assist, buf) = Setup("plain");
        Assert.Null(assist.OpenLinePrefix(Ctx(buf, 0, 5), above: false));
    }

    // ── Engine integration ──

    private static VimEngine CreateMdEngine(string text)
    {
        var engine = new VimEngine(new VimConfig());
        engine.CurrentBuffer.FilePath = "doc.md";
        engine.SetText(text);
        return engine;
    }

    [Fact]
    public void Engine_EnterContinuesListInInsertMode()
    {
        var engine = CreateMdEngine("- item");
        engine.ProcessKey("A", false, false, false);   // append at end of line → Insert
        engine.ProcessKey("Return", false, false, false);
        Assert.Equal("- item\n- ", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_TabIndentsEmptyListItem()
    {
        var engine = CreateMdEngine("- ");
        engine.ProcessKey("A", false, false, false);   // caret after "- "
        engine.ProcessKey("Tab", false, false, false);
        Assert.Equal("  - ", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_OpensListItemBelowWithO()
    {
        var engine = CreateMdEngine("- item");
        engine.ProcessKey("o", false, false, false);   // open line below → Insert
        Assert.Equal("- item\n- ", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(new CursorPosition(1, 2), engine.Cursor);
    }

    [Fact]
    public void Engine_OpensOrderedItemBelowWithO()
    {
        var engine = CreateMdEngine("1. first");
        engine.ProcessKey("o", false, false, false);
        Assert.Equal("1. first\n2. ", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Engine_OpensListItemAboveWithShiftO()
    {
        var engine = CreateMdEngine("- item");
        engine.ProcessKey("O", false, false, true);    // open line above → Insert
        Assert.Equal("- \n- item", engine.CurrentBuffer.Text.GetText());
        Assert.Equal(new CursorPosition(0, 2), engine.Cursor);
    }

    [Fact]
    public void Engine_NonMarkdownFileUnaffected()
    {
        var engine = new VimEngine(new VimConfig());
        engine.CurrentBuffer.FilePath = "code.txt";
        engine.SetText("- item");
        engine.ProcessKey("A", false, false, false);
        engine.ProcessKey("Return", false, false, false);
        Assert.Equal("- item\n", engine.CurrentBuffer.Text.GetText());
    }
}
