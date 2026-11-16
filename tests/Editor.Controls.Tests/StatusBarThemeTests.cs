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
