using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HolyScreen.Capture;
using HolyScreen.Interop;

namespace HolyScreen.E2E.Driver;

/// <summary>Known content put on screen by the test runner itself (text to OCR, colours to pick).</summary>
internal static class TestWindows
{
    public static Window ShowText(string text, PixelRect at, double fontSize = 40)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            Foreground = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 24, 0),
        };
        return Show(new Border { Background = Brushes.White, Child = block }, at);
    }

    public static Window ShowColors(PixelRect at, params Color[] colors)
    {
        var row = new UniformGrid1(colors.Length);
        foreach (Color color in colors) row.Children.Add(new Border { Background = new SolidColorBrush(color) });
        return Show(row, at);
    }

    private static Window Show(UIElement content, PixelRect at)
    {
        var window = new Window
        {
            Title = "HolyScreen E2E fixture",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Content = content,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 100,
            Height = 100,
        };
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, at.X, at.Y, at.Width, at.Height, Win32.SWP_NOACTIVATE);
        };
        window.Show();
        Pump(300);
        return window;
    }

    /// <summary>Keeps this (STA) thread's windows responsive while waiting.</summary>
    public static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
        while (DateTime.UtcNow < until);
    }

    /// <summary>A minimal one-row uniform grid (System.Windows.Controls.Primitives.UniformGrid needs no rows set).</summary>
    private sealed class UniformGrid1 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid1(int columns)
        {
            Rows = 1;
            Columns = columns;
        }
    }
}
