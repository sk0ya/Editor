using System.Text;

namespace Editor.Controls.Tests;

/// <summary>
/// キャレットの移動とスクロールの通知の切り分け（EditorCanvas.CursorRevealedInPlace）。
/// <para>以前はキャレットが1つ動くたびに「スクロールした」と通知していて、見えている範囲の装飾の組み直しと、
/// ホストのスクロール同期（Loomo のプレビュー追従）が j/k の1打鍵ごとに走っていた。いまはスクロールが
/// 実際に起きたときだけ通知するが、キャレットに付いていく振る舞い（ホバーを引っ込める等）は変えない。</para>
/// </summary>
public class EditorViewportEventsTests
{
    /// <summary>タイマーや背景の継続が届くよう、ディスパッチャを回しながら待つ。</summary>
    private static void PumpFor(TimeSpan limit, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    private static string ManyLines(int count) =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => $"int value{i} = {i};"));

    [Fact]
    public void ViewportScrolled_IsRaisedOnlyWhenTheViewActuallyScrolls()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: true, (editor, _) =>
        {
            editor.SetText(ManyLines(400));
            editor.NavigateTo(0, 0);
            LspCompletionTestHarness.Pump();
            int scrolled = 0;
            editor.ViewportScrolled += (_, _) => scrolled++;

            editor.NavigateTo(3, 2);
            editor.NavigateTo(5, 0);
            LspCompletionTestHarness.Pump();
            Assert.Equal(0, scrolled);

            editor.NavigateTo(300, 0);
            LspCompletionTestHarness.Pump();
            Assert.True(scrolled > 0, "moving the caret off-screen must scroll and notify");
        });
    }

    [Fact]
    public void CaretMoveWithinView_StillClosesAnUnpinnedHover()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.HoverResult = "```csharp\nint Count\n```";
            editor.SetText("var count = 1;\nvar other = 2;\nvar third = 3;");
            _ = editor.ShowHoverInfoForTestAsync(0, 4);
            LspCompletionTestHarness.Pump();
            Assert.NotNull(editor.HoverPopupContentForTest);

            // キーを介さずにキャレットだけが動く（ホストの NavigateTo）。スクロールは起きない。
            editor.NavigateTo(1, 2);

            Assert.Null(editor.HoverPopupContentForTest);
        });
    }

    [Fact]
    public void CaretMoveWithinView_KeepsAPinnedHover()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.HoverResult = "```csharp\nint Count\n```";
            editor.SetText("var count = 1;\nvar other = 2;");
            _ = editor.ShowHoverInfoForTestAsync(0, 4);
            LspCompletionTestHarness.Pump();
            Assert.True(editor.SendHoverPopupKeyForTest(System.Windows.Input.Key.K,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift));

            editor.NavigateTo(1, 2);

            Assert.NotNull(editor.HoverPopupContentForTest);
        });
    }

    [Fact]
    public void DeepBreadcrumb_IsScrolledToItsInnermostSegment()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: true, (editor, _) =>
        {
            // 外側の名前を長くして、パンくずがバーの幅からはみ出すようにする。
            var source = new StringBuilder();
            source.AppendLine("namespace AnExtremelyLongNamespaceNameThatTakesUpMostOfTheBreadcrumbBarWidth.AndAnotherSegment");
            source.AppendLine("{");
            source.AppendLine("    public class AnotherRatherLongOuterClassNameForTheBreadcrumbOverflowTest");
            source.AppendLine("    {");
            source.AppendLine("        public class InnerClassWithAnAlsoQuiteLongNameForOverflowPurposes");
            source.AppendLine("        {");
            source.AppendLine("            public void TheInnermostMethodWhereTheCaretSits()");
            source.AppendLine("            {");
            source.AppendLine("                int caretIsHere = 0;");
            source.AppendLine("            }");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine("}");
            var path = Path.Combine(Path.GetTempPath(), $"breadcrumb-{Guid.NewGuid():N}.cs");
            File.WriteAllText(path, source.ToString());
            try
            {
                editor.LoadFile(path);
                LspCompletionTestHarness.RaisePreviewKeyDown(editor, System.Windows.Input.Key.Escape); // Normal へ
                editor.NavigateTo(8, 20);
                // 代替の記号一覧は手が止まってから（250ms）背景で作り直されて、パンくずへ届く。
                PumpFor(TimeSpan.FromMilliseconds(1500), () => editor.BreadcrumbPanel.Children.Count >= 5);
                editor.UpdateLayout();

                var scroll = editor.BreadcrumbScroll;
                Assert.True(editor.BreadcrumbPanel.Children.Count >= 5, $"expected a deep path, got {editor.BreadcrumbPanel.Children.Count} items");
                Assert.True(scroll.ScrollableWidth > 0, "the path must overflow the bar for this test to mean anything");
                Assert.Equal(scroll.ScrollableWidth, scroll.HorizontalOffset, 1);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
