using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Core;

namespace HolyScreen.Output;

/// <summary>PNG/JPEG encoding with the pixel-format care that WIC's encoders need.</summary>
internal static class ImageEncoding
{
    public static string Extension(ImageFileFormat format) => format == ImageFileFormat.Jpeg ? ".jpg" : ".png";

    public static byte[] Encode(BitmapSource image, ImageFileFormat format, int jpegQuality = 90)
    {
        BitmapEncoder encoder;
        BitmapSource source;
        if (format == ImageFileFormat.Jpeg)
        {
            source = Flatten(image, Colors.White);
            encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
        }
        else
        {
            // Opaque images are written as 24-bit RGB (smaller files); anything with transparency keeps straight alpha.
            source = IsOpaque(image)
                ? Convert(image, PixelFormats.Bgr24)
                : Convert(image, PixelFormats.Bgra32);
            encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        }

        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Decode(byte[] data)
    {
        using var stream = new MemoryStream(data);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static bool IsOpaque(BitmapSource image)
    {
        PixelFormat format = image.Format;
        if (format == PixelFormats.Bgr24 || format == PixelFormats.Rgb24 || format == PixelFormats.Bgr32 ||
            format == PixelFormats.Gray8 || format == PixelFormats.Bgr565 || format == PixelFormats.Bgr555)
        {
            return true;
        }

        BitmapSource bgra = Convert(image, PixelFormats.Bgra32);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0xFF) return false;
        }

        return true;
    }

    public static BitmapSource Convert(BitmapSource image, PixelFormat format)
    {
        if (image.Format == format) return image;
        var converted = new FormatConvertedBitmap(image, format, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>Composites the image over a solid colour, producing an opaque 24-bit bitmap.</summary>
    public static BitmapSource Flatten(BitmapSource image, Color background)
    {
        if (IsOpaque(image)) return Convert(image, PixelFormats.Bgr24);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            dc.DrawRectangle(new SolidColorBrush(background), null, rect);
            dc.DrawImage(image, rect);
        }

        var target = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return Convert(target, PixelFormats.Bgr24);
    }
}
