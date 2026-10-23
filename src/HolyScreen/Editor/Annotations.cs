using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Windows;

namespace HolyScreen.Editor;

/// <summary>The drawing tools of the editor.</summary>
internal enum ToolKind
{
    Arrow,
    Rectangle,
    Ellipse,
    Line,
    Pencil,
    Marker,
    Text,
    Step,
    Pixelate,
    Redact,
}

/// <summary>
/// One drawn object. Plain data in virtual-screen pixel coordinates (so it can be saved with the history and
/// re-opened); drawing, hit-testing and editing live in <see cref="AnnotationRenderer"/> and
/// <see cref="AnnotationGeometry"/>. <see cref="Thickness"/> and font sizes are in image pixels.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(RectangleAnnotation), "rectangle")]
[JsonDerivedType(typeof(EllipseAnnotation), "ellipse")]
[JsonDerivedType(typeof(PencilAnnotation), "pencil")]
[JsonDerivedType(typeof(MarkerAnnotation), "marker")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(PixelateAnnotation), "pixelate")]
[JsonDerivedType(typeof(RedactAnnotation), "redact")]
internal abstract class Annotation
{
    /// <summary>Colour as 0xAARRGGBB.</summary>
    public uint Color { get; set; } = 0xFFFF3B30;

    public double Thickness { get; set; } = 4;

    [JsonIgnore]
    public abstract ToolKind Tool { get; }

    public abstract Annotation Clone();

    /// <summary>Copies every value of <paramref name="source"/> (same type) into this object.</summary>
    public void CopyFrom(Annotation source)
    {
        if (source.GetType() != GetType()) throw new ArgumentException("Annotation types differ.", nameof(source));
        foreach (PropertyInfo property in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            object? value = property.GetValue(source);
            if (value is List<Point> points) value = points.ToList();
            property.SetValue(this, value);
        }
    }
}

/// <summary>Shapes defined by two points (start → end).</summary>
internal abstract class TwoPointAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }
}

internal sealed class ArrowAnnotation : TwoPointAnnotation
{
    public override ToolKind Tool => ToolKind.Arrow;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}

internal sealed class LineAnnotation : TwoPointAnnotation
{
    public override ToolKind Tool => ToolKind.Line;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}

/// <summary>Shapes defined by two opposite corners (in any order).</summary>
internal abstract class BoxAnnotation : Annotation
{
    public Point A { get; set; }
    public Point B { get; set; }

    [JsonIgnore]
    public Rect Box => new(A, B);
}

internal sealed class RectangleAnnotation : BoxAnnotation
{
    public override ToolKind Tool => ToolKind.Rectangle;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}

internal sealed class EllipseAnnotation : BoxAnnotation
{
    public override ToolKind Tool => ToolKind.Ellipse;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}

/// <summary>Hides what is underneath behind large averaged blocks of the original pixels.</summary>
internal sealed class PixelateAnnotation : BoxAnnotation
{
    public int BlockSize { get; set; } = 12;
    public override ToolKind Tool => ToolKind.Pixelate;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}

/// <summary>An opaque box — the safest way to hide text.</summary>
internal sealed class RedactAnnotation : BoxAnnotation
{
    public override ToolKind Tool => ToolKind.Redact;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}

/// <summary>Freehand strokes.</summary>
internal abstract class StrokeAnnotation : Annotation
{
    public List<Point> Points { get; set; } = new();
}

internal sealed class PencilAnnotation : StrokeAnnotation
{
    public override ToolKind Tool => ToolKind.Pencil;
    public override Annotation Clone() => new PencilAnnotation { Color = Color, Thickness = Thickness, Points = Points.ToList() };
}

/// <summary>A translucent highlighter stroke.</summary>
internal sealed class MarkerAnnotation : StrokeAnnotation
{
    public override ToolKind Tool => ToolKind.Marker;
    public override Annotation Clone() => new MarkerAnnotation { Color = Color, Thickness = Thickness, Points = Points.ToList() };
}

internal sealed class TextAnnotation : Annotation
{
    /// <summary>Top-left of the first line.</summary>
    public Point Origin { get; set; }
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 22;
    public override ToolKind Tool => ToolKind.Text;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}

/// <summary>A numbered circle (1, 2, 3 …) for step-by-step instructions.</summary>
internal sealed class StepAnnotation : Annotation
{
    public Point Center { get; set; }
    public int Number { get; set; } = 1;
    public override ToolKind Tool => ToolKind.Step;
    public override Annotation Clone() => (Annotation)MemberwiseClone();
}
