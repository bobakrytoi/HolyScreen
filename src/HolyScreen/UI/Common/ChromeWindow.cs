using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HolyScreen.Capture;

namespace HolyScreen.UI.Common;

/// <summary>
/// Base class for the app's dialog windows: borderless, rounded graphite surface with a draggable title bar and a
/// close button, consistent dark theming, and Esc-to-close. Derived windows just set <see cref="Window.Title"/>
/// and <see cref="Window.Content"/>.
/// </summary>
public abstract class ChromeWindow : Window
{
    protected ChromeWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        FontFamily = (FontFamily)Application.Current.FindResource("AppFontFamily");
        Foreground = (Brush)Application.Current.FindResource("PrimaryTextBrush"); // inherited by all text in the window

        Template = (ControlTemplate)Application.Current.FindResource("ChromeWindowTemplate");

        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>When true (default) pressing Esc closes the window.</summary>
    public bool CloseOnEscape { get; set; } = true;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_TitleBar") is UIElement titleBar)
        {
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch (InvalidOperationException) { /* released mid-drag */ }
                }
            };
        }

        if (GetTemplateChild("PART_Close") is Button close)
        {
            close.Click += (_, _) => OnCloseButton();
        }
    }

    protected virtual void OnCloseButton() => Close();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        WindowEffects.UseDarkTitleBar(handle);
        WindowEffects.UseRoundedCorners(handle);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CloseOnEscape && e.Key == Key.Escape && !e.Handled)
        {
            Close();
        }
    }

    /// <summary>Centers the window on the monitor under the cursor once it has its size.</summary>
    public void CenterOnCursorMonitor()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Interop.Win32.POINT cursor = DisplayLayout.GetCursorPosition();
        MonitorInfo monitor = DisplayLayout.FromPoint(DisplayLayout.GetMonitors(), cursor.X, cursor.Y);
        double scale = monitor.Scale;
        double workLeft = monitor.WorkArea.X / scale;
        double workTop = monitor.WorkArea.Y / scale;
        double workWidth = monitor.WorkArea.Width / scale;
        double workHeight = monitor.WorkArea.Height / scale;
        Left = workLeft + workWidth / 4;
        Top = workTop + workHeight / 4;

        Loaded += (_, _) =>
        {
            Left = workLeft + Math.Max(0, (workWidth - ActualWidth) / 2);
            Top = workTop + Math.Max(0, (workHeight - ActualHeight) / 2);
        };
    }
}
