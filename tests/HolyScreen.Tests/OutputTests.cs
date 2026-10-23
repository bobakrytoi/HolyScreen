using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Core;
using HolyScreen.Output;
using Xunit;

namespace HolyScreen.Tests;

public class FileNameTemplateTests
{
    private static readonly DateTime Time = new(2026, 10, 23, 14, 5, 1);

    [Fact]
    public void Default_MatchesSpec()
    {
        Assert.Equal("Screenshot_2026-10-23_14-05-01", FileNameTemplate.Expand(FileNameTemplate.Default, Time));
    }

    [Theory]
    [InlineData("", "Screenshot_2026-10-23_14-05-01")]
    [InlineData("   ", "Screenshot_2026-10-23_14-05-01")]
    [InlineData("shot {d}", "shot 23")]                // single letter = custom format, not short date
    [InlineData("a/b:c*{HH}", "a_b_c_14")]             // forbidden characters are replaced
    [InlineData("shot{yyyy", "shot{yyyy")]             // unclosed brace stays literal
    [InlineData("CON", "_CON")]                        // reserved device name
    [InlineData("name...", "name")]                    // trailing dots are not allowed on Windows
    public void Expand_ProducesSafeNames(string template, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.Expand(template, Time));
    }

    [Fact]
    public void Expand_LimitsLength()
    {
        string name = FileNameTemplate.Expand(new string('x', 400), Time);
        Assert.True(name.Length <= 150);
    }
}

public class ScreenshotSaverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HolyScreenTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WriteNew_NeverOverwrites_AndCountsUp()
    {
        string first = ScreenshotSaver.WriteNew(_dir, "Screenshot_x", ".png", new byte[] { 1 });
        string second = ScreenshotSaver.WriteNew(_dir, "Screenshot_x", ".png", new byte[] { 2 });
        string third = ScreenshotSaver.WriteNew(_dir, "Screenshot_x", ".png", new byte[] { 3 });

        Assert.Equal("Screenshot_x.png", Path.GetFileName(first));
        Assert.Equal("Screenshot_x_2.png", Path.GetFileName(second));
        Assert.Equal("Screenshot_x_3.png", Path.GetFileName(third));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
    }

    [Fact]
    public void Save_WritesDecodablePngToConfiguredFolder()
    {
        Sta.Run(() =>
        {
            var settings = new AppSettings { SaveFolder = _dir };
            string path = ScreenshotSaver.Save(TestImages.Opaque(5, 4), settings, new DateTime(2026, 10, 23, 14, 5, 1));

            Assert.Equal(Path.Combine(_dir, "Screenshot_2026-10-23_14-05-01.png"), path);
            BitmapSource decoded = ImageEncoding.Decode(File.ReadAllBytes(path));
            Assert.Equal(5, decoded.PixelWidth);
            Assert.Equal(4, decoded.PixelHeight);
        });
    }

    [Fact]
    public void Save_UsesJpegExtensionWhenConfigured()
    {
        Sta.Run(() =>
        {
            var settings = new AppSettings { SaveFolder = _dir, SaveFormat = ImageFileFormat.Jpeg };
            string path = ScreenshotSaver.Save(TestImages.Opaque(8, 8), settings, DateTime.Now);
            Assert.EndsWith(".jpg", path);
            byte[] data = File.ReadAllBytes(path);
            Assert.Equal(0xFF, data[0]); // JPEG SOI marker
            Assert.Equal(0xD8, data[1]);
        });
    }
}

public class ImageEncodingTests
{
    [Fact]
    public void Png_OpaqueImage_IsLosslessAnd24Bit()
    {
        Sta.Run(() =>
        {
            BitmapSource source = TestImages.Opaque(7, 3);
            BitmapSource decoded = ImageEncoding.Decode(ImageEncoding.Encode(source, ImageFileFormat.Png));

            Assert.Equal(PixelFormats.Bgr24, decoded.Format);
            Assert.Equal(TestImages.ReadBgra(source), TestImages.ReadBgra(decoded));
        });
    }

    [Fact]
    public void Png_TransparentImage_KeepsAlpha()
    {
        Sta.Run(() =>
        {
            BitmapSource source = TestImages.WithAlpha(4, 4, alpha: 0x40);
            BitmapSource decoded = ImageEncoding.Decode(ImageEncoding.Encode(source, ImageFileFormat.Png));

            Assert.False(ImageEncoding.IsOpaque(decoded));
            byte[] pixels = TestImages.ReadBgra(decoded);
            Assert.Equal(0x40, pixels[3]);
        });
    }

    [Fact]
    public void Jpeg_FlattensTransparencyOntoWhite()
    {
        Sta.Run(() =>
        {
            BitmapSource source = TestImages.WithAlpha(16, 16, alpha: 0);
            BitmapSource decoded = ImageEncoding.Decode(ImageEncoding.Encode(source, ImageFileFormat.Jpeg, 95));
            byte[] pixels = TestImages.ReadBgra(decoded);
            Assert.True(pixels[0] > 240 && pixels[1] > 240 && pixels[2] > 240, "fully transparent pixels should become white");
        });
    }
}

/// <summary>Small synthetic bitmaps with known pixels.</summary>
internal static class TestImages
{
    public static BitmapSource Opaque(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4 + 0] = (byte)(i * 37);
            pixels[i * 4 + 1] = (byte)(i * 11 + 5);
            pixels[i * 4 + 2] = (byte)(255 - i * 23);
            pixels[i * 4 + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource WithAlpha(int width, int height, byte alpha)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4 + 0] = 10;
            pixels[i * 4 + 1] = 20;
            pixels[i * 4 + 2] = 30;
            pixels[i * 4 + 3] = alpha;
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] ReadBgra(BitmapSource source)
    {
        BitmapSource bgra = ImageEncoding.Convert(source, PixelFormats.Bgra32);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }
}
