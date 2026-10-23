using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HolyScreen.Interop;

namespace HolyScreen.Capture;

/// <summary>A visible top-level window at capture time, with its visible frame in virtual-screen pixels.</summary>
internal sealed record WindowInfo(IntPtr Handle, PixelRect Bounds, string ClassName, string Title);

/// <summary>
/// Takes a z-ordered snapshot of the visible top-level windows at the moment of capture, so the overlay can snap the
/// selection to the window under the cursor. Bounds come from DWM's extended frame bounds: <c>GetWindowRect</c>
/// includes the invisible resize border (≈7 px at 100 %) that would otherwise end up in the screenshot.
/// </summary>
internal static class WindowFinder
{
    public static IReadOnlyList<WindowInfo> Snapshot(PixelRect virtualScreen)
    {
        var list = new List<WindowInfo>();
        Win32.EnumWindowsProc proc = (hwnd, _) =>
        {
            try
            {
                if (TryDescribe(hwnd, virtualScreen, out WindowInfo? info)) list.Add(info!);
            }
            catch
            {
                // A window can disappear while we enumerate; skip it.
            }

            return true;
        };

        Win32.EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    /// <summary>The topmost window whose visible frame contains the point, or null.</summary>
    public static WindowInfo? HitTest(IReadOnlyList<WindowInfo> windows, int x, int y)
    {
        foreach (WindowInfo window in windows)
        {
            if (window.Bounds.Contains(x, y)) return window;
        }

        return null;
    }

    private static bool TryDescribe(IntPtr hwnd, PixelRect virtualScreen, out WindowInfo? info)
    {
        info = null;
        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) || IsCloaked(hwnd)) return false;

        long exStyle = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        bool clickThrough = (exStyle & Win32.WS_EX_TRANSPARENT) != 0 && (exStyle & Win32.WS_EX_LAYERED) != 0;
        if (clickThrough) return false; // game/recording overlays, our own toasts

        string className = Win32.GetClassName(hwnd);
        if (className is "Progman" or "WorkerW") return false; // the desktop: the overlay falls back to the monitor

        if (!TryGetVisibleBounds(hwnd, out PixelRect bounds)) return false;
        bounds = bounds.Intersect(virtualScreen);
        if (bounds.Width < 8 || bounds.Height < 8) return false;

        info = new WindowInfo(hwnd, bounds, className, Win32.GetWindowText(hwnd));
        return true;
    }

    public static bool TryGetVisibleBounds(IntPtr hwnd, out PixelRect bounds)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT frame,
                Marshal.SizeOf<Win32.RECT>()) == 0 && frame.Width > 0 && frame.Height > 0)
        {
            bounds = PixelRect.FromEdges(frame.Left, frame.Top, frame.Right, frame.Bottom);
            return true;
        }

        if (Win32.GetWindowRect(hwnd, out Win32.RECT rect) && rect.Width > 0 && rect.Height > 0)
        {
            bounds = PixelRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }

        bounds = PixelRect.Empty;
        return false;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
}
