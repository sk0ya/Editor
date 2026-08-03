using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Controls.Themes;

namespace Editor.Controls.Lsp;

/// <summary>
/// Rename Symbol の入力ダイアログ。<see cref="VimEditorControl"/> から切り出してあるのは、
/// 「見切れないこと」と「テーマに追随すること」をウィンドウを表示せずに検証できるようにするため。
///
/// <para>設計の要点は 2 つ。
/// (1) <b>寸法を固定しない</b>。以前は <c>Width=300 / Height=140 / NoResize</c> の決め打ちで、
/// フォントサイズ・日本語フォントの行高・DPI スケールのどれかが想定より大きいだけでボタン行が
/// 下にはみ出して見切れていた。ここでは <see cref="SizeToContent.WidthAndHeight"/> で中身に合わせ、
/// 横だけ <see cref="ContentMinWidth"/>〜<see cref="ContentMaxWidth"/> に収める。高さに上限を置かないので
/// 見切れは原理的に起こらない。
/// (2) <b>色をテーマから取る</b>。以前は Dracula の色を直値で書いていたので、他のテーマ
/// (Kanagawa など) ではエディタ本体と全く違う見た目になっていた。配色は
/// <see cref="EditorTheme"/> からのみ取り、既存の <c>ShowCopyableMessage</c> と同じ
/// クロームレスウィンドウの流儀に揃える。</para>
/// </summary>
internal static class RenameSymbolDialog
{
    /// <summary>識別子を打ち込むのに窮屈でない最小幅。中身がこれより小さくてもここまでは広げる。</summary>
    internal const double ContentMinWidth = 340;

    /// <summary>長い識別子が既定値でも、画面いっぱいの横長ウィンドウにしないための上限。
    /// これを超える分は入力欄が横スクロールする。</summary>
    internal const double ContentMaxWidth = 640;

    /// <summary>組み立て済みのダイアログ。表示せずにレイアウトとテーマを検証できるよう、
    /// 検証で触りたい要素だけを公開する。</summary>
    /// <param name="Window">表示用のウィンドウ。</param>
    /// <param name="Shell">角丸の外枠。ウィンドウ自体は透明なので、実際の背景色はこちらが持つ。</param>
    /// <param name="Input">名前の入力欄。</param>
    /// <param name="Buttons">確定／取消のボタン行。見切れ検証の対象。</param>
    /// <param name="Header">タイトル帯。ここを掴んでウィンドウを動かすので、当たり判定の検証対象。</param>
    internal sealed record Parts(
        Window Window, Border Shell, TextBox Input, FrameworkElement Buttons, FrameworkElement Header);

