using System.Windows;
using Editor.Controls.Ime;
using Editor.Core.Engine;

namespace Editor.Controls.Tests;

/// <summary>
/// IME ON のままコマンドライン（: / ?）に日本語を打てること。以前は IME ON のキーをすべて横取りして
/// 英字として入れていたので、検索パターンや Ex の引数に日本語を入れられなかった。組み立て中の文字は
/// ステータスバーのコマンドライン末尾に重ね、確定した文字だけがエンジンのコマンドラインに入る。
/// </summary>
public sealed class CommandLineImeCompositionTests
{
    [Fact]
    public void Composition_is_shown_after_the_command_line_until_committed()
        => WithEditor((editor, bar) =>
        {
            editor.Engine.ProcessKeyLiteral("/");
            editor.Engine.ProcessKeyLiteral("a");
            bar.UpdateCommandLine("/a");

            Host(editor).OnCompositionUpdated("ほげ", 2);

            Assert.Equal("ほげ", bar.CurrentCommandComposition);
            Assert.Equal("/aほげ", bar.CurrentCommandLineText);
            Assert.Equal("a", editor.Engine.CommandLine); // 確定前はエンジンに入らない

            Host(editor).OnCompositionCommitted("ほげ");

            Assert.Equal("aほげ", editor.Engine.CommandLine);
            Assert.Equal("", bar.CurrentCommandComposition);
            Assert.Equal("/aほげ", bar.CurrentCommandLineText);
        });

    [Fact]
    public void Ex_command_gets_full_width_ascii_back_as_half_width()
        => WithEditor((editor, _) =>
        {
            editor.Engine.ProcessKeyLiteral(":");

            Host(editor).OnCompositionCommitted("ｅ　ほげ.txt");

            Assert.Equal(VimMode.Command, editor.Engine.Mode);
            Assert.Equal("e ほげ.txt", editor.Engine.CommandLine);
        });

    [Fact]
    public void Search_keeps_the_committed_text_as_typed()
        => WithEditor((editor, _) =>
        {
            editor.Engine.ProcessKeyLiteral("/");

            Host(editor).OnCompositionCommitted("ＡＢ　漢字");

            Assert.Equal("ＡＢ　漢字", editor.Engine.CommandLine);
        });

    [Fact]
    public void Command_line_accepts_composition()
        => WithEditor((editor, _) =>
        {
            Assert.False(Host(editor).IsCompositionAllowed);
            editor.Engine.ProcessKeyLiteral("/");
            Assert.True(Host(editor).IsCompositionAllowed);
        });

    // ProcessKeyLiteral: ユーザーの vimrc（`nnoremap ; :` など）に左右されずにモードへ入る。
    private static IEditorTextStoreHost Host(VimEditorControl editor) => editor;

    private static void WithEditor(Action<VimEditorControl, VimStatusBar> action)
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            var editor = new VimEditorControl();
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(editor);
            panel.Children.Add(bar);
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(panel);
                editor.SetSharedStatusBar(bar);
                editor.SyncStatusBar();
                editor.SetText("ほげ\nabc");
                action(editor, bar);
            }
            finally
            {
                try { window?.Close(); }
                finally
                {
                    if (window != null) window.Content = null;
                    editor.Dispose();
                }
            }
        });
}
