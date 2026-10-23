using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;

namespace HolyScreen.Editor;

/// <summary>Produces the final picture: the frozen pixels of the selection with every annotation drawn on top.</summary>
internal static class AnnotationExporter
{
    public static BitmapSource Render(CapturedFrame frame, PixelRect region, IReadOnlyList<Annotation> annotations)
    {
        BitmapSource background = frame.CreateBitmap(region);
        if (annotations.Count == 0) return background; // nothing drawn: the exact original pixels

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Ideal);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(background, new Rect(0, 0, region.Width, region.Height));
            dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
            foreach (Annotation annotation in annotations)
            {
                AnnotationRenderer.Render(dc, annotation, frame);
            }

            dc.Pop();
        }

        var target = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
