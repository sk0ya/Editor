using Editor.Core.Buffer;
using Editor.Core.Editing;
using Editor.Core.Marks;
using Editor.Core.Models;
using Editor.Core.Syntax;

namespace Editor.Core.Tests;

/// <summary>
/// How changes reach a listener, as opposed to what each change says (<see cref="TextBufferChangeTests"/>):
/// a transaction rolled back inside another, a listener that throws, a buffer that arrives already
/// loaded. In each case a host replaying the changes must still end up with the buffer's text.
/// </summary>
public class TextChangeDeliveryTests
{
    private static string Replay(string text, IEnumerable<TextBufferChange> changes)
    {
        var lines = text.Split('\n').ToList();
        foreach (var change in changes)
        {
            var head = lines[change.StartLine][..change.StartColumn];
            var tail = lines[change.EndLine][change.EndColumn..];
            lines.RemoveRange(change.StartLine, change.EndLine - change.StartLine + 1);
            lines.InsertRange(change.StartLine, (head + change.NewText + tail).Split('\n'));
        }
        return string.Join("\n", lines);
    }

    private static EditTransactionService CreateService(BufferManager buffers)
    {
        var cursor = CursorPosition.Zero;
        return new(buffers, new MarkManager(), new SyntaxEngine(), () => cursor, value => cursor = value,
            () => false, (events, message) => events.Add(VimEvent.StatusMessage(message)));
    }

    [Fact]
    public void A_nested_rollback_drops_only_its_own_changes_not_the_enclosing_ones()
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("abc");
        var service = CreateService(buffers);
        var changes = new List<TextBufferChange>();
        buffers.Current.Text.Changed += changes.Add;

        service.Execute([], outer =>
        {
            outer.Buffer.InsertChar(0, 0, 'X');
            service.Execute([], inner =>
            {
                inner.Buffer.InsertChar(0, 0, 'Y');
                inner.Rollback();
                return null;
            });
            outer.Buffer.InsertChar(0, 4, 'Z');
            return null;
        });

        Assert.Equal("XabcZ", buffers.Current.Text.GetText());
        Assert.Equal("XabcZ", Replay("abc", changes));
    }

    [Fact]
    public void A_throwing_listener_neither_escapes_nor_starves_the_other_listeners()
    {
        var buffers = new BufferManager();
        buffers.Current.Text.SetText("abc");
        var service = CreateService(buffers);
        var changes = new List<TextBufferChange>();
        buffers.Current.Text.Changed += _ => throw new InvalidOperationException("host bug");
        buffers.Current.Text.Changed += changes.Add;

        // Unbatched, then a batch of several changes delivered at commit.
        buffers.Current.Text.InsertChar(0, 0, '1');
        var result = service.Execute([], tx =>
        {
            tx.Buffer.InsertChar(0, 0, '2');
            tx.Buffer.InsertChar(0, 0, '3');
            return null;
        });

        Assert.True(result.Applied);
        Assert.Equal(3, changes.Count);
        Assert.Equal("321abc", Replay("abc", changes));
    }

    [Fact]
    public void BufferTextChanged_isolates_its_own_handlers_too()
    {
        var buffers = new BufferManager();
        var received = 0;
        buffers.BufferTextChanged += (_, _) => throw new InvalidOperationException("host bug");
        buffers.BufferTextChanged += (_, _) => received++;

        buffers.Current.Text.InsertChar(0, 0, 'x');

        Assert.Equal(1, received);
    }

    [Fact]
    public void Opening_a_file_reports_its_content_as_a_load_from_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"editor-open-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "hello\nworld");
        try
        {
            var buffers = new BufferManager();
            var changes = new List<(VimBuffer Buffer, TextBufferChange Change)>();
            buffers.BufferTextChanged += (b, c) => changes.Add((b, c));

            var opened = buffers.OpenFile(path);

            var (buffer, change) = Assert.Single(changes);
            Assert.Same(opened, buffer);
            Assert.Same(opened, buffers.Current);   // announced once it is the current buffer
            Assert.Equal(TextBufferChangeKind.Reload, change.Kind);
            Assert.Equal(opened.Text.Version, change.Version);
            Assert.Equal(opened.Text.GetText(), Replay("", [change]));

            // Edits that follow apply on top of what was announced.
            opened.Text.InsertChar(1, 0, 'W');
            Assert.Equal(opened.Text.GetText(), Replay("", changes.Select(c => c.Change)));

            // Re-opening the same path reuses the buffer: nothing new to announce.
            buffers.NewBuffer();
            var before = changes.Count;
            buffers.OpenFile(path);
            Assert.Equal(before, changes.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Nothing_is_announced_while_nobody_listens()
    {
        var buffers = new BufferManager();
        buffers.NewBuffer();
        var changes = new List<TextBufferChange>();
        buffers.BufferTextChanged += (_, c) => changes.Add(c);

        buffers.Current.Text.InsertChar(0, 0, 'x');

        Assert.Single(changes);   // the edit only — subscribing announces nothing retroactively
    }
}
