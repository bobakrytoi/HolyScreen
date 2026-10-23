using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.Interop;
using HolyScreen.Output;

namespace HolyScreen.E2E.Driver;

/// <summary>Opens, inspects and closes the capture overlay of a running app.</summary>
internal static class OverlayDriver
{
    public const string Title = "HolyScreen Overlay";

    public static void PressCaptureHotkey() => Simulate.GlobalHotkey(Win32.VK_MENU, Win32.VK_SHIFT, 'S');

    /// <summary>Opens the overlay with Alt+Shift+S and waits until every monitor shows a revealed overlay window.</summary>
    public static List<WindowRecord> Open(AppProcess app)
    {
        int monitors = DisplayLayout.GetMonitors().Count;
        int shownBefore = Regex.Matches(app.ReadLog(), "Overlay visible").Count;
        PressCaptureHotkey();

        List<WindowRecord>? windows = Wait.For(() =>
        {
            List<WindowRecord> showing = WindowQuery.Showing(app.Id, Title);
            return showing.Count == monitors ? showing : null;
        }, TimeSpan.FromSeconds(4));

        Check.That(windows is not null, $"overlay did not appear on all {monitors} monitor(s)");
        Check.That(Wait.Until(() => Regex.Matches(app.ReadLog(), "Overlay visible").Count > shownBefore, TimeSpan.FromSeconds(2)),
            "overlay never reported being revealed");
        Thread.Sleep(220); // let the veil finish fading in before anyone looks at pixels
        return windows!;
    }

    public static void WaitClosed(AppProcess app)
    {
        Check.That(Wait.Until(() => WindowQuery.Showing(app.Id, Title).Count == 0, TimeSpan.FromSeconds(3)), "overlay did not close");
    }

    public static bool IsOpen(AppProcess app) => WindowQuery.Showing(app.Id, Title).Count > 0;

    /// <summary>Last "Overlay visible N ms" figure from the log.</summary>
    public static int? LastRevealMs(AppProcess app)
    {
        MatchCollection matches = Regex.Matches(app.ReadLog(), @"Overlay visible (\d+) ms");
        return matches.Count == 0 ? null : int.Parse(matches[^1].Groups[1].Value);
    }

    /// <summary>Every overlay window must cover exactly its monitor, at that monitor's DPI, topmost.</summary>
    public static void AssertCoversMonitors(IReadOnlyList<WindowRecord> windows)
    {
        foreach (MonitorInfo monitor in DisplayLayout.GetMonitors())
        {
            WindowRecord? window = windows.FirstOrDefault(w => w.Bounds == monitor.Bounds);
            Check.That(window is not null, $"no overlay window exactly covering monitor {monitor.DeviceName} {monitor.Bounds} " +
                $"(windows: {string.Join(", ", windows.Select(w => w.Bounds))})");
            Check.Equal(monitor.Dpi, window!.Dpi, $"overlay DPI on {monitor.DeviceName}");
            Check.That(window.Topmost, "overlay window is not topmost");
        }
    }

    /// <summary>
    /// Share of interior pixels of <paramref name="region"/> that look exactly like <paramref name="reference"/>
    /// on the live screen now — i.e. the overlay shows the frozen frame 1:1 inside the selection.
    /// </summary>
    public static double InteriorMatch(CapturedFrame reference, PixelRect region, int margin = 14)
    {
        CapturedFrame now = Screen.Grab();
        PixelRect inner = PixelRect.FromEdges(region.X + margin, region.Y + margin, region.Right - margin, region.Bottom - margin);
        int same = 0, total = 0;
        for (int y = inner.Y; y < inner.Bottom; y += 3)
        {
            for (int x = inner.X; x < inner.Right; x += 3)
            {
                total++;
                if (reference.GetPixel(x, y) == now.GetPixel(x, y)) same++;
            }
        }

        return total == 0 ? 1 : same / (double)total;
    }

    /// <summary>
    /// Share of pixels in <paramref name="area"/> that now show exactly the veil blended over the reference pixel:
    /// veil·α + original·(1−α) with the overlay's #8A06080D (±3 per channel for rounding).
    /// </summary>
    public static double VeilMatch(CapturedFrame reference, PixelRect area)
    {
        const double alpha = 0x8A / 255.0;
        CapturedFrame now = Screen.Grab();
        int same = 0, total = 0;
        for (int y = area.Y; y < area.Bottom; y += 5)
        {
            for (int x = area.X; x < area.Right; x += 5)
            {
                Color original = reference.GetPixel(x, y);
                Color actual = now.GetPixel(x, y);
                total++;
                if (Near(actual.R, 0x06 * alpha + original.R * (1 - alpha)) &&
                    Near(actual.G, 0x08 * alpha + original.G * (1 - alpha)) &&
                    Near(actual.B, 0x0D * alpha + original.B * (1 - alpha)))
                {
                    same++;
                }
            }
        }

        return total == 0 ? 1 : same / (double)total;
    }

    private static bool Near(byte actual, double expected) => Math.Abs(actual - expected) <= 3;

    /// <summary>Exact-match share between a bitmap and the same region of a reference frame.</summary>
    public static double Match(CapturedFrame reference, BitmapSource image, PixelRect region) =>
        Screen.Similarity(reference, image, region);
}
