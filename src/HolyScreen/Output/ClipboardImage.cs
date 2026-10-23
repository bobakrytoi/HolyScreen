using System;
using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Core;
using HolyScreen.Interop;

namespace HolyScreen.Output;

/// <summary>
/// Puts an image on the clipboard in two forms: "PNG" (lossless, keeps transparency — what browsers, chat apps and
/// Office prefer) and CF_DIB (24-bit, flattened on white — understood by every Windows program; Windows synthesises
/// CF_BITMAP and CF_DIBV5 from it on demand).
/// </summary>
internal static class ClipboardImage
{
    private static readonly Lazy<uint> PngFormat = new(() => Win32.RegisterClipboardFormat("PNG"));

    public static uint PngFormatId => PngFormat.Value;

    public static bool TrySet(BitmapSource image, IntPtr owner, out string? error)
    {
        byte[] png;
        byte[] dib;
        try
        {
            png = ImageEncoding.Encode(image, ImageFileFormat.Png);
            dib = CreateDib(image);
        }
        catch (Exception ex)
        {
            Log.Error("Encoding the image for the clipboard failed", ex);
            error = "The image could not be prepared for the clipboard.";
            return false;
        }

        return ClipboardNative.TryWrite(owner, () =>
        {
            bool dibOk = ClipboardNative.SetData(Win32.CF_DIB, dib);
            bool pngOk = ClipboardNative.SetData(PngFormatId, png);
            if (!dibOk || !pngOk) Log.Warn($"Clipboard formats accepted: DIB={dibOk}, PNG={pngOk}");
            return dibOk || pngOk;
        }, out error);
    }

    /// <summary>A packed, bottom-up 24-bit DIB (BITMAPINFOHEADER + pixels), as CF_DIB expects.</summary>
    internal static byte[] CreateDib(BitmapSource image)
    {
        BitmapSource bgr = ImageEncoding.Flatten(image, Colors.White); // Bgr24
        int width = bgr.PixelWidth;
        int height = bgr.PixelHeight;
        int sourceStride = width * 3;
        int stride = (sourceStride + 3) & ~3; // DIB rows are 4-byte aligned

        byte[] pixels = new byte[sourceStride * height];
        bgr.CopyPixels(pixels, sourceStride, 0);

        const int HeaderSize = 40;
        byte[] dib = new byte[HeaderSize + stride * height];
        Span<byte> header = dib.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[0..], HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height); // positive = bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 0); // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 3780); // 96 DPI in pixels per metre
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], 3780);

        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(pixels, row * sourceStride, dib, HeaderSize + (height - 1 - row) * stride, sourceStride);
        }

        return dib;
    }
}
