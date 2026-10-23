using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using Xunit;

namespace HolyScreen.Tests;

/// <summary>These run against the real desktop of the machine executing the tests.</summary>
public class CaptureTests
{
    [Fact]
    public void CaptureDesktop_CoversTheWholeVirtualScreen_WithOpaquePixels()
    {
        CapturedFrame? frame = ScreenCapture.CaptureDesktop();

        Assert.NotNull(frame);
        Assert.Equal(DisplayLayout.GetVirtualScreenFromMetrics(), frame!.Bounds);
        Assert.Equal(frame.Bounds.Width * frame.Bounds.Height * 4, frame.Pixels.Length);
        Assert.NotEmpty(frame.Monitors);

        for (int i = 3; i < frame.Pixels.Length; i += 4096)
        {
            Assert.Equal(0xFF, frame.Pixels[i]);
        }

        // A real desktop is never a single flat colour.
        Assert.True(Enumerable.Range(0, 2000).Select(i => frame.Pixels[(i * 7919 * 4) % frame.Pixels.Length]).Distinct().Count() > 3);
    }

    [Fact]
    public void Monitors_UnionToTheVirtualScreen_AndHaveSaneDpi()
    {
        var monitors = DisplayLayout.GetMonitors();
        Assert.Equal(DisplayLayout.GetVirtualScreenFromMetrics(), DisplayLayout.GetVirtualScreen(monitors));
        Assert.All(monitors, m => Assert.InRange(m.Dpi, 96u, 480u));
        Assert.Single(monitors, m => m.IsPrimary);
        Assert.Contains(monitors, m => m.Bounds.X == 0 && m.Bounds.Y == 0 && m.IsPrimary);
    }

    [Fact]
    public void CreateBitmap_CopiesTheExactRegion()
    {
        CapturedFrame frame = ScreenCapture.CaptureDesktop()!;
        var region = new PixelRect(frame.Bounds.X + 13, frame.Bounds.Y + 7, 40, 30);

        Sta.Run(() =>
        {
            BitmapSource bitmap = frame.CreateBitmap(region);
            Assert.Equal(40, bitmap.PixelWidth);
            Assert.Equal(30, bitmap.PixelHeight);

            byte[] pixels = TestImages.ReadBgra(bitmap);
            for (int y = 0; y < 30; y += 7)
            {
                for (int x = 0; x < 40; x += 9)
                {
                    Color expected = frame.GetPixel(region.X + x, region.Y + y);
                    int o = (y * 40 + x) * 4;
                    Assert.Equal(expected.B, pixels[o]);
                    Assert.Equal(expected.G, pixels[o + 1]);
                    Assert.Equal(expected.R, pixels[o + 2]);
                }
            }
        });
    }

    [Fact]
    public void CreateBitmap_ClipsRegionsThatStickOutOfTheFrame()
    {
        CapturedFrame frame = ScreenCapture.CaptureDesktop()!;
        var region = new PixelRect(frame.Bounds.Right - 10, frame.Bounds.Bottom - 5, 50, 50);
        Sta.Run(() =>
        {
            BitmapSource bitmap = frame.CreateBitmap(region);
            Assert.Equal(10, bitmap.PixelWidth);
            Assert.Equal(5, bitmap.PixelHeight);
        });
    }

    [Fact]
    public void WindowSnapshot_FindsTheTaskbar_WithVisibleFrameBounds()
    {
        PixelRect screen = DisplayLayout.GetVirtualScreenFromMetrics();
        var windows = WindowFinder.Snapshot(screen);

        Assert.Contains(windows, w => w.ClassName == "Shell_TrayWnd");
        Assert.All(windows, w => Assert.True(screen.Contains(w.Bounds), $"{w.ClassName} {w.Bounds} outside {screen}"));
        Assert.DoesNotContain(windows, w => w.ClassName is "Progman" or "WorkerW");
    }
}
