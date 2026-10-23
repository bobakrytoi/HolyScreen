using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace HolyScreen.Tests;

/// <summary>Runs WPF-dependent test code on a dedicated STA thread (xUnit uses MTA thread-pool threads).</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() => { result = func(); });
        return result;
    }
}
