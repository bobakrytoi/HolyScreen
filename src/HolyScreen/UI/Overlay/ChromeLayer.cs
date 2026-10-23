using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HolyScreen.UI.Overlay;

/// <summary>What the chrome layer of one monitor window should draw, already in that window's DIPs.</summary>
internal sealed record ChromeState(
    double Scale,
    Point? Crosshair,
    Rect? Highlight,
    string? HighlightTitle,
    string? HighlightSize,
    Rect? Selection,
    bool Handles,
    HandleKind ActiveHandle,
    string? SizeLabel,
    Rect? AnnotationBounds = null,
    Point[]? AnnotationHandles = null,
    string? Transient = null,
    Point? TransientAt = null,
    string? Hint = null)
{
    public static readonly ChromeState Empty = new(1, null, null, null, null, null, false, HandleKind.None, null);
}

/// <summary>
/// Draws the overlay's line work — crosshair, hovered-window frame, selection frame, handles and labels — in one
/// lightweight <see cref="OnRender"/> pass. Lines are snapped to device pixels so they stay crisp at any scaling.
/// </summary>
internal sealed class ChromeLayer : FrameworkElement
{
    private const double HandleRadius = 4.5;
    private const double LabelGap = 7;

    public ChromeState State { get; set; } = ChromeState.Empty;

    protected override void OnRender(DrawingContext dc)
    {
        ChromeState s = State;
        double px = 1 / s.Scale; // one device pixel, in DIPs
        double width = ActualWidth;
        double height = ActualHeight;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (s.Crosshair is Point c)
        {
            double x = Snap(c.X, s.Scale);
            double y = Snap(c.Y, s.Scale);
            dc.DrawRectangle(OverlayTheme.CrosshairShade, null, new Rect(x + px, 0, px, height));
            dc.DrawRectangle(OverlayTheme.CrosshairShade, null, new Rect(0, y + px, width, px));
            dc.DrawRectangle(OverlayTheme.Crosshair, null, new Rect(x, 0, px, height));
            dc.DrawRectangle(OverlayTheme.Crosshair, null, new Rect(0, y, width, px));
        }

        if (s.Highlight is Rect highlight)
        {
            double t = Math.Max(1, Math.Round(2 * s.Scale)) * px;
            FrameInside(dc, highlight, t, OverlayTheme.Highlight);
            FrameInside(dc, Deflate(highlight, t), px, OverlayTheme.HighlightShade);
            if (s.HighlightTitle is not null || s.HighlightSize is not null)
            {
                DrawLabel(dc, highlight, s.HighlightTitle, s.HighlightSize, pixelsPerDip, width, height, preferInside: true);
            }
        }

        if (s.Selection is Rect selection)
        {
            double t = Math.Max(1, Math.Round(s.Scale)) * px;
            FrameOutside(dc, Inflate(selection, t), t, OverlayTheme.SelectionShade);
            FrameOutside(dc, selection, t, OverlayTheme.SelectionLine);

            if (s.Handles)
            {
                DrawHandles(dc, selection, s.ActiveHandle);
            }

            if (s.SizeLabel is not null)
            {
                DrawLabel(dc, selection, null, s.SizeLabel, pixelsPerDip, width, height, preferInside: false);
            }
        }

        if (s.AnnotationBounds is Rect bounds)
        {
            // The selected annotation: a dashed accent outline and square grab handles.
            Rect outline = Inflate(bounds, 4);
            dc.DrawRectangle(null, DashedPen(px), outline);
            foreach (Point handle in s.AnnotationHandles ?? Array.Empty<Point>())
            {
                var square = new Rect(handle.X - 4, handle.Y - 4, 8, 8);
                dc.DrawRectangle(OverlayTheme.HandleShade, null, Inflate(square, 1));
                dc.DrawRectangle(OverlayTheme.HandleFill, new Pen(OverlayTheme.Highlight, px), square);
            }
        }

        if (s.Transient is string transient && s.TransientAt is Point at)
        {
            DrawLabel(dc, new Rect(at.X + 18, at.Y + 30, 1, 1), null, transient, pixelsPerDip, width, height, preferInside: false);
        }

        if (s.Hint is string hint)
        {
            // A mode hint centred at the top of the monitor the pointer is on.
            FormattedText text = Text(hint, OverlayTheme.Semibold, OverlayTheme.Text, pixelsPerDip, width - 80);
            double pillWidth = Math.Ceiling(text.WidthIncludingTrailingWhitespace + 28);
            double pillHeight = Math.Ceiling(text.Height + 14);
            var pill = new Rect(Math.Round((width - pillWidth) / 2), 24, pillWidth, pillHeight);
            dc.DrawRoundedRectangle(OverlayTheme.PillBackground, OverlayTheme.PillBorder, pill, pillHeight / 2, pillHeight / 2);
            dc.DrawText(text, new Point(pill.X + 14, pill.Y + 7));
        }
    }

    private static Pen DashedPen(double px)
    {
        var pen = new Pen(OverlayTheme.Highlight, Math.Max(px, 1)) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        pen.Freeze();
        return pen;
    }

