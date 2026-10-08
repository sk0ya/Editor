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

    [Fact]
    public void A_throwing_handler_does_not_starve_the_others_or_break_the_keystroke()
    {
        WpfTestHost.Run(() =>
        {
            var editor = new VimEditorControl();
            editor.SetText("abc");
            var mirror = editor.Text;
            editor.TextEdited += (_, _) => throw new InvalidOperationException("host bug");
            editor.TextEdited += (_, e) => mirror = Apply(mirror, e.Change);

            foreach (var key in new[] { "i", "x", "y", "Escape" })
                editor.Engine.ProcessKey(key, ctrl: false, shift: false, alt: false);

            Assert.Equal("xyabc", editor.Text);
            Assert.Equal(editor.Text, mirror);
        });
    }

    [Fact]
    public void Widening_the_line_number_labels_rewraps_the_text()
    {
        WpfTestHost.Run(() => WpfTestHost.WithLoadedControl<VimEditorControl>((editor, _) =>
        {
            var canvas = (Rendering.EditorCanvas)typeof(VimEditorControl)
                .GetField("Canvas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(editor)!;
            editor.SetText(string.Join(" ", Enumerable.Repeat("word", 120)));
            canvas.WrapLines = true;
            editor.UpdateLayout();
            var rows = canvas.VisualLineCount;

            // A much wider gutter leaves less room for text: the line has to wrap into more rows
            // right away, not at the next unrelated edit.
            editor.SetLineNumberLabels(_ => "x", 40);

            Assert.True(canvas.VisualLineCount > rows, $"rows before={rows} after={canvas.VisualLineCount}");
        }));
    }
}
