using Editor.Core.Buffer;
using Editor.Core.Lsp;

namespace Editor.Controls.Tests;

/// <summary>
/// <see cref="VimEditorControl.TextEdited"/>: a host that mirrors the document from these changes
/// alone must end up with exactly the editor's text, whatever path the edit took (keys, a host
/// workspace edit, undo, a reload).
/// </summary>
public sealed class TextEditedEventTests
{
    private static string Apply(string text, TextBufferChange change)
    {
        var lines = text.Split('\n').ToList();
        var head = lines[change.StartLine][..change.StartColumn];
        var tail = lines[change.EndLine][change.EndColumn..];
        lines.RemoveRange(change.StartLine, change.EndLine - change.StartLine + 1);
        lines.InsertRange(change.StartLine, (head + change.NewText + tail).Split('\n'));
        return string.Join("\n", lines);
    }

    [Fact]
    public void Replaying_the_changes_reproduces_the_text_for_every_kind_of_edit()
    {
        WpfTestHost.Run(() =>
        {
            var editor = new VimEditorControl();
            editor.SetText("alpha\nbeta\ngamma");
            var mirror = editor.Text;
            var reloads = 0;
            editor.TextEdited += (_, e) =>
            {
                Assert.True(e.IsCurrentBuffer);
                if (e.Change.Kind == TextBufferChangeKind.Reload) reloads++;
                mirror = Apply(mirror, e.Change);
            };

            // Keys through the engine (Vim motions/operators or plain insertion, depending on the default).
            foreach (var key in new[] { "j", "d", "d", "o", "n", "e", "w", "Escape", "u", "x" })
            {
                editor.Engine.ProcessKey(key, ctrl: false, shift: false, alt: false);
                Assert.Equal(editor.Text, mirror);
            }

            // A host-side edit (refactoring, formatting) spanning lines.
            Assert.True(editor.TryApplyLspTextEdits(
                [new LspTextEdit(new LspRange(new LspPosition(0, 1), new LspPosition(1, 2)), "X\nY")],
                expectedVersion: null, out _));
            Assert.Equal(editor.Text, mirror);

            // A reload is reported as such, and still replays to the right text.
            editor.SetText("brand\nnew");
            Assert.Equal(editor.Text, mirror);
            Assert.Equal(1, reloads);
        });
    }

    [Fact]
    public void Unsubscribing_detaches_from_the_buffers()
    {
        WpfTestHost.Run(() =>
        {
            var editor = new VimEditorControl();
            editor.SetText("a");
            var calls = 0;
            EventHandler<EditorTextEditedEventArgs> handler = (_, _) => calls++;

            editor.TextEdited += handler;
            editor.SetText("b");
            editor.TextEdited -= handler;
            editor.SetText("c");

            Assert.Equal(1, calls);
        });
    }
}
