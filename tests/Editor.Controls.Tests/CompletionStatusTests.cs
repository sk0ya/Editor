using System.Windows.Input;
using static Editor.Controls.Tests.LspCompletionTestHarness;

namespace Editor.Controls.Tests;

/// <summary>
/// 「LSP: completion loading…」は待機の表示なので、待機が終われば必ず消えることの回帰テスト。
/// かつては成功経路（候補が出た）と破棄経路で後始末が無く、候補を採用したあとも
/// ステータスバーが「読み込み中」のまま固定されていた。
/// </summary>
public sealed class CompletionStatusTests
{
    private const string NoCompletions = "LSP: no completions at this position";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Loading_status_is_cleared_once_the_completion_popup_opens(bool vimEnabled)
    {
        WithEditor(vimEnabled, (editor, lsp) =>
        {
            lsp.CompletionResult = ""; // 成功（ポップアップが開いた）
            lsp.CompletionVisible = true;

            TypeText(editor, "a.");
            Pump();

            Assert.Single(lsp.CompletionRequests);
            Assert.Equal("", editor.CurrentStatusText);
        });
    }

    [Fact]
    public void Failed_completion_replaces_the_loading_status_with_the_reason()
    {
        WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.CompletionResult = NoCompletions;

            TypeText(editor, "a.");
            Pump();

            Assert.Equal(NoCompletions, editor.CurrentStatusText);
        });
    }

    [Fact]
    public void Discarded_completion_does_not_leave_the_loading_status_behind()
    {
        // Escape でポップアップを閉じると飛行中の要求が取り消され、応答は null（破棄）で返る。
        // その場合も待機は終わっているので「読み込み中」は残ってはいけない。
        WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.DeferCompletionRequests = true;

            TypeText(editor, "a.");
            Assert.Equal(1, lsp.PendingCompletionCount);
            Assert.Equal(VimEditorControl.CompletionLoadingStatus, editor.CurrentStatusText);

            lsp.ResolveCompletion(0, null);
            Pump();

            Assert.Equal("", editor.CurrentStatusText);
        });
    }

    [Fact]
    public void Explicit_close_clears_loading_before_a_server_finishes()
    {
        WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.DeferCompletionRequests = true;

            TypeText(editor, "a.");
            Assert.Equal(VimEditorControl.CompletionLoadingStatus, editor.CurrentStatusText);
            lsp.CompletionVisible = true;

            // The fake deliberately does not complete the request here. Closing the
            // transient UI must still release the user-visible wait state immediately.
            RaisePreviewKeyDown(editor, Key.Escape);
            RaiseKeyDown(editor, Key.Escape);
            Assert.Equal("", editor.CurrentStatusText);

            lsp.ResolveCompletion(0, null);
            Pump();
            Assert.Equal("", editor.CurrentStatusText);
        });
    }

    [Fact]
    public void Late_response_does_not_overwrite_the_status_of_a_newer_request()
    {
        WithEditor(vimEnabled: false, (editor, lsp) =>
        {
            lsp.DeferCompletionRequests = true;

            TypeText(editor, "a.");
            TypeText(editor, "b.");
            Assert.Equal(2, lsp.PendingCompletionCount);

            lsp.ResolveCompletion(1, NoCompletions); // 新しい要求が先に決着する
            Pump();
            Assert.Equal(NoCompletions, editor.CurrentStatusText);

            lsp.ResolveCompletion(0, null); // 古い要求の遅れた応答
            Pump();

            Assert.Equal(NoCompletions, editor.CurrentStatusText);
        });
    }
}
