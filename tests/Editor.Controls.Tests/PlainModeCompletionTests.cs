using System.Windows.Input;
using Editor.Core.Lsp;

using static Editor.Controls.Tests.LspCompletionTestHarness;

namespace Editor.Controls.Tests;

/// <summary>
/// Vim を無効にした plain モードでも LSP 補完が「出る・選べる・確定できる」ことの回帰テスト。
/// かつては plain モードで補完要求そのものを送らず、補完の無いエディタになっていた。
/// </summary>
public sealed class PlainModeCompletionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Typing_a_trigger_character_requests_completion_in_both_modes(bool vimEnabled)
    {
        WithEditor(vimEnabled, (editor, lsp) =>
        {
            TypeText(editor, "a.");

            Assert.Single(lsp.CompletionRequests);
        });
    }

    [Fact]
    public void Completion_popup_is_navigable_with_arrow_keys_while_vim_is_disabled()
    {
        WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.SelectedItem = new LspCompletionItem("WriteLine");
            lsp.CompletionVisible = true;

            RaiseKeyDown(editor, Key.Down);
            RaiseKeyDown(editor, Key.Up);

            Assert.Equal(new[] { 1, -1 }, lsp.SelectionMoves);
        });
    }

    [Fact]
    public void Enter_accepts_the_selected_item_while_vim_is_disabled()
    {
        WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            TypeText(editor, "Wri");
            lsp.SelectedItem = new LspCompletionItem("WriteLine");
            lsp.CompletionVisible = true;

            RaiseKeyDown(editor, Key.Return);

            Assert.Equal("WriteLine", editor.Text);
        });
    }

    [Fact]
    public void Escape_closes_the_popup_without_dropping_the_plain_selection()
    {
        WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            editor.SetText("selected line");
            editor.SelectLine(0);
            lsp.SelectedItem = new LspCompletionItem("WriteLine");
            lsp.CompletionVisible = true;
            int hidesBefore = lsp.HideCompletionCalls; // VimEnabled=false 自体も1回閉じる

            RaiseKeyDown(editor, Key.Escape);

            Assert.Equal(hidesBefore + 1, lsp.HideCompletionCalls);
            Assert.False(lsp.CompletionVisible);
            // plain モードには抜ける先のモードが無いので、Escape をエンジンへ転送しない
            // （転送すると本文の選択まで消える）。
            Assert.True(editor.HasSelection);
        });
    }

}
