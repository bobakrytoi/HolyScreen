using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HolyScreen.Core;
using HolyScreen.Interop;

namespace HolyScreen.Output;

/// <summary>Raw Win32 clipboard writes with a retry when another program briefly holds the clipboard.</summary>
internal static class ClipboardNative
{
    private const int Attempts = 10;

    /// <summary>Opens and empties the clipboard, runs <paramref name="write"/>, and closes it again.</summary>
    public static bool TryWrite(IntPtr owner, Func<bool> write, out string? error)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (Win32.OpenClipboard(owner))
            {
                try
                {
                    if (!Win32.EmptyClipboard())
                    {
                        error = "The clipboard could not be cleared.";
                        return false;
                    }

                    bool ok = write();
                    error = ok ? null : "Windows did not accept the clipboard data.";
                    return ok;
                }
                finally
                {
                    Win32.CloseClipboard();
                }
            }

            Thread.Sleep(15 + attempt * 10);
        }

        Log.Warn($"OpenClipboard kept failing (0x{Marshal.GetLastPInvokeError():X8})");
        error = "The clipboard is busy in another application. Try again.";
        return false;
    }

    /// <summary>Hands <paramref name="data"/> to the clipboard (must be open). Ownership passes to Windows on success.</summary>
    public static bool SetData(uint format, byte[] data)
    {
        IntPtr handle = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return false;

        IntPtr target = Win32.GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return false;
        }

        try
        {
            Marshal.Copy(data, 0, target, data.Length);
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }

        if (Win32.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return false;
        }

        return true;
    }
}

/// <summary>Puts plain Unicode text on the clipboard (OCR results, picked colours).</summary>
internal static class ClipboardText
{
    public static bool TrySet(string text, IntPtr owner, out string? error)
    {
        byte[] data = Encoding.Unicode.GetBytes(text + "\0");
        return ClipboardNative.TryWrite(owner, () => ClipboardNative.SetData(Win32.CF_UNICODETEXT, data), out error);
    }
}
