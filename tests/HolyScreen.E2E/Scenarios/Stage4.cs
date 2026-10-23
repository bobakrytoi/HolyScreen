using System;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.E2E.Driver;
using HolyScreen.Interop;
using HolyScreen.Output;

namespace HolyScreen.E2E.Scenarios;

/// <summary>Stage 4: drawing annotations on the live overlay and getting them into the copied image.</summary>
internal static class Stage4
{
    public static void Annotate(E2EContext ctx)
    {
        const string name = "annotate";
        MonitorInfo primary = DisplayLayout.GetMonitors().First(m => m.IsPrimary);
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        uint clipboardBefore = ClipboardReader.Sequence;

        Simulate.MoveTo(ox + 320, oy + 220);
        OverlayDriver.Open(app);
        var selection = new PixelRect(ox + 300, oy + 200, 601, 451);
        Simulate.Drag(ox + 300, oy + 200, ox + 900, oy + 650);
        Thread.Sleep(250);

        // Arrow via the tool panel (mouse), everything else via the keyboard shortcuts.
        Uia.Click(Uia.WaitFor(app, "ToolArrow", TimeSpan.FromSeconds(2)));
        Simulate.Drag(ox + 350, oy + 300, ox + 600, oy + 450);

        Simulate.Press('R');
        Simulate.Drag(ox + 650, oy + 250, ox + 850, oy + 400);

        Simulate.Press('X');
        Simulate.Drag(ox + 400, oy + 500, ox + 548, oy + 596);

        Simulate.Press('T');
        Simulate.Click(ox + 640, oy + 520);
        Thread.Sleep(250);
        Simulate.Type("Привет ✓");
        Simulate.Press(Win32.VK_RETURN);
        Thread.Sleep(150);

        Simulate.Press('N');
        Simulate.Click(ox + 700, oy + 610);
        Simulate.Click(ox + 760, oy + 610);
        Thread.Sleep(150);

        // Undo the second step, redo it.
        Simulate.Chord(Win32.VK_CONTROL, 'Z');
        Thread.Sleep(120);
        CapturedFrame afterUndo = Screen.Grab();
        Simulate.Chord(Win32.VK_CONTROL, 'Y');
        Thread.Sleep(150);
        CapturedFrame during = Screen.Grab();
        ctx.SnapshotRegion(name, "annotated", selection.Inflate(60));

        Color stepColorUndone = afterUndo.GetPixel(ox + 760 + 9, oy + 610);
        Color stepColorRedone = during.GetPixel(ox + 760 + 9, oy + 610);
        ctx.Note($"step 2 fill after undo {Hex(stepColorUndone)}, after redo {Hex(stepColorRedone)}");
        Check.That(IsRed(stepColorRedone) && !IsRed(stepColorUndone), "undo/redo of the second step is not visible");

        Simulate.Chord(Win32.VK_CONTROL, 'C');
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != clipboardBefore && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)),
            "the annotated image was not copied");
        BitmapSource copied = ClipboardReader.ReadPng()!;
        Check.Equal(selection.Width, copied.PixelWidth, "copied width");
        Check.Equal(selection.Height, copied.PixelHeight, "copied height");

        byte[] px = ReadBgra(copied);
        Color At(int x, int y) // screen coordinates → copied image
        {
            int o = ((y - selection.Y) * copied.PixelWidth + (x - selection.X)) * 4;
            return Color.FromRgb(px[o + 2], px[o + 1], px[o]);
        }

        Check.That(IsRed(At(ox + 475, oy + 375)), $"arrow shaft is not red in the copy ({Hex(At(ox + 475, oy + 375))})");
        Check.That(IsRed(At(ox + 650, oy + 325)), $"rectangle edge is not red in the copy ({Hex(At(ox + 650, oy + 325))})");
        Check.That(IsRed(At(ox + 700 + 9, oy + 610)) && IsRed(At(ox + 760 + 9, oy + 610)), "step circles are missing in the copy");

        // Pixelation: every 12×12 block is a single colour, starting at the area's corner.
        for (int by = 0; by < 7; by++)
        {
            for (int bx = 0; bx < 11; bx++)
            {
                int x0 = ox + 400 + bx * 12, y0 = oy + 500 + by * 12;
                Color first = At(x0, y0);
                Check.That(At(x0 + 11, y0) == first && At(x0, y0 + 11) == first && At(x0 + 6, y0 + 7) == first,
                    $"pixelation block ({bx},{by}) is not uniform");
            }
        }

        // Text: red ink near where it was typed.
        int redInText = 0;
        for (int y = oy + 500; y < oy + 545; y++)
        {
            for (int x = ox + 640; x < ox + 760; x++)
            {
                if (IsRed(At(x, y))) redInText++;
            }
        }

        ctx.Note($"{redInText} red text pixels");
        Check.That(redInText > 150, $"typed text is missing from the copy ({redInText} red pixels)");

        // WYSIWYG: the copy matches what the overlay showed (allowing anti-aliasing differences on edges).
        double near = NearMatch(during, copied, selection, margin: 10);
        ctx.Note($"copy vs overlay: {near:P1} of pixels within ±40 per channel");
        Check.That(near > 0.97, $"the copy differs from what the overlay showed ({near:P1})");

        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    public static void EditExisting(E2EContext ctx)
    {
        const string name = "annotate-edit";
        MonitorInfo primary = DisplayLayout.GetMonitors().First(m => m.IsPrimary);
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        uint clipboardBefore = ClipboardReader.Sequence;

        Simulate.MoveTo(ox + 320, oy + 220);
        OverlayDriver.Open(app);
        var selection = new PixelRect(ox + 300, oy + 200, 501, 401);
        Simulate.Drag(ox + 300, oy + 200, ox + 800, oy + 600);
        Thread.Sleep(200);

        Simulate.Press('R');
        Simulate.Drag(ox + 350, oy + 250, ox + 450, oy + 330);   // rectangle #1
        Simulate.Drag(ox + 600, oy + 450, ox + 700, oy + 550);   // rectangle #2 (to be deleted)

        Simulate.RightClick(ox + 550, oy + 400);                 // put the tool down (does not close)
        Thread.Sleep(150);
        Check.That(OverlayDriver.IsOpen(app), "right-click with annotations closed the overlay");

        Simulate.Drag(ox + 350, oy + 290, ox + 410, oy + 340);   // grab #1 by its left edge, move by (60, 50)
        Simulate.Click(ox + 600, oy + 500);                      // select #2
        Simulate.Press(Win32.VK_DELETE);
        Thread.Sleep(150);
        ctx.SnapshotRegion(name, "edited", selection.Inflate(40));

        Simulate.Press(Win32.VK_RETURN);
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != clipboardBefore && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)), "nothing copied");
        BitmapSource copied = ClipboardReader.ReadPng()!;
        byte[] px = ReadBgra(copied);
        Color At(int x, int y)
        {
            int o = ((y - selection.Y) * copied.PixelWidth + (x - selection.X)) * 4;
            return Color.FromRgb(px[o + 2], px[o + 1], px[o]);
        }

        Check.That(IsRed(At(ox + 410, oy + 330)), "moved rectangle is not at its new place");
        Check.That(!IsRed(At(ox + 350, oy + 290)), "moved rectangle is still at its old place");
        Check.That(!IsRed(At(ox + 600, oy + 500)) && !IsRed(At(ox + 650, oy + 450)), "deleted rectangle is still in the copy");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    public static void Palette(E2EContext ctx)
    {
        const string name = "palette";
        MonitorInfo primary = DisplayLayout.GetMonitors().First(m => m.IsPrimary);
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        uint clipboardBefore = ClipboardReader.Sequence;

        Simulate.MoveTo(ox + 320, oy + 220);
        OverlayDriver.Open(app);
        var selection = new PixelRect(ox + 300, oy + 200, 501, 401);
        Simulate.Drag(ox + 300, oy + 200, ox + 800, oy + 600);
        Thread.Sleep(200);

        Uia.Click(Uia.WaitFor(app, "ColorSwatch", TimeSpan.FromSeconds(2)));
        System.Windows.Rect blue = Uia.WaitFor(app, "ColorFF0A84FF", TimeSpan.FromSeconds(2));
        Thread.Sleep(350);
        ctx.SnapshotRegion(name, "palette-open", PixelRect.FromEdges(ox + 300, oy + 300, ox + 1350, oy + 700));
        Uia.Click(blue);

        Simulate.Press('L');
        Simulate.MoveTo(ox + 500, oy + 400);
        Simulate.Wheel(120);
        Simulate.Wheel(120);
        Simulate.Wheel(120);
        Thread.Sleep(80);
        ctx.SnapshotRegion(name, "size-badge", new PixelRect(ox + 440, oy + 350, 220, 140));
        Simulate.Drag(ox + 350, oy + 300, ox + 750, oy + 300);

        Simulate.Press(Win32.VK_RETURN);
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != clipboardBefore && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)), "nothing copied");
        BitmapSource copied = ClipboardReader.ReadPng()!;
        byte[] px = ReadBgra(copied);
        int o = ((300 - 200) * copied.PixelWidth + (550 - 300)) * 4;
        var c = Color.FromRgb(px[o + 2], px[o + 1], px[o]);
        ctx.Note($"line colour in the copy {Hex(c)}");
        Check.That(c.B > 200 && c.R < 60 && c.G is > 100 and < 160, $"the line is not the picked blue ({Hex(c)})");

        // Size 4 + 3 wheel notches = 7 px: the line centred on y=300.0 covers 296.5…303.5, so row 302 is fully
        // blue (a 4 px line would end at 302.0), and row 305 is clear of it.
        int edge = ((302 - 200) * copied.PixelWidth + (550 - 300)) * 4;
        int beyond = ((305 - 200) * copied.PixelWidth + (550 - 300)) * 4;
        Check.That(px[edge] > 200, "the wheel did not make the line thicker");
        Check.That(!(px[beyond] > 200 && px[beyond + 2] < 60), "the line is thicker than the chosen size");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
    }

    private static bool IsRed(Color c) => c.R > 190 && c.G < 110 && c.B < 110;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static byte[] ReadBgra(BitmapSource bitmap)
    {
        BitmapSource bgra = ImageEncoding.Convert(bitmap, PixelFormats.Bgra32);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }

    private static double NearMatch(CapturedFrame screen, BitmapSource image, PixelRect region, int margin)
    {
        byte[] px = ReadBgra(image);
        var random = new Random(7);
        int near = 0, total = 0;
        for (int i = 0; i < 6000; i++)
        {
            int x = margin + random.Next(region.Width - 2 * margin);
            int y = margin + random.Next(region.Height - 2 * margin);
            Color s = screen.GetPixel(region.X + x, region.Y + y);
            int o = (y * image.PixelWidth + x) * 4;
            total++;
            if (Math.Abs(s.B - px[o]) <= 40 && Math.Abs(s.G - px[o + 1]) <= 40 && Math.Abs(s.R - px[o + 2]) <= 40) near++;
        }

        return near / (double)total;
    }
}
