using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HolyScreen.Capture;

/// <summary>
/// The frozen desktop: the pixels of every monitor at the moment the hotkey was pressed (opaque BGRA, top-down,
/// covering the virtual-screen rectangle) plus the monitor and window layout of that same moment.
/// </summary>
internal sealed class CapturedFrame
{
    public CapturedFrame(PixelRect bounds, byte[] pixels, IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<WindowInfo> windows, DateTime capturedAt)
    {
        if (pixels.Length != (long)bounds.Width * bounds.Height * 4)
        {
            throw new ArgumentException("Pixel buffer size does not match the frame bounds.", nameof(pixels));
        }

        Bounds = bounds;
        Pixels = pixels;
        Monitors = monitors;
        Windows = windows;
        CapturedAt = capturedAt;
    }

    /// <summary>Virtual-screen rectangle covered by <see cref="Pixels"/>.</summary>
    public PixelRect Bounds { get; }

    /// <summary>BGRA pixels, top-down, alpha always 255, stride = <see cref="Stride"/>.</summary>
    public byte[] Pixels { get; }

    public int Stride => Bounds.Width * 4;

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Visible top-level windows, topmost first.</summary>
    public IReadOnlyList<WindowInfo> Windows { get; }

    public DateTime CapturedAt { get; }

    /// <summary>Copies <paramref name="region"/> (virtual-screen pixels, clipped to the frame) into a compact buffer.</summary>
    public byte[] CopyPixels(PixelRect region, out PixelRect actual)
    {
        actual = region.Intersect(Bounds);
        if (actual.IsEmpty) return Array.Empty<byte>();

        int rowBytes = actual.Width * 4;
        byte[] buffer = GC.AllocateUninitializedArray<byte>(rowBytes * actual.Height);
        int sourceOffset = (actual.Y - Bounds.Y) * Stride + (actual.X - Bounds.X) * 4;
        for (int row = 0; row < actual.Height; row++)
        {
            Buffer.BlockCopy(Pixels, sourceOffset + row * Stride, buffer, row * rowBytes, rowBytes);
        }

        return buffer;
    }

    /// <summary>
    /// A frozen bitmap of <paramref name="region"/>. <paramref name="dpi"/> only sets the bitmap's metadata (its natural
    /// size in WPF units); the pixels are always copied 1:1.
    /// </summary>
    public BitmapSource CreateBitmap(PixelRect region, double dpi = 96)
    {
        byte[] buffer = CopyPixels(region, out PixelRect actual);
        if (actual.IsEmpty)
        {
            throw new ArgumentException($"Region {region} lies outside the captured frame {Bounds}.", nameof(region));
        }

        BitmapSource bitmap = BitmapSource.Create(actual.Width, actual.Height, dpi, dpi, PixelFormats.Bgra32, null,
            buffer, actual.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Writes <paramref name="image"/> into the frame at <paramref name="topLeft"/> (used to re-open a capture from
    /// history: its pixels are placed back where they were, over the current desktop).
    /// </summary>
    public void Paste(BitmapSource image, int left, int top)
    {
        BitmapSource bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        byte[] source = new byte[width * height * 4];
        bgra.CopyPixels(source, width * 4, 0);

        PixelRect target = new PixelRect(left, top, width, height).Intersect(Bounds);
        for (int y = target.Y; y < target.Bottom; y++)
        {
            int sourceOffset = ((y - top) * width + (target.X - left)) * 4;
            int destinationOffset = (y - Bounds.Y) * Stride + (target.X - Bounds.X) * 4;
            Buffer.BlockCopy(source, sourceOffset, Pixels, destinationOffset, target.Width * 4);
        }

        for (int y = target.Y; y < target.Bottom; y++)
        {
            int offset = (y - Bounds.Y) * Stride + (target.X - Bounds.X) * 4;
            for (int x = 0; x < target.Width; x++) Pixels[offset + x * 4 + 3] = 0xFF;
        }
    }

    /// <summary>The colour at a virtual-screen pixel (clamped to the frame).</summary>
    public Color GetPixel(int x, int y)
    {
        x = Math.Clamp(x, Bounds.X, Bounds.Right - 1);
        y = Math.Clamp(y, Bounds.Y, Bounds.Bottom - 1);
        int offset = (y - Bounds.Y) * Stride + (x - Bounds.X) * 4;
        return Color.FromRgb(Pixels[offset + 2], Pixels[offset + 1], Pixels[offset]);
    }
}
