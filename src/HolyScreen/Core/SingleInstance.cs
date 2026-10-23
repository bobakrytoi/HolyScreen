using System;
using System.Threading;

namespace HolyScreen.Core;

internal enum InstanceCommand
{
    CaptureRegion = 0,
    CaptureFullScreen = 1,
    Settings = 2,
    Exit = 3,
    History = 4,
    CaptureDelayed = 5,
}

/// <summary>
/// Single-instance enforcement for the current Windows session. A second launch signals the running instance
/// (capture / open settings / exit) through named events instead of starting again.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly InstanceCommand[] Commands =
    {
        InstanceCommand.CaptureRegion, InstanceCommand.CaptureFullScreen, InstanceCommand.Settings, InstanceCommand.Exit,
        InstanceCommand.History, InstanceCommand.CaptureDelayed,
    };

    private readonly Mutex _mutex;
    private readonly EventWaitHandle[] _events;
    private readonly RegisteredWaitHandle?[] _waits;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle[] events)
    {
        _mutex = mutex;
        _events = events;
        _waits = new RegisteredWaitHandle?[events.Length];
    }

    /// <summary>Returns null when another instance already owns this data directory in this session.</summary>
    public static SingleInstance? TryAcquire(string key)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\HolyScreen.{key}.Instance", out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var events = new EventWaitHandle[Commands.Length];
        for (int i = 0; i < Commands.Length; i++)
        {
            events[i] = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(key, Commands[i]));
        }

        return new SingleInstance(mutex, events);
    }

    /// <summary>Invokes <paramref name="handler"/> on a thread-pool thread whenever another instance signals.</summary>
    public void Listen(Action<InstanceCommand> handler)
    {
        for (int i = 0; i < _events.Length; i++)
        {
            InstanceCommand command = Commands[i];
            _waits[i] = ThreadPool.RegisterWaitForSingleObject(
                _events[i], (_, _) => handler(command), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public static bool Signal(string key, InstanceCommand command)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(key, command), out EventWaitHandle? handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    private static string EventName(string key, InstanceCommand command) => $@"Local\HolyScreen.{key}.{command}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (RegisteredWaitHandle? wait in _waits)
        {
            wait?.Unregister(null);
        }

        foreach (EventWaitHandle handle in _events)
        {
            handle.Dispose();
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; disposing still releases the handle.
        }

        _mutex.Dispose();
    }
}
