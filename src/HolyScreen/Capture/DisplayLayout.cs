using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HolyScreen.Interop;

namespace HolyScreen.Capture;

/// <summary>One display, in physical pixels, together with its effective DPI.</summary>
internal sealed record MonitorInfo(IntPtr Handle, string DeviceName, PixelRect Bounds, PixelRect WorkArea, uint Dpi, bool IsPrimary)
{
    /// <summary>Device pixels per WPF device-independent pixel on this monitor (1.0 at 96 DPI, 1.5 at 144 DPI).</summary>
    public double Scale => Dpi / 96.0;
}

/// <summary>Enumerates monitors and answers "which monitor is this point on" in virtual-screen pixels.</summary>
internal static class DisplayLayout
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Win32.MonitorEnumProc proc = (IntPtr monitor, IntPtr hdc, ref Win32.RECT rect, IntPtr data) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>(), szDevice = string.Empty };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                uint dpi = 96;
                if (Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    dpi = dpiX;
                }

                list.Add(new MonitorInfo(monitor, info.szDevice ?? string.Empty, ToPixelRect(info.rcMonitor),
                    ToPixelRect(info.rcWork), dpi, (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        if (list.Count == 0)
        {
            // Should never happen on a desktop session; fall back to the virtual-screen metrics.
            PixelRect virtualScreen = GetVirtualScreenFromMetrics();
            list.Add(new MonitorInfo(IntPtr.Zero, string.Empty, virtualScreen, virtualScreen, 96, true));
        }

        list.Sort((a, b) => a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
        return list;
    }

    /// <summary>The bounding rectangle of all monitors.</summary>
    public static PixelRect GetVirtualScreen(IReadOnlyList<MonitorInfo> monitors)
    {
        PixelRect union = PixelRect.Empty;
        foreach (MonitorInfo monitor in monitors) union = union.Union(monitor.Bounds);
        return union;
    }

    public static PixelRect GetVirtualScreenFromMetrics() => new(
        Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN), Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN), Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));

    /// <summary>The monitor containing the point, or the nearest one when the point lies in a gap between monitors.</summary>
    public static MonitorInfo FromPoint(IReadOnlyList<MonitorInfo> monitors, int x, int y)
    {
        MonitorInfo? best = null;
        long bestDistance = long.MaxValue;
        foreach (MonitorInfo monitor in monitors)
        {
            if (monitor.Bounds.Contains(x, y)) return monitor;
            long dx = x < monitor.Bounds.X ? monitor.Bounds.X - x : x >= monitor.Bounds.Right ? x - monitor.Bounds.Right + 1 : 0;
            long dy = y < monitor.Bounds.Y ? monitor.Bounds.Y - y : y >= monitor.Bounds.Bottom ? y - monitor.Bounds.Bottom + 1 : 0;
            long distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = monitor;
            }
        }

        return best ?? monitors[0];
    }

    public static Win32.POINT GetCursorPosition()
    {
        Win32.GetCursorPos(out Win32.POINT point);
        return point;
    }

    private static PixelRect ToPixelRect(Win32.RECT rect) => PixelRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
