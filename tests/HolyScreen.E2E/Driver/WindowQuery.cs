using System;
using System.Collections.Generic;
using System.Linq;
using HolyScreen.Capture;
using HolyScreen.Interop;

namespace HolyScreen.E2E.Driver;

internal sealed record WindowRecord(IntPtr Handle, string ClassName, string Title, PixelRect Bounds, bool Visible,
    bool Cloaked, bool Topmost, uint Dpi);

/// <summary>Finds and describes top-level windows of a process.</summary>
internal static class WindowQuery
{
    public static List<WindowRecord> ForProcess(int processId)
    {
        var result = new List<WindowRecord>();
        Win32.EnumWindowsProc proc = (hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == processId) result.Add(Describe(hwnd));
            return true;
        };
        Win32.EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return result;
    }

    public static WindowRecord Describe(IntPtr hwnd)
    {
        Win32.GetWindowRect(hwnd, out Win32.RECT rect);
        bool cloaked = Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int value, sizeof(int)) == 0 && value != 0;
        long exStyle = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        return new WindowRecord(hwnd, Win32.GetClassName(hwnd), Win32.GetWindowText(hwnd),
            PixelRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom), Win32.IsWindowVisible(hwnd), cloaked,
            (exStyle & Win32.WS_EX_TOPMOST) != 0, Win32.GetDpiForWindow(hwnd));
    }

    /// <summary>Visible, uncloaked windows of the process whose title starts with <paramref name="titlePrefix"/>.</summary>
    public static List<WindowRecord> Showing(int processId, string titlePrefix) =>
        ForProcess(processId).Where(w => w.Visible && !w.Cloaked && w.Title.StartsWith(titlePrefix, StringComparison.Ordinal)).ToList();

    public static WindowRecord? Foreground()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        return hwnd == IntPtr.Zero ? null : Describe(hwnd);
    }

    public static int ForegroundProcessId()
    {
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint pid);
        return (int)pid;
    }
}
