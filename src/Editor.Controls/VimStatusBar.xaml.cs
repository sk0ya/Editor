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

    public void UpdateMode(VimMode mode)
    {
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

    public void UpdateFile(string? filePath, bool modified)
    {
        var name = filePath != null ? System.IO.Path.GetFileName(filePath) : "[No Name]";
        FileText.Text = modified ? $"{name} [+]" : name;
    }

    public void UpdateCursor(CursorPosition pos, int totalLines)
    {
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
}
