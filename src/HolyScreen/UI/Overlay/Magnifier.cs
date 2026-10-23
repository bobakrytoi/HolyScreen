using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HolyScreen.UI.Overlay;

/// <summary>
/// The loupe that follows the cursor: a 15×15 pixel neighbourhood magnified 8× with the exact pixel under the cursor
/// framed, plus its coordinates and colour. It reuses the monitor bitmap already on the GPU and only moves a
/// transform per mouse move, so it costs next to nothing.
/// </summary>
internal sealed class Magnifier : Border
{
    public const int PixelsAcross = 15; // odd, so there is a true centre pixel
    private const double Cell = 8;      // DIPs per magnified pixel
    private const double ViewSize = PixelsAcross * Cell;
    private const double CursorGap = 22;

    private readonly MatrixTransform _zoomTransform = new();
    private readonly TranslateTransform _offset = new();
    private readonly Image _zoom;
    private readonly TextBlock _hex;
    private readonly TextBlock _position;
    private readonly Border _swatch;
    private bool _visible;

    public Magnifier()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        CornerRadius = new CornerRadius(12);
        Background = OverlayTheme.PillBackground;
        BorderBrush = OverlayTheme.PillBorder.Brush;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(5);
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        RenderTransform = _offset;
        Opacity = 0;
        Visibility = Visibility.Collapsed;

        _zoom = new Image { Stretch = Stretch.Fill, RenderTransform = _zoomTransform };
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);

        var viewport = new Canvas
        {
            Width = ViewSize,
            Height = ViewSize,
            ClipToBounds = true,
            Background = Brushes.Black,
            Clip = new RectangleGeometry(new Rect(0, 0, ViewSize, ViewSize), 8, 8),
        };
        viewport.Children.Add(_zoom);
        viewport.Children.Add(new Rectangle { Width = ViewSize, Height = ViewSize, Fill = CreateGridBrush() });

        double centre = PixelsAcross / 2 * Cell;
        var outer = new Rectangle { Width = Cell + 2, Height = Cell + 2, Stroke = OverlayTheme.Frozen(Color.FromArgb(0xB0, 0, 0, 0)), StrokeThickness = 1 };
        Canvas.SetLeft(outer, centre - 1);
        Canvas.SetTop(outer, centre - 1);
        var inner = new Rectangle { Width = Cell, Height = Cell, Stroke = Brushes.White, StrokeThickness = 1 };
        Canvas.SetLeft(inner, centre);
        Canvas.SetTop(inner, centre);
        viewport.Children.Add(outer);
        viewport.Children.Add(inner);

        _swatch = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(3),
            BorderBrush = OverlayTheme.Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _hex = new TextBlock
        {
            FontFamily = OverlayTheme.MonoFont,
            FontSize = 11.5,
            Foreground = OverlayTheme.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _position = new TextBlock
        {
            FontFamily = OverlayTheme.Font,
            FontSize = 11,
            Foreground = OverlayTheme.SecondaryText,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var info = new DockPanel { Margin = new Thickness(3, 6, 3, 2), LastChildFill = false };
        DockPanel.SetDock(_swatch, Dock.Left);
        DockPanel.SetDock(_hex, Dock.Left);
        DockPanel.SetDock(_position, Dock.Right);
        info.Children.Add(_swatch);
        info.Children.Add(_hex);
        info.Children.Add(_position);

        var stack = new StackPanel { Width = ViewSize };
        stack.Children.Add(viewport);
        stack.Children.Add(info);
        Child = stack;
    }

    /// <summary>The monitor bitmap to magnify (drawn at one DIP per source pixel, then scaled by the transform).</summary>
    public void SetSource(BitmapSource? bitmap)
    {
        _zoom.Source = bitmap;
        _zoom.Width = bitmap?.PixelWidth ?? 0;
        _zoom.Height = bitmap?.PixelHeight ?? 0;
    }

    /// <param name="pixelX">Cursor pixel relative to the monitor bitmap.</param>
    /// <param name="screenX">Cursor pixel in virtual-screen coordinates (shown to the user).</param>
    public void Update(int pixelX, int pixelY, int screenX, int screenY, Color color, Point cursor, Size area)
    {
        double tx = ViewSize / 2 - (pixelX + 0.5) * Cell;
        double ty = ViewSize / 2 - (pixelY + 0.5) * Cell;
        _zoomTransform.Matrix = new Matrix(Cell, 0, 0, Cell, tx, ty);

        _hex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _swatch.Background = OverlayTheme.Frozen(color);
        _position.Text = $"{screenX}, {screenY}";

        if (DesiredSize.Width <= 0) Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = DesiredSize;
        double x = cursor.X + CursorGap;
        double y = cursor.Y + CursorGap;
        if (x + size.Width > area.Width - 4) x = cursor.X - CursorGap - size.Width;
        if (y + size.Height > area.Height - 4) y = cursor.Y - CursorGap - size.Height;
        _offset.X = Math.Round(Math.Max(4, x));
        _offset.Y = Math.Round(Math.Max(4, y));
    }

    public void SetVisible(bool visible, bool animate)
    {
        if (visible == _visible) return;
        _visible = visible;

        if (!visible)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        if (animate)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() });
        }
        else
        {
            Opacity = 1;
        }
    }

    private static Brush CreateGridBrush()
    {
        var line = OverlayTheme.Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        var geometry = new GeometryGroup();
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, Cell, 0.5)));
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, 0.5, Cell)));
        var brush = new DrawingBrush(new GeometryDrawing(line, null, geometry))
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, Cell, Cell),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, Cell, Cell),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
