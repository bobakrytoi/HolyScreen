using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Overlay;

/// <summary>
/// A frosted-glass toolbar: blurred backdrop of what is underneath, a graphite tint, a soft top sheen, a hairline
/// rim and a shadow. Hosts icon buttons in a row or a column.
/// </summary>
internal sealed class GlassPanel : Grid
{
    private const double Radius = 13;

    private readonly ImageBrush _backdropBrush = new() { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
    private readonly Border _backdrop;
    private readonly StackPanel _items;
    private readonly TranslateTransform _slide = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private bool _shown;

    public GlassPanel(Orientation orientation, string name)
    {
        Orientation = orientation;
        Name = name;
        Opacity = 0;
        Visibility = Visibility.Hidden;
        SnapsToDevicePixels = true;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _scale, _slide } };
        Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.5, Color = Colors.Black };

        _backdrop = new Border { CornerRadius = new CornerRadius(Radius), Background = _backdropBrush };
        Children.Add(_backdrop);
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = OverlayTheme.Frozen(Color.FromArgb(0xA8, 0x15, 0x18, 0x1E)),
        });
        Children.Add(new Border { CornerRadius = new CornerRadius(Radius), Background = Sheen() });
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius),
            BorderBrush = OverlayTheme.Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        });

        _items = new StackPanel { Orientation = orientation, Margin = new Thickness(5) };
        Children.Add(_items);

        // Clicks on the glass itself must never fall through to the overlay (they would start a new selection).
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseRightButtonUp += (_, e) => e.Handled = true;
    }

    public Orientation Orientation { get; }

    public PlacementMode TooltipPlacement { get; set; } = PlacementMode.Top;

    public Button AddButton(string automationId, string icon, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("PanelButton"),
            Content = Glyph(icon),
            ToolTip = tooltip,
        };
        ToolTipService.SetPlacement(button, TooltipPlacement);
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        _items.Children.Add(button);
        return button;
    }

    public ToggleButton AddToggle(string automationId, string icon, string tooltip, Action onClick)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("PanelToggle"),
            Content = Glyph(icon),
            ToolTip = tooltip,
        };
        ToolTipService.SetPlacement(toggle, TooltipPlacement);
        System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, automationId);
        System.Windows.Automation.AutomationProperties.SetName(toggle, tooltip);
        toggle.Click += (_, _) => onClick();
        _items.Children.Add(toggle);
        return toggle;
    }

    /// <summary>A button showing arbitrary content (a colour dot, a size dot) instead of an icon.</summary>
    public Button AddContentButton(string automationId, FrameworkElement content, string tooltip, Action onClick, double width = 36)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("PanelButton"),
            Content = content,
            ToolTip = tooltip,
            Width = width,
        };
        ToolTipService.SetPlacement(button, TooltipPlacement);
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        _items.Children.Add(button);
        return button;
    }

    public void AddElement(FrameworkElement element) => _items.Children.Add(element);

    public void AddSeparator()
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        _items.Children.Add(new Border
        {
            Width = horizontal ? 1 : 22,
            Height = horizontal ? 22 : 1,
            Margin = horizontal ? new Thickness(5, 0, 5, 0) : new Thickness(0, 5, 0, 5),
            Background = (Brush)Application.Current.FindResource("PanelSeparatorBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
    }

    /// <summary>Points the glass at the blurred pixels underneath its current position.</summary>
    public void UpdateBackdrop(BitmapSource? blurred, Point topLeft, double scale)
    {
        _backdropBrush.ImageSource = blurred;
        if (blurred is null) return;

        double k = scale * GlassBackdrop.Downscale; // local DIPs → backdrop pixels
        Size size = DesiredSize;
        _backdropBrush.Viewbox = new Rect(topLeft.X * k, topLeft.Y * k, Math.Max(1, size.Width * k), Math.Max(1, size.Height * k));
    }

    /// <summary>Glides the panel in (fade, small rise and scale with a gentle overshoot), buttons following in a wave.</summary>
    public void Show(bool animate)
    {
        if (_shown) return;
        _shown = true;
        Visibility = Visibility.Visible;
        if (!animate)
        {
            Opacity = 1;
            return;
        }

        var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        double rise = Orientation == Orientation.Horizontal ? 8 : 0;
        double shift = Orientation == Orientation.Vertical ? -8 : 0;
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-rise, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

        int index = 0;
        foreach (UIElement child in _items.Children)
        {
            var delay = TimeSpan.FromMilliseconds(40 + index++ * 16);
            child.Opacity = 0;
            child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { BeginTime = delay });
        }
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Visibility = Visibility.Hidden;
    }

    public bool IsShown => _shown;

    private static Brush Sheen()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55));
        brush.Freeze();
        return brush;
    }

    private static FrameworkElement Glyph(string icon)
    {
        var path = new Path
        {
            Data = Icons.Get(icon),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = false,
        };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ButtonBase), 1),
        });
        return path;
    }
}
