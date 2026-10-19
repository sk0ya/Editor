using System.IO;
using System.Reflection;

namespace Editor.Controls.Tests;

/// <summary>
/// <see cref="VimEditorControlOptions.WatchFileOnDisk"/>：ホストが外部変更を自分で見張るときは、
/// エディタは監視を張らない（張ると1回の書き換えに両方が反応し、確認が二重に出る）。読み直しは
/// <see cref="VimEditorControl.ReloadFromDisk"/> でホストから行う。
/// </summary>
public sealed class FileWatchOptionTests
{
    private static readonly FieldInfo WatcherField = typeof(VimEditorControl)
        .GetField("_fileWatcher", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void By_default_the_open_file_is_watched()
        => WithFile(new VimEditorControlOptions(), editor => Assert.NotNull(WatcherField.GetValue(editor)));

    [Fact]
    public void When_the_host_watches_the_editor_does_not()
        => WithFile(new VimEditorControlOptions { WatchFileOnDisk = false }, editor =>
            Assert.Null(WatcherField.GetValue(editor)));

    [Fact]
    public void ReloadFromDisk_keeps_undo_history_and_the_caret()
        => WithFile(new VimEditorControlOptions { WatchFileOnDisk = false }, editor =>
        {
            File.WriteAllText(editor.FilePath!, "line 0\nline 1\nline 2\n");
            Assert.True(editor.ReloadFromDisk());
            editor.NavigateTo(2, 3);
            File.WriteAllText(editor.FilePath!, "line 0\nline 1\nchanged\n");

            Assert.True(editor.ReloadFromDisk());

            Assert.Equal("line 0\nline 1\nchanged\n", editor.Text);
            Assert.False(editor.IsModified);
            Assert.Equal(2, editor.Caret.Line);
            // :e! と違い、外からの書き換えも1回の編集として取り消せる。
            editor.ExecuteCommand("undo");
            Assert.Equal("line 0\nline 1\nline 2\n", editor.Text);
        });

    [Fact]
    public void ReloadFromDisk_reads_a_preparation_made_off_the_ui_thread()
        => WithFile(new VimEditorControlOptions { WatchFileOnDisk = false }, editor =>
        {
            File.WriteAllText(editor.FilePath!, "prepared\n");
            var prepared = Task.Run(() => Editor.Core.Buffer.PreparedFileLoad.Prepare(editor.FilePath!)).Result;

            Assert.True(editor.ReloadFromDisk(prepared));
            Assert.Equal("prepared\n", editor.Text);
        });

    private static void WithFile(VimEditorControlOptions options, Action<VimEditorControl> action)
        => WpfTestHost.Run(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "editor-file-watch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "disk\n");
            var editor = new VimEditorControl(options);
            try
            {
                editor.LoadFile(path);
                action(editor);
            }
            finally
            {
                editor.Dispose();
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        });
}
