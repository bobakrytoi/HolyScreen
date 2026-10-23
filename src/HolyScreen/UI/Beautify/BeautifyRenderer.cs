using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace HolyScreen.UI.Beautify;

internal sealed record BeautifyBackground(string Name, Color? From, Color? To);

internal sealed record BeautifyOptions(int Background, int Padding, int Radius, bool Shadow);

/// <summary>
/// Presents a screenshot the way people share it: on a gradient backdrop, with rounded corners and a soft drop
/// shadow (CleanShot / Xnapper style). The screenshot itself is drawn 1:1, never resampled.
/// </summary>
internal static class BeautifyRenderer
{
    public static readonly BeautifyBackground[] Backgrounds =
    {
        new("Sunset", Rgb(0xFF7E5F), Rgb(0xFEB47B)),
        new("Ocean", Rgb(0x2E3192), Rgb(0x1BFFFF)),
        new("Violet", Rgb(0x8E2DE2), Rgb(0x4A00E0)),
        new("Mint", Rgb(0x11998E), Rgb(0x38EF7D)),
        new("Peach", Rgb(0xFFDDE1), Rgb(0xEE9CA7)),
        new("Sky", Rgb(0x56CCF2), Rgb(0x2F80ED)),
        new("Graphite", Rgb(0x232526), Rgb(0x414345)),
        new("Paper", Rgb(0xFFFFFF), Rgb(0xEEF1F5)),
        new("Transparent", null, null),
    };

    public static BitmapSource Render(BitmapSource image, BeautifyOptions options)
    {
        BeautifyBackground background = Backgrounds[Math.Clamp(options.Background, 0, Backgrounds.Length - 1)];
        int pad = Math.Max(0, options.Padding);
        int width = image.PixelWidth + 2 * pad;
        int height = image.PixelHeight + 2 * pad;
        double radius = Math.Min(options.Radius, Math.Min(image.PixelWidth, image.PixelHeight) / 2.0);
        var frame = new Rect(pad, pad, image.PixelWidth, image.PixelHeight);

        var root = new ContainerVisual();

        var backdrop = new DrawingVisual();
        using (DrawingContext dc = backdrop.RenderOpen())
        {
            if (background.From is Color from && background.To is Color to)
            {
                dc.DrawRectangle(Gradient(from, to), null, new Rect(0, 0, width, height));
            }
        }

        root.Children.Add(backdrop);

        if (options.Shadow && pad > 0)
        {
            double blur = Math.Clamp(pad * 0.45, 8, 60);
            var shadow = new DrawingVisual { Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian } };
            using (DrawingContext dc = shadow.RenderOpen())
            {
                var brush = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
                brush.Freeze();
                dc.DrawRoundedRectangle(brush, null, new Rect(frame.X, frame.Y + Math.Max(3, pad * 0.12), frame.Width, frame.Height), radius, radius);
            }

            root.Children.Add(shadow);
        }

        var picture = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        using (DrawingContext dc = picture.RenderOpen())
        {
            if (radius > 0) dc.PushClip(new RectangleGeometry(frame, radius, radius));
            dc.DrawImage(image, frame);
            if (radius > 0) dc.Pop();
        }

        root.Children.Add(picture);

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        target.Freeze();
        return target;
    }

    public static Brush Swatch(BeautifyBackground background)
    {
        if (background.From is Color from && background.To is Color to) return Gradient(from, to);
        return CheckerBrush(6);
    }

    /// <summary>The usual grey checkerboard that stands for "transparent".</summary>
    public static Brush CheckerBrush(double cell)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), null, new RectangleGeometry(new Rect(0, 0, 2 * cell, 2 * cell))));
        var dark = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, cell, cell))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(cell, cell, cell, cell))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 2 * cell, 2 * cell),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
