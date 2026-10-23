using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HolyScreen.Capture;
using HolyScreen.Editor;
using HolyScreen.Interop;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Overlay;

/// <summary>Everything one monitor window needs to draw a frame of the overlay (all rectangles in virtual-screen pixels).</summary>
internal sealed record OverlayView(
    PixelRect? Selection,
    PixelRect? Highlight,
    string? HighlightTitle,
    bool ShowHandles,
    HandleKind ActiveHandle,
    bool ShowCrosshair,
    bool ShowMagnifier,
    int CursorX,
    int CursorY,
    Color CursorColor,
    Cursor Cursor,
    MonitorInfo? LabelMonitor,
    Rect? AnnotationBounds = null,
    Point[]? AnnotationHandles = null,
    string? Transient = null,
    string? Hint = null,
    bool Veil = true);

/// <summary>
/// One opaque, topmost window covering one monitor and showing its frozen pixels 1:1, with the dim veil, the
/// selection chrome and the magnifier on top. Windows are pooled: created once per monitor, attached to a session
/// for a capture and hidden (not destroyed) afterwards, because creating a WPF window is the slowest part of
/// opening the overlay. Input goes straight to the attached <see cref="OverlaySession"/>, which owns all state.
/// </summary>
internal sealed class OverlayWindow : Window
{
    public const string WindowTitle = "HolyScreen Overlay";

    private readonly Image _image;
    private readonly RectangleGeometry _dimOuter = new();
    private readonly RectangleGeometry _dimHole = new(Rect.Empty);
    private readonly Path _dim;
    private readonly ChromeLayer _chrome;
    private readonly Magnifier _magnifier;
    private readonly Rectangle _flash;
    private OverlaySession? _session;
    private IntPtr _hwnd;
    private bool _animate;
    private bool _magnifierEnabled;
    private TextBox? _textBox;

    public OverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;

        Title = WindowTitle;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false; // opaque on purpose: full GPU rendering, no per-frame read-back of a layered window
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FocusVisualStyle = null;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // A best guess in DIPs; the exact device-pixel placement happens in OnSourceInitialized.
        Left = monitor.Bounds.X / monitor.Scale;
        Top = monitor.Bounds.Y / monitor.Scale;
        Width = monitor.Bounds.Width / monitor.Scale;
        Height = monitor.Bounds.Height / monitor.Scale;
        Cursor = Cursors.Cross;

        var root = new Grid { ClipToBounds = true };

        _image = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        root.Children.Add(_image);

        var dimGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dimGeometry.Children.Add(_dimOuter);
        dimGeometry.Children.Add(_dimHole);
        _dim = new Path { Data = dimGeometry, Fill = OverlayTheme.Dim, Opacity = 0, IsHitTestVisible = false };
        root.Children.Add(_dim);

        Annotations = new AnnotationSurface();
        root.Children.Add(Annotations);

        _chrome = new ChromeLayer { IsHitTestVisible = false };
        root.Children.Add(_chrome);

        var flashLayer = new Canvas { IsHitTestVisible = false };
        _flash = new Rectangle { Fill = OverlayTheme.Flash, Opacity = 0 };
        flashLayer.Children.Add(_flash);
        root.Children.Add(flashLayer);

        _magnifier = new Magnifier();
        root.Children.Add(_magnifier);

        PanelLayer = new Canvas();
        root.Children.Add(PanelLayer);

        Content = root;
        root.SizeChanged += (_, _) => _dimOuter.Rect = new Rect(root.RenderSize);

