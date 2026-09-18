using System.Windows.Input;

namespace Editor.Controls.Tests;

/// <summary>
/// ホバーの説明ポップアップをピン留めできること。
///
/// <para>留められなかった頃、この表示は<b>打鍵・ウィンドウの非アクティブ化・マウスが離れる</b>の
/// どれでも消えた。スクリーンショットを撮る操作（Win+Shift+S、PrintScreen）はその全部に当たるので、
/// 画面に出ている説明を画像にする方法が無かった——ここはその「消える経路」を一つずつ押さえる。</para>
/// </summary>
public class HoverPinTests
{
    /// <summary>ピンが無ければこれまでどおり。別のアプリへ切り替えたら引っ込む
    /// （<see cref="System.Windows.Controls.Primitives.Popup"/> は最前面に出るので、置き去りにしない）。</summary>
    [Fact]
    public void Hover_NotPinned_ClosesWhenTheWindowIsDeactivated()
    {
        WithHover((editor, _) =>
        {
            editor.RaiseWindowDeactivatedForTest();

            Assert.Null(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>本命：留めてあれば非アクティブ化で消えない——これがスクリーンショットを撮れる条件。</summary>
    [Fact]
    public void Hover_Pinned_SurvivesWindowDeactivation()
    {
        WithHover((editor, _) =>
        {
            Pin(editor);

            editor.RaiseWindowDeactivatedForTest();

            Assert.NotNull(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>留めてあれば打鍵でも消えない。閉じる合図は Escape だけ。</summary>
    [Fact]
    public void Hover_Pinned_SurvivesTyping_AndEscapeCloses()
    {
        WithHover((editor, _) =>
        {
            Pin(editor);

            LspCompletionTestHarness.RaisePreviewKeyDown(editor, Key.A);
            Assert.NotNull(editor.HoverPopupContentForTest);

            LspCompletionTestHarness.RaisePreviewKeyDown(editor, Key.Escape);
            Assert.Null(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>キャプチャツールへマウスを移す間に消えては意味が無い。</summary>
    [Fact]
    public void Hover_Pinned_SurvivesThePointerLeaving()
    {
        WithHover((editor, _) =>
        {
            editor.SetPointerInHoverPopupForTest(true);
            Pin(editor);

            editor.SetPointerInHoverPopupForTest(false);

            Assert.NotNull(editor.HoverPopupContentForTest);
        });
    }

    [Fact]
    public void Hover_NotPinned_ClosesWhenThePointerLeaves()
    {
        WithHover((editor, _) =>
        {
            editor.SetPointerInHoverPopupForTest(true);
            editor.SetPointerInHoverPopupForTest(false);

            Assert.Null(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>印は<b>マウスを乗せている間だけ</b>出す。幅を予約しないため、そして撮るころには
    /// マウスが抜けているので<b>画像に印が写らない</b>ため。</summary>
    [Fact]
    public void HoverChrome_IsHiddenUntilThePointerEntersThePopup()
    {
        WithHover((editor, _) =>
        {
            Assert.False(editor.HoverChromeVisibleForTest);

            editor.SetPointerInHoverPopupForTest(true);
            Assert.True(editor.HoverChromeVisibleForTest);
            Assert.NotNull(editor.HoverPinChipForTest);
            Assert.NotNull(editor.HoverCopyChipForTest);
        });
    }

    /// <summary>右上の 📌 を押しても、キー（Ctrl+Shift+K）と同じ入切になる。</summary>
    [Fact]
    public void PinChip_Click_TogglesThePin()
    {
        WithHover((editor, _) =>
        {
            editor.SetPointerInHoverPopupForTest(true);

            Click(editor.HoverPinChipForTest!);
            Assert.True(editor.HoverPinnedForTest);

            Click(editor.HoverPinChipForTest!);
            Assert.False(editor.HoverPinnedForTest);
            // マウスはまだポップアップの上。外した直後に消えはしない（「離れたら閉じる」へ戻るだけ）。
            Assert.NotNull(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>別の説明を出したらピンは引き継がない——留めたのは前の説明。</summary>
    [Fact]
    public void Hover_ShowingAnotherHover_DropsThePin()
    {
        WithHover((editor, lsp) =>
        {
            Pin(editor);

            lsp.HoverResult = "```csharp\nint Other\n```";
            _ = editor.ShowHoverInfoForTestAsync(0, 10);
            LspCompletionTestHarness.Pump();

            Assert.False(editor.HoverPinnedForTest);
        });
    }

    /// <summary>ピンが無い間は、これまでどおり打鍵で引っ込む（読んでいる最中の板を残さない）。</summary>
    [Fact]
    public void Hover_NotPinned_ClosesOnTyping()
    {
        WithHover((editor, _) =>
        {
            LspCompletionTestHarness.RaisePreviewKeyDown(editor, Key.A);

            Assert.Null(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>説明が出ているところで Ctrl+Shift+C を押すと、写しのキーとして使い切られる
    /// （本文の編集へは渡らない）。</summary>
    [Fact]
    public void Hover_CopyChord_IsConsumedByThePopup()
    {
        WithHover((editor, _) =>
        {
            var consumed = editor.SendHoverPopupKeyForTest(
                Key.C, ModifierKeys.Control | ModifierKeys.Shift);

            Assert.True(consumed);
            Assert.NotNull(editor.HoverPopupContentForTest);   // 写しただけで閉じない
        });
    }

    private static void Pin(VimEditorControl editor)
    {
        Assert.True(editor.SendHoverPopupKeyForTest(Key.K, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.True(editor.HoverPinnedForTest);
    }

    /// <summary>説明ポップアップを 1 つ出した状態を用意する。</summary>
    private static void WithHover(Action<VimEditorControl, FakeEditorLspView> action)
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.HoverResult = "```csharp\nint Count\n```";
            editor.SetText("var count = 1;");

            _ = editor.ShowHoverInfoForTestAsync(0, 4);
            LspCompletionTestHarness.Pump();
            Assert.NotNull(editor.HoverPopupContentForTest);

            action(editor, lsp);
        });
    }

    private static void Click(System.Windows.UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent,
        });
}
