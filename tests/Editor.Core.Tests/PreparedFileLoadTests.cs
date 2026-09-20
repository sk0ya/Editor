using System.Text;
using Editor.Core.Buffer;
using Editor.Core.Engine;

namespace Editor.Core.Tests;

/// <summary>
/// Opening a file with its disk side read ahead of time must land in exactly the same state as
/// opening it the ordinary way — that equivalence is the whole licence for doing the read on a
/// background thread.
/// </summary>
public class PreparedFileLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"editor-prepared-{Guid.NewGuid():N}");

    public PreparedFileLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void A_prepared_load_lands_in_the_same_state_as_a_plain_one()
    {
        var path = Write("sample.cs", "class A\n{\n}\n");

        var direct = new VimEngine();
        direct.LoadFile(path);

        var prepared = new VimEngine();
        prepared.LoadFile(path, PreparedFileLoad.Prepare(path));

        Assert.Equal(direct.CurrentBuffer.Text.GetText(), prepared.CurrentBuffer.Text.GetText());
        Assert.Equal(direct.CurrentBuffer.FilePath, prepared.CurrentBuffer.FilePath);
        Assert.Equal(direct.CurrentBuffer.FileEncoding, prepared.CurrentBuffer.FileEncoding);
        Assert.Equal(direct.CurrentBuffer.FileFormat, prepared.CurrentBuffer.FileFormat);
        Assert.False(prepared.CurrentBuffer.Text.IsModified);
    }

    [Fact]
    public void Crlf_and_encoding_detection_survive_the_hand_off()
    {
        var path = Write("sjis.txt", "あ\r\nい\r\n", Encoding.GetEncoding("shift-jis"));

        var engine = new VimEngine();
        engine.LoadFile(path, PreparedFileLoad.Prepare(path));

        Assert.Equal("dos", engine.CurrentBuffer.FileFormat);
        Assert.Equal("shift-jis", engine.CurrentBuffer.FileEncoding);
        Assert.Equal("あ\nい\n", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void A_preparation_for_another_file_is_ignored_not_trusted()
    {
        var wanted = Write("wanted.txt", "wanted\n");
        var other = Write("other.txt", "other\n");

        var engine = new VimEngine();
        engine.LoadFile(wanted, PreparedFileLoad.Prepare(other));

        Assert.Equal(wanted, engine.CurrentBuffer.FilePath);
        Assert.Equal("wanted\n", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void A_file_that_does_not_exist_yet_opens_empty_just_as_before()
    {
        var path = Path.Combine(_dir, "new.txt");

        var engine = new VimEngine();
        engine.LoadFile(path, PreparedFileLoad.Prepare(path));

        Assert.Equal(path, engine.CurrentBuffer.FilePath);
        Assert.Equal("", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void A_file_deleted_between_preparing_and_opening_does_not_resurrect_its_content()
    {
        var path = Write("doomed.txt", "was here\n");
        var prepared = PreparedFileLoad.Prepare(path);
        File.Delete(path);

        var engine = new VimEngine();
        engine.LoadFile(path, prepared);

        Assert.Equal("", engine.CurrentBuffer.Text.GetText());
    }

    [Fact]
    public void An_already_open_file_keeps_its_unsaved_buffer()
    {
        var path = Write("open.txt", "on disk\n");
        var engine = new VimEngine();
        engine.LoadFile(path);
        engine.CurrentBuffer.Text.ReplaceLine(0, "edited in memory");

        engine.LoadFile(path, PreparedFileLoad.Prepare(path));

        Assert.Equal("edited in memory\n", engine.CurrentBuffer.Text.GetText());
    }
}
