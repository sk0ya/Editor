using Editor.Core.Buffer;
using Editor.Core.Config;
using Editor.Core.Engine;

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
