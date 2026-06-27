using System.Windows;
using Editor.Controls;

namespace Editor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Prime the JIT for the expensive editor-construction path (.vimrc parsing) on a
        // background thread while WPF spins up the window, so the first editor we create
        // doesn't block startup. See VimEditorControl.WarmUpAsync.
        _ = VimEditorControl.WarmUpAsync();

        base.OnStartup(e);
    }
}
