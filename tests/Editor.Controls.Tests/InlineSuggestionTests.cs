using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Editor.Core.Completion;

namespace Editor.Controls.Tests;

/// <summary>
/// 打鍵からゴースト表示までを実物の <see cref="VimEditorControl"/> で通す。
///
/// <para>「薄い提案が出ない」は、条件で弾かれたのか、そもそも呼ばれていないのか、
/// 描かれていないのかが外から区別できない——この経路を端から端まで踏む自動テストが要る。</para>
/// </summary>
public sealed class InlineSuggestionTests
{
    /// <summary>デバウンス（120ms）とその後の反映が起きるまでディスパッチャを回す。</summary>
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

    private static void WithEditor(Action<VimEditorControl, FakeEditorLspView> action)
        => LspCompletionTestHarness.WithEditor(vimEnabled: false, action);

    /// <summary>同じ書き出しの行がすでにあるバッファ。内蔵の予測が答えられる形。</summary>
    private static void SeedRepeatingLines(VimEditorControl editor)
    {
        editor.SetText("Assert.Equal(1, actual.Count);\n");
        editor.Engine.SetCursorPosition(new Editor.Core.Models.CursorPosition(1, 0));
    }

    [Fact]
    public void Typing_a_line_that_repeats_an_earlier_one_shows_a_ghost_suggestion()
    {
        WithEditor((editor, _) =>
        {
            SeedRepeatingLines(editor);
            LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
            PumpFor(300);

            Assert.Equal("al(1, actual.Count);", editor.InlineSuggestionText);
            Assert.Equal(InlineSuggestionSource.BufferLine, editor.InlineSuggestionOrigin);
        });
    }

    [Fact]
    public void Tab_accepts_the_whole_suggestion()
    {
        WithEditor((editor, _) =>
        {
            SeedRepeatingLines(editor);
            LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
            PumpFor(300);
            Assert.NotNull(editor.InlineSuggestionText);

            LspCompletionTestHarness.RaisePreviewKeyDown(editor, Key.Tab);
            PumpFor(50);

            Assert.Equal("Assert.Equal(1, actual.Count);", editor.Engine.CurrentBuffer.Text.GetLine(1));
        });
    }

    [Fact]
    public void Accepting_one_word_keeps_the_rest_of_the_suggestion()
    {
        WithEditor((editor, _) =>
        {
            editor.SetText("value.WriteLine(message);\n");
            editor.Engine.SetCursorPosition(new Editor.Core.Models.CursorPosition(1, 0));
            LspCompletionTestHarness.TypeText(editor, "value.Wri");
            PumpFor(300);
            Assert.Equal("teLine(message);", editor.InlineSuggestionText);

            editor.AcceptInlineSuggestionForTest(wordOnly: true);
            PumpFor(50);

            Assert.Equal("value.WriteLine", editor.Engine.CurrentBuffer.Text.GetLine(1));
            Assert.Equal("(message);", editor.InlineSuggestionText);
        });
    }

    /// <summary>当たっている間は作り直さず、提案が 1 文字ぶん縮む（ちらつかせない）。</summary>
    [Fact]
    public void A_matching_keystroke_shrinks_the_live_suggestion_instead_of_recomputing()
    {
        WithEditor((editor, _) =>
        {
            SeedRepeatingLines(editor);
            LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
            PumpFor(300);
            Assert.Equal("al(1, actual.Count);", editor.InlineSuggestionText);

            LspCompletionTestHarness.TypeText(editor, "a");   // 提案の先頭と同じ文字
            Assert.Equal("l(1, actual.Count);", editor.InlineSuggestionText);   // 待たずに縮む
        });
    }

    [Fact]
    public void Nothing_is_shown_when_the_option_is_off()
    {
        WithEditor((editor, _) =>
        {
            editor.Engine.Options.InlineSuggest = false;
            SeedRepeatingLines(editor);
            LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
            PumpFor(300);

            Assert.Null(editor.InlineSuggestionText);
        });
    }

    /// <summary>
    /// 補完ポップアップとは共存する。役割が違う（ポップアップは識別子 1 つの候補一覧、
    /// 先読みは行全体の予想）し、何より <c>.cs</c> は打鍵した瞬間にポップアップが出るので、
    /// 排他にすると先読みが一度も出ないエディタになる——実際そうなっていた。
    /// </summary>
    [Fact]
    public void The_suggestion_coexists_with_an_open_completion_popup()
    {
        WithEditor((editor, lsp) =>
        {
            SeedRepeatingLines(editor);
            lsp.CompletionVisible = true;
            LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
            PumpFor(300);

            Assert.Equal("al(1, actual.Count);", editor.InlineSuggestionText);
        });
    }

    /// <summary>Tab の取り合いは先読みが勝つ。ポップアップは Enter でも確定できるので、
    /// 譲っても行き場が無くならない。</summary>
    [Fact]
    public void Tab_takes_the_suggestion_even_when_the_popup_is_open()
    {
        WithEditor((editor, lsp) =>
        {
            SeedRepeatingLines(editor);
            LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
            PumpFor(300);
            Assert.NotNull(editor.InlineSuggestionText);

            lsp.CompletionVisible = true;
            LspCompletionTestHarness.RaisePreviewKeyDown(editor, Key.Tab);
            PumpFor(50);

            Assert.Equal("Assert.Equal(1, actual.Count);", editor.Engine.CurrentBuffer.Text.GetLine(1));
        });
    }

    /// <summary>ホストが差した供給元の答えは、内蔵の予測より後から届いて置き換える。</summary>
    [Fact]
    public void A_host_provider_replaces_the_built_in_prediction_when_it_answers()
    {
        WpfTestHost.Run(() =>
        {
            var lsp = new FakeEditorLspView();
            var editor = new VimEditorControl(new VimEditorControlOptions
            {
                LspViewFactory = () => lsp,
                InlineSuggestionProvider = (_, _) => Task.FromResult<InlineSuggestion?>(
                    new InlineSuggestion("al(1, actual.Count);   // ホスト", InlineSuggestionSource.External)),
            });
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(editor);
                editor.VimEnabled = false;
                editor.Focus();

                SeedRepeatingLines(editor);
                LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
                PumpFor(400);

                Assert.Equal(InlineSuggestionSource.External, editor.InlineSuggestionOrigin);
                Assert.Equal("al(1, actual.Count);   // ホスト", editor.InlineSuggestionText);
            }
            finally
            {
                if (window is not null) { window.Close(); window.Content = null; }
                editor.Dispose();
            }
        });
    }

    /// <summary>供給元が投げても入力は止まらない（提案が出ないだけ）。</summary>
    [Fact]
    public void A_throwing_host_provider_does_not_disturb_typing()
    {
        WpfTestHost.Run(() =>
        {
            var lsp = new FakeEditorLspView();
            var editor = new VimEditorControl(new VimEditorControlOptions
            {
                LspViewFactory = () => lsp,
                InlineSuggestionProvider = (_, _) => throw new InvalidOperationException("供給元の失敗"),
            });
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(editor);
                editor.VimEnabled = false;
                editor.Focus();

                SeedRepeatingLines(editor);
                LspCompletionTestHarness.TypeText(editor, "Assert.Equ");
                PumpFor(300);

                Assert.Equal("Assert.Equ", editor.Engine.CurrentBuffer.Text.GetLine(1));
                Assert.Equal("al(1, actual.Count);", editor.InlineSuggestionText);   // 内蔵は残る
            }
            finally
            {
                if (window is not null) { window.Close(); window.Content = null; }
                editor.Dispose();
            }
        });
    }
}
