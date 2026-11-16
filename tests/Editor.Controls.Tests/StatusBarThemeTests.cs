using System.Windows;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Engine;

namespace Editor.Controls.Tests;

/// <summary>
/// ステータスバー（フッター）の地と文字がエディタのテーマに従うこと。以前はモードの札だけがテーマの色で、
/// 地は #1E1E1E・文字は明るい色の直書きだったため、明色のテーマでもフッターだけ黒いままだった。
/// </summary>
public sealed class StatusBarThemeTests
{
    private static EditorTheme Light() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
        Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0x3A, 0x42)),
        LineNumberBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
        LineNumberFg = new SolidColorBrush(Color.FromRgb(0x9D, 0x9D, 0x9F)),
        CurrentLineBg = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE6)),
        TokenType = new SolidColorBrush(Color.FromRgb(0x01, 0x84, 0xBC)),
    };

    [Fact]
    public void Shared_bar_is_painted_from_the_theme_set_before_attaching()
        => WpfTestHost.Run(() =>
        {
            var theme = Light();
            var bar = new VimStatusBar();
            var editor = new VimEditorControl();
            try
            {
                editor.SetTheme(theme);
                editor.SetSharedStatusBar(bar);
                AssertPainted(bar, theme);
            }
            finally { editor.Dispose(); }
        });

    [Fact]
    public void Shared_bar_follows_a_theme_change_after_attaching()
        => WpfTestHost.Run(() =>
        {
            var theme = Light();
            var bar = new VimStatusBar();
            var editor = new VimEditorControl();
            try
            {
                editor.SetSharedStatusBar(bar);
                editor.SetTheme(theme);
                AssertPainted(bar, theme);
            }
            finally { editor.Dispose(); }
        });

    [Fact]
    public void Theme_change_keeps_the_current_mode_badge()
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar();
            bar.UpdateMode(VimMode.Insert);
            var theme = Light();
            bar.Theme = theme;
            Assert.Equal("-- INSERT --", bar.ModeText.Text);
            Assert.Same(theme.StatusBarInsert, bar.ModeBorder.Background);
            Assert.Same(theme.StatusBarFg, bar.ModeText.Foreground);
        });

    [Theory]
    [InlineData(1000, true, true, true, true)]   // 全部入る
    [InlineData(330, true, true, false, false)]  // 印は丸ごと外れ、[+] とファイル名が残る
    public void Narrow_bar_keeps_the_modified_mark_and_file_name_first(
        double width, bool file, bool modified, bool format, bool encoding)
        => WpfTestHost.Run(() =>
        {
            var bar = new VimStatusBar { Width = width };
            bar.UpdateFile(@"C:\work\my-theme-2.json", modified: true, "dos", "utf-8");
            bar.UpdateGitBranch("main");
            bar.UpdateStatus("\"my-theme-2.json\" 39L written");
            bar.UpdateCursor(new Editor.Core.Models.CursorPosition(0, 0), 39);
            var window = WpfTestHost.Load(bar, width + 40, 100);
            try
            {
                window.UpdateLayout();
                // ActualWidth は枠より狭く置かれても元の幅のまま（見える範囲を切り取るだけ）なので、置かれた枠の幅で見る
                static double Slot(FrameworkElement e) => System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(e).Width;
                Assert.Equal(file, Slot(bar.FileText) > 0);
                Assert.Equal(modified, Slot(bar.ModifiedText) > 0);
                // 印は途中で切らない：見えるなら全幅、見えないなら 0
                foreach (var (tag, shown) in new[] { (bar.FormatText, format), (bar.EncodingText, encoding), (bar.ModifiedText, modified) })
                {
                    tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (shown) Assert.Equal(tag.DesiredSize.Width, Slot(tag), 1);
                    else Assert.Equal(0, Slot(tag));
                }
                // カーソル位置はいつも見える
                Assert.True(Slot(bar.PosText) > 0);
            }
            finally { window.Close(); }
        });

    private static void AssertPainted(VimStatusBar bar, EditorTheme theme)
    {
        Assert.Same(theme.LineNumberBg, bar.Background);
        Assert.Same(theme.Foreground, bar.FileText.Foreground);
        Assert.Same(theme.Foreground, bar.PosText.Foreground);
        Assert.Same(theme.Foreground, bar.CommandText.Foreground);
        Assert.Same(theme.Foreground, bar.StatusText.Foreground);
        Assert.Same(theme.Foreground, bar.BranchText.Foreground);
        Assert.Same(theme.CurrentLineBg, bar.WildmenuBorder.Background);
    }
}
