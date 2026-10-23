using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace HolyScreen.Tests;

/// <summary>
/// One long-lived STA thread with a WPF <see cref="Application"/> and the app's theme merged in, for tests that build
/// real windows (they look styles and brushes up through <see cref="Application.Current"/>). WPF allows a single
/// Application per process, so every such test shares this thread. Windows are built but never shown.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Dispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action) => Dispatcher.Value.Invoke(action);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        Exception? error = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/HolyScreen;component/UI/Theme/Theme.xaml"),
                });
                dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            ready.Set();
            if (error is null) System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test UI thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (error is not null) throw new InvalidOperationException("The WPF test application could not start.", error);
        return dispatcher!;
    }
}
