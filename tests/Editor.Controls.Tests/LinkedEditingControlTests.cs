using System.Windows.Input;
using System.Windows.Threading;
using Editor.Core.Editing;
using Editor.Core.Models;

namespace Editor.Controls.Tests;

/// <summary>
/// 連動編集のコントロール側: 言語サーバーへの先回りの問い合わせが、打鍵の後のエンジンへ
/// 同期的に渡ることを実物の <see cref="VimEditorControl"/> で通す。
/// </summary>
public sealed class LinkedEditingControlTests
{
    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>キャレットを (0, column + 1) に置いてから ← で (0, column) へ — コントロールの
    /// CursorMoved 経路（先回りの問い合わせの予約）を踏ませるため。</summary>
    private static void PlaceCaret(VimEditorControl editor, int column)
    {
        editor.Engine.SetCursorPosition(new CursorPosition(0, column + 1));
        LspCompletionTestHarness.RaiseKeyDown(editor, Key.Left);
        Assert.Equal(new CursorPosition(0, column), editor.Engine.Cursor);
        // 実機では KeyDown の後に対の TextInput が来て「Vim が処理済み」の印を消す。それを模す。
        var composition = new TextComposition(InputManager.Current, editor, "");
        editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = TextCompositionManager.TextInputEvent,
            Source = editor
        });
    }

    [Fact]
    public void Server_ranges_are_prefetched_and_mirror_typing_in_tsx()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.LinkedEditingAnswer = new LinkedEditingRanges([new(0, 1, 4), new(0, 7, 10)]);
            editor.SetText("<div></div>");
            editor.Engine.CurrentBuffer.FilePath = @"C:\work\App.tsx";
            PlaceCaret(editor, 4);
            PumpFor(300);

            Assert.Equal([(0, 4)], lsp.LinkedEditingRequests);
            LspCompletionTestHarness.TypeText(editor, "x");

            Assert.Equal("<divx></divx>", editor.Engine.CurrentBuffer.Text.GetLine(0));
            Assert.Equal(2, editor.Engine.LinkedEditingRanges.Count);
        });
    }

    [Fact]
    public void Typing_before_the_answer_arrives_does_not_link_tsx()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.LinkedEditingAnswer = new LinkedEditingRanges([new(0, 1, 4), new(0, 7, 10)]);
            editor.SetText("<div></div>");
            editor.Engine.CurrentBuffer.FilePath = @"C:\work\App.tsx";
            PlaceCaret(editor, 4);
            LspCompletionTestHarness.TypeText(editor, "x"); // デバウンス前 — 答えはまだ無い

            Assert.Equal("<divx></div>", editor.Engine.CurrentBuffer.Text.GetLine(0));
        });
    }

    [Fact]
    public void Server_saying_none_overrides_markup_fallback()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.LinkedEditingAnswer = LinkedEditingRanges.None;
            editor.SetText("<div></div>");
            editor.Engine.CurrentBuffer.FilePath = @"C:\work\index.html";
            PlaceCaret(editor, 4);
            PumpFor(300);
            LspCompletionTestHarness.TypeText(editor, "x");

            Assert.Equal("<divx></div>", editor.Engine.CurrentBuffer.Text.GetLine(0));
        });
    }

    [Fact]
    public void Without_a_server_xaml_uses_the_fallback()
    {
        LspCompletionTestHarness.WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            editor.SetText("<Grid>\n</Grid>");
            editor.Engine.CurrentBuffer.FilePath = @"C:\work\View.xaml";
            PlaceCaret(editor, 5);
            LspCompletionTestHarness.TypeText(editor, "s");

            Assert.Equal("<Grids>\n</Grids>", editor.Engine.CurrentBuffer.Text.GetText());
            Assert.Empty(lsp.LinkedEditingRequests);
        });
    }
}
