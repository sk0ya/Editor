using System.Text;

namespace Editor.Controls.Tests;

/// <summary>
/// Switching the line ending / encoding a buffer will be saved with (<see cref="VimEditorControl.SetFileFormat"/>,
/// <see cref="VimEditorControl.SetFileEncoding"/> — the API form of <c>:set fileformat=</c> / <c>fileencoding=</c>).
/// The text is untouched, the buffer becomes modified, and saving writes the new form.
/// </summary>
public sealed class FileFormatSwitchTests
{
    [Fact]
    public void Switching_line_ending_marks_modified_and_save_writes_it()
        => WithFile("a\nb\r\nc\n", (editor, path) =>
        {
            Assert.Equal("unix", editor.FileFormat);
            Assert.False(editor.IsModified);

            Assert.True(editor.SetFileFormat("dos"));

            Assert.True(editor.IsModified);   // text unchanged, but the file would change on save
            editor.Save();
            Assert.Equal("a\r\nb\r\nc\r\n", File.ReadAllText(path));   // mixed endings are normalized too
            Assert.False(editor.IsModified);
        });

    [Fact]
    public void Switching_encoding_marks_modified_and_save_writes_it()
        => WithFile("設定\n", (editor, path) =>
        {
            Assert.True(editor.SetFileEncoding("utf-8-bom"));

            Assert.True(editor.IsModified);
            editor.Save();
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("設定\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
            Assert.False(editor.IsModified);
        });

    [Fact]
    public void Choosing_the_current_format_on_a_mixed_file_still_normalizes_on_save()
        => WithFile("a\r\nb\nc\r\n", (editor, path) =>
        {
            Assert.Equal("dos", editor.FileFormat);   // decided by the first line ending
            Assert.False(editor.IsModified);

            Assert.True(editor.SetFileFormat("dos"));

            // Nothing about the format changed, but the file is mixed: it has to be saved to become uniform.
            Assert.True(editor.IsModified);
            editor.Save();
            Assert.Equal("a\r\nb\r\nc\r\n", File.ReadAllText(path));
            Assert.False(editor.IsModified);
            Assert.True(editor.SetFileFormat("dos"));
            Assert.False(editor.IsModified);          // uniform now: choosing it again changes nothing
        });

    [Fact]
    public void Switching_back_clears_modified()
        => WithFile("a\n", (editor, _) =>
        {
            editor.SetFileFormat("dos");
            editor.SetFileFormat("unix");

            Assert.False(editor.IsModified);
        });

    [Fact]
    public void Unknown_values_are_refused()
        => WithFile("a\n", (editor, _) =>
        {
            Assert.False(editor.SetFileFormat("windows"));
            Assert.False(editor.SetFileEncoding("klingon"));

            Assert.Equal(("unix", "utf-8"), (editor.FileFormat, editor.FileEncoding));
            Assert.False(editor.IsModified);
        });

    [Fact]
    public void Unrelated_set_does_not_touch_the_file_format()
        => WithFile("a\r\n", (editor, _) =>
        {
            editor.ExecuteCommand("set number");

            Assert.Equal("dos", editor.FileFormat);
            Assert.False(editor.IsModified);
        });

    private static void WithFile(string content, Action<VimEditorControl, string> action)
        => WpfTestHost.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "editor-fileformat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            var editor = new VimEditorControl();
            try
            {
                editor.LoadFile(path);
                action(editor, path);
            }
            finally
            {
                editor.Dispose();
                try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup */ }
            }
        });
}
