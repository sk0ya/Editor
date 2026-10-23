using System.Windows;
using System.Windows.Input;

namespace Editor.Controls.Tests;

/// <summary>
/// The status bar's line-ending and encoding indicators are clickable when a host subscribes —
/// the entry point for "convert to UTF-8 / normalize line endings" in the host.
/// </summary>
public sealed class StatusBarFileInfoClickTests
{
    [Fact]
    public void File_line_keeps_name_format_encoding_and_modified_mark()
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            bar.UpdateFile(@"C:\work\a.txt", modified: true, "dos", "shift_jis");

            Assert.Equal("a.txt [dos] [shift_jis] [+]", bar.CurrentFileLine);
        });

    [Fact]
    public void Clicking_indicators_raises_their_events()
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            bar.UpdateFile(@"C:\work\a.txt", modified: false, "unix", "utf-8");
            var (format, encoding) = (0, 0);
            bar.FileFormatClicked += (_, _) => format++;
            bar.FileEncodingClicked += (_, _) => encoding++;

            Click(bar.FormatText);
            Click(bar.EncodingText);
            Click(bar.EncodingText);

            Assert.Equal((1, 2), (format, encoding));
        });

    [Fact]
    public void Indicators_look_clickable_only_while_someone_listens()
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            Assert.Null(bar.EncodingText.Cursor);

            EventHandler handler = (_, _) => { };
            bar.FileEncodingClicked += handler;
            Assert.Equal(Cursors.Hand, bar.EncodingText.Cursor);

            bar.FileEncodingClicked -= handler;
            Assert.Null(bar.EncodingText.Cursor);
        });

    [Fact]
    public void Empty_indicator_does_not_raise()
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            bar.UpdateFile(null, modified: false);   // no format / encoding known yet
            var raised = false;
            bar.FileEncodingClicked += (_, _) => raised = true;

            Click(bar.EncodingText);

            Assert.False(raised);
        });

    [Fact]
    public void Ruler_shows_a_single_percent_sign()
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            bar.UpdateCursor(new Editor.Core.Models.CursorPosition(49, 3), totalLines: 200);

            Assert.Equal("50/200  Col 4  25%", bar.PosText.Text);
        });

    private static void Click(UIElement element)
        => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
}
