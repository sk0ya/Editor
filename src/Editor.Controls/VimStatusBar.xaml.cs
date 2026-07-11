using System.Windows.Controls;
using System.Windows.Media;
using Editor.Controls.Themes;
using Editor.Core.Engine;
using Editor.Core.Models;

namespace Editor.Controls;

public partial class VimStatusBar : UserControl
{
    public EditorTheme Theme { get; set; } = EditorTheme.Dracula;

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

    public void UpdateFile(string? filePath, bool modified, string? fileFormat = null)
    {
        var name = filePath != null ? System.IO.Path.GetFileName(filePath) : "[No Name]";
        var fmt = fileFormat != null ? $" [{fileFormat}]" : "";
        FileText.Text = modified ? $"{name}{fmt} [+]" : $"{name}{fmt}";
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

    public void UpdateCommandLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            CommandText.Visibility = System.Windows.Visibility.Collapsed;
            FileText.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            CommandText.Text = text;
            CommandText.Visibility = System.Windows.Visibility.Visible;
            FileText.Visibility = System.Windows.Visibility.Collapsed;
        }
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
