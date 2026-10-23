using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.E2E.Driver;
using HolyScreen.Interop;
using HolyScreen.Output;

namespace HolyScreen.E2E.Scenarios;

/// <summary>Stage 5: pin, OCR, colour picker, beautify, history re-edit, repeat region, delayed capture.</summary>
internal static class Stage5
{
    private static MonitorInfo Primary => DisplayLayout.GetMonitors().First(m => m.IsPrimary);

    public static void Pin(E2EContext ctx)
    {
        const string name = "pin";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);

        Simulate.MoveTo(ox + 420, oy + 320);
        OverlayDriver.Open(app);
        var region = new PixelRect(ox + 400, oy + 300, 401, 301);
        Simulate.Drag(region.X, region.Y, region.Right - 1, region.Bottom - 1);
        Uia.Click(Uia.WaitFor(app, "Pin", TimeSpan.FromSeconds(2)));
        OverlayDriver.WaitClosed(app);

        WindowRecord? pin = Wait.For(() => WindowQuery.Showing(app.Id, "HolyScreen Pin").FirstOrDefault(), TimeSpan.FromSeconds(3));
        Check.That(pin is not null, "the pin window did not appear");
        TestWindows.Pump(400);
        pin = WindowQuery.Showing(app.Id, "HolyScreen Pin").First();
        int margin = (int)Math.Round(17 * primary.Scale);
        ctx.Note($"pin window {pin.Bounds}, region {region}");
        Check.That(Math.Abs(pin.Bounds.X + margin - region.X) <= 1 && Math.Abs(pin.Bounds.Y + margin - region.Y) <= 1,
            "the pinned picture is not where the selection was");
        Check.That(Math.Abs(pin.Bounds.Width - 2 * margin - region.Width) <= 2, $"the pinned picture is not 1:1 ({pin.Bounds.Width - 2 * margin} vs {region.Width})");
        ctx.SnapshotRegion(name, "pinned", region.Inflate(40));

