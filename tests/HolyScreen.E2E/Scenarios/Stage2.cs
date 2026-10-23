using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.E2E.Driver;
using HolyScreen.Interop;
using HolyScreen.Output;

namespace HolyScreen.E2E.Scenarios;

/// <summary>Stage 2: the selection overlay — drag, cross-monitor, window click, keyboard, cancel.</summary>
internal static class Stage2
{
    private static MonitorInfo Primary => DisplayLayout.GetMonitors().First(m => m.IsPrimary);

    private static MonitorInfo? Secondary => DisplayLayout.GetMonitors().FirstOrDefault(m => !m.IsPrimary);

    public static void DragCopy(E2EContext ctx) => DragCopyOn(ctx, "drag-copy", Primary);

    public static void DragCopyOnSecondary(E2EContext ctx)
    {
        if (Secondary is not { } secondary)
        {
            ctx.Note("single monitor — skipped");
            return;
        }

        DragCopyOn(ctx, "drag-copy-secondary", secondary);
    }

    /// <summary>Drag a rectangle on <paramref name="monitor"/>, verify the overlay visuals, Ctrl+C, verify the clipboard.</summary>
    internal static void DragCopyOn(E2EContext ctx, string name, MonitorInfo monitor)
    {
        using AppProcess app = ctx.Launch(name);
        IntPtr foregroundBefore = Win32.GetForegroundWindow();

        int ox = monitor.Bounds.X, oy = monitor.Bounds.Y;
        Simulate.MoveTo(ox + 380, oy + 260);
        CapturedFrame before = Screen.Grab();
        uint clipboardBefore = ClipboardReader.Sequence;

        var windows = OverlayDriver.Open(app);
        OverlayDriver.AssertCoversMonitors(windows);
        Check.Equal(app.Id, WindowQuery.ForegroundProcessId(), "foreground process while the overlay is open");
        ctx.Note($"overlay visible {OverlayDriver.LastRevealMs(app)} ms after the hotkey");
        ctx.SnapshotRegion(name, "1-hover", new PixelRect(ox + 120, oy + 60, 700, 520));

        // Drag 300,200 → 700,500 (monitor-relative): both end pixels are included → 401 × 301.
        var expected = new PixelRect(ox + 300, oy + 200, 401, 301);
        Simulate.Drag(ox + 300, oy + 200, ox + 700, oy + 500);
        Thread.Sleep(120);
        CapturedFrame during = Screen.Grab(); // exactly what the overlay shows now
        ctx.SnapshotRegion(name, "2-selected", new PixelRect(ox + 200, oy + 120, 640, 460));

        // The live screen may have changed a little since `before` (terminal output, clocks): allow some slack there.
        double inside = OverlayDriver.InteriorMatch(before, expected);
        double veil = OverlayDriver.VeilMatch(before, new PixelRect(ox + 900, oy + 600, 300, 200));
        ctx.Note($"inside selection {inside:P1} identical to the pre-capture screen, outside {veil:P1} exactly veiled");
        Check.That(inside > 0.9, $"selection interior is not shown 1:1 ({inside:P1} identical)");
        Check.That(veil > 0.9, $"area outside the selection is not dimmed as expected ({veil:P1} match the veil blend)");

        Simulate.Chord(Win32.VK_CONTROL, 'C');
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != clipboardBefore && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)),
            "nothing was copied to the clipboard");

        BitmapSource? copied = ClipboardReader.ReadPng();
        Check.That(copied is not null, "clipboard PNG could not be decoded");
        Check.Equal(expected.Width, copied!.PixelWidth, "copied width");
        Check.Equal(expected.Height, copied.PixelHeight, "copied height");
        // What you see is what you get: the copy must equal what the overlay displayed (handles excluded).
        double wysiwyg = Screen.Similarity(during, copied, expected, margin: 8);
        double match = OverlayDriver.Match(before, copied, expected);
        ctx.Note($"clipboard image {copied.PixelWidth}x{copied.PixelHeight}: {wysiwyg:P2} identical to the overlay, {match:P1} to the pre-capture screen");
        Check.That(wysiwyg > 0.999, $"copied pixels differ from what the overlay showed ({wysiwyg:P2} identical)");
        Check.That(match > 0.9, $"copied pixels differ from the screen ({match:P1} identical)");

        var dib = ClipboardReader.ReadDibHeader();
        Check.That(dib is { Bits: 24 } && dib.Value.Width == expected.Width && dib.Value.Height == expected.Height,
            $"CF_DIB missing or wrong: {dib}");

        Check.That(Wait.Until(() => Win32.GetForegroundWindow() == foregroundBefore, TimeSpan.FromSeconds(2)),
            "focus did not return to the previously active window");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    public static void CrossMonitorSave(E2EContext ctx) => CrossMonitorSaveNamed(ctx, "cross-monitor-save");

    internal static void CrossMonitorSaveNamed(E2EContext ctx, string name)
    {
        MonitorInfo primary = Primary;
        if (Secondary is not { } secondary || secondary.Bounds.X != primary.Bounds.Right)
        {
            ctx.Note("needs a second monitor directly right of the primary — skipped");
            return;
        }

        using AppProcess app = ctx.Launch(name);
        string shots = Path.Combine(ctx.ScenarioDir(name), "shots");
        int seam = primary.Bounds.Right;
        Simulate.MoveTo(seam - 150, 500);
        CapturedFrame before = Screen.Grab();

        OverlayDriver.Open(app);
        var expected = PixelRect.FromEdges(seam - 120, 600, seam + 131, 801); // drag (seam-120,600) → (seam+130,800)
        Simulate.Drag(seam - 120, 600, seam + 130, 800, steps: 30);
        Thread.Sleep(120);
        ctx.SnapshotRegion(name, "selected-across-seam", new PixelRect(seam - 260, 520, 520, 360));

        Simulate.Chord(Win32.VK_CONTROL, 'S');
        OverlayDriver.WaitClosed(app);
        string? file = Wait.For(() => Directory.Exists(shots) ? Directory.GetFiles(shots, "*.png").FirstOrDefault() : null, TimeSpan.FromSeconds(5));
        Check.That(file is not null, "Ctrl+S did not save a file");

        BitmapSource saved = ImageEncoding.Decode(File.ReadAllBytes(file!));
        Check.Equal(expected.Width, saved.PixelWidth, "saved width");
        Check.Equal(expected.Height, saved.PixelHeight, "saved height");
        double match = OverlayDriver.Match(before, saved, expected);
        ctx.Note($"saved {saved.PixelWidth}x{saved.PixelHeight} across the seam, {match:P1} identical");
        Check.That(match > 0.97, $"saved pixels differ from the screen ({match:P1})");

        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    public static void WindowClick(E2EContext ctx)
    {
        const string name = "window-click";
        PixelRect screen = Screen.VirtualScreen;
        WindowInfo? taskbar = WindowFinder.Snapshot(screen).FirstOrDefault(w => w.ClassName == "Shell_TrayWnd");
        if (taskbar is null)
        {
            ctx.Note("no taskbar window — skipped");
            return;
        }

        using AppProcess app = ctx.Launch(name);
        string shots = Path.Combine(ctx.ScenarioDir(name), "shots");
        int cx = taskbar.Bounds.X + taskbar.Bounds.Width / 2 - 300;
        int cy = taskbar.Bounds.Y + taskbar.Bounds.Height / 2;
        Simulate.MoveTo(cx, cy - 200);

        OverlayDriver.Open(app);
        Simulate.MoveTo(cx, cy);
        Thread.Sleep(150);
        ctx.SnapshotRegion(name, "hover-taskbar", PixelRect.FromEdges(taskbar.Bounds.X, taskbar.Bounds.Y - 120, taskbar.Bounds.X + 900, taskbar.Bounds.Bottom));
        Simulate.Click(cx, cy);
        Thread.Sleep(120);

        Simulate.Chord(Win32.VK_CONTROL, 'S');
        OverlayDriver.WaitClosed(app);
        string? file = Wait.For(() => Directory.Exists(shots) ? Directory.GetFiles(shots, "*.png").FirstOrDefault() : null, TimeSpan.FromSeconds(5));
        Check.That(file is not null, "Ctrl+S after a window click did not save a file");

        BitmapSource saved = ImageEncoding.Decode(File.ReadAllBytes(file!));
        ctx.Note($"clicked taskbar {taskbar.Bounds} → saved {saved.PixelWidth}x{saved.PixelHeight}");
        Check.Equal(taskbar.Bounds.Width, saved.PixelWidth, "window capture width");
        Check.Equal(taskbar.Bounds.Height, saved.PixelHeight, "window capture height");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
    }

    public static void KeyboardNudge(E2EContext ctx)
    {
        const string name = "keyboard-nudge";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        Simulate.MoveTo(ox + 450, oy + 350);
        CapturedFrame before = Screen.Grab();
        uint clipboardBefore = ClipboardReader.Sequence;

        OverlayDriver.Open(app);
        Simulate.Drag(ox + 500, oy + 400, ox + 599, oy + 499);          // 100 × 100 at (500, 400)
        for (int i = 0; i < 3; i++) Simulate.Press(Win32.VK_RIGHT);       // move right 3
        Simulate.Chord(Win32.VK_CONTROL, Win32.VK_RIGHT);                 // grow width 1
        Simulate.Chord(Win32.VK_CONTROL, Win32.VK_RIGHT);                 // grow width 1
        Simulate.Chord(Win32.VK_SHIFT, Win32.VK_DOWN);                    // move down 10
        Simulate.Chord(Win32.VK_CONTROL, Win32.VK_SHIFT, Win32.VK_UP);    // shrink height 10
        Thread.Sleep(100);
        var expected = new PixelRect(ox + 503, oy + 410, 102, 90);

        Simulate.Press(Win32.VK_RETURN);
        OverlayDriver.WaitClosed(app);
        Check.That(Wait.Until(() => ClipboardReader.Sequence != clipboardBefore && ClipboardReader.HasPng, TimeSpan.FromSeconds(4)),
            "Enter did not copy");
        BitmapSource copied = ClipboardReader.ReadPng()!;
        Check.Equal(expected.Width, copied.PixelWidth, "width after keyboard resize");
        Check.Equal(expected.Height, copied.PixelHeight, "height after keyboard resize");
        double match = OverlayDriver.Match(before, copied, expected);
        Check.That(match > 0.97, $"keyboard-moved selection copied the wrong pixels ({match:P1})");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
    }

    public static void CancelPaths(E2EContext ctx)
    {
        const string name = "cancel";
        MonitorInfo primary = Primary;
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        string shots = Path.Combine(ctx.ScenarioDir(name), "shots");
        uint clipboardBefore = ClipboardReader.Sequence;

        // Esc closes straight away.
        Simulate.MoveTo(ox + 600, oy + 400);
        OverlayDriver.Open(app);
        Simulate.Press(Win32.VK_ESCAPE);
        OverlayDriver.WaitClosed(app);

        // Right-click drops the selection first, then closes.
        OverlayDriver.Open(app);
        Simulate.Drag(ox + 300, oy + 300, ox + 500, oy + 450);
        Simulate.RightClick(ox + 420, oy + 380);
        Thread.Sleep(200);
        Check.That(OverlayDriver.IsOpen(app), "first right-click should only drop the selection");
        Simulate.RightClick(ox + 420, oy + 380);
        OverlayDriver.WaitClosed(app);

        // Holding the hotkey's Alt into the overlay must not swallow Esc (system menu loop).
        Simulate.KeyDown(Win32.VK_MENU);
        Simulate.KeyDown(Win32.VK_SHIFT);
        Simulate.KeyDown('S');
        Simulate.KeyUp('S');
        Thread.Sleep(400);
        Simulate.KeyUp(Win32.VK_SHIFT);
        Simulate.KeyUp(Win32.VK_MENU);
        Check.That(Wait.Until(() => OverlayDriver.IsOpen(app), TimeSpan.FromSeconds(3)), "overlay did not open");
        Thread.Sleep(250);
        Simulate.Press(Win32.VK_ESCAPE);
        OverlayDriver.WaitClosed(app);

        Thread.Sleep(300);
        Check.Equal(clipboardBefore, ClipboardReader.Sequence, "clipboard sequence (nothing may be copied)");
        Check.That(!Directory.Exists(shots) || Directory.GetFiles(shots).Length == 0, "a file was saved although every capture was cancelled");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }
}
