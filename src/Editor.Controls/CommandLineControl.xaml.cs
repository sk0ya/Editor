using System.Windows;
using System.Windows.Controls;

namespace Editor.Controls;

public partial class CommandLineControl : UserControl
{
    public CommandLineControl()
    {
        InitializeComponent();
    }

    public void Update(string text)
    {
        CmdText.Text = text;
        Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
