using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.E2E.Driver;
using HolyScreen.Output;

namespace HolyScreen.E2E.Scenarios;

/// <summary>Stage 3: the glass action panel — placement, flipping, Copy / Save / Close by mouse.</summary>
internal static class Stage3
{
    public static void Panels(E2EContext ctx)
    {
        const string name = "panels";
        MonitorInfo primary = DisplayLayout.GetMonitors().First(m => m.IsPrimary);
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        double scale = primary.Scale;
        string shots = Path.Combine(ctx.ScenarioDir(name), "shots");
        using AppProcess app = ctx.Launch(name);

        // 1) Panel under the selection, right-aligned; Copy by mouse.
        uint clipboardBefore = ClipboardReader.Sequence;
        Simulate.MoveTo(ox + 400, oy + 300);
        OverlayDriver.Open(app);
        var selection = new PixelRect(ox + 300, oy + 200, 401, 301);
        Simulate.Drag(ox + 300, oy + 200, ox + 700, oy + 500);
        Rect copy = Uia.WaitFor(app, "Copy", TimeSpan.FromSeconds(2));
        Rect close = Uia.WaitFor(app, "Close", TimeSpan.FromSeconds(1));
        Thread.Sleep(350); // entrance animation
        ctx.SnapshotRegion(name, "1-panel-below", new PixelRect(ox + 260, oy + 160, 560, 440));

        double gap = copy.Top - selection.Bottom;
        double rightEdgeOffset = selection.Right - close.Right;
        ctx.Note($"panel {gap:F0}px below the selection, its right side {rightEdgeOffset:F0}px inside the selection's right edge");
        Check.That(gap > 4 * scale && gap < 24 * scale, $"action panel is not just below the selection (gap {gap:F0}px)");
        Check.That(rightEdgeOffset >= 0 && rightEdgeOffset < 20 * scale, $"action panel is not right-aligned (offset {rightEdgeOffset:F0}px)");

        Uia.Click(copy);
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != clipboardBefore && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)),
            "the Copy button did not copy");
        BitmapSource copied = ClipboardReader.ReadPng()!;
        Check.Equal(selection.Width, copied.PixelWidth, "copied width");
        Check.Equal(selection.Height, copied.PixelHeight, "copied height");

        // 2) Selection at the bottom edge: the panel flips above it; Save by mouse.
        Simulate.MoveTo(ox + 1300, oy + 950);
        OverlayDriver.Open(app);
        var low = PixelRect.FromEdges(ox + 1200, oy + primary.Bounds.Height - 180, ox + 1501, oy + primary.Bounds.Height - 30);
        Simulate.Drag(low.X, low.Y, low.Right - 1, low.Bottom - 1);
        Rect save = Uia.WaitFor(app, "Save", TimeSpan.FromSeconds(2));
        Thread.Sleep(350);
        ctx.SnapshotRegion(name, "2-panel-flipped-above", PixelRect.FromEdges(low.X - 60, low.Y - 120, low.Right + 60, low.Bottom + 20));
        Check.That(save.Bottom <= low.Y, $"panel did not flip above a selection at the bottom edge (panel bottom {save.Bottom}, selection top {low.Y})");

        Uia.Click(save);
        OverlayDriver.WaitClosed(app);
        string? file = Wait.For(() => Directory.Exists(shots) ? Directory.GetFiles(shots, "*.png").FirstOrDefault() : null, TimeSpan.FromSeconds(5));
        Check.That(file is not null, "the Save button did not save");
        BitmapSource saved = ImageEncoding.Decode(File.ReadAllBytes(file!));
        Check.Equal(low.Width, saved.PixelWidth, "saved width");
        Check.Equal(low.Height, saved.PixelHeight, "saved height");

        // 3) Close discards.
        uint clipboardMid = ClipboardReader.Sequence;
        OverlayDriver.Open(app);
        Simulate.Drag(ox + 500, oy + 300, ox + 800, oy + 500);
        Uia.Click(Uia.WaitFor(app, "Close", TimeSpan.FromSeconds(2)));
        OverlayDriver.WaitClosed(app);
        Thread.Sleep(300);
        Check.Equal(clipboardMid, ClipboardReader.Sequence, "clipboard after Close");
        Check.Equal(1, Directory.GetFiles(shots, "*.png").Length, "files after Close");

        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }
}
