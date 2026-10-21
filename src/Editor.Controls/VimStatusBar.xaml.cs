using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Controls;

public partial class VimStatusBar : UserControl
{
    public EditorTheme Theme { get; set; } = EditorTheme.Dracula;

    /// <summary>
    /// このバーを今どの編集ビューが代表しているか（共有バーとして複数の
    /// <see cref="VimEditorControl"/> にぶら下がっているときだけ意味を持つ）。
    /// <para>
    /// 分割・タブで複数のビューが1本のバーを共有すると、どのビューも自分の都合で書き込むため、
    /// 裏になったビューの再描画が<b>現在のファイル名・行数を上書きする</b>。持ち主を1つに決めて
    /// おき、持ち主でないビューの書き込みは自分の専用バーへ逃がすことでこれを防ぐ
    /// （<c>VimEditorControl.ActiveStatusBar</c>）。
    /// </para>
    /// <para><c>null</c>＝まだ誰も名乗っていない。この間はどのビューも書ける（従来どおりの挙動）。</para>
    /// </summary>
    internal object? Owner { get; set; }

    public VimStatusBar()
    {
        InitializeComponent();
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        UpdateMode(VimMode.Normal);
    }

    /// <summary>
    /// Scales the status-bar text to follow the editor font size. The XAML font
    /// sizes were tuned for the default editor size (14 px), so each element is
    /// scaled by <c>editorFontSize / 14</c> to keep its relative proportion, and
    /// the bar's minimum height grows so larger text is not clipped.
    /// </summary>
    public void SetEditorFontSize(double editorFontSize)
    {
        var ratio = System.Math.Max(1, editorFontSize) / 14.0;
        ModeText.FontSize     = 13 * ratio;
        FileText.FontSize     = 12 * ratio;
        FormatText.FontSize   = 12 * ratio;
        EncodingText.FontSize = 12 * ratio;
        ModifiedText.FontSize = 12 * ratio;
        CommandText.FontSize  = 13 * ratio;
        StatusText.FontSize   = 12 * ratio;
        BranchText.FontSize   = 12 * ratio;
        PosText.FontSize      = 12 * ratio;
        WildmenuText.FontSize = 12 * ratio;
        StatusGrid.MinHeight  = 24 * ratio;
    }

