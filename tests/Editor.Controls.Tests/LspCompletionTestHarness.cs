using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Editor.Controls.Tests;

/// <summary>
/// 実物の <see cref="VimEditorControl"/> を WPF テストホスト上に載せ、LSP ビューだけを
/// <see cref="FakeEditorLspView"/> に差し替えて駆動するための共通土台。
/// 補完まわりのテストが入力の送り方を共有するために切り出してある。
/// </summary>
internal static class LspCompletionTestHarness
{
    public static void WithEditor(bool vimEnabled, Action<VimEditorControl, FakeEditorLspView> action)
    {
        WpfTestHost.Run(() =>
        {
            var lsp = new FakeEditorLspView();
            var editor = new VimEditorControl(new VimEditorControlOptions { LspViewFactory = () => lsp });
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(editor);
                editor.VimEnabled = vimEnabled;
                if (vimEnabled) TypeText(editor, "i"); // Insert へ入る（plain は常時 Insert 相当）
                editor.Focus();
                action(editor, lsp);
            }
            finally
            {
                if (window != null) { window.Close(); window.Content = null; }
                editor.Dispose();
            }
        });
    }

    public static void TypeText(VimEditorControl editor, string text)
    {
        foreach (var ch in text)
        {
            var composition = new TextComposition(InputManager.Current, editor, ch.ToString());
            editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.TextInputEvent,
                Source = editor
            });
        }
    }

    public static void RaiseKeyDown(VimEditorControl editor, Key key)
    {
        editor.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = editor
        });
    }

    public static void RaisePreviewKeyDown(VimEditorControl editor, Key key)
    {
        editor.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = editor
        });
    }

    /// <summary>
    /// ディスパッチャに溜まった継続（await の再開など）を処理する。テスト本体は
    /// ディスパッチャスレッド上で動くので、Background 優先度で入れ子フレームを回して
    /// Normal 優先度で投函された継続を先に流し切る。
    /// </summary>
    public static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