    private static double Snap(double value, double scale) => Math.Floor(value * scale + 0.001) / scale;

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private static Rect Deflate(Rect r, double d) =>
        new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    /// <summary>A frame of thickness <paramref name="t"/> hugging the outside of <paramref name="r"/>.</summary>
    private static void FrameOutside(DrawingContext dc, Rect r, double t, Brush brush)
    {
        dc.DrawRectangle(brush, null, new Rect(r.X - t, r.Y - t, r.Width + 2 * t, t));
        dc.DrawRectangle(brush, null, new Rect(r.X - t, r.Bottom, r.Width + 2 * t, t));
        dc.DrawRectangle(brush, null, new Rect(r.X - t, r.Y, t, r.Height));
        dc.DrawRectangle(brush, null, new Rect(r.Right, r.Y, t, r.Height));
    }

    /// <summary>A frame of thickness <paramref name="t"/> just inside <paramref name="r"/>.</summary>
    private static void FrameInside(DrawingContext dc, Rect r, double t, Brush brush)
    {
        if (r.Width <= 2 * t || r.Height <= 2 * t)
        {
            dc.DrawRectangle(brush, null, r);
            return;
        }

        dc.DrawRectangle(brush, null, new Rect(r.X, r.Y, r.Width, t));
        dc.DrawRectangle(brush, null, new Rect(r.X, r.Bottom - t, r.Width, t));
        dc.DrawRectangle(brush, null, new Rect(r.X, r.Y + t, t, r.Height - 2 * t));
        dc.DrawRectangle(brush, null, new Rect(r.Right - t, r.Y + t, t, r.Height - 2 * t));
    }

    private static void DrawHandles(DrawingContext dc, Rect r, HandleKind active)
    {
        double cx = r.X + r.Width / 2;
        double cy = r.Y + r.Height / 2;
        bool roomForEdgeHandles = r.Width > 6 * HandleRadius && r.Height > 6 * HandleRadius;

        Handle(dc, new Point(r.X, r.Y), active == HandleKind.TopLeft);
        Handle(dc, new Point(r.Right, r.Y), active == HandleKind.TopRight);
        Handle(dc, new Point(r.Right, r.Bottom), active == HandleKind.BottomRight);
        Handle(dc, new Point(r.X, r.Bottom), active == HandleKind.BottomLeft);
        if (roomForEdgeHandles)
        {
            Handle(dc, new Point(cx, r.Y), active == HandleKind.Top);
            Handle(dc, new Point(r.Right, cy), active == HandleKind.Right);
            Handle(dc, new Point(cx, r.Bottom), active == HandleKind.Bottom);
            Handle(dc, new Point(r.X, cy), active == HandleKind.Left);
        }
    }

    private static void Handle(DrawingContext dc, Point center, bool active)
    {
        dc.DrawEllipse(OverlayTheme.HandleShade, null, center, HandleRadius + 1.4, HandleRadius + 1.4);
        dc.DrawEllipse(active ? OverlayTheme.HandleActive : OverlayTheme.HandleFill, null, center, HandleRadius, HandleRadius);
    }

    /// <summary>A small dark pill with an optional bold title and a secondary text, attached to a corner of <paramref name="anchor"/>.</summary>
    private static void DrawLabel(DrawingContext dc, Rect anchor, string? title, string? detail, double pixelsPerDip,
        double width, double height, bool preferInside)
    {
        FormattedText? titleText = title is null ? null : Text(title, OverlayTheme.Semibold, OverlayTheme.Text, pixelsPerDip, 360);
        FormattedText? detailText = detail is null ? null : Text(detail, OverlayTheme.Regular,
            title is null ? OverlayTheme.Text : OverlayTheme.SecondaryText, pixelsPerDip, 200);

        const double padX = 9;
        const double padY = 4.5;
        const double gap = 8;
        double contentWidth = (titleText?.WidthIncludingTrailingWhitespace ?? 0) + (detailText?.WidthIncludingTrailingWhitespace ?? 0) +
                              (titleText is not null && detailText is not null ? gap : 0);
        double textHeight = Math.Max(titleText?.Height ?? 0, detailText?.Height ?? 0);
        double pillWidth = Math.Ceiling(contentWidth + 2 * padX);
        double pillHeight = Math.Ceiling(textHeight + 2 * padY);

        double x = anchor.X;
        double y = preferInside ? anchor.Y + LabelGap : anchor.Y - pillHeight - LabelGap;
        if (!preferInside && y < 4) y = anchor.Y + LabelGap;                       // no room above: tuck inside
        if (preferInside && anchor.Height < pillHeight + 2 * LabelGap) y = anchor.Y - pillHeight - LabelGap;
        if (preferInside) x += LabelGap;
        x = Math.Clamp(x, 4, Math.Max(4, width - pillWidth - 4));
        y = Math.Clamp(y, 4, Math.Max(4, height - pillHeight - 4));
        x = Math.Round(x);
        y = Math.Round(y);

        var pill = new Rect(x, y, pillWidth, pillHeight);
        dc.DrawRoundedRectangle(OverlayTheme.PillBackground, OverlayTheme.PillBorder, pill, pillHeight / 2, pillHeight / 2);

        double cursor = x + padX;
        if (titleText is not null)
        {
            dc.DrawText(titleText, new Point(cursor, y + (pillHeight - titleText.Height) / 2));
            cursor += titleText.WidthIncludingTrailingWhitespace + gap;
        }

        if (detailText is not null)
        {
            dc.DrawText(detailText, new Point(cursor, y + (pillHeight - detailText.Height) / 2));
        }
    }

    private static FormattedText Text(string text, Typeface typeface, Brush brush, double pixelsPerDip, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, brush, pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return formatted;
    }
}
