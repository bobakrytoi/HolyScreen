using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.Interop;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Pin;

/// <summary>
/// A screenshot pinned above all windows. It appears exactly where the captured pixels were (as if the selection
/// detached from the screen) at 1:1 size. Drag to move, wheel to zoom, Ctrl+wheel for opacity, double-click or Esc
/// to close, right-click for Copy / Save.
/// </summary>
internal sealed class PinWindow : Window
{
    public const string WindowTitle = "HolyScreen Pin";
    private const double ShadowMargin = 16; // room for the shadow, in DIPs

    private readonly BitmapSource _image;
    private readonly PixelRect _origin;
    private readonly Border _frame;
    private readonly Func<BitmapSource, bool> _copy;
    private readonly Func<BitmapSource, bool> _save;
    private double _zoom = 1;
    private bool _placed;
    private bool _userMoved;

    public PinWindow(BitmapSource image, PixelRect origin, Func<BitmapSource, bool> copy, Func<BitmapSource, bool> save)
    {
        _image = image;
        _origin = origin;
        _copy = copy;
        _save = save;

        Title = WindowTitle;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true; // the soft shadow around the picture needs per-pixel alpha
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        _frame = new Border
        {
            Margin = new Thickness(ShadowMargin),
            CornerRadius = new CornerRadius(6),
            Background = brush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Opacity = 0.5, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Cursor = Cursors.SizeAll,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_frame, "PinImage");
        Content = _frame;
        ContextMenu = BuildMenu();

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Win32.AddExtendedStyle(hwnd, Win32.WS_EX_TOOLWINDOW);
        };
        Loaded += (_, _) => PlaceOverOrigin();
        MouseLeftButtonDown += OnLeftButtonDown;
        MouseWheel += OnWheel;
        KeyDown += OnKey;
        ApplySize();
    }

    /// <summary>Device pixels per DIP for this window right now.</summary>
    private double Scale => PresentationSource.FromVisual(this) is { } source ? source.CompositionTarget.TransformToDevice.M11 : 1;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplySize();
        if (_placed && !_userMoved)
        {
            // Arrived on a monitor with another scale while being placed: line up with the pixels again.
            Dispatcher.BeginInvoke(new Action(() => AlignWithOrigin()), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Picture size in DIPs so that it is shown 1:1 in physical pixels (times the zoom).</summary>
    private void ApplySize()
    {
        double scale = PresentationSource.FromVisual(this) is null ? DpiAt(_origin) : Scale;
        _frame.Width = _image.PixelWidth * _zoom / scale + 2; // + border
        _frame.Height = _image.PixelHeight * _zoom / scale + 2;
    }

    private static double DpiAt(PixelRect rect)
    {
        MonitorInfo monitor = DisplayLayout.FromPoint(DisplayLayout.GetMonitors(), rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        return monitor.Scale;
    }

    private void PlaceOverOrigin()
    {
        if (_placed) return;
        _placed = true;
        AlignWithOrigin();

        var pop = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        var transform = (ScaleTransform)_frame.RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Moves the window so the picture (inside the shadow margin and 1 px border) covers the captured pixels.</summary>
    private void AlignWithOrigin()
    {
        ApplySize();
        UpdateLayout();
        double scale = Scale;
        int x = _origin.X - (int)Math.Round((ShadowMargin + 1) * scale);
        int y = _origin.Y - (int)Math.Round((ShadowMargin + 1) * scale);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            Close();
            return;
        }

        _userMoved = true;
        try { DragMove(); } catch (InvalidOperationException) { /* released mid-drag */ }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        int notches = Math.Sign(e.Delta);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Opacity + 0.1 * notches, 0.2, 1);
            return;
        }

        // Zoom around the cursor: the image point under the pointer stays under the pointer.
        _userMoved = true;
        Win32.GetCursorPos(out Win32.POINT cursor);
        Win32.GetWindowRect(new WindowInteropHelper(this).Handle, out Win32.RECT before);
        double oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * (notches > 0 ? 1.15 : 1 / 1.15), 0.15, 6);
        if (Math.Abs(_zoom - 1) < 0.04) _zoom = 1;
        ApplySize();
        UpdateLayout();

        double ratio = _zoom / oldZoom;
        double margin = (ShadowMargin + 1) * Scale;
        double fx = cursor.X - before.Left - margin;
        double fy = cursor.Y - before.Top - margin;
        int x = (int)Math.Round(cursor.X - fx * ratio - margin);
        int y = (int)Math.Round(cursor.Y - fy * ratio - margin);
        Win32.SetWindowPos(new WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) Close();
        else if (ctrl && e.Key == Key.C) _copy(_image);
        else if (ctrl && e.Key == Key.S) _save(_image);
        else if (e.Key is Key.D0 or Key.D1 or Key.NumPad0 or Key.NumPad1)
        {
            _zoom = 1;
            Opacity = 1;
            ApplySize();
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Copy", "Ctrl+C", () => _copy(_image)));
        menu.Items.Add(Item("Save", "Ctrl+S", () => _save(_image)));
        menu.Items.Add(Item("Actual size", "1", () =>
        {
            _zoom = 1;
            Opacity = 1;
            ApplySize();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close", "Esc", Close));
        return menu;
    }

    private static MenuItem Item(string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture };
        item.Click += (_, _) => action();
        return item;
    }
}

/// <summary>Keeps track of open pins (for "Close all pins").</summary>
internal sealed class PinManager
{
    private readonly List<PinWindow> _pins = new();

    public int Count => _pins.Count;

    public PinWindow Open(BitmapSource image, PixelRect origin, Func<BitmapSource, bool> copy, Func<BitmapSource, bool> save)
    {
        var pin = new PinWindow(image, origin, copy, save);
        pin.Closed += (_, _) => _pins.Remove(pin);
        _pins.Add(pin);
        pin.Show();
        pin.Activate();
        return pin;
    }

    public void CloseAll()
    {
        foreach (PinWindow pin in _pins.ToList()) pin.Close();
    }
}
