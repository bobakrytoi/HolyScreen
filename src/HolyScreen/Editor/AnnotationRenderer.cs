using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;

namespace HolyScreen.Editor;

/// <summary>
/// Draws annotations into any <see cref="DrawingContext"/> in virtual-screen pixel coordinates. The overlay and the
/// exported image use this same code with different transforms, so the saved picture is exactly what was on screen.
/// The drawing visual that hosts it must use nearest-neighbour bitmap scaling (pixelation blocks stay sharp).
/// </summary>
internal static class AnnotationRenderer
{
    public static readonly Typeface TextTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface StepTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush Shadow = Frozen(Color.FromArgb(0x48, 0, 0, 0));
    private static readonly Brush RedactFill = Frozen(Color.FromRgb(0x11, 0x13, 0x16));
    private static readonly ConditionalWeakTable<PixelateAnnotation, PixelationCache> Pixelations = new();

    public static double ArrowHeadLength(double thickness) => Math.Max(thickness * 4.2, 11);
    public static double ArrowHeadWidth(double thickness) => Math.Max(thickness * 3.6, 10);
    public static double MarkerWidth(double thickness) => Math.Max(thickness * 3.5, 12);
    public static double StepRadius(double thickness) => 8 + thickness * 1.7;

    public static void Render(DrawingContext dc, Annotation annotation, CapturedFrame? frame)
    {
        Brush brush = Frozen(ToColor(annotation.Color));
        double t = annotation.Thickness;
        double shadowOffset = Math.Max(1, t * 0.35);

        switch (annotation)
        {
            case ArrowAnnotation arrow:
            {
                Geometry geometry = ArrowGeometry(arrow.Start, arrow.End, t);
                var outline = new Pen(brush, Math.Max(0.8, t * 0.3)) { LineJoin = PenLineJoin.Round };
                outline.Freeze();
                WithShadow(dc, shadowOffset, () => dc.DrawGeometry(Shadow, new Pen(Shadow, outline.Thickness) { LineJoin = PenLineJoin.Round }, geometry));
                dc.DrawGeometry(brush, outline, geometry);
                break;
            }

            case LineAnnotation line:
                WithShadow(dc, shadowOffset, () => dc.DrawLine(RoundPen(Shadow, t), line.Start, line.End));
                dc.DrawLine(RoundPen(brush, t), line.Start, line.End);
                break;

            case RectangleAnnotation rectangle:
                WithShadow(dc, shadowOffset, () => dc.DrawRectangle(null, RoundPen(Shadow, t), rectangle.Box));
                dc.DrawRectangle(null, RoundPen(brush, t), rectangle.Box);
                break;

            case EllipseAnnotation ellipse:
            {
                Rect box = ellipse.Box;
                var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
                WithShadow(dc, shadowOffset, () => dc.DrawEllipse(null, RoundPen(Shadow, t), center, box.Width / 2, box.Height / 2));
                dc.DrawEllipse(null, RoundPen(brush, t), center, box.Width / 2, box.Height / 2);
                break;
            }

            case PencilAnnotation pencil:
                DrawStroke(dc, pencil.Points, RoundPen(brush, t), shadow: RoundPen(Shadow, t), shadowOffset);
                break;

            case MarkerAnnotation marker:
            {
                Color c = ToColor(marker.Color);
                Brush ink = Frozen(Color.FromArgb(0x70, c.R, c.G, c.B));
                var pen = new Pen(ink, MarkerWidth(t)) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Round };
                pen.Freeze();
                DrawStroke(dc, marker.Points, pen, shadow: null, 0);
                break;
            }

            case TextAnnotation text when text.Text.Length > 0:
            {
                FormattedText shadow = FormatText(text, Shadow);
                dc.DrawText(shadow, text.Origin + new Vector(0, Math.Max(1, text.FontSize * 0.05)));
                dc.DrawText(FormatText(text, brush), text.Origin);
                break;
            }

            case StepAnnotation step:
            {
                double r = StepRadius(t);
                dc.DrawEllipse(Shadow, null, step.Center + new Vector(0, Math.Max(1, r * 0.1)), r, r);
                dc.DrawEllipse(brush, RoundPen(Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), Math.Max(1.2, r * 0.11)), step.Center, r, r);
                var number = new FormattedText(step.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, StepTypeface, r * 1.1, Brushes.White, 1.0);
                double inkWidth = number.Width - number.OverhangLeading - number.OverhangTrailing;
                double inkCenterY = number.Height + number.OverhangAfter - number.Extent / 2;
                dc.DrawText(number, new Point(step.Center.X - number.OverhangLeading - inkWidth / 2, step.Center.Y - inkCenterY));
                break;
            }

            case PixelateAnnotation pixelate when frame is not null:
            {
                PixelRect area = ToPixelRect(pixelate.Box);
                if (area.IsEmpty) break;
                int block = Math.Max(2, pixelate.BlockSize);
                // Drawn 1:1 (blocks already expanded): no bitmap scaling is involved, so no renderer can smear them.
                dc.DrawImage(PixelatedBlocks(pixelate, frame, area, block), new Rect(area.X, area.Y, area.Width, area.Height));
                break;
            }

