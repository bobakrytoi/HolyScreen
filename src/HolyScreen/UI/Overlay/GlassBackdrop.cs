using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace HolyScreen.UI.Overlay;

/// <summary>
/// The frosted-glass source for one monitor: its frozen pixels shrunk to a quarter and Gaussian-blurred once. Panels
/// show the part of it that lies under them (an <see cref="ImageBrush"/> viewbox), which is a genuine blur of the
/// real content behind the glass — and costs nothing per frame.
/// </summary>
internal static class GlassBackdrop
{
    /// <summary>Backdrop pixels per monitor pixel.</summary>
    public const double Downscale = 0.25;

    public static BitmapSource Create(BitmapSource monitor, double radius = 9)
    {
        int width = Math.Max(1, (int)Math.Round(monitor.PixelWidth * Downscale));
        int height = Math.Max(1, (int)Math.Round(monitor.PixelHeight * Downscale));

        // Pad by the blur radius with the image stretched over the padding, so the blur does not fade to
        // transparent (dark) at the monitor edges.
        int pad = (int)Math.Ceiling(radius * 1.5);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(monitor, new Rect(-pad, -pad, width + 2 * pad, height + 2 * pad));
            dc.DrawImage(monitor, new Rect(0, 0, width, height));
        }

        visual.Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
