using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.E2E.Driver;
using HolyScreen.Interop;
using HolyScreen.Output;

namespace HolyScreen.E2E.Scenarios;

/// <summary>Stage 1: tray app, global shortcut and full-screen capture to a file.</summary>
internal static class Stage1
{
    public static void FullScreenCapture(E2EContext ctx)
    {
        const string name = "fullscreen-capture";
        string shots = Path.Combine(ctx.ScenarioDir(name), "shots");

        // The full-screen action gets its own shortcut here so this scenario stays valid once the region
        // shortcut opens the overlay.
        using AppProcess app = ctx.Launch(name, s => s.HotkeyCaptureFullScreen = "Ctrl+Alt+Shift+F11");
        Check.That(app.WaitForLog("Hotkey registered: Capture region = Alt+Shift+S", TimeSpan.FromSeconds(5)),
            "the default Alt+Shift+S shortcut was not registered");
        Check.That(app.WaitForLog("Hotkey registered: Capture full screen = Ctrl+Alt+Shift+F11", TimeSpan.FromSeconds(5)),
            "the configured full-screen shortcut was not registered");

        CapturedFrame before = Screen.Grab();
        Simulate.GlobalHotkey(Win32.VK_CONTROL, Win32.VK_MENU, Win32.VK_SHIFT, Win32.VK_F1 + 10);

        string? file = Wait.For(() => Directory.Exists(shots) ? Directory.GetFiles(shots, "*.png").FirstOrDefault() : null,
            TimeSpan.FromSeconds(8));
        Check.That(file is not null, "no screenshot appeared after the full-screen shortcut");
        Check.That(Regex.IsMatch(Path.GetFileName(file!), @"^Screenshot_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(_\d+)?\.png$"),
            $"unexpected file name {Path.GetFileName(file)}");

        System.Threading.Thread.Sleep(450); // let the "Saved" toast finish its entrance
        ctx.Snapshot(name, "toast-saved");

        BitmapSource saved = ImageEncoding.Decode(File.ReadAllBytes(file!));
        Check.Equal(before.Bounds.Width, saved.PixelWidth, "saved width");
        Check.Equal(before.Bounds.Height, saved.PixelHeight, "saved height");
        double similarity = Screen.Similarity(before, saved, before.Bounds);
        ctx.Note($"saved {saved.PixelWidth}x{saved.PixelHeight}, {similarity:P1} of sampled pixels identical to the live screen");
        Check.That(similarity > 0.95, $"saved image does not match the screen ({similarity:P1} identical)");

        // A second launch forwards its command to the running instance instead of starting again.
        app.Signal("--capture-full");
        Check.That(Wait.Until(() => Directory.GetFiles(shots, "*.png").Length == 2, TimeSpan.FromSeconds(8)),
            "a second instance with --capture-full did not trigger a capture");
        Check.Equal(1, System.Diagnostics.Process.GetProcessesByName("HolyScreen").Length, "running HolyScreen processes");

        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit on --exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }
}
