using System.Windows;
using System.Windows.Media;
using Editor.Controls.Lsp;
using Editor.Controls.Themes;

namespace Editor.Controls.Tests;

/// <summary>Regression coverage for the Rename Symbol dialog: it used to be a fixed 300x140 non-resizable
/// window painted in hardcoded Dracula colours, so the button row was clipped whenever the content needed
/// more than 140px (larger font, Japanese font metrics, DPI scaling) and the dialog never matched the active
/// theme. These tests build the dialog without showing it and check both properties directly.</summary>
public sealed class RenameSymbolDialogTests
{
    /// <summary>組み立て → 測定 → 配置まで済ませ、実寸で検証できる状態にする。</summary>
    private static RenameSymbolDialog.Parts BuildAndLayout(EditorTheme theme, string currentName)
    {
        var parts = RenameSymbolDialog.Build(theme, currentName);
        parts.Shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        parts.Shell.Arrange(new Rect(parts.Shell.DesiredSize));
        parts.Shell.UpdateLayout();
        return parts;
    }

    [Fact]
    public void Window_sizes_itself_to_its_content_instead_of_a_fixed_height()
    {
        WpfTestHost.Run(() =>
        {
            var parts = RenameSymbolDialog.Build(EditorTheme.Dracula, "symbol");

            // 高さを決め打ちしない＝中身が育っても見切れない、というのがこの修正の核心。
            Assert.Equal(SizeToContent.WidthAndHeight, parts.Window.SizeToContent);
            Assert.True(double.IsNaN(parts.Window.Height), "ウィンドウ高さは固定してはいけない");
            Assert.True(double.IsNaN(parts.Window.Width), "ウィンドウ幅は固定してはいけない");
        });
    }

    /// <summary>旧実装のウィンドウ高さ（決め打ち）。見切れの基準線。</summary>
    private const double OldFixedHeight = 140;

    private static double ButtonsBottom(RenameSymbolDialog.Parts parts) =>
        parts.Buttons
            .TransformToAncestor(parts.Shell)
            .Transform(new Point(0, parts.Buttons.ActualHeight)).Y;

    [Fact]
    public void Buttons_fit_inside_the_dialog_and_are_never_clipped()
    {
        WpfTestHost.Run(() =>
        {
            var parts = BuildAndLayout(EditorTheme.Dracula, "symbol");

            Assert.True(parts.Buttons.ActualHeight > 0, "ボタン行が測定されていない");
            var bottom = ButtonsBottom(parts);

            // (1) 旧実装の壊れ方の再現。旧ダイアログは Height=140 を**タイトルバー込み**で指定していた
            // （WindowStyle 既定＝OS のキャプションが乗る）ので、中身に使えるのは 140 − キャプション高。
            // ボタン行の下端はその範囲を超える＝押し込めば必ず下が切れる、という関係をここで固定する。
            var oldClientHeight = OldFixedHeight - SystemParameters.WindowCaptionHeight;
            Assert.True(
                bottom > oldClientHeight,
                $"ボタン行の下端 {bottom} が旧実装のクライアント高 {oldClientHeight} に収まっている（この前提が崩れたらテストの意味が無い）");

            // (2) 新実装：高さを内容から決めるので、その中には必ず収まる。
            Assert.True(
                bottom <= parts.Shell.DesiredSize.Height + 0.5,
                $"ボタン行の下端 {bottom} がダイアログ高さ {parts.Shell.DesiredSize.Height} を超えている");
        });
    }

    [Fact]
    public void Dialog_grows_taller_than_the_old_fixed_height_would_have_allowed()
    {
        WpfTestHost.Run(() =>
        {
            var parts = BuildAndLayout(EditorTheme.Dracula, "symbol");

            // 旧実装の 140px は「ちょうど収まる」前提の値だった。実測がそれを超える＝
            // 固定値のままでは（フォント・DPI が変わる前から）既に見切れている、という根拠。
            Assert.True(
                parts.Shell.DesiredSize.Height > OldFixedHeight,
                $"必要高さ {parts.Shell.DesiredSize.Height} が旧固定高 {OldFixedHeight} 以下");
            Assert.True(parts.Shell.DesiredSize.Width >= RenameSymbolDialog.ContentMinWidth);
        });
    }

