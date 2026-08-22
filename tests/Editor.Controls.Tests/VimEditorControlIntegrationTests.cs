using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Editor.Controls.Ime;

namespace Editor.Controls.Tests;

public sealed class VimEditorControlIntegrationTests
{
    [Fact]
    public void LoadedControl_ProcessesRoutedKeyboardAndTextCompositionInput()
    {
        WpfTestHost.Run(() => WpfTestHost.WithLoadedControl<VimEditorControl>((editor, _) =>
        {
            editor.Focus();
            bool keyboardRouted = false;
            editor.AddHandler(Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler((_, _) => keyboardRouted = true), true);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(editor)!, Environment.TickCount, Key.I)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
                Source = editor
            };
            editor.RaiseEvent(key);

            var insertComposition = new TextComposition(InputManager.Current, editor, "i");
            editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, insertComposition)
            {
                RoutedEvent = TextCompositionManager.TextInputEvent,
                Source = editor
            });

            var composition = new TextComposition(InputManager.Current, editor, "日本語");
            var text = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.TextInputEvent,
                Source = editor
            };
            editor.RaiseEvent(text);

            Assert.True(keyboardRouted);
            Assert.True(text.Handled);
            Assert.Equal("日本語", editor.Text);
        }));
    }

    [Fact]
    public void LoadedControl_ChangesVirtualDocumentAndReportsHostSave()
    {
        WpfTestHost.Run(() =>
        {
          WpfTestHost.WithLoadedControl<VimEditorControl>((editor, window) => {
            Assert.True(editor.IsLoaded);
            Assert.True(editor.ActualWidth > 0);

            string id = editor.OpenVirtualDocument("notes", "first", "Markdown");
            editor.SetText("first\n日本語 😀");
            SaveRequestedEventArgs? request = null;
            editor.SaveRequested += (_, e) => request = e;
            editor.ExecuteCommand("write");

            Assert.Equal("first\n日本語 😀", editor.Text);
            Assert.NotNull(request);
            Assert.True(request!.IsVirtual);
            Assert.Equal(id, request.DocumentId);
            Assert.Null(request.FilePath);

            editor.MarkSaved(id);
            Assert.False(editor.IsModified);
          });
        });
    }

    [Fact]
    public void Save_WritesUnicodeFileAndClearsModifiedState()
    {
        WpfTestHost.Run(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"editor-controls-{Guid.NewGuid():N}.txt");
            try
            {
              WpfTestHost.WithLoadedControl<VimEditorControl>((editor, window) => {
                editor.SetText("alpha\r\n補助面: 𠮷");
                editor.Save(path);
                Assert.Equal("alpha\n補助面: 𠮷", File.ReadAllText(path));
                Assert.False(editor.IsModified);
                Assert.Equal(Path.GetFullPath(path), editor.DocumentInfo.FilePath);
              });
            }
            finally { File.Delete(path); }
        });
    }


    /// <summary>テストグリフのクリックはコントロールの公開イベントへそのまま転送され、
    /// あわせてエディタへフォーカスが戻る（ホストのボタン操作でキャレットを失わないため）。</summary>
    [Fact]
    public void TestGlyphClick_IsForwardedByTheControl_AndRestoresFocus()
    {
        WpfTestHost.Run(() => WpfTestHost.WithLoadedControl<VimEditorControl>((editor, _) =>
        {
            editor.SetText(string.Join(Environment.NewLine, "one", "two", "three"));
            editor.SetTestGlyphsEnabled(true);
            editor.SetTestGlyphs([new Editor.Controls.Rendering.EditorTestGlyph(1, Editor.Controls.Rendering.TestGlyphKind.Run, "実行する")]);

            var forwarded = new List<int>();
            editor.TestGlyphClicked += forwarded.Add;

            // Canvas 側のイベントが（列のクリック経由で）コントロールの公開イベントへ抜けてくる。
            var canvas = editor.Canvas;
            var lineHeight = (double)typeof(Editor.Controls.Rendering.EditorCanvas)
                .GetField("_lineHeight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(canvas)!;
            var metrics = (System.Runtime.CompilerServices.ITuple)typeof(Editor.Controls.Rendering.EditorCanvas)
                .GetMethod("GetGutterMetrics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(canvas, null)!;
            double x = (int)metrics[0]! + (int)metrics[1]! / 2.0;

            Assert.True(canvas.TryClickTestGlyphColumn(new Point(x, lineHeight * 1.5)));
            Assert.Equal(new[] { 1 }, forwarded);
            Assert.True(editor.IsKeyboardFocusWithin || canvas.IsFocused || editor.IsFocused);

            // ファイルを切り替えたら前のファイルのグリフは残さない。
            string path = Path.Combine(Path.GetTempPath(), $"editor-testglyph-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(path, new[] { "alpha", "beta", "gamma" });
            try
            {
                editor.LoadFile(path);
                forwarded.Clear();
                Assert.True(canvas.TryClickTestGlyphColumn(new Point(x, lineHeight * 1.5)));
                Assert.Empty(forwarded);
            }
            finally { File.Delete(path); }
        }));
    }
}
