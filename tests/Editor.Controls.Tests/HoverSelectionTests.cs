using System.Windows;
using System.Windows.Controls;
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
    /// <summary>本命その 1：FlowDocument に移しても、修正行はマウスで届く場所に居る。</summary>
    [Fact]
    public void FixRow_StaysReachableByTheMouse_InsideTheDocumentViewer()
    {
        WpfTestHost.Run(() =>
        {
            var fix = new LspCodeAction("using を削除", LspCodeActionKinds.QuickFix, null);
            var document = HoverContentBuilder.Build(
                [Warning("CS8019: 不要な using")], HoverMarkdown.Parse("説明"),
                EditorTheme.Dracula, new FontFamily("Consolas"), 12, null,
                new HoverFixSection(
                    Show: true, Expanded: true, Loading: false, Loaded: true,
                    Fixes: [fix], Hidden: 0, OnToggle: null, OnApply: _ => { }));

            // 本物と同じ設定の表示器。
            var viewer = new FlowDocumentScrollViewer
            {
                Document = document,
                IsSelectionEnabled = true,
                Focusable = true,
                IsTabStop = false,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };

            Window? window = null;
            try
            {
                window = WpfTestHost.Load(viewer);
                viewer.UpdateLayout();

                var row = HoverQuickFixTests.FindByTag(document, fix);
                Assert.NotNull(row);
                Assert.True(row!.ActualWidth > 0 && row.ActualHeight > 0, "修正行が配置されていない");

                var center = row.TranslatePoint(
                    new Point(row.ActualWidth / 2, row.ActualHeight / 2), viewer);
                var hit = VisualTreeHelper.HitTest(viewer, center)?.VisualHit;

                Assert.NotNull(hit);
                Assert.True(
                    IsSelfOrDescendant(hit!, row),
                    "表示器がマウス入力を吸っている——修正行を押せない");
            }
            finally
            {
                if (window is not null) { window.Close(); window.Content = null; }
            }
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
