using System;
using System.IO;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Output;

namespace HolyScreen.E2E.Driver;

/// <summary>Screen grabs for verification: pixels for assertions and PNG files for visual review.</summary>
internal static class Screen
{
    public static PixelRect VirtualScreen => DisplayLayout.GetVirtualScreenFromMetrics();

    public static CapturedFrame Grab() =>
        ScreenCapture.CaptureDesktop() ?? throw new E2EFailure("could not grab the screen");

    /// <summary>Saves the current screen (or part of it) as PNG under the artifacts folder and returns the path.</summary>
    public static string Snapshot(string folder, string name, PixelRect? region = null)
    {
        CapturedFrame frame = Grab();
        BitmapSource bitmap = frame.CreateBitmap(region ?? frame.Bounds);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".png");
        File.WriteAllBytes(path, ImageEncoding.Encode(bitmap, ImageFileFormat.Png));
        return path;
    }

    /// <summary>
    /// Share of sampled pixels that are identical between a frame and a bitmap of <paramref name="area"/>, optionally
    /// ignoring a <paramref name="margin"/> along the edges (where the overlay draws its handles).
    /// </summary>
    public static double Similarity(CapturedFrame a, BitmapSource b, PixelRect area, int samples = 4000, int margin = 0)
    {
        byte[] other = new byte[b.PixelWidth * b.PixelHeight * 4];
        BitmapSource bgra = ImageEncoding.Convert(b, System.Windows.Media.PixelFormats.Bgra32);
        bgra.CopyPixels(other, b.PixelWidth * 4, 0);

        var random = new Random(1234);
        int same = 0;
        for (int i = 0; i < samples; i++)
        {
            int x = margin + random.Next(Math.Max(1, area.Width - 2 * margin));
            int y = margin + random.Next(Math.Max(1, area.Height - 2 * margin));
            var expected = a.GetPixel(area.X + x, area.Y + y);
            int o = (y * b.PixelWidth + x) * 4;
            if (other[o] == expected.B && other[o + 1] == expected.G && other[o + 2] == expected.R) same++;
        }

        return same / (double)samples;
    }
}
