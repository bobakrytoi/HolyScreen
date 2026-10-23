using System;
using System.Collections.Generic;
using System.Windows;

namespace HolyScreen.Editor;

/// <summary>Hit-testing, bounds, handles and edits of annotations (virtual-screen pixels throughout).</summary>
internal static class AnnotationGeometry
{
    /// <summary>The visual extent of an annotation, including its stroke.</summary>
    public static Rect Bounds(Annotation annotation)
    {
        switch (annotation)
        {
            case ArrowAnnotation arrow:
            {
                var rect = new Rect(arrow.Start, arrow.End);
                rect.Inflate(AnnotationRenderer.ArrowHeadWidth(arrow.Thickness) / 2, AnnotationRenderer.ArrowHeadWidth(arrow.Thickness) / 2);
                return rect;
            }

            case TwoPointAnnotation line:
            {
                var rect = new Rect(line.Start, line.End);
                rect.Inflate(line.Thickness / 2, line.Thickness / 2);
                return rect;
            }

            case PixelateAnnotation or RedactAnnotation:
                return ((BoxAnnotation)annotation).Box;

            case BoxAnnotation box:
            {
                Rect rect = box.Box;
                rect.Inflate(box.Thickness / 2, box.Thickness / 2);
                return rect;
            }

            case StrokeAnnotation stroke when stroke.Points.Count > 0:
            {
                var rect = new Rect(stroke.Points[0], stroke.Points[0]);
                foreach (Point p in stroke.Points) rect.Union(p);
                double half = StrokeWidth(stroke) / 2;
                rect.Inflate(half, half);
                return rect;
            }

            case TextAnnotation text:
            {
                Size size = AnnotationRenderer.MeasureText(text);
                return new Rect(text.Origin, size);
            }

            case StepAnnotation step:
            {
                double r = AnnotationRenderer.StepRadius(step.Thickness);
                return new Rect(step.Center.X - r, step.Center.Y - r, 2 * r, 2 * r);
            }

            default:
                return Rect.Empty;
        }
    }

    /// <summary>True when <paramref name="p"/> is on the annotation's ink (outlines count only near their line).</summary>
    public static bool HitTest(Annotation annotation, Point p, double tolerance)
    {
        switch (annotation)
        {
            case ArrowAnnotation arrow:
            {
                double d = DistanceToSegment(p, arrow.Start, arrow.End);
                if (d <= tolerance + arrow.Thickness) return true;
                double head = AnnotationRenderer.ArrowHeadLength(arrow.Thickness);
                return (p - arrow.End).Length <= head + tolerance && d <= AnnotationRenderer.ArrowHeadWidth(arrow.Thickness) / 2 + tolerance;
            }

            case LineAnnotation line:
                return DistanceToSegment(p, line.Start, line.End) <= tolerance + line.Thickness / 2;

            case RectangleAnnotation rectangle:
            {
                Rect box = rectangle.Box;
                double band = tolerance + rectangle.Thickness / 2;
                Rect outer = box;
                outer.Inflate(band, band);
                if (!outer.Contains(p)) return false;
                if (box.Width <= 2 * band || box.Height <= 2 * band) return true;
                Rect inner = box;
                inner.Inflate(-band, -band);
                return !inner.Contains(p);
            }

            case EllipseAnnotation ellipse:
            {
                Rect box = ellipse.Box;
                double rx = Math.Max(box.Width / 2, 0.5);
                double ry = Math.Max(box.Height / 2, 0.5);
                double nx = (p.X - (box.X + rx)) / rx;
                double ny = (p.Y - (box.Y + ry)) / ry;
                double distance = Math.Abs(Math.Sqrt(nx * nx + ny * ny) - 1) * Math.Min(rx, ry);
                return distance <= tolerance + ellipse.Thickness / 2;
            }

            case BoxAnnotation solid:
            {
                Rect box = solid.Box;
                box.Inflate(tolerance, tolerance);
                return box.Contains(p);
            }

            case StrokeAnnotation stroke:
            {
                double reach = tolerance + StrokeWidth(stroke) / 2;
                List<Point> points = stroke.Points;
                if (points.Count == 1) return (p - points[0]).Length <= reach;
                for (int i = 1; i < points.Count; i++)
                {
                    if (DistanceToSegment(p, points[i - 1], points[i]) <= reach) return true;
                }

                return false;
            }

            case TextAnnotation text:
            {
                Rect bounds = Bounds(text);
                bounds.Inflate(tolerance, tolerance);
                return bounds.Contains(p);
            }

            case StepAnnotation step:
                return (p - step.Center).Length <= AnnotationRenderer.StepRadius(step.Thickness) + tolerance;

            default:
                return false;
        }
    }

