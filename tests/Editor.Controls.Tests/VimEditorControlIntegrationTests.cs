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

}

