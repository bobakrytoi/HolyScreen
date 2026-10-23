using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using HolyScreen.Capture;
using HolyScreen.Core;

namespace HolyScreen.UI.Overlay;

/// <summary>
/// Keeps one overlay window per monitor alive between captures. Creating a WPF window (HWND, render target,
/// visual tree) costs ~40 ms per monitor; reusing it makes the overlay appear in roughly the time of the screen
/// grab itself. Windows for monitors that disappeared or changed are closed.
/// </summary>
internal sealed class OverlayWindowPool : IDisposable
{
    private readonly List<OverlayWindow> _windows = new();

    /// <summary>A window for <paramref name="monitor"/>, created on first use.</summary>
    public OverlayWindow Get(MonitorInfo monitor)
    {
        OverlayWindow? window = _windows.FirstOrDefault(w => w.Fits(monitor));
        if (window is not null) return window;

        window = new OverlayWindow(monitor);
        new WindowInteropHelper(window).EnsureHandle();
        _windows.Add(window);
        return window;
    }

    /// <summary>Closes windows whose monitor is not in <paramref name="monitors"/> any more.</summary>
    public void Retain(IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (OverlayWindow stale in _windows.Where(w => !monitors.Any(w.Fits)).ToList())
        {
            _windows.Remove(stale);
            Close(stale);
        }
    }

    public void Clear()
    {
        foreach (OverlayWindow window in _windows) Close(window);
        _windows.Clear();
    }

    private static void Close(OverlayWindow window)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing a pooled overlay window failed", ex);
        }
    }

    public void Dispose() => Clear();
}
