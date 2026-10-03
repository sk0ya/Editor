using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Controls.Rendering;

namespace Editor.Controls.Tests;

/// <summary>
/// 行末の値表示（デバッグの Inline Values）。中身はホストが決めるので、エディタ側で確かめるのは
/// 「全置換で入る／同じ行は連結される／別ファイルを載せたら消える／描いても落ちない」だけ。
/// </summary>
public sealed class InlineValueTests
{
    [Fact]
    public void Values_replace_wholesale_join_on_the_same_line_and_ignore_invalid_entries()
    {
        WpfTestHost.Run(() =>
        {
            var canvas = new EditorCanvas();
            canvas.SetInlineValues([
                new EditorInlineValue(-1, "x = 1"),
                new EditorInlineValue(2, ""),
                new EditorInlineValue(3, "x = 1"),
                new EditorInlineValue(3, "y = 2"),
                new EditorInlineValue(5, "s = \"abc\""),
            ]);

            Assert.Equal(2, canvas.InlineValueLineCount);
            Assert.Equal("x = 1, y = 2", canvas.InlineValueTextAt(3));
            Assert.Equal("s = \"abc\"", canvas.InlineValueTextAt(5));

            canvas.SetInlineValues([new EditorInlineValue(0, "z = 9")]);
            Assert.Equal(1, canvas.InlineValueLineCount);
            Assert.Null(canvas.InlineValueTextAt(3));

            canvas.SetInlineValues([]);
            Assert.Equal(0, canvas.InlineValueLineCount);
        });
    }

    [Fact]
    public void Loading_another_file_drops_the_values_of_the_previous_one()
    {
        WithEditor((editor, dir) =>
        {
            var first = Path.Combine(dir, "a.cs");
            File.WriteAllText(first, "int x = 1;\nint y = x + 1;\n");
            editor.LoadFile(first);
            editor.SetInlineValues([new EditorInlineValue(1, "y = 2, x = 1")]);
            Assert.Equal(1, CanvasOf(editor).InlineValueLineCount);

            var second = Path.Combine(dir, "b.cs");
            File.WriteAllText(second, "class B { }\n");
            editor.LoadFile(second);

            Assert.Equal(0, CanvasOf(editor).InlineValueLineCount);
        });
    }

    [Fact]
    public void Rendering_values_on_wrapped_and_unwrapped_lines_does_not_throw()
    {
        WithEditor((editor, _) =>
        {
            editor.SetText("var x = 1;\n" + new string('a', 300) + "\nreturn x;\n");
            editor.SetInlineValues([
                new EditorInlineValue(0, "x = 1"),
                new EditorInlineValue(1, "a = \"…\""),
                new EditorInlineValue(2, "x = 1"),
            ]);
            Render(editor);
            CanvasOf(editor).WrapLines = true;
            Render(editor);
            Assert.Equal(3, CanvasOf(editor).InlineValueLineCount);
        });
    }

    private static void Render(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)element.ActualWidth), Math.Max(1, (int)element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
    }

    private static EditorCanvas CanvasOf(VimEditorControl editor)
        => (EditorCanvas)typeof(VimEditorControl)
            .GetField("Canvas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(editor)!;

    private static void WithEditor(Action<VimEditorControl, string> action)
        => WpfTestHost.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "editor-inline-values-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var editor = new VimEditorControl();
            Window? window = null;
            try
            {
                window = WpfTestHost.Load(editor);
                action(editor, dir);
            }
            finally
            {
                try { window?.Close(); }
                finally
                {
                    if (window != null) window.Content = null;
                    editor.Dispose();
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
        });
}
