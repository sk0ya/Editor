using Editor.Core.Buffer;
using Editor.Core.Editing;
using Editor.Core.Models;

namespace Editor.Core.Tests;

public class CStyleCommentEditAssistTests
{
    private static (CStyleCommentEditAssist assist, TextBuffer buf) Setup(string text)
    {
        var buf = new TextBuffer();
        buf.SetText(text);
        return (new CStyleCommentEditAssist(), buf);
    }

    private static EditContext Ctx(TextBuffer buf, int line, int col, string filePath, bool expandTab = true, int shiftWidth = 2)
        => new(buf, new CursorPosition(line, col), filePath, shiftWidth, expandTab);

    // ── AppliesTo ──

    [Theory]
    [InlineData("main.cs", true)]
    [InlineData("app.js", true)]
    [InlineData("app.ts", true)]
    [InlineData("lib.rs", true)]
    [InlineData("main.go", true)]
    [InlineData("main.cpp", true)]
    [InlineData("style.css", true)]
    [InlineData("style.scss", true)]
    [InlineData("notes.md", false)]
    [InlineData("script.py", false)]
    [InlineData(null, false)]
    public void AppliesTo_ExpectedExtensions(string? path, bool expected)
        => Assert.Equal(expected, new CStyleCommentEditAssist().AppliesTo(path));

    // ── OnEnter: line comment continuation ──

    [Fact]
    public void OnEnter_ContinuesLineComment_Cs()
    {
        var (assist, buf) = Setup("// hello");
        var r = assist.OnEnter(Ctx(buf, 0, 8, "main.cs"));
        Assert.True(r.Handled);
        Assert.Equal("// hello\n// ", buf.GetText());
        Assert.Equal(new CursorPosition(1, 3), r.Cursor);
    }

    [Fact]
    public void OnEnter_ContinuesLineComment_Ts_PreservesIndent()
    {
        var (assist, buf) = Setup("    // note");
        var r = assist.OnEnter(Ctx(buf, 0, 11, "app.ts"));
        Assert.True(r.Handled);
        Assert.Equal("    // note\n    // ", buf.GetText());
        Assert.Equal(new CursorPosition(1, 7), r.Cursor);
    }

    [Fact]
    public void OnEnter_EmptyLineComment_ClearsLineAndExits()
    {
        var (assist, buf) = Setup("// ");
        var r = assist.OnEnter(Ctx(buf, 0, 3, "main.cs"));
        Assert.True(r.Handled);
        Assert.Equal("", buf.GetText());
        Assert.Equal(new CursorPosition(0, 0), r.Cursor);
    }

    [Fact]
    public void OnEnter_CursorInsideLineCommentMarker_NotHandled()
    {
        var (assist, buf) = Setup("// hello");
        Assert.False(assist.OnEnter(Ctx(buf, 0, 1, "main.cs")).Handled);
    }

    // ── OnEnter: block comment continuation ──

    [Fact]
    public void OnEnter_ContinuesBlockCommentOpener_WithAlignmentSpace()
    {
        var (assist, buf) = Setup("/** summary");
        var r = assist.OnEnter(Ctx(buf, 0, 11, "main.cs"));
        Assert.True(r.Handled);
        Assert.Equal("/** summary\n * ", buf.GetText());
        Assert.Equal(new CursorPosition(1, 3), r.Cursor);
    }

    [Fact]
    public void OnEnter_ContinuesBlockCommentBody_NoExtraSpace_Css()
    {
        var (assist, buf) = Setup(" * body line");
        var r = assist.OnEnter(Ctx(buf, 0, 12, "style.css"));
        Assert.True(r.Handled);
        Assert.Equal(" * body line\n * ", buf.GetText());
        Assert.Equal(new CursorPosition(1, 3), r.Cursor);
    }

    [Fact]
    public void OnEnter_Css_LineCommentSlashSlash_NotContinuedAsLineComment()
    {
        var (assist, buf) = Setup("// not a comment");
        var r = assist.OnEnter(Ctx(buf, 0, 17, "style.css"));
        // CSS has no // line comments, so this should not be handled at all.
        Assert.False(r.Handled);
    }

    // ── OnEnter: declines ──

    [Fact]
    public void OnEnter_NonCommentLine_NotHandled()
    {
        var (assist, buf) = Setup("var x = 1;");
        Assert.False(assist.OnEnter(Ctx(buf, 0, 10, "main.cs")).Handled);
    }

    [Fact]
    public void OnEnter_PointerDereferenceLine_NotMistakenForBlockCommentBody()
    {
        // "*ptr = 5;" is ordinary C/C++ code, not a "* ..." block-comment continuation line.
        var (assist, buf) = Setup("*ptr = 5;");
        Assert.False(assist.OnEnter(Ctx(buf, 0, 9, "main.c")).Handled);
    }

    // ── OpenLinePrefix: o / O ──

    [Fact]
    public void OpenLinePrefix_LineComment_ReturnsSlashSlashPrefix()
    {
        var (assist, buf) = Setup("  // note");
        Assert.Equal("  // ", assist.OpenLinePrefix(Ctx(buf, 0, 9, "main.cs"), above: false));
    }

    [Fact]
    public void OpenLinePrefix_BlockCommentOpener_ReturnsAlignedPrefix()
    {
        var (assist, buf) = Setup("/** doc");
        Assert.Equal(" * ", assist.OpenLinePrefix(Ctx(buf, 0, 7, "main.cs"), above: false));
    }

    [Fact]
    public void OpenLinePrefix_NonCommentLine_ReturnsNull()
    {
        var (assist, buf) = Setup("var x = 1;");
        Assert.Null(assist.OpenLinePrefix(Ctx(buf, 0, 10, "main.cs"), above: false));
    }
}
