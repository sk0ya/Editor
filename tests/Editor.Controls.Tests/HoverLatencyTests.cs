namespace Editor.Controls.Tests;

/// <summary>
/// ホバーが出るまでの速さ。実測（<c>SK0YA_EDITOR_IDE_DIAG=1</c> のログ）では
/// 温まった言語サーバーの応答が 3〜55ms、組み立てが 4〜83ms で、待ちを 400ms にしていた頃は
/// <b>出るまでの 77〜98% が固定の待ち</b>だった。冷えている初回だけは別物で、Roslyn が
/// ソリューションを読む 5.5 秒がまるごと乗る。
/// </summary>
public class HoverLatencyTests
{
    /// <summary>待ちは応答時間に対して支配的にならない値であること。上げ直すときは、
    /// 実測（request 3〜55ms）に照らして納得できる根拠と一緒に。</summary>
    [Fact]
    public void Dwell_IsNotTheDominantPartOfTheWait()
    {
        WpfTestHost.Run(() =>
        {
            using var editor = new VimEditorControl();

            Assert.InRange(editor.HoverInfoDelayMs, 1, 300);
        });
    }

    /// <summary>ホストは待ちを変えられる（速い環境ではもっと詰められる）。</summary>
    [Fact]
    public void Dwell_CanBeSetByTheHost()
    {
        WpfTestHost.Run(() =>
        {
            using var editor = new VimEditorControl(
                new VimEditorControlOptions { HoverInfoDelayMs = 120 });

            Assert.Equal(120, editor.HoverInfoDelayMs);
        });
    }

    /// <summary>負の待ちは受け取らない（0 は「止まった瞬間に投げる」として許す）。</summary>
    [Fact]
    public void Dwell_NeverGoesNegative()
    {
        WpfTestHost.Run(() =>
        {
            using var editor = new VimEditorControl { HoverInfoDelayMs = -50 };

            Assert.Equal(0, editor.HoverInfoDelayMs);
        });
    }
}