            case RedactAnnotation redact:
            {
                PixelRect area = ToPixelRect(redact.Box);
                if (!area.IsEmpty) dc.DrawRectangle(RedactFill, null, new Rect(area.X, area.Y, area.Width, area.Height));
                break;
            }
        }
    }

    public static Geometry ArrowGeometry(Point start, Point end, double thickness)
    {
        Vector v = end - start;
        double length = v.Length;
        if (length < 0.5) return Geometry.Empty;

        Vector d = v / length;
        var n = new Vector(-d.Y, d.X);
        double headLength = ArrowHeadLength(thickness);
        double headWidth = ArrowHeadWidth(thickness);
        if (headLength > length * 0.6)
        {
            double k = length * 0.6 / headLength;
            headLength *= k;
            headWidth *= k;
        }

        // A tapered shaft (thin at the tail, full width at the head) feels lighter than a uniform one.
        double tail = thickness * 0.28;
        double neck = thickness * 0.55;
        Point b = end - d * headLength;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext c = geometry.Open())
        {
            c.BeginFigure(start + n * tail, isFilled: true, isClosed: true);
            c.PolyLineTo(new[]
            {
                b + n * neck, b + n * headWidth / 2, end, b - n * headWidth / 2, b - n * neck, start - n * tail,
            }, isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>A smooth path through the points (quadratic curves via the segment mid-points).</summary>
    public static Geometry SmoothPath(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext c = geometry.Open())
        {
            c.BeginFigure(points[0], isFilled: false, isClosed: false);
            if (points.Count == 2)
            {
                c.LineTo(points[1], true, true);
            }
            else
            {
                for (int i = 1; i < points.Count - 1; i++)
                {
                    var mid = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                    c.QuadraticBezierTo(points[i], mid, true, true);
                }

                c.LineTo(points[^1], true, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    public static FormattedText FormatText(TextAnnotation text, Brush brush) =>
        new(text.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextTypeface, Math.Max(1, text.FontSize), brush, 1.0);

    public static Size MeasureText(TextAnnotation text)
    {
        if (text.Text.Length == 0) return new Size(text.FontSize * 0.6, text.FontSize * 1.33);
        FormattedText formatted = FormatText(text, Brushes.Black);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>
    /// The pixelated area at full size: the area is cut into <paramref name="block"/>-pixel squares starting at its
    /// top-left corner, and every pixel of a square gets the average colour of the original pixels in it.
    /// </summary>
    public static BitmapSource Pixelate(CapturedFrame frame, PixelRect area, int block)
    {
        int width = area.Width;
        int height = area.Height;
        byte[] result = new byte[width * height * 4];
        byte[] source = frame.Pixels;
        int stride = frame.Stride;

        for (int y0 = 0; y0 < height; y0 += block)
        {
            int y1 = Math.Min(y0 + block, height);
            for (int x0 = 0; x0 < width; x0 += block)
            {
                int x1 = Math.Min(x0 + block, width);
                long b = 0, g = 0, r = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int sy = area.Y + y;
                    if (sy < frame.Bounds.Y || sy >= frame.Bounds.Bottom) continue;
                    int offset = (sy - frame.Bounds.Y) * stride;
                    for (int x = x0; x < x1; x++)
                    {
                        int sx = area.X + x;
                        if (sx < frame.Bounds.X || sx >= frame.Bounds.Right) continue;
                        int o = offset + (sx - frame.Bounds.X) * 4;
                        b += source[o];
                        g += source[o + 1];
                        r += source[o + 2];
                        count++;
                    }
                }

                byte cb = count > 0 ? (byte)(b / count) : (byte)0;
                byte cg = count > 0 ? (byte)(g / count) : (byte)0;
                byte cr = count > 0 ? (byte)(r / count) : (byte)0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int t = row + x * 4;
                        result[t] = cb;
                        result[t + 1] = cg;
                        result[t + 2] = cr;
                        result[t + 3] = 0xFF;
                    }
                }
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, result, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static PixelRect ToPixelRect(Rect rect)
    {
        if (rect.IsEmpty) return PixelRect.Empty;
        return PixelRect.FromEdges((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top), (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
    }

    public static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static uint ToArgb(Color color) => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static BitmapSource PixelatedBlocks(PixelateAnnotation annotation, CapturedFrame frame, PixelRect area, int block)
    {
        if (Pixelations.TryGetValue(annotation, out PixelationCache? cache) && cache.Area == area && cache.Block == block &&
            ReferenceEquals(cache.Frame, frame))
        {
            return cache.Bitmap;
        }

        BitmapSource bitmap = Pixelate(frame, area, block);
        Pixelations.AddOrUpdate(annotation, new PixelationCache(frame, area, block, bitmap));
        return bitmap;
    }

    private static void DrawStroke(DrawingContext dc, List<Point> points, Pen pen, Pen? shadow, double shadowOffset)
    {
        if (points.Count == 0) return;
        if (points.Count == 1)
        {
            double r = pen.Thickness / 2;
            if (shadow is not null) WithShadow(dc, shadowOffset, () => dc.DrawEllipse(shadow.Brush, null, points[0], r, r));
            dc.DrawEllipse(pen.Brush, null, points[0], r, r);
            return;
        }

        Geometry path = SmoothPath(points);
        if (shadow is not null) WithShadow(dc, shadowOffset, () => dc.DrawGeometry(null, shadow, path));
        dc.DrawGeometry(null, pen, path);
    }

    private static void WithShadow(DrawingContext dc, double offset, Action draw)
    {
        dc.PushTransform(new TranslateTransform(0, offset));
        draw();
        dc.Pop();
    }

    private static Pen RoundPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed record PixelationCache(CapturedFrame Frame, PixelRect Area, int Block, BitmapSource Bitmap);
}
