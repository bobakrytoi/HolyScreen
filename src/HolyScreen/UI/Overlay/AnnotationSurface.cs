using System.Windows;
using System.Windows.Media;
using HolyScreen.Editor;

namespace HolyScreen.UI.Overlay;

/// <summary>
/// Draws the annotations on one monitor window: clipped to the selection (what is outside will not be in the
/// picture, so it is not shown either) and mapped from virtual-screen pixels to this window's DIPs.
/// </summary>
internal sealed class AnnotationSurface : FrameworkElement
{
    public AnnotationSurface()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor); // pixelation blocks stay crisp
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
    }

    public AnnotationEditor? Editor { get; set; }

    /// <summary>The selection in local DIPs.</summary>
    public Rect ClipRect { get; set; } = Rect.Empty;

    /// <summary>Virtual-screen pixels → local DIPs.</summary>
    public Matrix PixelsToLocal { get; set; } = Matrix.Identity;

    protected override void OnRender(DrawingContext dc)
    {
        if (Editor is null || ClipRect.IsEmpty) return;
        dc.PushClip(new RectangleGeometry(ClipRect));
        dc.PushTransform(new MatrixTransform(PixelsToLocal));
        Editor.Render(dc);
        dc.Pop();
        dc.Pop();
    }
}
