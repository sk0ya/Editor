using Editor.Core.Buffer;

namespace Editor.Core.Tests;

/// <summary>
/// <see cref="TextBuffer.Changed"/> must describe every mutation exactly: replaying the reported
/// changes onto a copy of the previous text has to reproduce the buffer. Hosts keep other views and
/// excerpts in step with nothing but these changes, so a wrong range is silent corruption there.
/// </summary>
public class TextBufferChangeTests
{
    /// <summary>A copy of the document kept up to date only from the reported changes.</summary>
    private sealed class Shadow
    {
        private readonly TextBuffer _buffer;
        public List<string> Lines { get; }
        public List<TextBufferChange> Changes { get; } = [];

        public Shadow(TextBuffer buffer)
        {
            _buffer = buffer;
            Lines = [.. buffer.GetText().Split('\n')];
            buffer.Changed += Apply;
        }

        private void Apply(TextBufferChange change)
        {
            Changes.Add(change);
            Assert.Equal(_buffer.Version, change.Version);
            var head = Lines[change.StartLine][..change.StartColumn];
            var tail = Lines[change.EndLine][change.EndColumn..];
            var replaced = (head + change.NewText + tail).Split('\n');
            Lines.RemoveRange(change.StartLine, change.EndLine - change.StartLine + 1);
            Lines.InsertRange(change.StartLine, replaced);
        }

        public void AssertInSync(string? operation = null)
        {
            var expected = _buffer.GetText();
            var actual = string.Join("\n", Lines);
            Assert.True(expected == actual,
                $"after {operation}: buffer={Escape(expected)} shadow={Escape(actual)} last={Changes.LastOrDefault()}");
        }
    }

    private static (TextBuffer Buffer, Shadow Shadow) Make(string text)
    {
        var buffer = new TextBuffer(text);
        return (buffer, new Shadow(buffer));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(4, 0)]      // past the end: the document is padded first
    public void InsertChar_and_InsertText_are_reported(int line, int column)
    {
        var (buffer, shadow) = Make("abc\nde");
        buffer.InsertChar(line, column, 'x');
        shadow.AssertInSync();
        buffer.InsertText(line, column, "p\nq\r\nr");
        shadow.AssertInSync();
    }

    [Fact]
    public void BreakLine_DeleteChar_DeleteRange_JoinLines_are_reported()
    {
        var (buffer, shadow) = Make("hello world\nsecond");
        buffer.BreakLine(0, 5);
        shadow.AssertInSync();
        buffer.DeleteChar(1, 0);
        shadow.AssertInSync();
        buffer.DeleteRange(1, 1, 4);
        shadow.AssertInSync();
        buffer.JoinLines(0);
        shadow.AssertInSync();
        buffer.JoinLines(0);
        shadow.AssertInSync();
    }

    [Theory]
    [InlineData(0, 0)]   // first line, more follow
    [InlineData(1, 2)]   // middle
    [InlineData(2, 3)]   // runs to the end: takes the line break before it
    [InlineData(0, 3)]   // everything: leaves one empty line
    public void DeleteLines_takes_exactly_one_line_break(int start, int end)
    {
        var (buffer, shadow) = Make("a\nb\nc\nd");
        buffer.DeleteLines(start, end);
        shadow.AssertInSync();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]   // after the last line
    public void InsertLines_and_InsertLineAbove_are_reported(int afterLine)
    {
        var (buffer, shadow) = Make("a\nb");
        buffer.InsertLines(afterLine, ["x", "y"]);
        shadow.AssertInSync();
        buffer.InsertLineAbove(buffer.LineCount, "tail");
        shadow.AssertInSync();
        buffer.InsertLineAbove(0, "head");
        shadow.AssertInSync();
    }

    [Fact]
    public void ReplaceLine_reports_the_line_and_any_padding()
    {
        var (buffer, shadow) = Make("a\nb");
        buffer.ReplaceLine(1, "changed");
        shadow.AssertInSync();
        buffer.ReplaceLine(4, "far");
        shadow.AssertInSync();
        var before = shadow.Changes.Count;
        buffer.ReplaceLine(0, "a");   // no-op: nothing reported
        Assert.Equal(before, shadow.Changes.Count);
    }

