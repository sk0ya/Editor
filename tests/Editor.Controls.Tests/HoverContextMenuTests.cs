using System.Windows.Controls;

namespace Editor.Controls.Tests;

/// <summary>
/// ホバーの説明ポップアップの右クリックメニュー。
///
/// <para>メニューそのものは Popup の中でも問題なく開いて留まる（実測で8通り確認済み）。
/// 出なかった原因は<b>こちら側</b>——メニューが出た拍子にポップアップの <c>MouseLeave</c> が走り、
/// ポップアップが閉じてメニューが道連れになっていた。だから守るべき不変条件は
/// 「メニューが出ている間は閉じる推測を止める」こと。</para>
/// </summary>
public class HoverContextMenuTests
{
    [Fact]
    public void Menu_OffersCopySelectPinAndClose()
    {
        WithHover(editor =>
        {
            var headers = Headers(editor.BuildHoverContextMenuForTest());

            Assert.Contains("すべてコピー", headers);
            Assert.Contains("すべて選択", headers);
            Assert.Contains("ピン留め", headers);
            Assert.Contains("閉じる", headers);
        });
    }

    /// <summary>何がコピーされるのかを名前で言い切る（「コピー」だけでは選んだ分か全体か分からない）。</summary>
    [Fact]
    public void Menu_NamesWhatTheCopyWillTake()
    {
        WithHover(editor =>
        {
            Assert.Contains("すべてコピー", Headers(editor.BuildHoverContextMenuForTest()));

            editor.SelectAllHoverTextForTest();

            Assert.Contains("選択範囲をコピー", Headers(editor.BuildHoverContextMenuForTest()));
        });
    }

    [Fact]
    public void Menu_ShowsUnpinWhilePinned()
    {
        WithHover(editor =>
        {
            editor.SendHoverPopupKeyForTest(
                System.Windows.Input.Key.K,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);

            Assert.Contains("ピン留めを解除", Headers(editor.BuildHoverContextMenuForTest()));
        });
    }

    /// <summary>本命：メニューが出ている間は、マウスが離れてもポップアップを閉じない。
    /// ここが効いていないと、メニューへマウスを移した瞬間に土台ごと消える。</summary>
    [Fact]
    public void Hover_WithTheMenuOpen_SurvivesThePointerLeaving()
    {
        WithHover(editor =>
        {
            editor.SetPointerInHoverPopupForTest(true);
            editor.SetHoverContextMenuOpenForTest(true);

            editor.SetPointerInHoverPopupForTest(false);

            Assert.NotNull(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>非アクティブ化（メニューは別ウィンドウ）でも同じ。</summary>
    [Fact]
    public void Hover_WithTheMenuOpen_SurvivesWindowDeactivation()
    {
        WithHover(editor =>
        {
            editor.SetHoverContextMenuOpenForTest(true);

            editor.RaiseWindowDeactivatedForTest();

            Assert.NotNull(editor.HoverPopupContentForTest);
        });
    }

    private static List<string> Headers(ContextMenu menu) =>
        [.. menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "")];

    private static void WithHover(Action<VimEditorControl> action)
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.HoverResult = "```csharp\nint Count\n```\n\nこの要素の個数です。";
            editor.SetText("var count = 1;");

            _ = editor.ShowHoverInfoForTestAsync(0, 4);
            LspCompletionTestHarness.Pump();
            Assert.NotNull(editor.HoverPopupContentForTest);

            action(editor);
        });
    }
}
