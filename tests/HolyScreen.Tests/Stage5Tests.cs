using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Editor;
using HolyScreen.UI.Beautify;
using Xunit;

namespace HolyScreen.Tests;

public class HistoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HolyScreenTests", Guid.NewGuid().ToString("N"), "history");
    private AppSettings _settings = new() { HistoryLimit = 3 };

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private HistoryService NewService() => new(_root, () => _settings);

    [Fact]
    public void Add_StoresEverything_AndLoadReturnsNewestFirst()
    {
        Sta.Run(() =>
        {
            HistoryService history = NewService();
            var annotations = new Annotation[] { new ArrowAnnotation { Start = new Point(1, 2), End = new Point(30, 40) } };
            history.Add(TestImages.Opaque(40, 30), TestImages.Opaque(40, 30), new PixelRect(5, 6, 40, 30), annotations, new DateTime(2026, 10, 23, 10, 0, 0), null);
            history.Add(TestImages.Opaque(20, 10), TestImages.Opaque(20, 10), new PixelRect(0, 0, 20, 10), Array.Empty<Annotation>(), new DateTime(2026, 10, 23, 11, 0, 0), @"C:\x.png");

            var entries = history.Load();
            Assert.Equal(2, entries.Count);
            Assert.Equal(new DateTime(2026, 10, 23, 11, 0, 0), entries[0].CapturedAt);
            Assert.Equal(@"C:\x.png", entries[0].SavedPath);
            Assert.Equal(new PixelRect(5, 6, 40, 30), entries[1].Region);
            Assert.True(File.Exists(entries[1].OriginalPath) && File.Exists(entries[1].ThumbnailPath));

            var loaded = history.LoadAnnotations(entries[1]);
            var arrow = Assert.IsType<ArrowAnnotation>(Assert.Single(loaded));
            Assert.Equal(new Point(30, 40), arrow.End);
        });
    }

    [Fact]
    public void Add_TrimsToTheLimit()
    {
        Sta.Run(() =>
        {
            HistoryService history = NewService();
            for (int i = 0; i < 5; i++)
            {
                history.Add(TestImages.Opaque(8, 8), TestImages.Opaque(8, 8), new PixelRect(0, 0, 8, 8), Array.Empty<Annotation>(), new DateTime(2026, 1, 1, 0, 0, i), null);
            }

            var entries = history.Load();
            Assert.Equal(3, entries.Count);
            Assert.Equal(4, entries[0].CapturedAt.Second);
            Assert.Equal(2, entries[^1].CapturedAt.Second);
        });
    }

    [Fact]
    public void Disabled_StoresNothing()
    {
        _settings = new AppSettings { HistoryEnabled = false };
        Sta.Run(() =>
        {
            Assert.Null(NewService().Add(TestImages.Opaque(8, 8), TestImages.Opaque(8, 8), new PixelRect(0, 0, 8, 8), Array.Empty<Annotation>(), DateTime.Now, null));
            Assert.Empty(NewService().Load());
        });
    }

    [Fact]
    public void DeleteAndClear_RemoveFolders_DamagedEntriesAreSkipped()
    {
        Sta.Run(() =>
        {
            HistoryService history = NewService();
            HistoryEntry a = history.Add(TestImages.Opaque(8, 8), TestImages.Opaque(8, 8), new PixelRect(0, 0, 8, 8), Array.Empty<Annotation>(), new DateTime(2026, 1, 1, 0, 0, 1), null)!;
            history.Add(TestImages.Opaque(8, 8), TestImages.Opaque(8, 8), new PixelRect(0, 0, 8, 8), Array.Empty<Annotation>(), new DateTime(2026, 1, 1, 0, 0, 2), null);
            Directory.CreateDirectory(Path.Combine(_root, "garbage"));
            File.WriteAllText(Path.Combine(_root, "garbage", "meta.json"), "{ not json");

            history.Delete(a);
            Assert.Single(history.Load());
            history.Clear();
            Assert.Empty(history.Load());
        });
    }
}

public class BeautifyTests
{
    [Fact]
    public void Output_IsPaddedAndKeepsThePictureExact()
    {
        Sta.Run(() =>
        {
            BitmapSource image = TestImages.Opaque(60, 40);
            BitmapSource result = BeautifyRenderer.Render(image, new BeautifyOptions(Background: 0, Padding: 32, Radius: 8, Shadow: true));
            Assert.Equal(124, result.PixelWidth);
            Assert.Equal(104, result.PixelHeight);

            byte[] source = TestImages.ReadBgra(image);
            byte[] output = TestImages.ReadBgra(result);
            int s = (20 * 60 + 30) * 4;
            int o = ((20 + 32) * 124 + 30 + 32) * 4;
            Assert.Equal(source[s..(s + 3)], output[o..(o + 3)]);

            // The corner shows the gradient's start colour (Sunset #FF7E5F).
            Assert.InRange(output[2], 0xF5, 0xFF);
            Assert.InRange(output[1], 0x76, 0x86);
            Assert.InRange(output[0], 0x57, 0x67);
        });
    }

    [Fact]
    public void TransparentBackground_RoundsTheCornersToAlpha()
    {
        Sta.Run(() =>
        {
            int transparent = Array.FindIndex(BeautifyRenderer.Backgrounds, b => b.From is null);
            BitmapSource result = BeautifyRenderer.Render(TestImages.Opaque(60, 40), new BeautifyOptions(transparent, 0, 12, Shadow: false));
            byte[] output = TestImages.ReadBgra(result);
            Assert.Equal(0, output[3]);                    // the very corner is cut away
            Assert.Equal(0xFF, output[(20 * 60 + 30) * 4 + 3]); // the middle is solid
        });
    }
}

public class FramePasteTests
{
    [Fact]
    public void Paste_WritesPixels_ClippedToTheFrame()
    {
        Sta.Run(() =>
        {
            CapturedFrame frame = TestFrames.Gradient(100, 80);
            BitmapSource patch = TestImages.WithAlpha(30, 20, alpha: 0x80);
            frame.Paste(patch, 80, 70);

            Color inside = frame.GetPixel(85, 75);
            Assert.Equal(Color.FromRgb(30, 20, 10), inside);
            Assert.Equal(0xFF, frame.Pixels[((75 * 100) + 85) * 4 + 3]); // forced opaque
            Assert.NotEqual(Color.FromRgb(30, 20, 10), frame.GetPixel(79, 75));
        });
    }

    [Fact]
    public void PlaceOnScreen_KeepsVisibleRegions_AndCentresLostOnes()
    {
        var monitors = new[]
        {
            new MonitorInfo(IntPtr.Zero, "A", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, true),
        };
        Assert.Equal(new PixelRect(100, 100, 300, 200), CaptureController.PlaceOnScreen(new PixelRect(100, 100, 300, 200), monitors));
        Assert.Equal(new PixelRect(810, 440, 300, 200), CaptureController.PlaceOnScreen(new PixelRect(2500, 100, 300, 200), monitors));
    }
}