        MouseLeftButtonDown += OnLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnLeftButtonUp;
        MouseRightButtonUp += OnRightButtonUp;
        MouseWheel += OnMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        ContentRendered += (_, _) =>
        {
            HasRendered = true;
            _session?.OnWindowFirstRendered(this);
        };
    }

    public MonitorInfo Monitor { get; }

    public IntPtr Handle => _hwnd;

    /// <summary>The frozen pixels of this monitor for the current capture.</summary>
    public BitmapSource? Bitmap => _image.Source as BitmapSource;

    /// <summary>True once WPF has presented this window at least once (its first ContentRendered).</summary>
    public bool HasRendered { get; private set; }

    /// <summary>Annotations of the selection are drawn here, under the chrome.</summary>
    public AnnotationSurface Annotations { get; }

    /// <summary>Tool and action panels are hosted here, above everything else.</summary>
    public Canvas PanelLayer { get; }

    /// <summary>Device pixels per DIP for this window as WPF currently renders it.</summary>
    public double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>A pooled window can serve a new capture only if its monitor is unchanged.</summary>
    public bool Fits(MonitorInfo monitor) =>
        monitor.Bounds == Monitor.Bounds && monitor.Dpi == Monitor.Dpi && monitor.DeviceName == Monitor.DeviceName;

    public Rect ToLocal(PixelRect r)
    {
        double s = Scale;
        return new Rect((r.X - Monitor.Bounds.X) / s, (r.Y - Monitor.Bounds.Y) / s, r.Width / s, r.Height / s);
    }

    public Point ToLocal(int x, int y)
    {
        double s = Scale;
        return new Point((x - Monitor.Bounds.X) / s, (y - Monitor.Bounds.Y) / s);
    }

    public Point ToLocalPoint(Point p)
    {
        double s = Scale;
        return new Point((p.X - Monitor.Bounds.X) / s, (p.Y - Monitor.Bounds.Y) / s);
    }

    // ------------------------------------------------------------------ text entry

    /// <summary>Shows an in-place text box over <paramref name="text"/> (font and colour match the final rendering).</summary>
    public void BeginTextEdit(TextAnnotation text, Action<string> changed, Action commit, Action cancel)
    {
        EndTextEdit();
        double s = Scale;
        var box = new TextBox
        {
            Style = (Style)FindResource("OverlayTextBox"),
            Text = text.Text,
            FontSize = text.FontSize / s,
            Foreground = OverlayTheme.Frozen(AnnotationRenderer.ToColor(text.Color)),
            CaretBrush = OverlayTheme.Frozen(AnnotationRenderer.ToColor(text.Color)),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "TextInput");

        // TextBox draws its text ~2 DIPs right of its edge; shift it so committed text lands where it was typed.
        Point local = ToLocalPoint(text.Origin);
        Canvas.SetLeft(box, Math.Round(local.X - 2));
        Canvas.SetTop(box, Math.Round(local.Y));
        PanelLayer.Children.Add(box);
        _textBox = box;

        bool finished = false;
        void Finish(Action action)
        {
            if (finished) return;
            finished = true;
            action();
        }

        box.TextChanged += (_, _) => changed(box.Text);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                Finish(commit);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Finish(cancel);
            }
        };
        box.LostKeyboardFocus += (_, _) => Finish(commit);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_textBox, box)) return;
            Activate();
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }), DispatcherPriority.Input);
    }

    public void EndTextEdit()
    {
        if (_textBox is not TextBox box) return;
        _textBox = null;
        PanelLayer.Children.Remove(box);
        Focus();
    }

    /// <summary>Keeps the text box's look in step with colour/size changes made while typing.</summary>
    public void UpdateTextEdit(TextAnnotation text)
    {
        if (_textBox is not TextBox box) return;
        Brush brush = OverlayTheme.Frozen(AnnotationRenderer.ToColor(text.Color));
        box.Foreground = brush;
        box.CaretBrush = brush;
        box.FontSize = text.FontSize / Scale;
    }

    // ------------------------------------------------------------------ session attachment

    /// <summary>Prepares the (cloaked) window for a capture: new frozen pixels, clean chrome, back on top.</summary>
    public void Attach(OverlaySession session, BitmapSource bitmap, bool showMagnifier)
    {
        _session = session;
        _magnifierEnabled = showMagnifier;
        _image.Source = bitmap;
        _magnifier.SetSource(showMagnifier ? bitmap : null);
        _magnifier.SetVisible(false, false);

        _dim.BeginAnimation(OpacityProperty, null);
        _dim.Opacity = 0;
        _flash.BeginAnimation(OpacityProperty, null);
        _flash.Opacity = 0;
        _dimHole.Rect = Rect.Empty;
        _chrome.State = ChromeState.Empty;
        _chrome.Visibility = Visibility.Visible;
        _chrome.InvalidateVisual();
        Annotations.Editor = null;
        Annotations.ClipRect = Rect.Empty;
        Annotations.InvalidateVisual();
        _textBox = null;
        PanelLayer.Children.Clear();
        PanelLayer.Visibility = Visibility.Visible;
        Cursor = Cursors.Cross;

        if (_hwnd != IntPtr.Zero)
        {
            WindowEffects.SetCloaked(_hwnd, true);
            PlaceOnMonitor();
        }
    }

    /// <summary>Hides the window and lets go of the session and its (large) bitmaps.</summary>
    public void Detach()
    {
        if (_hwnd != IntPtr.Zero) WindowEffects.SetCloaked(_hwnd, true);
        if (IsMouseCaptured) ReleaseMouseCapture();
        Hide();
        _session = null;
        _image.Source = null;
        _magnifier.SetSource(null);
        Annotations.Editor = null;
        _textBox = null;
        PanelLayer.Children.Clear();
    }

    // ------------------------------------------------------------------ native setup

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        Win32.AddExtendedStyle(_hwnd, Win32.WS_EX_TOOLWINDOW); // never in Alt+Tab or the taskbar
        WindowEffects.SetCloaked(_hwnd, true);                 // invisible until the frame is rendered
        WindowEffects.SetCornerPreference(_hwnd, Win32.DWMWCP_DONOTROUND);
        WindowEffects.HideBorder(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        PlaceOnMonitor();
    }

    private void PlaceOnMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;
        PixelRect b = Monitor.Bounds;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Win32.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch ((uint)msg)
        {
            case Win32.WM_DPICHANGED:
                // Moving onto a monitor with another scale: whatever rectangle Windows suggests, this window must
                // cover exactly its monitor. WPF applies the (rewritten) suggestion; we re-assert it afterwards too.
                PixelRect b = Monitor.Bounds;
                Marshal.StructureToPtr(new Win32.RECT(b.X, b.Y, b.Right, b.Bottom), lParam, false);
                Dispatcher.BeginInvoke(new Action(PlaceOnMonitor), DispatcherPriority.Send);
                break;

            case Win32.WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == Win32.SC_KEYMENU:
                // Releasing Alt (left over from Alt+Shift+S) must not enter the invisible system-menu loop,
                // which would swallow the next key press (e.g. Esc).
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _session?.OnWindowDpiChanged(this);
    }

    // ------------------------------------------------------------------ input → session

    private (int X, int Y) ToScreen(MouseEventArgs e)
    {
        Point p = PointToScreen(e.GetPosition(this));
        return ((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_session is null) return;
        (int x, int y) = ToScreen(e);
        CaptureMouse(); // keep receiving moves while the drag crosses onto another monitor
        _session.OnPointerDown(this, x, y, Keyboard.Modifiers, e.ClickCount);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_session is null) return;
        (int x, int y) = ToScreen(e);
        _session.OnPointerMove(this, x, y, Keyboard.Modifiers, IsOverPanel(e));
    }

    private void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_session is null) return;
        (int x, int y) = ToScreen(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        _session.OnPointerUp(this, x, y, Keyboard.Modifiers);
        e.Handled = true;
    }

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_session is null) return;
        (int x, int y) = ToScreen(e);
        _session.OnRightClick(this, x, y);
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_session is null) return;
        _session.OnWheel(this, e.Delta);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null || e.OriginalSource is TextBox) return; // the text tool is typing
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_session.OnKeyDown(key, Keyboard.Modifiers)) e.Handled = true;
    }

    private bool IsOverPanel(MouseEventArgs e)
    {
        if (PanelLayer.Children.Count == 0) return false;
        return PanelLayer.InputHitTest(e.GetPosition(PanelLayer)) is not null and not Canvas;
    }

    // ------------------------------------------------------------------ rendering

    public void Render(OverlayView view)
    {
        PixelRect? hole = view.Selection ?? view.Highlight;
        _dimHole.Rect = hole is PixelRect h ? ToLocal(h) : Rect.Empty;

        bool cursorHere = Monitor.Bounds.Contains(view.CursorX, view.CursorY);
        bool labelHere = ReferenceEquals(view.LabelMonitor, Monitor) || view.LabelMonitor?.Bounds == Monitor.Bounds;
        double s = Scale;

        Rect? annotationBounds = null;
        Point[]? annotationHandles = null;
        if (view.AnnotationBounds is Rect bounds)
        {
            annotationBounds = new Rect(ToLocalPoint(bounds.TopLeft), ToLocalPoint(bounds.BottomRight));
            annotationHandles = Array.ConvertAll(view.AnnotationHandles ?? Array.Empty<Point>(), ToLocalPoint);
        }

        _chrome.State = new ChromeState(
            s,
            view.ShowCrosshair && cursorHere ? ToLocal(view.CursorX, view.CursorY) : null,
            view.Highlight is PixelRect highlight ? ToLocal(highlight) : null,
            labelHere ? view.HighlightTitle : null,
            labelHere && view.Highlight is PixelRect hs ? $"{hs.Width} × {hs.Height}" : null,
            view.Selection is PixelRect selection ? ToLocal(selection) : null,
            view.ShowHandles,
            view.ActiveHandle,
            labelHere && view.Selection is PixelRect ss ? $"{ss.Width} × {ss.Height}" : null,
            annotationBounds,
            annotationHandles,
            cursorHere ? view.Transient : null,
            cursorHere ? ToLocal(view.CursorX, view.CursorY) : null,
            cursorHere ? view.Hint : null);
        _chrome.InvalidateVisual();
        _dim.Visibility = view.Veil ? Visibility.Visible : Visibility.Hidden;

        Annotations.ClipRect = view.Selection is PixelRect clip ? ToLocal(clip) : Rect.Empty;
        Annotations.PixelsToLocal = new Matrix(1 / s, 0, 0, 1 / s, -Monitor.Bounds.X / s, -Monitor.Bounds.Y / s);
        Annotations.InvalidateVisual();

        bool showMagnifier = _magnifierEnabled && view.ShowMagnifier && cursorHere;
        if (showMagnifier)
        {
            _magnifier.Update(view.CursorX - Monitor.Bounds.X, view.CursorY - Monitor.Bounds.Y, view.CursorX, view.CursorY,
                view.CursorColor, ToLocal(view.CursorX, view.CursorY), RenderSize);
        }

        _magnifier.SetVisible(showMagnifier, _animate);

        if (!ReferenceEquals(Cursor, view.Cursor)) Cursor = view.Cursor;
    }

    // ------------------------------------------------------------------ reveal / animations

    /// <summary>Uncloaks the (already rendered) window and fades the veil in.</summary>
    public void Reveal(bool animate)
    {
        _animate = animate;
        WindowEffects.SetCloaked(_hwnd, false);
        if (animate)
        {
            _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            _dim.Opacity = 1;
        }
    }

    /// <summary>A brief white flash over the captured area — the "shutter".</summary>
    public void Flash(PixelRect region)
    {
        Rect local = ToLocal(region);
        Canvas.SetLeft(_flash, local.X);
        Canvas.SetTop(_flash, local.Y);
        _flash.Width = local.Width;
        _flash.Height = local.Height;
        _flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Hides the chrome and fades the veil out; the session hides the window when its own timer ends.</summary>
    public void AnimateOut(PixelRect? flash, bool animate)
    {
        _chrome.Visibility = Visibility.Hidden;
        PanelLayer.Visibility = Visibility.Hidden;
        _magnifier.SetVisible(false, false);
        if (!animate) return;

        if (flash is PixelRect region && region.IntersectsWith(Monitor.Bounds)) Flash(region);
        _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(_dim.Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        });
    }
}
