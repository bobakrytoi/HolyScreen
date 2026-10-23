using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.E2E.Driver;
using HolyScreen.Interop;

namespace HolyScreen.E2E.Scenarios;

/// <summary>Stage 6: the settings window — recording shortcuts end to end, and that they really work afterwards.</summary>
internal static class Stage6
{
    private const string SettingsTitle = "Settings";
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(3);

    public static void Settings(E2EContext ctx)
    {
        const string name = "settings";
        MonitorInfo primary = DisplayLayout.GetMonitors().First(m => m.IsPrimary);
        using AppProcess app = ctx.Launch(name);
        string settingsFile = Path.Combine(app.DataDir, "settings.json");

        Simulate.MoveTo(primary.Bounds.X + primary.Bounds.Width / 2, primary.Bounds.Y + primary.Bounds.Height / 2);
        app.Signal("--settings");
        WindowRecord? window = Wait.For(() => WindowQuery.Showing(app.Id, SettingsTitle).FirstOrDefault(), TimeSpan.FromSeconds(5));
        Check.That(window is not null, "the settings window did not open");
        TestWindows.Pump(500);
        ctx.SnapshotRegion(name, "general", window!.Bounds);

        Uia.Click(Uia.WaitFor(app, "Shortcuts", Short, SettingsTitle));
        TestWindows.Pump(400);
        Check.Equal("Alt+Shift+S", Uia.NameOf(app, "HotkeyCaptureRegion.Value", SettingsTitle), "capture shortcut shown");
        Check.Equal("Not set", Uia.NameOf(app, "HotkeyPickColor.Value", SettingsTitle), "pick colour shortcut shown");
        ctx.SnapshotRegion(name, "shortcuts", window.Bounds);

        // 1) While a shortcut is being recorded the global shortcuts are off: Alt+Shift+S lands in the recorder.
        Uia.Click(Uia.WaitFor(app, "HotkeyCaptureRegion.Change", Short, SettingsTitle));
        Thread.Sleep(250);
        Simulate.Chord(Win32.VK_MENU, Win32.VK_SHIFT, 'S');
        Check.That(Wait.Until(() => Uia.NameOf(app, "HotkeyCaptureRegion.Value", SettingsTitle) == "Alt+Shift+S", Short),
            $"the recorder did not show Alt+Shift+S (shows '{Uia.NameOf(app, "HotkeyCaptureRegion.Value", SettingsTitle)}')");
        Thread.Sleep(700);
        Check.That(!OverlayDriver.IsOpen(app), "Alt+Shift+S started a capture while it was being recorded");
        Uia.Click(Uia.WaitFor(app, "HotkeyCaptureRegion.Cancel", Short, SettingsTitle));

        // 2) A shortcut another action already uses is refused, with the reason, and nothing changes.
        Uia.Click(Uia.WaitFor(app, "HotkeyPickColor.Change", Short, SettingsTitle));
        Thread.Sleep(250);
        Simulate.Chord(Win32.VK_MENU, Win32.VK_SHIFT, 'S');
        Uia.Click(Uia.WaitFor(app, "HotkeyPickColor.Save", Short, SettingsTitle));
        string? error = null;
        Wait.Until(() => (error = Uia.NameOf(app, "HotkeyPickColor.Error", SettingsTitle)) is not null, Short);
        ctx.Note($"conflict message: '{error}'");
        Check.That(error is not null && error.Contains("Capture region", StringComparison.Ordinal), "no conflict message for a shortcut that is taken");
        Check.Equal("Not set", Uia.NameOf(app, "HotkeyPickColor.Value", SettingsTitle), "pick colour after the refused shortcut");
        ctx.SnapshotRegion(name, "conflict", window.Bounds);

        // 3) A free shortcut is registered and saved.
        Uia.Click(Uia.WaitFor(app, "HotkeyPickColor.Change", Short, SettingsTitle));
        Thread.Sleep(250);
        Simulate.Chord(Win32.VK_CONTROL, Win32.VK_MENU, 'K');
        Uia.Click(Uia.WaitFor(app, "HotkeyPickColor.Save", Short, SettingsTitle));
        Check.That(Wait.Until(() => ReadSettings(settingsFile)?.HotkeyPickColor == "Ctrl+Alt+K", Short),
            $"the new shortcut was not saved (file has '{ReadSettings(settingsFile)?.HotkeyPickColor}')");
        Check.Equal("Ctrl+Alt+K", Uia.NameOf(app, "HotkeyPickColor.Value", SettingsTitle), "pick colour shortcut shown");
        ctx.SnapshotRegion(name, "recorded", window.Bounds);

        // 4) Esc closes the window; the new shortcut opens the colour picker and the capture shortcut still works.
        Simulate.Press(Win32.VK_ESCAPE);
        Check.That(Wait.Until(() => WindowQuery.Showing(app.Id, SettingsTitle).Count == 0, Short), "Esc did not close the settings");

        int shown = Regex.Matches(app.ReadLog(), "Overlay visible").Count;
        Simulate.GlobalHotkey(Win32.VK_CONTROL, Win32.VK_MENU, 'K');
        Check.That(Wait.Until(() => OverlayDriver.IsOpen(app), Short), "Ctrl+Alt+K did not open the overlay");
        Check.That(Wait.Until(() => Regex.Matches(app.ReadLog(), @"Overlay visible .*PickColor").Count > 0
                                    && Regex.Matches(app.ReadLog(), "Overlay visible").Count > shown, Short),
            "Ctrl+Alt+K opened the overlay, but not as the colour picker");
        Simulate.Press(Win32.VK_ESCAPE);
        OverlayDriver.WaitClosed(app);

        OverlayDriver.Open(app); // Alt+Shift+S, which was suspended and restored during the recording
        Simulate.Press(Win32.VK_ESCAPE);
        OverlayDriver.WaitClosed(app);

        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    /// <summary>Eight captures in a row: the memory the app keeps after each one must level off, not keep growing.</summary>
    public static void MemorySteady(E2EContext ctx)
    {
        const string name = "memory";
        MonitorInfo primary = DisplayLayout.GetMonitors().First(m => m.IsPrimary);
        int ox = primary.Bounds.X, oy = primary.Bounds.Y;
        using AppProcess app = ctx.Launch(name);
        var readings = new System.Collections.Generic.List<double>();

        for (int i = 0; i < 8; i++)
        {
            int before = MemoryReadings(app).Count;
            Simulate.MoveTo(ox + 300, oy + 250);
            OverlayDriver.Open(app);
            Simulate.Drag(ox + 300 + i * 20, oy + 250, ox + 900, oy + 650);
            Simulate.Press('R');                                   // one annotation, so the export path runs too
            Simulate.Drag(ox + 600, oy + 300, ox + 750, oy + 400); // inside every selection of the loop
            Simulate.Press(Win32.VK_RETURN);
            OverlayDriver.WaitClosed(app);
            Check.That(Wait.Until(() => MemoryReadings(app).Count > before, TimeSpan.FromSeconds(5)), "no memory reading after the capture");
            readings.Add(MemoryReadings(app)[^1]);
        }

        ctx.Note("private MB after each capture: " + string.Join(", ", readings.Select(r => r.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
        double growth = readings[^1] - readings[1]; // the first capture may still build one-time caches
        Check.That(growth < 25, $"memory keeps growing: +{growth:F1} MB from the 2nd to the 8th capture");
        Check.That(app.Exit(TimeSpan.FromSeconds(8)), "app did not exit");
        Check.That(app.LogErrors().Count == 0, "errors in the app log: " + string.Join(" | ", app.LogErrors()));
    }

    private static System.Collections.Generic.List<double> MemoryReadings(AppProcess app) =>
        Regex.Matches(app.ReadLog(), @"Memory after the overlay closed: .*?private ([\d.]+) MB")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    private static AppSettings? ReadSettings(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return SettingsService.Deserialize(reader.ReadToEnd());
        }
        catch (Exception)
        {
            return null; // being rewritten right now
        }
    }
}
