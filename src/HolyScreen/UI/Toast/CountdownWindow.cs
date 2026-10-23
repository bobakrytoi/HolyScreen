using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HolyScreen.Capture;
using HolyScreen.Interop;

namespace HolyScreen.UI.Toast;

/// <summary>
/// "Capturing in 3 · 2 · 1" for delayed captures: a small click-through pill at the bottom of the screen with a
/// ticking number. It closes itself (and is gone from the screen) before the capture happens.
/// </summary>
internal sealed class CountdownWindow : Window
{
    private readonly TextBlock _number;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action _done;
    private int _remaining;

    public CountdownWindow(int seconds, Action done)
    {
        _remaining = seconds;
        _done = done;
        Title = "HolyScreen countdown";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        FontFamily = (FontFamily)Application.Current.FindResource("AppFontFamily");

        _number = new TextBlock
        {
            Text = seconds.ToString(),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "Capturing in",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCE, 0xD6)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(_number);

        Content = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1A, 0x1D, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 8, 22, 8),
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.5 },
            Child = row,
        };

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Win32.AddExtendedStyle(hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW);
        };
        Loaded += (_, _) => Position();
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        Show();
        _timer.Start();
        Pulse();
    }

    public void Cancel()
    {
        _timer.Stop();
        Close();
    }

    private void Tick()
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            Close();
            Win32.DwmFlush(); // be sure the pill is off the screen before the desktop is frozen
            _done();
            return;
        }

        _number.Text = _remaining.ToString();
        Pulse();
    }

    private void Pulse()
    {
        var scale = (ScaleTransform)_number.RenderTransform;
        var pop = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void Position()
    {
        Win32.POINT cursor = DisplayLayout.GetCursorPosition();
        MonitorInfo monitor = DisplayLayout.FromPoint(DisplayLayout.GetMonitors(), cursor.X, cursor.Y);
        double scale = monitor.Scale;
        int w = (int)Math.Ceiling(ActualWidth * scale);
        int h = (int)Math.Ceiling(ActualHeight * scale);
        int x = monitor.WorkArea.X + monitor.WorkArea.Width / 2 - w / 2;
        int y = monitor.WorkArea.Bottom - h - (int)(56 * scale);
        Win32.SetWindowPos(new WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }
}
