using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Interop;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Toast;

internal enum ToastKind
{
    Success,
    Info,
    Warning,
}

/// <summary>
/// Brief, non-intrusive confirmations ("Copied to clipboard", "Saved …"). The toast window never activates, so it
/// cannot steal focus from the app the user is about to paste into. It is click-through unless it carries an action
/// (e.g. "Show in folder"). Only one toast is visible at a time.
/// </summary>
internal sealed class ToastService : IDisposable
{
    private readonly Func<bool> _animate;
    private ToastWindow? _current;

    public ToastService(Func<bool> animate)
    {
        _animate = animate;
    }

    public void Show(string message, ToastKind kind = ToastKind.Success, int durationMs = 2200, Action? onClick = null,
        Color? swatch = null, string actionText = "Show in folder")
    {
        DismissCurrent();
        try
        {
            var toast = new ToastWindow(message, kind, _animate(), onClick, swatch, actionText);
            _current = toast;
            toast.Closed += (_, _) => { if (ReferenceEquals(_current, toast)) _current = null; };
            toast.ShowFor(durationMs);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not display a toast", ex);
        }
    }

    public void DismissCurrent()
    {
        try { _current?.CloseNow(); } catch { /* ignore */ }
        _current = null;
    }

    public void Dispose() => DismissCurrent();
}

/// <summary>The toast popup. Code-only; borderless and non-activating.</summary>
internal sealed class ToastWindow : Window
{
    private readonly bool _animate;
    private readonly Action? _onClick;
    private readonly DispatcherTimer _timer = new();
    private bool _closing;

    public ToastWindow(string message, ToastKind kind, bool animate, Action? onClick, Color? swatch = null, string actionText = "Show in folder")
    {
        _animate = animate;
        _onClick = onClick;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        IsHitTestVisible = onClick is not null;
        FontFamily = (FontFamily)Application.Current.FindResource("AppFontFamily");
        Title = "HolyScreen notification";

        Content = BuildContent(message, kind, onClick is not null ? actionText : null, swatch);
        SourceInitialized += OnSourceInitialized;
        if (onClick is not null)
        {
            Cursor = Cursors.Hand;
            MouseLeftButtonUp += (_, _) =>
            {
                try { _onClick?.Invoke(); } catch (Exception ex) { Log.Warn("Toast action failed", ex); }
                CloseNow();
            };
        }
    }

    private static UIElement BuildContent(string message, ToastKind kind, string? actionText, Color? swatch)
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1F, 0x25)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(15, 11, 17, 11),
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.5,
                Color = Colors.Black,
            },
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };

        (string path, Color color) = kind switch
        {
            ToastKind.Success => (Icons.Check, Color.FromRgb(0x8F, 0xD3, 0x9A)),
            ToastKind.Warning => (Icons.Warning, Color.FromRgb(0xE3, 0xC3, 0x6B)),
            _ => (Icons.Info, Color.FromRgb(0xC7, 0xD2, 0xE3)),
        };

        if (swatch is Color sample)
        {
            stack.Children.Add(new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(sample),
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
        }
        else
        {
            var iconBrush = new SolidColorBrush(color);
            iconBrush.Freeze();
            stack.Children.Add(new Path
            {
                Data = Icons.Get(path),
                Stroke = iconBrush,
                StrokeThickness = 2,
                Stretch = Stretch.Uniform,
                Width = 17,
                Height = 17,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF8)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 520,
        });

        if (actionText is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = actionText,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA2, 0xA7, 0xB0)),
                FontSize = 12,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = TextDecorations.Underline,
            });
        }

        pill.Child = stack;
        return pill;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Never activate and never appear in Alt+Tab. Without an action it is also click-through.
        long styles = Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
        if (_onClick is null) styles |= Win32.WS_EX_TRANSPARENT;
        Win32.AddExtendedStyle(hwnd, styles);
    }

    public void ShowFor(int durationMs)
    {
        Show();
        Dispatcher.BeginInvoke(new Action(PositionAndReveal), DispatcherPriority.Loaded);

        _timer.Interval = TimeSpan.FromMilliseconds(durationMs);
        _timer.Tick += (_, _) => { _timer.Stop(); FadeOutAndClose(); };
        _timer.Start();
    }

    private void PositionAndReveal()
    {
        Win32.POINT cursor = DisplayLayout.GetCursorPosition();
        MonitorInfo monitor = DisplayLayout.FromPoint(DisplayLayout.GetMonitors(), cursor.X, cursor.Y);
        double scale = monitor.Scale;
        int w = (int)Math.Ceiling(ActualWidth * scale);
        int h = (int)Math.Ceiling(ActualHeight * scale);
        int x = monitor.WorkArea.X + monitor.WorkArea.Width / 2 - w / 2;
        int y = monitor.WorkArea.Bottom - h - (int)(56 * scale);

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        if (!_animate)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
        var transform = new TranslateTransform();
        if (Content is UIElement element)
        {
            element.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        BeginAnimation(OpacityProperty, fade);
    }

    private void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;

        if (!_animate)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    public void CloseNow()
    {
        _timer.Stop();
        _closing = true;
        Close();
    }
}