    public void UpdateMode(VimMode mode, bool vimEnabled = true, bool showMode = true)
    {
        if (!vimEnabled || !showMode)
        {
            ModeBorder.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        ModeBorder.Visibility = System.Windows.Visibility.Visible;
        (ModeText.Text, var bg) = mode switch
        {
            VimMode.Insert => ("-- INSERT --", Theme.StatusBarInsert),
            VimMode.Replace => ("-- REPLACE --", Theme.StatusBarReplace),
            VimMode.Visual => ("-- VISUAL --", Theme.StatusBarVisual),
            VimMode.VisualLine => ("-- VISUAL LINE --", Theme.StatusBarVisual),
            VimMode.VisualBlock => ("-- VISUAL BLOCK --", Theme.StatusBarVisual),
            VimMode.Command => ("-- COMMAND --", Theme.StatusBarNormal),
            VimMode.SearchForward => ("-- SEARCH --", Theme.StatusBarNormal),
            VimMode.SearchBackward => ("-- SEARCH -- ", Theme.StatusBarNormal),
            _ => ("NORMAL", Theme.StatusBarNormal)
        };
        ModeBorder.Background = bg;
        ModeText.Foreground = Theme.StatusBarFg;
    }

    public void UpdateFile(string? filePath, bool modified, string? fileFormat = null, string? fileEncoding = null)
    {
        FileText.Text = filePath != null ? System.IO.Path.GetFileName(filePath) : "[No Name]";
        FormatText.Text = fileFormat != null ? $" [{fileFormat}]" : "";
        EncodingText.Text = fileEncoding != null ? $" [{fileEncoding}]" : "";
        ModifiedText.Text = modified ? " [+]" : "";
    }

    /// <summary>The file line displayed (name, format, encoding, modified mark). Exposed for tests.</summary>
    internal string CurrentFileLine => FileText.Text + FormatText.Text + EncodingText.Text + ModifiedText.Text;

    private EventHandler? _fileFormatClicked;
    private EventHandler? _fileEncodingClicked;

    /// <summary>
    /// 改行形式の表示（<c>[dos]</c> など）がクリックされた。ホストが改行をそろえる操作などの入口に使う。
    /// 購読者がいるときだけ、その表示を押せる見た目（手のカーソル・ツールチップ）にする。
    /// </summary>
    public event EventHandler? FileFormatClicked
    {
        add { _fileFormatClicked += value; UpdateClickable(); }
        remove { _fileFormatClicked -= value; UpdateClickable(); }
    }

    /// <summary>
    /// 文字コードの表示（<c>[utf-8]</c> など）がクリックされた。ホストが文字コードを変換する操作などの入口に使う。
    /// 購読者がいるときだけ、その表示を押せる見た目（手のカーソル・ツールチップ）にする。
    /// </summary>
    public event EventHandler? FileEncodingClicked
    {
        add { _fileEncodingClicked += value; UpdateClickable(); }
        remove { _fileEncodingClicked -= value; UpdateClickable(); }
    }

    private void UpdateClickable()
    {
        FormatText.Cursor = _fileFormatClicked != null ? System.Windows.Input.Cursors.Hand : null;
        FormatText.ToolTip = _fileFormatClicked != null ? "改行コード（クリックで変更）" : null;
        EncodingText.Cursor = _fileEncodingClicked != null ? System.Windows.Input.Cursors.Hand : null;
        EncodingText.ToolTip = _fileEncodingClicked != null ? "文字コード（クリックで変更）" : null;
    }

    private void OnFormatTextClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_fileFormatClicked is null || string.IsNullOrEmpty(FormatText.Text))
            return;
        e.Handled = true;
        _fileFormatClicked(this, EventArgs.Empty);
    }

    private void OnEncodingTextClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_fileEncodingClicked is null || string.IsNullOrEmpty(EncodingText.Text))
            return;
        e.Handled = true;
        _fileEncodingClicked(this, EventArgs.Empty);
    }

    public void UpdateGitBranch(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            BranchText.Text = "";
            BranchText.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        BranchText.Text = $"git:{branchName}";
        BranchText.Visibility = System.Windows.Visibility.Visible;
    }

    public void UpdateCursor(CursorPosition pos, int totalLines, bool ruler = true)
    {
        if (!ruler)
        {
            PosText.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }
        PosText.Visibility = System.Windows.Visibility.Visible;
        var pct = totalLines > 0 ? (int)((pos.Line + 1) * 100.0 / totalLines) : 0;
        PosText.Text = $"{pos.Line + 1}/{totalLines}  Col {pos.Column + 1}  {pct}%%";
    }

    public void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    /// <summary>The status message currently displayed. Exposed for tests.</summary>
    internal string CurrentStatus => StatusText.Text;

    private string _commandLine = "";
    private string _commandComposition = "";
    private Run? _compositionRun;

    public void UpdateCommandLine(string text)
    {
        _commandLine = text ?? "";
        RenderCommandLine();
    }

    /// <summary>
    /// コマンドライン（<c>:</c> <c>/</c> <c>?</c>）で IME が組み立て中の文字。確定するまでは
    /// エンジンのコマンドラインに入っていないので、末尾に下線付きで重ねて見せる。空で消す。
    /// </summary>
    public void UpdateCommandLineComposition(string? composition)
    {
        composition ??= "";
        if (composition == _commandComposition) return;
        _commandComposition = composition;
        RenderCommandLine();
    }

    /// <summary>組み立て中の文字の先頭（無ければコマンドラインの末尾）の画面座標。IME の候補窓の位置合わせに使う。</summary>
    internal bool TryGetCommandLineCaretScreenRect(out Rect rect)
    {
        rect = Rect.Empty;
        if (!CommandText.IsVisible) return false;
        try
        {
            var pointer = _compositionRun?.ContentStart ?? CommandText.ContentEnd;
            var local = pointer.GetCharacterRect(LogicalDirection.Forward);
            if (local.IsEmpty) return false;
            var top = CommandText.PointToScreen(local.TopLeft);
            var bottom = CommandText.PointToScreen(local.BottomLeft);
            rect = new Rect(top, new Point(top.X + 1, bottom.Y));
            return true;
        }
        catch { return false; }
    }

    /// <summary>コマンドライン欄に今見えている文字（組み立て中の文字を含む）。テスト用。</summary>
    internal string CurrentCommandLineText
        => CommandText.Visibility == Visibility.Visible
            ? string.Concat(CommandText.Inlines.OfType<Run>().Select(r => r.Text))
            : "";

    /// <summary>下線付きで重ねている組み立て中の文字。テスト用。</summary>
    internal string CurrentCommandComposition => _commandComposition;

    private void RenderCommandLine()
    {
        if (_commandLine.Length == 0 && _commandComposition.Length == 0)
        {
            _compositionRun = null;
            CommandText.Visibility = Visibility.Collapsed;
            FileInfoPanel.Visibility = Visibility.Visible;
            return;
        }

        CommandText.Inlines.Clear();
        CommandText.Inlines.Add(new Run(_commandLine));
        _compositionRun = null;
        if (_commandComposition.Length > 0)
        {
            _compositionRun = new Run(_commandComposition) { TextDecorations = TextDecorations.Underline };
            CommandText.Inlines.Add(_compositionRun);
        }
        CommandText.Visibility = Visibility.Visible;
        FileInfoPanel.Visibility = Visibility.Collapsed;
    }

    public void ShowCompletions(string[] items, int selectedIndex)
    {
        if (items.Length == 0) { HideCompletions(); return; }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append("  ");
            if (i == selectedIndex)
                sb.Append($"[{items[i]}]");
            else
                sb.Append(items[i]);
        }
        WildmenuText.Text = sb.ToString();
        WildmenuBorder.Visibility = System.Windows.Visibility.Visible;
    }

    public void HideCompletions()
    {
        WildmenuBorder.Visibility = System.Windows.Visibility.Collapsed;
    }
}