    [Fact]
    public void Header_is_draggable_across_its_whole_width()
    {
        WpfTestHost.Run(() =>
        {
            var parts = BuildAndLayout(EditorTheme.Dracula, "symbol");

            // タイトル文字でも ✕ でもない「空白部分」を掴む。背景を敷いていないと当たり判定が無く、
            // クリックは背後の外枠へ落ちて DragMove が始まらない（＝ヘッダーを掴んでも動かせない）。
            var header = parts.Header;
            var origin = header.TransformToAncestor(parts.Shell).Transform(new Point(0, 0));
            var probe = new Point(
                origin.X + header.ActualWidth * 0.6,
                origin.Y + header.ActualHeight / 2);

            var hit = VisualTreeHelper.HitTest(parts.Shell, probe)?.VisualHit as DependencyObject;

            Assert.NotNull(hit);
            Assert.True(IsSelfOrDescendantOf(hit!, header), "ヘッダーの空白がヒットテストに乗っていない");
        });
    }

    private static bool IsSelfOrDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    [Fact]
    public void A_very_long_identifier_widens_the_dialog_but_stays_within_the_cap()
    {
        WpfTestHost.Run(() =>
        {
            var shortName = BuildAndLayout(EditorTheme.Dracula, "a");
            var longName = BuildAndLayout(EditorTheme.Dracula, new string('W', 400));

            Assert.True(longName.Shell.DesiredSize.Width >= shortName.Shell.DesiredSize.Width);
            Assert.True(
                longName.Shell.DesiredSize.Width <= RenameSymbolDialog.ContentMaxWidth + 0.5,
                $"幅 {longName.Shell.DesiredSize.Width} が上限 {RenameSymbolDialog.ContentMaxWidth} を超えている");
        });
    }

    [Fact]
    public void Colours_come_from_the_active_theme_not_from_a_hardcoded_palette()
    {
        WpfTestHost.Run(() =>
        {
            var nord = RenameSymbolDialog.Build(EditorTheme.Nord, "symbol");

            Assert.Same(EditorTheme.Nord.LineNumberBg, nord.Shell.Background);
            Assert.Same(EditorTheme.Nord.Foreground, nord.Input.Foreground);
            Assert.Same(EditorTheme.Nord.Background, nord.Input.Background);
        });
    }

    [Fact]
    public void Two_different_themes_produce_two_different_dialogs()
    {
        WpfTestHost.Run(() =>
        {
            var dracula = RenameSymbolDialog.Build(EditorTheme.Dracula, "symbol");
            var nord = RenameSymbolDialog.Build(EditorTheme.Nord, "symbol");

            // 決め打ちに戻ったらここで落ちる。
            Assert.NotEqual(
                ((SolidColorBrush)dracula.Shell.Background).Color,
                ((SolidColorBrush)nord.Shell.Background).Color);
        });
    }

    [Fact]
    public void Dialog_opens_centred_on_its_owner_window()
    {
        WpfTestHost.Run(() =>
        {
            var parts = RenameSymbolDialog.Build(EditorTheme.Dracula, "symbol");

            // 画面中央だとマルチモニタでエディタと違う画面に出てしまう。
            Assert.Equal(WindowStartupLocation.CenterOwner, parts.Window.WindowStartupLocation);
        });
    }

    [Fact]
    public void Input_is_prefilled_with_the_current_name()
    {
        WpfTestHost.Run(() =>
        {
            var parts = RenameSymbolDialog.Build(EditorTheme.Dracula, "OldName");
            Assert.Equal("OldName", parts.Input.Text);
        });
    }
}
