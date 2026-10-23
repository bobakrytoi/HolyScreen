using System;
using System.IO;
using System.Windows;
using HolyScreen.Core;
using HolyScreen.Interop;

namespace HolyScreen.UI.Common;

/// <summary>
/// Creates native HICONs from the embedded AppIcon.ico at exactly the size Windows asks for, so the tray icon stays
/// crisp at any DPI (the shell would otherwise blur a 16 px icon up to 20 or 24 px).
/// </summary>
internal static class IconLoader
{
    private const uint IconResourceVersion = 0x00030000;

    /// <summary>Returns an HICON (caller destroys it) for the tray at the system DPI, or IntPtr.Zero.</summary>
    public static IntPtr LoadTrayIcon()
    {
        int size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, Win32.GetDpiForSystem());
        if (size <= 0) size = 16;

        IntPtr icon = FromEmbeddedIco(size);
        if (icon != IntPtr.Zero) return icon;

        try
        {
            Win32.ExtractIconEx(AppPaths.ExecutablePath, 0, out IntPtr large, out IntPtr small, 1);
            if (small != IntPtr.Zero && large != IntPtr.Zero && small != large) Win32.DestroyIcon(large);
            return small != IntPtr.Zero ? small : large;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the tray icon from the executable", ex);
            return IntPtr.Zero;
        }
    }

    private static IntPtr FromEmbeddedIco(int size)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));
            if (info is null) return IntPtr.Zero;

            using Stream stream = info.Stream;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] ico = memory.ToArray();

            if (!TryPickEntry(ico, size, out int offset, out int length)) return IntPtr.Zero;
            byte[] image = new byte[length];
            Buffer.BlockCopy(ico, offset, image, 0, length);
            return Win32.CreateIconFromResourceEx(image, (uint)length, true, IconResourceVersion, size, size, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not create the tray icon from the embedded .ico", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>Picks the smallest entry that is at least <paramref name="size"/> (else the largest one).</summary>
    internal static bool TryPickEntry(byte[] ico, int size, out int offset, out int length)
    {
        offset = length = 0;
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1) return false;

        int count = BitConverter.ToUInt16(ico, 4);
        int bestSize = 0;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            if (entry + 16 > ico.Length) break;

            int width = ico[entry] == 0 ? 256 : ico[entry];
            int bytes = BitConverter.ToInt32(ico, entry + 8);
            int start = BitConverter.ToInt32(ico, entry + 12);
            if (bytes <= 0 || start < 0 || start + bytes > ico.Length) continue;

            bool better = !found
                || (width >= size && (bestSize < size || width < bestSize))
                || (width < size && bestSize < size && width > bestSize);
            if (better)
            {
                found = true;
                bestSize = width;
                offset = start;
                length = bytes;
            }
        }

        return found;
    }
}
