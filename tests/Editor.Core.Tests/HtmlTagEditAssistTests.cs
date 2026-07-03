using Editor.Core.Buffer;
using Editor.Core.Editing;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class HtmlTagEditAssistTests
{
    private static (HtmlTagEditAssist assist, TextBuffer buf) Setup(string text)
    {
        var buf = new TextBuffer();
        buf.SetText(text);
        return (new HtmlTagEditAssist(), buf);
    }

    private static EditContext Ctx(TextBuffer buf, int line, int col, string filePath)
        => new(buf, new CursorPosition(line, col), filePath, 2, true);

    // ── AppliesTo ──

    [Theory]
    [InlineData("index.html", true)]
    [InlineData("page.htm", true)]
    [InlineData("data.xml", true)]
    [InlineData("App.xaml", true)]
    [InlineData("Component.jsx", true)]
    [InlineData("Component.tsx", true)]
    [InlineData("App.vue", true)]
    [InlineData("icon.svg", true)]
    [InlineData("main.cs", false)]
    [InlineData("script.py", false)]
    [InlineData(null, false)]
    public void AppliesTo_ExpectedExtensions(string? path, bool expected)
        => Assert.Equal(expected, new HtmlTagEditAssist().AppliesTo(path));

    // ── OnChar: auto-close ──

    [Fact]
    public void OnChar_OpenTag_InsertsClosingTag()
    {
        var (assist, buf) = Setup("<div");
        var r = assist.OnChar(Ctx(buf, 0, 4, "index.html"), '>');
        Assert.True(r.Handled);
        Assert.Equal("<div></div>", buf.GetText());
        Assert.Equal(new CursorPosition(0, 5), r.Cursor);
    }

    [Fact]
    public void OnChar_OpenTagWithAttributes_InsertsClosingTag()
    {
        var (assist, buf) = Setup("<a href=\"x\"");
        var r = assist.OnChar(Ctx(buf, 0, 11, "index.html"), '>');
        Assert.True(r.Handled);
        Assert.Equal("<a href=\"x\"></a>", buf.GetText());
        Assert.Equal(new CursorPosition(0, 12), r.Cursor);
    }

    // ── OnChar: declines ──

    [Fact]
    public void OnChar_SelfClosingTag_NotHandled()
    {
        var (assist, buf) = Setup("<br/");
        var r = assist.OnChar(Ctx(buf, 0, 4, "index.html"), '>');
        Assert.False(r.Handled);
        Assert.Equal("<br/", buf.GetText());
    }

    [Fact]
    public void OnChar_VoidElement_NotHandled()
    {
        var (assist, buf) = Setup("<img src=\"x\"");
        var r = assist.OnChar(Ctx(buf, 0, 13, "index.html"), '>');
        Assert.False(r.Handled);
        Assert.Equal("<img src=\"x\"", buf.GetText());
    }

    [Fact]
    public void OnChar_ExistingClosingTag_NotHandled()
    {
        var (assist, buf) = Setup("<div></div");
        var r = assist.OnChar(Ctx(buf, 0, 10, "index.html"), '>');
        Assert.False(r.Handled);
        Assert.Equal("<div></div", buf.GetText());
    }

    [Fact]
    public void OnChar_NonGtChar_NotHandled()
    {
        var (assist, buf) = Setup("<div");
        var r = assist.OnChar(Ctx(buf, 0, 4, "index.html"), 'x');
        Assert.False(r.Handled);
    }

    [Fact]
    public void OnChar_NonHtmlExtension_NotHandled()
    {
        var (assist, buf) = Setup("<div");
        var r = assist.OnChar(Ctx(buf, 0, 4, "main.cs"), '>');
        Assert.False(r.Handled);
    }

    [Fact]
    public void OnChar_CommentOpener_NotHandled()
    {
        var (assist, buf) = Setup("<!--");
        var r = assist.OnChar(Ctx(buf, 0, 4, "index.html"), '>');
        Assert.False(r.Handled);
    }

    [Fact]
    public void OnChar_NoOpenAngleBracket_NotHandled()
    {
        var (assist, buf) = Setup("hello");
        var r = assist.OnChar(Ctx(buf, 0, 5, "index.html"), '>');
        Assert.False(r.Handled);
    }
}
