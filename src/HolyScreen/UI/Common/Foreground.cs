using System;
using HolyScreen.Interop;

namespace HolyScreen.UI.Common;

/// <summary>Foreground (keyboard focus) hand-over between the overlay and the app the user was working in.</summary>
internal static class Foreground
{
    /// <summary>
    /// Brings <paramref name="hwnd"/> to the foreground. Right after a hotkey or tray click Windows allows that
    /// directly; after a delayed capture the foreground lock may refuse it, in which case we briefly attach to the
    /// foreground thread's input queue (the documented way to share the right to set focus).
    /// </summary>
    public static bool Force(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (Win32.SetForegroundWindow(hwnd) && Win32.GetForegroundWindow() == hwnd) return true;

        IntPtr current = Win32.GetForegroundWindow();
        uint foregroundThread = Win32.GetWindowThreadProcessId(current, out _);
        uint ourThread = Win32.GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != ourThread && Win32.AttachThreadInput(ourThread, foregroundThread, true))
        {
            try
            {
                Win32.BringWindowToTop(hwnd);
                Win32.SetForegroundWindow(hwnd);
            }
            finally
            {
                Win32.AttachThreadInput(ourThread, foregroundThread, false);
            }
        }

        return Win32.GetForegroundWindow() == hwnd;
    }

    /// <summary>Gives focus back to the window that was active before the capture, so Ctrl+V works right away.</summary>
    public static void Restore(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && Win32.IsWindow(hwnd))
        {
            Win32.SetForegroundWindow(hwnd);
        }
    }
}