    /// <summary>Undo restores a snapshot of the whole document; the change must cover only the lines
    /// that differ, so positions in the untouched head and tail survive it.</summary>
    [Fact]
    public void RestoreSnapshot_reports_only_the_lines_that_differ()
    {
        var (buffer, shadow) = Make("one\ntwo\nthree\nfour");
        var snapshot = buffer.Snapshot();
        buffer.ReplaceLine(2, "THREE");
        buffer.InsertLines(2, ["extra"]);
        buffer.RestoreSnapshot(snapshot);
        shadow.AssertInSync();

        var undo = shadow.Changes[^1];
        Assert.Equal(2, undo.StartLine);
        Assert.Equal(4, undo.EndLine);
        Assert.Equal("three\n", undo.NewText);
    }

    [Theory]
    [InlineData("a\nb\nc", "a\nb\nc\nd")]       // appended at the end
    [InlineData("a\nb\nc\nd", "a\nb")]          // removed from the end
    [InlineData("a\nb\nc", "x\na\nb\nc")]       // added at the start
    [InlineData("a\nb\nc", "a\nB\nc")]          // one line changed
    [InlineData("a", "")]
    [InlineData("", "a\nb")]
    public void ReplaceAll_reports_a_minimal_line_range(string before, string after)
    {
        var (buffer, shadow) = Make(before);
        buffer.ReplaceAll(after);
        shadow.AssertInSync();
        Assert.Single(shadow.Changes);
    }

    [Fact]
    public void SetText_is_reported_as_a_reload()
    {
        var (buffer, shadow) = Make("old\ntext");
        buffer.SetText("new");
        shadow.AssertInSync();
        Assert.Equal(TextBufferChangeKind.Reload, shadow.Changes.Single().Kind);
    }

    [Fact]
    public void Random_edits_keep_the_shadow_in_sync()
    {
        var random = new Random(20261008);
        var (buffer, shadow) = Make("alpha\nbeta\ngamma\ndelta");
        var snapshots = new List<string[]>();
        for (var i = 0; i < 2000; i++)
        {
            var line = random.Next(0, buffer.LineCount + 2);
            var column = random.Next(0, 8);
            var op = random.Next(12);
            var before = Escape(buffer.GetText());
            switch (op)
            {
                case 0: buffer.InsertChar(line, column, (char)('a' + random.Next(26))); break;
                case 1: buffer.InsertText(line, column, random.Next(3) == 0 ? "x\ny" : "zz"); break;
                case 2: buffer.BreakLine(line, column); break;
                case 3: buffer.DeleteChar(line, column); break;
                case 4: buffer.DeleteRange(line, column, column + random.Next(4)); break;
                case 5: buffer.JoinLines(line); break;
                case 6: buffer.DeleteLines(line, line + random.Next(3)); break;
                case 7: buffer.InsertLines(line - 1, ["n" + i, "m" + i]); break;
                case 8: buffer.InsertLineAbove(line, "above" + i); break;
                case 9: buffer.ReplaceLine(Math.Min(line, buffer.LineCount + 1), "r" + i); break;
                case 10: snapshots.Add(buffer.Snapshot()); break;
                case 11 when snapshots.Count > 0: buffer.RestoreSnapshot(snapshots[random.Next(snapshots.Count)]); break;
            }
            shadow.AssertInSync($"op {op} line {line} col {column} on {before}");
        }
    }

    private static string Escape(string text) => text.Replace("\n", "\\n");

    [Fact]
    public void Nothing_is_raised_without_a_subscriber_and_text_is_unchanged()
    {
        var buffer = new TextBuffer("a\nb");
        buffer.InsertText(0, 1, "x\ny");
        buffer.DeleteLines(0, 0);
        buffer.RestoreSnapshot(["q"]);
        Assert.Equal("q", buffer.GetText());
    }
}
