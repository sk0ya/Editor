using Editor.Core.Buffer;

namespace Editor.Core.Tests;

/// <summary>
/// <see cref="TextBuffer.GetText"/> joins the whole document, so it is computed once per
/// <see cref="TextBuffer.Version"/>. These pin both halves of that: the second ask for an unchanged
/// buffer must not do the work again, and every kind of mutation must invalidate it.
/// </summary>
public class TextBufferTextCacheTests
{
    [Fact]
    public void Asking_twice_without_an_edit_returns_the_same_string()
    {
        var buf = new TextBuffer("a\nb\nc");

        Assert.Same(buf.GetText(), buf.GetText());
    }

    [Fact]
    public void An_edit_makes_the_next_ask_see_the_new_text()
    {
        var buf = new TextBuffer("hello");
        var before = buf.GetText();

        buf.InsertChar(0, 5, '!');

        Assert.Equal("hello", before);
        Assert.Equal("hello!", buf.GetText());
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public void Every_mutation_invalidates_the_cached_text(string name, Action<TextBuffer> mutate)
    {
        var buf = new TextBuffer("one\ntwo\nthree");
        _ = buf.GetText();   // 先に作らせておく

        mutate(buf);

        Assert.Equal(string.Join("\n", Lines(buf)), buf.GetText());
        Assert.True(name.Length > 0);
    }

    public static TheoryData<string, Action<TextBuffer>> Mutations() => new()
    {
        { "InsertChar", b => b.InsertChar(0, 0, 'x') },
        { "InsertText", b => b.InsertText(1, 0, "ab\ncd") },
        { "BreakLine", b => b.BreakLine(1, 1) },
        { "DeleteChar", b => b.DeleteChar(0, 0) },
        { "DeleteRange", b => b.DeleteRange(0, 0, 2) },
        { "JoinLines", b => b.JoinLines(0) },
        { "DeleteLines", b => b.DeleteLines(0, 1) },
        { "InsertLines", b => b.InsertLines(1, ["inserted"]) },
        { "InsertLineAbove", b => b.InsertLineAbove(1, "above") },
        { "ReplaceLine", b => b.ReplaceLine(0, "replaced") },
        { "SetText", b => b.SetText("brand\nnew") },
        { "ReplaceAll", b => b.ReplaceAll("replaced\nwhole") },
        { "RestoreSnapshot", b => b.RestoreSnapshot(["restored"]) },
    };

    /// <summary>The document read line by line — i.e. without going through the cached join.</summary>
    private static IEnumerable<string> Lines(TextBuffer buf)
    {
        for (var i = 0; i < buf.LineCount; i++)
            yield return buf.GetLine(i);
    }

    [Fact]
    public void Marking_saved_does_not_change_the_text()
    {
        var buf = new TextBuffer("a\nb");
        var before = buf.GetText();

        buf.MarkSaved();

        Assert.Equal(before, buf.GetText());
    }
}
