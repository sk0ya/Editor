using WVim.Core.Buffer;
using WVim.Core.Models;

namespace WVim.Core.Tests;

public class TextBufferTests
{
    [Fact]
    public void InsertChar_AddsCharAtPosition()
    {
        var buf = new TextBuffer("hello");
        buf.InsertChar(0, 5, '!');
        Assert.Equal("hello!", buf.GetLine(0));
    }

    [Fact]
    public void DeleteChar_RemovesCharAtPosition()
    {
        var buf = new TextBuffer("hello");
        buf.DeleteChar(0, 1);
        Assert.Equal("hllo", buf.GetLine(0));
    }

    [Fact]
    public void BreakLine_SplitsLineAtColumn()
    {
        var buf = new TextBuffer("hello world");
        buf.BreakLine(0, 5);
        Assert.Equal(2, buf.LineCount);
        Assert.Equal("hello", buf.GetLine(0));
        Assert.Equal(" world", buf.GetLine(1));
    }

    [Fact]
    public void JoinLines_MergesConsecutiveLines()
    {
        var buf = new TextBuffer("hello\nworld");
        buf.JoinLines(0);
        Assert.Equal(1, buf.LineCount);
        Assert.Equal("helloworld", buf.GetLine(0));
    }

    [Fact]
    public void DeleteLines_RemovesLines()
    {
        var buf = new TextBuffer("line1\nline2\nline3");
        buf.DeleteLines(0, 1);
        Assert.Equal(1, buf.LineCount);
        Assert.Equal("line3", buf.GetLine(0));
    }

    [Fact]
    public void FindNext_ReturnsCorrectPosition()
    {
        var buf = new TextBuffer("hello world\nhello again");
        // FindNext searches from AFTER current position (like Vim's n command)
        // From (0,0), the next "hello" after col 0 is on line 1
        var pos = buf.FindNext("hello", new CursorPosition(0, 0), forward: true);
        Assert.NotNull(pos);
        Assert.Equal(1, pos!.Value.Line);
        Assert.Equal(0, pos!.Value.Column);

        // From line 1, col 0, wrapping around finds line 0
        pos = buf.FindNext("hello", pos!.Value, forward: true);
        Assert.NotNull(pos);
        Assert.Equal(0, pos!.Value.Line);
    }

    [Fact]
    public void ClampCursor_NormalMode_StaysWithinLine()
    {
        var buf = new TextBuffer("abc");
        var pos = buf.ClampCursor(new CursorPosition(0, 10));
        Assert.Equal(2, pos.Column); // max col for "abc" in normal mode
    }

    [Fact]
    public void ClampCursor_InsertMode_CanGoToEnd()
    {
        var buf = new TextBuffer("abc");
        var pos = buf.ClampCursor(new CursorPosition(0, 10), insertMode: true);
        Assert.Equal(3, pos.Column); // insert mode can go to end
    }
}
