using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Controls.Rendering;
using Editor.Controls.Themes;
using Editor.Core.Lsp;
using Editor.Core.Text;

namespace Editor.Controls.Tests;

/// <summary>
/// ホバーの説明ポップアップの文字を選んで持ち出せること。
///
/// <para>中身は <c>TextBlock</c> を積んだものだったので、WPF では<b>一文字も選べなかった</b>——
/// 読めるのに取り出せず、シグネチャは手で打ち直すしかなかった。<c>FlowDocument</c> へ移して
/// 選べるようにしたが、その移動で<b>押せる行（電球と修正）が押せなくなっていないか</b>は
/// 合成イベントでは分からない（<c>RaiseEvent</c> は当たり判定を通らない）。ここは実際に
/// レイアウトしてヒットテストを撃ち、入力が表示器に吸われていないことを確かめる。</para>
/// </summary>
public class HoverSelectionTests
{
    /// <summary>本命その 1：押せる行は <see cref="Hyperlink"/> であること。
    ///
    /// <para>ここは以前、<see cref="BlockUIContainer"/> に入れた <c>Border</c> の<b>当たり判定</b>を
    /// 撃って「表示器が入力を吸っていない」と確かめていた。ヒットテストは通るのに、<b>実機のマウスでは
    /// 一度も押せていなかった</b>——選択を有効にした表示器では文書の選択層がマウスを先に取り、
    /// 埋め込み要素には <c>MouseEnter</c> すら届かない（強調も出なかった）。当たり判定は
    /// 入力の経路の証明にならない。文書の中で押せるのは選択層が通す <see cref="Hyperlink"/> だけなので、
    /// 形そのものを固定する。</para></summary>
    [Fact]
    public void FixRowAndBulb_ArePressableLinks_NotEmbeddedElements()
    {
        WpfTestHost.Run(() =>
        {
            var fix = new LspCodeAction("using を削除", LspCodeActionKinds.QuickFix, null);
            var document = HoverContentBuilder.Build(
                [Warning("CS8019: 不要な using")], HoverMarkdown.Parse("説明"),
                EditorTheme.Dracula, new FontFamily("Consolas"), 12, null,
                new HoverFixSection(
                    Show: true, Expanded: true, Loading: false, Loaded: true,
                    Fixes: [fix], Hidden: 0, OnToggle: () => { }, OnApply: _ => { }));

            Assert.IsType<Hyperlink>(HoverQuickFixTests.FindByTag(document, fix));
            Assert.IsType<Hyperlink>(
                HoverQuickFixTests.FindByTag(document, HoverContentBuilder.FixToggleTag));
        });
    }

    /// <summary>WPF のテキスト選択は<b>フォーカスを取れたときだけ</b>始まる。ここを false にすると
    /// ドラッグしても一文字も選べない——しかも例外も警告も出ず、他のどのテストも赤くならない
    /// （実測で判明：false は空、true は選択が返る）。焦点は
    /// <c>ReturnFocusToBuffer</c> がマウスを離した直後に本文へ返すので、借りるのは一瞬。</summary>
    [Fact]
    public void Viewer_IsFocusable_OrDraggingSelectsNothing()
    {
        WithHover(editor =>
        {
            var viewer = editor.HoverPopupViewerForTest;

            Assert.NotNull(viewer);
            Assert.True(viewer!.Focusable, "Focusable=false だとドラッグ選択が黙って効かなくなる");
            Assert.True(viewer.IsSelectionEnabled);
            Assert.False(viewer.IsTabStop, "Tab の巡回先にはしない（焦点はマウスのときだけ借りる）");
        });
    }

    /// <summary>焦点を本文へ返すと、WPF は既定で<b>選択の強調だけ</b>を消す（中身は残るので
    /// <c>Selection.Text</c> を見ていても気づけない）。実測では選択色の画素が 4032 → 0 になった。
    /// マウスを離した瞬間に選んだ範囲が見えなくなるので、選べないのとほぼ同じになる。</summary>
    [Fact]
    public void Selection_StaysVisibleAfterFocusGoesBackToTheBuffer()
    {
        WithHover(editor =>
        {
            var viewer = editor.HoverPopupViewerForTest;

            Assert.NotNull(viewer);
            Assert.True(
                viewer!.IsInactiveSelectionHighlightEnabled,
                "false だとマウスを離した瞬間に選択の強調が消える");
        });
    }

    /// <summary>本命その 2：説明の文字が選べる（選んだ中身がちゃんと取れる）。</summary>
    [Fact]
    public void HoverText_CanBeSelected()
    {
        WithHover(editor =>
        {
            editor.SelectAllHoverTextForTest();

            Assert.Contains("int Count", editor.HoverSelectionTextForTest);
        });
    }

    /// <summary>フォーカスは本文に残したままなので、素の Ctrl+C はこちらへは来ない——
    /// <b>選んでいるときだけ</b>もらう。</summary>
    [Fact]
    public void CtrlC_WithASelection_CopiesFromThePopup()
    {
        WithHover(editor =>
        {
            editor.SelectAllHoverTextForTest();

            Assert.True(editor.SendHoverPopupKeyForTest(Key.C, ModifierKeys.Control));
            Assert.NotNull(editor.HoverPopupContentForTest);   // 写しただけで閉じない
        });
    }

    /// <summary>選んでいなければ素通し——Ctrl+C はヤンク（本文のコピー）のままでなければ困る。
    /// ポップアップの方は、打鍵なのでこれまでどおり引っ込む。</summary>
    [Fact]
    public void CtrlC_WithoutASelection_FallsThroughToTheBuffer()
    {
        WithHover(editor =>
        {
            Assert.False(editor.SendHoverPopupKeyForTest(Key.C, ModifierKeys.Control));
            Assert.Null(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>選んだそばから消えては意味が無い。選択が残っている間はクリックで閉じない。</summary>
    [Fact]
    public void Hover_WithSelection_DoesNotCloseOnClick()
    {
        WithHover(editor =>
        {
            editor.SelectAllHoverTextForTest();

            ClickPopup(editor);

            Assert.NotNull(editor.HoverPopupContentForTest);
        });
    }

    /// <summary>何も選んでいなければ、これまでどおりクリックで引っ込む
    /// （押しても何も起きない板を本文の上にかぶせたままにしない）。</summary>
    [Fact]
    public void Hover_WithoutSelection_StillClosesOnClick()
    {
        WithHover(editor =>
        {
            ClickPopup(editor);

            Assert.Null(editor.HoverPopupContentForTest);
        });
    }

    private static void ClickPopup(VimEditorControl editor)
    {
        var border = editor.HoverPopupBorderForTest;
        Assert.NotNull(border);
        border!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });
        border.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
    }

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

    private static bool IsSelfOrDescendant(DependencyObject hit, DependencyObject ancestor)
    {
        for (DependencyObject? node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor)) return true;
        }
        return false;
    }

    private static LspDiagnostic Warning(string message) =>
        new(new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)),
            message, DiagnosticSeverity.Warning);
}
