using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

/// <summary>
/// Emoji like 🖼️ (U+1F5BC FRAME WITH PICTURE + U+FE0F variation selector) are 3 UTF-16
/// <c>char</c> units for one visible character. Cursor-stepping and character-wise
/// deletion must treat that whole cluster as a single step, not split it.
/// </summary>
public class GraphemeClusterMotionTests
{
    // "a" + 🖼️ (U+1F5BC + U+FE0F, 3 UTF-16 units) + "b"
    private static readonly string Pic = char.ConvertFromUtf32(0x1F5BC) + "️";
    private static readonly string Text = "a" + Pic + "b";

    private static VimEngine CreateEngine(string text)
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(text);
        return engine;
    }

    [Fact]
    public void L_FromBeforeEmoji_SkipsWholeClusterInOneStep()
    {
        var engine = CreateEngine(Text);
        engine.ProcessKey("l"); // a(0) -> cluster start (1)

        Assert.Equal(new CursorPosition(0, 1), engine.Cursor);

        engine.ProcessKey("l"); // cluster start (1) -> 'b' (4)

        Assert.Equal(new CursorPosition(0, 1 + Pic.Length), engine.Cursor);
    }

    [Fact]
    public void H_FromAfterEmoji_SkipsWholeClusterInOneStep()
    {
        var engine = CreateEngine(Text);
        engine.ProcessKey("$"); // land on 'b'
        engine.ProcessKey("h"); // 'b' -> cluster start

        Assert.Equal(new CursorPosition(0, 1), engine.Cursor);
    }

    [Fact]
    public void X_OnEmojiCluster_DeletesWholeClusterNotHalf()
    {
        var engine = CreateEngine(Text);
        engine.ProcessKey("l"); // onto cluster start
        engine.ProcessKey("x");

        Assert.Equal("ab", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Backspace_AfterEmojiCluster_DeletesWholeClusterNotHalf()
    {
        var engine = CreateEngine(Text);
        engine.ProcessKey("A"); // end of line, insert mode
        engine.ProcessKey("Back"); // delete 'b'
        engine.ProcessKey("Back"); // delete the whole emoji cluster in one press

        Assert.Equal("a", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Delete_OnEmojiCluster_DeletesWholeClusterNotHalf()
    {
        var engine = CreateEngine(Text);
        engine.ProcessKey("l"); // Normal-mode move onto cluster start
        engine.ProcessKey("i"); // enter Insert mode, cursor stays before the cluster
        engine.ProcessKey("Delete");

        Assert.Equal("ab", engine.CurrentBuffer.Text.GetText());
    }
}
