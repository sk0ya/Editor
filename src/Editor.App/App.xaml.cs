using System.Windows;
using System.Windows.Threading;
using Editor.Controls;

namespace Editor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Last-resort safety net: keep the editor alive when an exception escapes the UI thread
        // (e.g. from a dispatcher callback or an unguarded code path) instead of letting WPF tear
        // the process down. The control guards its own key/render paths; this backstops the rest.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Prime the JIT for the expensive editor-construction path (.vimrc parsing) on a
        // background thread while WPF spins up the window, so the first editor we create
        // doesn't block startup. See VimEditorControl.WarmUpAsync.
        _ = VimEditorControl.WarmUpAsync();

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled UI exception (kept alive): {e.Exception}");
        e.Handled = true;
    }
}