        Simulate.DoubleClick(region.X + region.Width / 2, region.Y + region.Height / 2);
        Check.That(Wait.Until(() => WindowQuery.Showing(app.Id, "HolyScreen Pin").Count == 0, TimeSpan.FromSeconds(3)), "double-click did not close the pin");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    public static void Ocr(E2EContext ctx)
    {
        const string name = "ocr";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        var card = new PixelRect(ox + 200, oy + 150, 640, 110);
        Window fixture = TestWindows.ShowText("HolyScreen OCR 4217", card);
        try
        {
            using AppProcess app = ctx.Launch(name, s => s.HotkeyCaptureText = "Ctrl+Alt+Shift+F10");
            uint before = ClipboardReader.Sequence;
            Simulate.MoveTo(card.X + 5, card.Y + 5);
            Simulate.GlobalHotkey(Win32.VK_CONTROL, Win32.VK_MENU, Win32.VK_SHIFT, Win32.VK_F1 + 9);
            Check.That(Wait.Until(() => OverlayDriver.IsOpen(app), TimeSpan.FromSeconds(3)), "the OCR shortcut did not open the overlay");
            TestWindows.Pump(350);
            ctx.SnapshotRegion(name, "hint", PixelRect.FromEdges(ox + 400, oy, ox + 1520, oy + 90));

            Simulate.Drag(card.X + 2, card.Y + 2, card.Right - 3, card.Bottom - 3);
            Check.That(Wait.Until(() => ClipboardReader.Sequence != before && Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT), TimeSpan.FromSeconds(8)),
                "no text was copied");
            string? text = ClipboardReader.ReadText();
            ctx.Note($"OCR copied: '{text}'");
            Check.That(text is not null && text.Contains("4217") && text.Contains("HolyScreen"), $"OCR text is wrong: '{text}'");
            Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        }
        finally
        {
            fixture.Close();
        }
    }

    public static void PickColor(E2EContext ctx)
    {
        const string name = "pick-color";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        var swatches = new PixelRect(ox + 300, oy + 300, 400, 120);
        Window fixture = TestWindows.ShowColors(swatches, Color.FromRgb(0x12, 0xAB, 0x34), Color.FromRgb(0xE0, 0x11, 0x5F));
        try
        {
            using AppProcess app = ctx.Launch(name, s => s.HotkeyPickColor = "Ctrl+Alt+Shift+F8");

            uint before = ClipboardReader.Sequence;
            Simulate.MoveTo(swatches.X + 100, swatches.Y + 60);
            Simulate.GlobalHotkey(Win32.VK_CONTROL, Win32.VK_MENU, Win32.VK_SHIFT, Win32.VK_F1 + 7);
            Check.That(Wait.Until(() => OverlayDriver.IsOpen(app), TimeSpan.FromSeconds(3)), "the colour shortcut did not open the overlay");
            TestWindows.Pump(350);
            ctx.SnapshotRegion(name, "picking", swatches.Inflate(160));
            Simulate.Click(swatches.X + 100, swatches.Y + 60);
            Check.That(Wait.Until(() => ClipboardReader.Sequence != before, TimeSpan.FromSeconds(4)), "no colour was copied");
            Check.Equal("#12AB34", ClipboardReader.ReadText(), "picked colour");

            // Shift+click gives rgb() notation; here via the overlay's C key instead of the shortcut.
            before = ClipboardReader.Sequence;
            OverlayDriver.Open(app);
            Simulate.Press('C');
            TestWindows.Pump(150);
            Simulate.KeyDown(Win32.VK_SHIFT);
            Simulate.Click(swatches.X + 300, swatches.Y + 60);
            Simulate.KeyUp(Win32.VK_SHIFT);
            Check.That(Wait.Until(() => ClipboardReader.Sequence != before, TimeSpan.FromSeconds(4)), "no colour was copied with Shift");
            Check.Equal("rgb(224, 17, 95)", ClipboardReader.ReadText(), "picked colour as RGB");
            Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        }
        finally
        {
            fixture.Close();
        }
    }

    public static void Beautify(E2EContext ctx)
    {
        const string name = "beautify";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        uint before = ClipboardReader.Sequence;

        Simulate.MoveTo(ox + 420, oy + 320);
        OverlayDriver.Open(app);
        Simulate.Drag(ox + 400, oy + 300, ox + 700, oy + 500); // 301 × 201
        Uia.Click(Uia.WaitFor(app, "Beautify", TimeSpan.FromSeconds(2)));
        OverlayDriver.WaitClosed(app);

        Rect copy = Uia.WaitFor(app, "BeautifyCopy", TimeSpan.FromSeconds(4));
        TestWindows.Pump(400);
        WindowRecord? window = WindowQuery.Showing(app.Id, "Beautify").FirstOrDefault();
        if (window is not null) ctx.SnapshotRegion(name, "window", window.Bounds);
        Uia.Click(copy);

        Check.That(Wait.Until(() => ClipboardReader.Sequence != before && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)), "beautified image not copied");
        BitmapSource image = ClipboardReader.ReadPng()!;
        ctx.Note($"beautified {image.PixelWidth}x{image.PixelHeight}");
        Check.Equal(301 + 128, image.PixelWidth, "beautified width (64 px padding each side)");
        Check.Equal(201 + 128, image.PixelHeight, "beautified height");
        Check.That(Wait.Until(() => WindowQuery.Showing(app.Id, "Beautify").Count == 0, TimeSpan.FromSeconds(2)), "Beautify window did not close after Copy");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
    }

    public static void HistoryEdit(E2EContext ctx)
    {
        const string name = "history-edit";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        uint before = ClipboardReader.Sequence;

        // 1) A capture with an annotation, copied (and so remembered).
        Simulate.MoveTo(ox + 420, oy + 320);
        OverlayDriver.Open(app);
        Simulate.Drag(ox + 400, oy + 300, ox + 800, oy + 600);
        Simulate.Press('R');
        Simulate.Drag(ox + 450, oy + 350, ox + 600, oy + 450);
        Simulate.Press(Win32.VK_RETURN);
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != before && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)), "first copy failed");
        BitmapSource first = ClipboardReader.ReadPng()!;
        string historyRoot = Path.Combine(app.DataDir, "history");
        Check.That(Wait.Until(() => Directory.Exists(historyRoot) && Directory.GetFiles(historyRoot, "meta.json", SearchOption.AllDirectories).Length == 1,
            TimeSpan.FromSeconds(5)), "the capture was not added to the history");

        // 2) Open the history (second instance → running app) and choose "Edit again".
        Simulate.MoveTo(ox + 900, oy + 500);
        app.Signal("--history");
        Rect edit = Uia.WaitFor(app, "Edit again", TimeSpan.FromSeconds(4));
        TestWindows.Pump(500);
        WindowRecord? historyWindow = WindowQuery.Showing(app.Id, "History").FirstOrDefault();
        if (historyWindow is not null) ctx.SnapshotRegion(name, "history-window", historyWindow.Bounds);
        Uia.Click(edit);
        Check.That(Wait.Until(() => OverlayDriver.IsOpen(app), TimeSpan.FromSeconds(4)), "Edit again did not reopen the overlay");
        TestWindows.Pump(500);
        ctx.SnapshotRegion(name, "reopened", PixelRect.FromEdges(ox + 340, oy + 240, ox + 900, oy + 680));

        // 3) The reopened capture copies to the same picture, annotation included.
        before = ClipboardReader.Sequence;
        Simulate.Press(Win32.VK_RETURN);
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != before && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)), "second copy failed");
        BitmapSource second = ClipboardReader.ReadPng()!;
        Check.Equal(first.PixelWidth, second.PixelWidth, "re-edited width");
        Check.Equal(first.PixelHeight, second.PixelHeight, "re-edited height");
        double same = SameShare(first, second);
        ctx.Note($"re-edited copy: {same:P1} identical to the original copy");
        Check.That(same > 0.995, $"the re-opened capture differs from the original ({same:P1})");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    public static void RepeatRegion(E2EContext ctx)
    {
        const string name = "repeat-region";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name, s => s.HotkeyRepeatLastRegion = "Ctrl+Alt+Shift+F9");
        uint before = ClipboardReader.Sequence;

        Simulate.MoveTo(ox + 520, oy + 420);
        OverlayDriver.Open(app);
        Simulate.Drag(ox + 500, oy + 400, ox + 699, oy + 549); // 200 × 150
        Simulate.Press(Win32.VK_RETURN);
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != before, TimeSpan.FromSeconds(4)), "first copy failed");

        before = ClipboardReader.Sequence;
        Simulate.GlobalHotkey(Win32.VK_CONTROL, Win32.VK_MENU, Win32.VK_SHIFT, Win32.VK_F1 + 8);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != before && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)), "repeat region did not copy");
        Check.That(!OverlayDriver.IsOpen(app), "repeat region should not open the overlay");
        BitmapSource image = ClipboardReader.ReadPng()!;
        Check.Equal(200, image.PixelWidth, "repeated width");
        Check.Equal(150, image.PixelHeight, "repeated height");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
    }

    public static void Delayed(E2EContext ctx)
    {
        const string name = "delayed";
        using AppProcess app = ctx.Launch(name);
        var started = DateTime.UtcNow;
        app.Signal("--capture-delayed");
        WindowRecord? countdown = Wait.For(() => WindowQuery.Showing(app.Id, "HolyScreen countdown").FirstOrDefault(), TimeSpan.FromSeconds(2));
        Check.That(countdown is not null, "no countdown appeared");
        TestWindows.Pump(300);
        ctx.SnapshotRegion(name, "countdown", countdown!.Bounds);
        Check.That(!OverlayDriver.IsOpen(app), "the overlay opened before the countdown ended");

        Check.That(Wait.Until(() => OverlayDriver.IsOpen(app), TimeSpan.FromSeconds(5)), "the overlay did not open after the countdown");
        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        ctx.Note($"overlay opened {seconds:F1} s after the request");
        Check.That(seconds is > 2.6 and < 4.5, $"the delay was {seconds:F1} s instead of ~3 s");
        Check.That(WindowQuery.Showing(app.Id, "HolyScreen countdown").Count == 0, "the countdown is still on screen during the capture");
        Simulate.Press(Win32.VK_ESCAPE);
        OverlayDriver.WaitClosed(app);
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
    }

    private static double SameShare(BitmapSource a, BitmapSource b)
    {
        byte[] pa = Pixels(a), pb = Pixels(b);
        int same = 0;
        for (int i = 0; i < pa.Length; i += 4)
        {
            if (Math.Abs(pa[i] - pb[i]) <= 2 && Math.Abs(pa[i + 1] - pb[i + 1]) <= 2 && Math.Abs(pa[i + 2] - pb[i + 2]) <= 2) same++;
        }

        return same / (pa.Length / 4.0);
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        BitmapSource bgra = ImageEncoding.Convert(bitmap, PixelFormats.Bgra32);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }

}