    /// <summary>ダイアログを組み立てる（表示はしない）。</summary>
    internal static Parts Build(EditorTheme theme, string currentName)
    {
        // 配色は ShowCopyableMessage と同じ対応付け（外枠=行番号帯、本文=エディタ背景）にして、
        // どのテーマでもエディタと地続きに見えるようにする。
        var bg = theme.LineNumberBg;
        var bodyBg = theme.Background;
        var fg = theme.Foreground;
        var muted = theme.LineNumberFg;
        var border = theme.IndentGuideBrush;
        var accent = theme.LinkColor;
        var mono = new FontFamily("Cascadia Code, Consolas");

        // ── Header ──────────────────────────────────────────────────────
        var accentBar = new Border
        {
            Width = 3,
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 10, 0),
        };
        var titleText = new TextBlock
        {
            Text = "Rename Symbol",
            Foreground = fg,
            FontFamily = mono,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var closeGlyph = new TextBlock
        {
            Text = "✕",
            Foreground = muted,
            FontFamily = mono,
            FontSize = 13,
            Cursor = Cursors.Hand,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            // 透明でも背景を敷かないとヒットテストに乗らず、Padding 部分のクリックが素通りする。
            Background = Brushes.Transparent,
        };

        // 同上。背景が無い Grid は当たり判定を持たないので、ヘッダーの空白を掴んでも
        // DragMove が始まらない（クリックは背後の shell に落ち、header をバブリングしない）。
        var header = new Grid { Margin = new Thickness(14, 11, 8, 11), Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(titleText, 1);
        Grid.SetColumn(closeGlyph, 3);
        header.Children.Add(accentBar);
        header.Children.Add(titleText);
        header.Children.Add(closeGlyph);

        // ── Body ────────────────────────────────────────────────────────
        var label = new TextBlock
        {
            Text = "New name",
            Foreground = muted,
            FontFamily = mono,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var input = new TextBox
        {
            Text = currentName,
            Foreground = fg,
            Background = bodyBg,
            CaretBrush = accent,
            SelectionBrush = accent,
            BorderThickness = new Thickness(0),
            FontFamily = mono,
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            // 上限幅まで来たら折り返さず横スクロールさせる（高さが暴れないように）。
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var inputWrap = new Border
        {
            Background = bodyBg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = input,
        };

        var body = new StackPanel { Margin = new Thickness(14, 0, 14, 0) };
        body.Children.Add(label);
        body.Children.Add(inputWrap);

        // ── Footer ──────────────────────────────────────────────────────
        var okButton = MakeButton("Rename", primary: true, fg, bodyBg, accent, theme.CurrentLineBg, mono);
        okButton.IsDefault = true;
        var cancelButton = MakeButton("Cancel", primary: false, fg, bodyBg, accent, theme.CurrentLineBg, mono);
        cancelButton.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var footer = new Grid { Margin = new Thickness(14, 12, 12, 12) };
        footer.Children.Add(buttons);

        // ── Assemble ────────────────────────────────────────────────────
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(footer);

        var shell = new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            // 幅だけ挟み込み、高さは中身のまま伸ばす＝ボタン行が切れることがない。
            MinWidth = ContentMinWidth,
            MaxWidth = ContentMaxWidth,
            Child = grid,
        };

        var window = new Window
        {
            Title = "Rename Symbol",
            Content = shell,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            // 画面中央ではなくエディタのウィンドウ中央に出す（マルチモニタでも視線が飛ばない）。
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        // クロームレスなのでヘッダーをつかんで移動、✕ / Esc で閉じる。
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) window.DragMove();
        };
        closeGlyph.MouseLeftButtonDown += (_, e) => { e.Handled = true; window.Close(); };
        cancelButton.Click += (_, _) => window.Close();
        okButton.Click += (_, _) => window.DialogResult = true;
        input.KeyDown += (_, e) => { if (e.Key == Key.Return) window.DialogResult = true; };
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

        return new Parts(window, shell, input, buttons, header);
    }

    /// <summary>ダイアログを表示し、確定された新しい名前を返す。取消時は null。</summary>
    internal static string? Show(EditorTheme theme, string currentName, Window? owner)
    {
        var parts = Build(theme, currentName);
        if (owner != null && owner != parts.Window) parts.Window.Owner = owner;
        return parts.Window.ShowDialog() == true ? parts.Input.Text.Trim() : null;
    }

    /// <summary>ShowCopyableMessage のボタンと同じ見た目（角丸・枠なし・テーマ色）を作る。</summary>
    private static Button MakeButton(
        string label, bool primary, Brush fg, Brush bodyBg, Brush accent, Brush neutral, FontFamily mono)
    {
        var b = new Button
        {
            Content = label,
            FontFamily = mono,
            FontSize = 12,
            // 高さは固定せず余白で確保する。フォントが大きくなればボタンも一緒に育つ。
            MinWidth = 84,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = primary ? bodyBg : fg,
            Background = primary ? accent : neutral,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(Button.Background))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        bd.SetValue(Border.PaddingProperty,
            new System.Windows.Data.Binding(nameof(Button.Padding))
            {
                RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent,
            });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;
        b.Template = tpl;
        return b;
    }
}
