using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Registers;

namespace Editor.Core.Tests;

/// <summary>
/// 行単位のヤンクがクリップボードを通っても行単位のまま届くこと。エディタはタブ・差分の左右ごとに
/// 別のエンジン（＝別のレジスタ）を持つので、エディタをまたぐヤンク＆ペーストはクリップボードだけが頼り。
/// Vim と同じく「行単位は末尾に改行を付けて出し、末尾が改行なら行単位として読む」。
/// </summary>
public class ClipboardLinewiseTests
{
    private sealed class SharedClipboard : IClipboardProvider
    {
        public string Text { get; set; } = "";
        public string GetText() => Text;
        public void SetText(string text) => Text = text;
    }

    private static VimEngine Engine(string text, SharedClipboard clipboard)
    {
        var config = new VimConfig();
        config.Options.Clipboard = "unnamed";
        var engine = new VimEngine(config);
        engine.SetText(text);
        engine.SetClipboardProvider(clipboard);
        return engine;
    }

    private static void Keys(VimEngine engine, params string[] keys)
    {
        foreach (var key in keys) engine.ProcessKey(key);
    }

    [Fact]
    public void Line_yanked_in_one_editor_is_pasted_as_a_line_in_another()
    {
        var clipboard = new SharedClipboard();
        var source = Engine("alpha\nbeta", clipboard);
        var target = Engine("one\ntwo", clipboard);

        Keys(source, "j", "y", "y");
        Keys(target, "p");

        Assert.Equal("beta\n", clipboard.Text);
        Assert.Equal("one\nbeta\ntwo", target.CurrentBuffer.Text.GetText());
    }

    /// <summary>最後の行が空の行ヤンク（<c>"a\n"</c> を持つ）も、空行を落とさずに届く。</summary>
    [Fact]
    public void Linewise_yank_ending_with_an_empty_line_keeps_it()
    {
        var clipboard = new SharedClipboard();
        var source = Engine("a\n\nb", clipboard);
        var target = Engine("x", clipboard);

        Keys(source, "y", "j");
        Keys(target, "p");

        Assert.Equal("x\na\n", target.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Deleted_line_travels_as_a_line_too()
    {
        var clipboard = new SharedClipboard();
        var source = Engine("alpha\nbeta", clipboard);
        var target = Engine("one\ntwo", clipboard);

        Keys(source, "d", "d");
        Keys(target, "P");

        Assert.Equal("alpha\none\ntwo", target.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Text_from_another_app_ending_with_a_newline_is_linewise()
    {
        var clipboard = new SharedClipboard { Text = "copied\r\n" };
        var target = Engine("one\ntwo", clipboard);

        Keys(target, "p");

        Assert.Equal("one\ncopied\ntwo", target.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Characterwise_yank_stays_characterwise()
    {
        var clipboard = new SharedClipboard();
        var source = Engine("alpha beta", clipboard);
        var target = Engine("xy", clipboard);

        Keys(source, "y", "w");
        Keys(target, "p");

        Assert.Equal("alpha ", clipboard.Text);
        Assert.Equal("xalpha y", target.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void Linewise_yank_into_the_plus_register_can_be_read_back_as_lines()
    {
        var clipboard = new SharedClipboard();
        var engine = Engine("alpha\nbeta", clipboard);

        Keys(engine, "\"", "+", "y", "y", "j", "\"", "+", "p");

        Assert.Equal("alpha\nbeta\nalpha", engine.CurrentBuffer.Text.GetText());
    }
}
