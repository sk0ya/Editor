using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Core.Tests;

/// <summary>
/// ホストの編集ガード（<see cref="VimEngine.EditGuard"/>）。抜粋ビューの見出し行のように文書の一部だけを
/// 守る。断った編集は本文に残らず、差分の通知（<see cref="TextBuffer.Changed"/>）にも一度も流れない——
/// 流れると、変更を別の文書へ写しているホストが「編集→巻き戻し」の両方を写してしまう。
/// </summary>
public class HostEditGuardTests
{
    private const string Text = "== header ==\nbody one\nbody two";

    /// <summary>1行目（見出し）に触れる編集を断るガード。</summary>
    private static string? ProtectFirstLine(IReadOnlyList<string> before, IReadOnlyList<string> after)
        => after.Count == 0 || after[0] != before[0] ? "見出し行は編集できません" : null;

    private static (VimEngine Engine, List<TextBufferChange> Changes) Guarded()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(Text);
        engine.EditGuard = ProtectFirstLine;
        var changes = new List<TextBufferChange>();
        engine.CurrentBuffer.Text.Changed += changes.Add;
        return (engine, changes);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("d", "d")]
    [InlineData("J")]
    [InlineData("A", "!", "Escape")]
    public void 守られた行への編集は巻き戻され差分も流れない(params string[] keys)
    {
        var (engine, changes) = Guarded();
        foreach (var key in keys) engine.ProcessKey(key);

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
        Assert.Empty(changes);
        Assert.Contains("見出し行は編集できません", engine.StatusMessage);
    }

    [Fact]
    public void 守られていない行の編集は通り差分が流れる()
    {
        var (engine, changes) = Guarded();
        engine.ProcessKey("j");
        engine.ProcessKey("x");

        Assert.Equal("== header ==\nody one\nbody two", engine.CurrentBuffer.Text.GetText());
        Assert.Single(changes);
        Assert.Equal(new TextBufferChange(1, 0, 1, 1, "", changes[0].Version), changes[0]);
    }

    [Fact]
    public void 断られたddの行はレジスタに残らない()
    {
        var (engine, _) = Guarded();
        engine.ProcessKey("d");
        engine.ProcessKey("d");
        // 本文の行（守られていない）で貼り付けても、断られた見出し行は出てこない。
        engine.ProcessKey("j");
        engine.ProcessKey("p");

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void 断られたccはInsertモードに残らない()
    {
        var (engine, _) = Guarded();
        engine.ProcessKey("c");
        engine.ProcessKey("c");

        Assert.Equal(VimMode.Normal, engine.Mode);
        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
        Assert.Contains("見出し行は編集できません", engine.StatusMessage);
    }

    [Fact]
    public void 守られていない行のccはInsertモードに入る()
    {
        var (engine, _) = Guarded();
        engine.ProcessKey("j");
        engine.ProcessKey("c");
        engine.ProcessKey("c");

        Assert.Equal(VimMode.Insert, engine.Mode);
        Assert.Equal("== header ==\n\nbody two", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void リンク編集の写し先が守られた行なら写さない()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText("<a>\nx\n</a>");
        engine.CurrentBuffer.FilePath = "C:\\t\\View.xaml";
        // 最終行（閉じタグ）だけを守る。
        engine.EditGuard = (before, after) => after[^1] != before[^1] ? "閉じタグは編集できません" : null;
        var changes = new List<TextBufferChange>();
        engine.CurrentBuffer.Text.Changed += changes.Add;

        engine.SetCursorPosition(new CursorPosition(0, 2));
        engine.ProcessKey("i");
        engine.ProcessKey("b");

        Assert.Equal("<ab>\nx\n</a>", engine.CurrentBuffer.Text.GetText());
        Assert.Empty(engine.LinkedEditingRanges);
        Assert.All(changes, c => Assert.Equal(0, c.StartLine));
    }

    [Fact]
    public void 読み取り専用で断った編集も差分として流れない()
    {
        var engine = new VimEngine(new VimConfig());
        engine.SetText(Text);
        engine.IsReadOnly = true;
        var changes = new List<TextBufferChange>();
        engine.CurrentBuffer.Text.Changed += changes.Add;

        foreach (var key in new[] { "i", "x", "Escape", ":", "s", "/", "b", "/", "B", "/", "Enter" })
            engine.ProcessKey(key);

        Assert.Equal(Text, engine.CurrentBuffer.Text.GetText());
        Assert.Empty(changes);
    }
}
