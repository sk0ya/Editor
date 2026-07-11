using System.Windows;
using System.Windows.Threading;

namespace Editor.Controls.Tests;

/// <summary>Runs a test in a real WPF STA, including load/layout and dispatcher work.</summary>
internal static class WpfTestHost
{
    private static readonly Thread UiThread;
    private static readonly TaskCompletionSource<Dispatcher> Ready = new();
    private static readonly TaskCompletionSource<object?> Lifetime = new();

    static WpfTestHost()
    {
        UiThread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
                Lifetime.TrySetException(new InvalidOperationException("The WPF test dispatcher stopped unexpectedly."));
            }
            catch (Exception ex)
            {
                Ready.TrySetException(ex);
                Lifetime.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "Editor.Controls.Tests WPF dispatcher" };
        UiThread.SetApartmentState(ApartmentState.STA);
        UiThread.Start();
    }

    public static void Run(Action action)
    {
        var dispatcher = Ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (Lifetime.Task.IsCompleted) Lifetime.Task.GetAwaiter().GetResult();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.BeginInvoke(() =>
        {
            try { action(); completion.TrySetResult(null); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, DispatcherPriority.Send);
        try
        {
            Task.WhenAny(completion.Task, Lifetime.Task).WaitAsync(TimeSpan.FromSeconds(15))
                .GetAwaiter().GetResult().GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            operation.Abort();
            throw new TimeoutException("Timed out invoking work on the WPF test dispatcher.");
        }
    }

    public static Window Load(FrameworkElement element, double width = 640, double height = 360)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000,
            Content = element
        };
        window.Show();
        element.UpdateLayout();
        return window;
    }

    public static void WithLoadedControl<T>(Action<T, Window> action, Action<T>? beforeLoad = null) where T : FrameworkElement, new()
    {
        T? control = null;
        Window? window = null;
        try
        {
            control = new T();
            beforeLoad?.Invoke(control);
            window = Load(control);
            action(control, window);
        }
        finally
        {
            try { window?.Close(); }
            finally
            {
                if (window != null) window.Content = null;
                if (control is IDisposable disposable) disposable.Dispose();
            }
        }
    }

    public static void DrainDispatcher()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        if (dispatcher.CheckAccess()) return;
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
