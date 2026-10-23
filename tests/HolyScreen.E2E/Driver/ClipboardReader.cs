using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using HolyScreen.Interop;
using HolyScreen.Output;

namespace HolyScreen.E2E.Driver;

/// <summary>Reads back what the app put on the clipboard.</summary>
internal static class ClipboardReader
{
    public static uint Sequence => Win32.GetClipboardSequenceNumber();

    /// <summary>The clipboard's Unicode text, or null when it holds none.</summary>
    public static string? ReadText()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Win32.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    IntPtr handle = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
                    if (handle == IntPtr.Zero) return null;
                    IntPtr pointer = Win32.GlobalLock(handle);
                    try
                    {
                        return Marshal.PtrToStringUni(pointer);
                    }
                    finally
                    {
                        Win32.GlobalUnlock(handle);
                    }
                }
                finally
                {
                    Win32.CloseClipboard();
                }
            }

            Thread.Sleep(25);
        }

        return null;
    }

    public static bool HasDib => Win32.IsClipboardFormatAvailable(Win32.CF_DIB);

    public static bool HasPng => Win32.IsClipboardFormatAvailable(ClipboardImage.PngFormatId);

    public static BitmapSource? ReadPng() => Read(ClipboardImage.PngFormatId, data => ImageEncoding.Decode(data));

    /// <summary>Width, height and bit depth from the CF_DIB header.</summary>
    public static (int Width, int Height, int Bits)? ReadDibHeader() =>
        Read(Win32.CF_DIB, data => ((int Width, int Height, int Bits)?)(BitConverter.ToInt32(data, 4), Math.Abs(BitConverter.ToInt32(data, 8)), BitConverter.ToInt16(data, 14)));

    private static T? Read<T>(uint format, Func<byte[], T> parse)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (Win32.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    IntPtr handle = Win32.GetClipboardData(format);
                    if (handle == IntPtr.Zero) return default;
                    IntPtr pointer = Win32.GlobalLock(handle);
                    try
                    {
                        byte[] data = new byte[(int)Win32.GlobalSize(handle)];
                        Marshal.Copy(pointer, data, 0, data.Length);
                        return parse(data);
                    }
                    finally
                    {
                        Win32.GlobalUnlock(handle);
                    }
                }
                finally
                {
                    Win32.CloseClipboard();
                }
            }

            Thread.Sleep(25);
        }

        return default;
    }
}
