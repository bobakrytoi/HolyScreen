using System;
using HolyScreen.Core;
using HolyScreen.Interop;

namespace HolyScreen.UI.Common;

/// <summary>DWM-based window polish (dark title bar, rounded corners, Mica, cloaking) applied best-effort per OS support.</summary>
internal static class WindowEffects
{
    public static void UseDarkTitleBar(IntPtr hwnd, bool dark = true)
    {
        int value = dark ? 1 : 0;
        if (Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
        {
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
        }
    }

    public static void SetCornerPreference(IntPtr hwnd, int preference)
    {
        if (!SystemInfo.IsWindows11) return;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    public static void UseRoundedCorners(IntPtr hwnd) => SetCornerPreference(hwnd, Win32.DWMWCP_ROUND);

    /// <summary>Removes the 1 px accent border Windows 11 draws around top-level windows.</summary>
    public static void HideBorder(IntPtr hwnd)
    {
        if (!SystemInfo.IsWindows11) return;
        int none = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    /// <summary>Enables the Mica backdrop on Windows 11 22H2+. Returns true if applied.</summary>
    public static bool TryEnableMica(IntPtr hwnd)
    {
        if (!SystemInfo.SupportsSystemBackdrop) return false;

        var margins = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int backdrop = Win32.DWMSBT_MAINWINDOW;
        return Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
    }

    /// <summary>
    /// Hides a window from the screen while DWM keeps composing it. Used to show a window, let WPF render its first
    /// frame, and only then reveal it — so a freshly created window never flashes an empty background.
    /// </summary>
    public static bool SetCloaked(IntPtr hwnd, bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        return Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_CLOAK, ref value, sizeof(int)) == 0;
    }

    /// <summary>Marks a window so that showing it does not steal keyboard focus (used by toasts).</summary>
    public static void MakeNonActivating(IntPtr hwnd)
    {
        Win32.AddExtendedStyle(hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
    }
}