    /// <summary>Topmost annotation under <paramref name="p"/>, or null.</summary>
    public static Annotation? HitTest(IReadOnlyList<Annotation> annotations, Point p, double tolerance)
    {
        for (int i = annotations.Count - 1; i >= 0; i--)
        {
            if (HitTest(annotations[i], p, tolerance)) return annotations[i];
        }

        return null;
    }

    /// <summary>Grab points for reshaping: the ends of two-point shapes, 8 points around boxes, none otherwise.</summary>
    public static Point[] Handles(Annotation annotation) => annotation switch
    {
        TwoPointAnnotation line => new[] { line.Start, line.End },
        BoxAnnotation box => BoxHandles(box.Box),
        _ => Array.Empty<Point>(),
    };

    /// <summary>Order: top-left, top, top-right, right, bottom-right, bottom, bottom-left, left.</summary>
    public static Point[] BoxHandles(Rect r) => new[]
    {
        r.TopLeft, new Point(r.X + r.Width / 2, r.Y), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
        r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.X, r.Y + r.Height / 2),
    };

    public static int HandleAt(Annotation annotation, Point p, double tolerance)
    {
        Point[] handles = Handles(annotation);
        for (int i = 0; i < handles.Length; i++)
        {
            if ((p - handles[i]).Length <= tolerance) return i;
        }

        return -1;
    }

    /// <summary>Reshapes <paramref name="target"/> from its <paramref name="start"/> state by dragging a handle.</summary>
    public static void DragHandle(Annotation target, Annotation start, int handle, Vector delta, bool constrain)
    {
        switch (target)
        {
            case TwoPointAnnotation line when start is TwoPointAnnotation from:
                if (handle == 0) line.Start = constrain ? SnapAngle(from.Start + delta, from.End) : from.Start + delta;
                else line.End = constrain ? SnapAngle(from.End + delta, from.Start) : from.End + delta;
                break;

            case BoxAnnotation box when start is BoxAnnotation from:
                Rect resized = ResizeBox(from.Box, handle, delta);
                box.A = resized.TopLeft;
                box.B = resized.BottomRight;
                break;
        }
    }

    public static Rect ResizeBox(Rect start, int handle, Vector delta)
    {
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        switch (handle)
        {
            case 0: left += delta.X; top += delta.Y; break;
            case 1: top += delta.Y; break;
            case 2: right += delta.X; top += delta.Y; break;
            case 3: right += delta.X; break;
            case 4: right += delta.X; bottom += delta.Y; break;
            case 5: bottom += delta.Y; break;
            case 6: left += delta.X; bottom += delta.Y; break;
            case 7: left += delta.X; break;
        }

        return new Rect(new Point(left, top), new Point(right, bottom)); // normalises a flip
    }

    /// <summary>Moves every point of the annotation.</summary>
    public static void Offset(Annotation annotation, Vector delta)
    {
        switch (annotation)
        {
            case TwoPointAnnotation line:
                line.Start += delta;
                line.End += delta;
                break;
            case BoxAnnotation box:
                box.A += delta;
                box.B += delta;
                break;
            case StrokeAnnotation stroke:
                for (int i = 0; i < stroke.Points.Count; i++) stroke.Points[i] += delta;
                break;
            case TextAnnotation text:
                text.Origin += delta;
                break;
            case StepAnnotation step:
                step.Center += delta;
                break;
        }
    }

    /// <summary>Keeps the segment anchor→p at a multiple of 45° (Shift while drawing lines and arrows).</summary>
    public static Point SnapAngle(Point p, Point anchor)
    {
        Vector v = p - anchor;
        if (v.Length < 0.001) return p;
        double step = Math.PI / 4;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / step) * step;
        return anchor + new Vector(Math.Cos(angle) * v.Length, Math.Sin(angle) * v.Length);
    }

    /// <summary>Makes the box anchor→p square (Shift while drawing boxes and ellipses).</summary>
    public static Point SnapSquare(Point p, Point anchor)
    {
        double dx = p.X - anchor.X;
        double dy = p.Y - anchor.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(anchor.X + (dx < 0 ? -side : side), anchor.Y + (dy < 0 ? -side : side));
    }

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSquared, 0, 1);
        return (p - (a + t * ab)).Length;
    }

    public static double StrokeWidth(StrokeAnnotation stroke) =>
        stroke is MarkerAnnotation ? AnnotationRenderer.MarkerWidth(stroke.Thickness) : stroke.Thickness;
}
