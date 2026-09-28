using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

/// <summary>
/// ホスト指定の読み取り専用（<see cref="VimEngine.IsReadOnly"/>）。差分の旧側のように「読んで
/// ヤンクはするが書き換えない」面のためのもので、移動・選択・ヤンクは通り、本文を変える操作だけが断られる。
/// 入口で断らない経路（Ex コマンド・挿入の入力）も含めて、本文が1文字も変わらないことを確かめる。
/// </summary>
public class ReadOnlyEngineTests
{
    private const string Text = "alpha\nbeta\ngamma";

    private static VimEngine ReadOnlyEngine()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(Text);
        engine.IsReadOnly = true;
        return engine;
    }

    private static void Keys(VimEngine engine, params string[] keys)
    {
        foreach (var key in keys)
            engine.ProcessKey(key);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("d", "d")]
    [InlineData("i", "Z", "Escape")]
    [InlineData("o", "Z", "Escape")]
    [InlineData("A", "Z", "Escape")]
    [InlineData(">", ">")]
    [InlineData("J")]
    [InlineData("~")]
    [InlineData("r", "Z")]
    [InlineData("c", "w", "Z", "Escape")]
    [InlineData("V", "d")]
    [InlineData("v", "l", "x")]
    public void Editing_keys_leave_the_text_untouched(params string[] keys)
    {
        var engine = ReadOnlyEngine();

        Keys(engine, keys);

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
        Assert.False(engine.CurrentBuffer.Text.IsModified);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Increment_and_decrement_leave_the_number_alone(bool increment)
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText("x = 41");
        engine.IsReadOnly = true;

        engine.ProcessKey(increment ? "a" : "x", ctrl: true);

        Assert.Equal("x = 41", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Dot_repeat_of_an_edit_made_before_becoming_read_only_is_refused()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(Text);
        Keys(engine, "x");
        engine.IsReadOnly = true;

        Keys(engine, "j", ".");

        Assert.Equal("lpha\nbeta\ngamma", engine.CurrentBuffer.Text.GetText());
    }

    [Theory]
    [InlineData("g", "-")]
    [InlineData("g", "+")]
    public void Time_travel_undo_is_refused(params string[] keys)
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(Text);
        Keys(engine, "x");
        engine.IsReadOnly = true;

        Keys(engine, keys);

        Assert.Equal("lpha\nbeta\ngamma", engine.CurrentBuffer.Text.GetText());
    }

    /// <summary>Vim を切った読み取り専用で断るのは本文を変えるキーだけ。関係の無いキーに E21 を出さない。</summary>
    [Theory]
    [InlineData("PageDown", false)]
    [InlineData("F5", false)]
    [InlineData("a", true)]
    public void Plain_mode_only_complains_about_editing_keys(string key, bool ctrl)
    {
        var engine = ReadOnlyEngine();
        engine.SetVimEnabled(false);

        engine.ProcessKey(key, ctrl: ctrl);

        Assert.DoesNotContain("E21", engine.StatusMessage);
        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
    }

    [Theory]
    [InlineData("v", true)]
    [InlineData("x", true)]
    [InlineData("Return", false)]
    [InlineData("Delete", false)]
    [InlineData("Space", false)]
    public void Plain_mode_editing_keys_are_refused(string key, bool ctrl)
    {
        var engine = ReadOnlyEngine();
        engine.SetVimEnabled(false);
        engine.SetClipboardProvider(new StubClipboard("pasted"));

        engine.ProcessKey(key, ctrl: ctrl);

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
    }

    private sealed class StubClipboard(string text) : Editor.Core.Registers.IClipboardProvider
    {
        public string GetText() => text;
        public void SetText(string value) { }
    }

    [Theory]
    [InlineData("%d")]
    [InlineData("s/a/Z/g")]
    [InlineData("%s/a/Z/g")]
    [InlineData("normal! x")]
    [InlineData("1,2m$")]
    public void Ex_commands_that_edit_are_rolled_back(string command)
    {
        var engine = ReadOnlyEngine();

        engine.ExecuteExCommand(command);

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Host_paste_is_refused()
    {
        var engine = ReadOnlyEngine();

        engine.PasteText("pasted");

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Moving_and_yanking_still_work()
    {
        var engine = ReadOnlyEngine();

        Keys(engine, "j", "y", "y");
        Assert.Equal(new CursorPosition(1, 0), engine.Cursor);

        // ヤンクした行が本当にレジスタへ入っていることを、読み取り専用を外して貼って確かめる。
        engine.IsReadOnly = false;
        Keys(engine, "G", "p");
        Assert.Equal("alpha\nbeta\ngamma\nbeta", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Visual_selection_can_be_made_and_yanked()
    {
        var engine = ReadOnlyEngine();

        Keys(engine, "v", "e");
        Assert.Equal("alpha", engine.GetSelectionText());
        Keys(engine, "y");

        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Undo_does_not_rewind_edits_made_before_becoming_read_only()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(Text);
        Keys(engine, "x");
        engine.IsReadOnly = true;

        Keys(engine, "u");

        Assert.Equal("lpha\nbeta\ngamma", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Host_can_still_replace_the_text()
    {
        var engine = ReadOnlyEngine();

        engine.SetText("replaced");

        Assert.Equal("replaced", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Plain_mode_typing_is_refused_but_navigation_works()
    {
        var engine = ReadOnlyEngine();
        engine.SetVimEnabled(false);

        Keys(engine, "Right", "Z", "Back");

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
        Assert.Equal(new CursorPosition(0, 1), engine.Cursor);
    }
}
