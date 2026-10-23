using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HolyScreen.Core;
using HolyScreen.Interop;

namespace HolyScreen.Capture;

/// <summary>
/// Grabs screen pixels with GDI <c>BitBlt</c> from the desktop DC into a 32-bit top-down DIB. Because the process is
/// per-monitor-v2 aware, the copy is 1:1 in physical pixels on every monitor regardless of its scaling.
/// </summary>
internal static class ScreenCapture
{
    /// <summary>Freezes the whole desktop: every monitor's pixels plus the window layout at this instant.</summary>
    public static CapturedFrame? CaptureDesktop()
    {
        var timer = Stopwatch.StartNew();
        IReadOnlyList<MonitorInfo> monitors = DisplayLayout.GetMonitors();
        PixelRect virtualScreen = DisplayLayout.GetVirtualScreen(monitors);

        // Snapshot windows first: BitBlt is the slow part and the layout should match the pixels we show.
        IReadOnlyList<WindowInfo> windows = WindowFinder.Snapshot(virtualScreen);
        byte[]? pixels = CapturePixels(virtualScreen);
        if (pixels is null) return null;

        Log.Info($"Captured {virtualScreen} on {monitors.Count} monitor(s), {windows.Count} windows, in {timer.ElapsedMilliseconds} ms");
        return new CapturedFrame(virtualScreen, pixels, monitors, windows, DateTime.Now);
    }

    /// <summary>Copies <paramref name="area"/> (virtual-screen pixels) into a BGRA buffer, or null on failure.</summary>
    public static byte[]? CapturePixels(PixelRect area)
    {
        if (area.IsEmpty) return null;

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            var header = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = area.Width,
                biHeight = -area.Height, // negative = top-down, matching WPF's row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB,
            };

            dib = Win32.CreateDIBSection(memDc, ref header, Win32.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Log.Warn($"CreateDIBSection failed for {area} (0x{Marshal.GetLastPInvokeError():X8})");
                return null;
            }

            previous = Win32.SelectObject(memDc, dib);
            if (!Win32.BitBlt(memDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Win32.SRCCOPY | Win32.CAPTUREBLT))
            {
                Log.Warn($"BitBlt failed for {area} (0x{Marshal.GetLastPInvokeError():X8})");
                return null;
            }

            Win32.GdiFlush(); // GDI may batch; make sure the copy landed before we read the bits

            byte[] buffer = GC.AllocateUninitializedArray<byte>(checked(area.Width * 4 * area.Height));
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            ForceOpaque(buffer);
            return buffer;
        }
        catch (Exception ex)
        {
            Log.Error("Screen capture failed", ex);
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) Win32.SelectObject(memDc, previous);
            if (dib != IntPtr.Zero) Win32.DeleteObject(dib);
            if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>GDI leaves the fourth byte undefined (usually 0). Make it 255 so the buffer is valid opaque BGRA.</summary>
    private static unsafe void ForceOpaque(byte[] buffer)
    {
        fixed (byte* start = buffer)
        {
            uint* p = (uint*)start;
            uint* end = p + buffer.Length / 4;
            for (; p < end; p++) *p |= 0xFF000000u;
        }
    }
}
