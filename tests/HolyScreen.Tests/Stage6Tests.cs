using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HolyScreen.Core;
using HolyScreen.Input;
using HolyScreen.Interop;
using HolyScreen.UI.Common;
using HolyScreen.UI.Settings;
using Xunit;

namespace HolyScreen.Tests;

public class ShutterSoundTests
{
    [Fact]
    public void CreateWav_IsAValidMonoPcmFile()
    {
        byte[] wav = ShutterSound.CreateWav(1.0);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));      // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));      // mono
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));
        Assert.Equal(88200, BitConverter.ToInt32(wav, 28));
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
        Assert.InRange((wav.Length - 44) / 2 / 44100.0, 0.12, 0.14);
    }

    [Fact]
    public void CreateWav_PeakFollowsVolume_AndIsDeterministic()
    {
        static int Peak(byte[] wav)
        {
            int peak = 0;
            for (int i = 44; i < wav.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));
            return peak;
        }

        int full = Peak(ShutterSound.CreateWav(1.0));
        int half = Peak(ShutterSound.CreateWav(0.5));
        Assert.InRange(full, (int)(0.65 * short.MaxValue) - 2, (int)(0.65 * short.MaxValue) + 2); // headroom, never clips
        Assert.InRange(half, full / 2 - 2, full / 2 + 2);
        Assert.Equal(0, Peak(ShutterSound.CreateWav(0)));
        Assert.Equal(ShutterSound.CreateWav(0.8), ShutterSound.CreateWav(0.8));
    }
}

public class ButtonLabelTests
{
    // The app-wide implicit TextBlock style paints text white. Inside a button the label must take the button's own
    // Foreground instead, or the primary button ends up white-on-light-blue and the danger button white, not red.
    [Theory]
    [InlineData(null)]
    [InlineData("GhostButton")]
    [InlineData("PrimaryButton")]
    [InlineData("DangerButton")]
    public void LabelUsesTheButtonForeground(string? styleKey)
    {
        UiThread.Run(() =>
        {
            var button = new Button { Content = "Label" };
            if (styleKey is not null) button.Style = (Style)Application.Current.FindResource(styleKey);
            button.Measure(new Size(200, 60));
            button.Arrange(new Rect(0, 0, 200, 60));
            button.UpdateLayout();

            TextBlock label = Descendants(button).OfType<TextBlock>().Single(t => t.Text == "Label");
            Assert.Equal(((SolidColorBrush)button.Foreground).Color, ((SolidColorBrush)label.Foreground).Color);
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject deeper in Descendants(child)) yield return deeper;
        }
    }
}

public class CloseAfterCommandTests
{
    [Theory]
    [InlineData(true, false, false)]  // default: Copy closes the editor…
    [InlineData(true, true, true)]    // …Shift+Copy keeps it open
    [InlineData(false, false, true)]  // "close after copy" off: Copy keeps it open…
    [InlineData(false, true, false)]  // …and Shift+Copy closes it
    public void ShiftInvertsTheSetting(bool closeByDefault, bool shift, bool keepsOpen) =>
        Assert.Equal(keepsOpen, HolyScreen.UI.Overlay.OverlaySession.KeepsEditorOpen(closeByDefault, shift));
}

public class HotkeySuspendTests
{
    // An unusual combination nobody else registers; the test owns it only for a moment.
    private static readonly HotkeyGesture Probe = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x86 /* F23 */);

    private static bool IsFree(HotkeyGesture gesture)
    {
        const int id = 0xBEEF;
        if (!Win32.RegisterHotKey(IntPtr.Zero, id, gesture.ToNativeModifiers() | Win32.MOD_NOREPEAT, (uint)gesture.Key)) return false;
        Win32.UnregisterHotKey(IntPtr.Zero, id);
        return true;
    }

    [Fact]
    public void SetSuspended_ReleasesAndRestoresTheShortcut_AndRepeatedCallsAreHarmless()
    {
        UiThread.Run(() =>
        {
            using var window = new MessageWindow();
            using var hotkeys = new HotkeyManager(window);
            Assert.True(IsFree(Probe), "precondition: the probe combination must be free");

            Assert.True(hotkeys.Register(HotkeyAction.PickColor, Probe).Success);
            Assert.False(IsFree(Probe));

            hotkeys.SetSuspended(true);
            Assert.True(IsFree(Probe));            // the keys reach the recorder instead of firing the action
            hotkeys.SetSuspended(true);
            Assert.True(IsFree(Probe));
            Assert.Equal(Probe, hotkeys.GetCurrent(HotkeyAction.PickColor)); // still remembered

            hotkeys.SetSuspended(false);
            Assert.False(IsFree(Probe));
            hotkeys.SetSuspended(false);           // must not unregister or double-register
            Assert.False(IsFree(Probe));

            hotkeys.Dispose();
            Assert.True(IsFree(Probe));
        });
    }
}

public sealed class SettingsWindowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HolyScreenTests", Guid.NewGuid().ToString("N"));

    public SettingsWindowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeHost
    {
        public readonly List<bool> Suspends = new();
        public readonly List<(HotkeyAction Action, HotkeyGesture? Gesture)> Applied = new();
        public Func<HotkeyAction, HotkeyGesture?, string?> Result = (_, _) => null;
        public int Cleared;

        public SettingsHost ToHost() => new()
        {
            ApplyHotkey = (action, gesture) =>
            {
                Applied.Add((action, gesture));
                return Result(action, gesture);
            },
            CurrentHotkey = action => action == HotkeyAction.CaptureRegion ? HotkeyGesture.Default : null,
            SuspendHotkeys = Suspends.Add,
            ClearHistory = () => Cleared++,
            OpenHistoryFolder = () => { },
        };
    }

    private SettingsService NewSettings()
    {
        SettingsService service = SettingsService.Load(Path.Combine(_dir, "settings.json"));
        service.Update(s => s.SaveFolder = _dir); // keep the known-folder lookup (and its folder creation) out of it
        return service;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject deeper in Descendants(node)) yield return deeper;
        }
    }

    private static T Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject =>
        Descendants(root).OfType<T>().First(x => match?.Invoke(x) ?? true);

    /// <summary>The row whose label is <paramref name="label"/> (rows are two-column grids: label stack + control).</summary>
    private static Grid Row(DependencyObject root, string label) =>
        Descendants(root).OfType<Grid>().First(g => Descendants(g).OfType<TextBlock>().Any(t => t.Text == label));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static Button ButtonIn(DependencyObject root, string text) => Find<Button>(root, b => b.Content as string == text);

    private static TabItem Tab(SettingsWindow window, string header) =>
        ((TabControl)window.Content).Items.Cast<TabItem>().First(t => (string)t.Header == header);

    [Fact]
    public void BuildsEveryTab_WithTheThemeResources()
    {
        UiThread.Run(() =>
        {
            var window = new SettingsWindow(NewSettings(), new FakeHost().ToHost());
            var tabs = (TabControl)window.Content;

            Assert.Equal(new[] { "General", "Shortcuts", "Capture", "Output", "Annotations", "About" },
                tabs.Items.Cast<TabItem>().Select(t => (string)t.Header));
            Assert.All(tabs.Items.Cast<TabItem>(), t => Assert.IsType<ScrollViewer>(t.Content)); // not the error fallback

            HotkeyRecorder[] recorders = Descendants(Tab(window, "Shortcuts")).OfType<HotkeyRecorder>().ToArray();
            Assert.Equal(5, recorders.Length);
            Assert.Equal(HotkeyGesture.Default, recorders[0].Current);
            Assert.All(recorders.Skip(1), r => Assert.Null(r.Current));
            window.Close();
        });
    }

    [Fact]
    public void Recording_KeepsShortcutsSuspended_UntilTheLastRecorderStops()
    {
        UiThread.Run(() =>
        {
            var fake = new FakeHost();
            var window = new SettingsWindow(NewSettings(), fake.ToHost());
            HotkeyRecorder[] recorders = Descendants(Tab(window, "Shortcuts")).OfType<HotkeyRecorder>().ToArray();
            HotkeyRecorder a = recorders[1], b = recorders[2];

            Click(ButtonIn(a, "Change"));
            Assert.Equal(new[] { true }, fake.Suspends);
            Click(ButtonIn(b, "Change"));           // the second recorder starts before the first reports it stopped
            Click(ButtonIn(a, "Cancel"));
            Assert.Equal(new[] { true }, fake.Suspends);
            Click(ButtonIn(b, "Cancel"));
            Assert.Equal(new[] { true, false }, fake.Suspends);

            Click(ButtonIn(a, "Change"));
            window.Close();                          // closing mid-recording must switch the shortcuts back on
            Assert.Equal(new[] { true, false, true, false }, fake.Suspends);
        });
    }

    [Fact]
    public void ResetAndClear_GoThroughTheHost_AndErrorsAreShownInline()
    {
        UiThread.Run(() =>
        {
            var fake = new FakeHost();
            var window = new SettingsWindow(NewSettings(), fake.ToHost());
            HotkeyRecorder[] recorders = Descendants(Tab(window, "Shortcuts")).OfType<HotkeyRecorder>().ToArray();

            Click(ButtonIn(recorders[0], "Reset"));
            Assert.Equal((HotkeyAction.CaptureRegion, (HotkeyGesture?)HotkeyGesture.Default), fake.Applied[^1]);

            recorders[4].Current = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'R');
            fake.Result = (_, _) => "Nope, not today.";
            Click(ButtonIn(recorders[4], "Clear"));
            Assert.Equal((HotkeyAction.RepeatLastRegion, (HotkeyGesture?)null), fake.Applied[^1]);
            Assert.NotNull(recorders[4].Current);    // a failed clear keeps the working shortcut
            Assert.Contains(Descendants(recorders[4]).OfType<TextBlock>(), t => t.Text == "Nope, not today." && t.Visibility == Visibility.Visible);

            fake.Result = (_, _) => null;
            Click(ButtonIn(recorders[4], "Clear"));
            Assert.Null(recorders[4].Current);
            window.Close();
        });
    }

    [Fact]
    public void Controls_WriteTheSettings_AndThePreviewFollows()
    {
        UiThread.Run(() =>
        {
            SettingsService settings = NewSettings();
            var fake = new FakeHost();
            var window = new SettingsWindow(settings, fake.ToHost());

            Find<CheckBox>(Row(Tab(window, "Capture"), "Close the editor after Copy")).IsChecked = false;
            Assert.False(settings.Current.CloseAfterCopy);
            Find<CheckBox>(Row(Tab(window, "General"), "Shutter sound")).IsChecked = true;
            Assert.True(settings.Current.CaptureSound);
            Find<Slider>(Row(Tab(window, "General"), "Sound volume")).Value = 30;
            Assert.Equal(0.3, settings.Current.SoundVolume, 3);
            Find<Slider>(Row(Tab(window, "Annotations"), "Stroke size")).Value = 7;
            Assert.Equal(7, settings.Current.AnnotationThickness);

            TabItem output = Tab(window, "Output");
            Find<TextBox>(output).Text = "Shot_{yyyy}";
            TextBlock preview = Find<TextBlock>(output, t => t.Text.StartsWith("→ ", StringComparison.Ordinal));
            Assert.Equal("Shot_{yyyy}", settings.Current.FileNameTemplate);
            Assert.Equal($"→ Shot_{DateTime.Now.Year}.png", preview.Text);

            Find<ComboBox>(Row(output, "File format")).SelectedIndex = 1;
            Assert.Equal(ImageFileFormat.Jpeg, settings.Current.SaveFormat);
            Assert.EndsWith(".jpg", preview.Text);

            // The annotation colour buttons store the colour as #RRGGBB.
            Click(Descendants(Tab(window, "Annotations")).OfType<Button>().ElementAt(2));
            Assert.Matches("^#[0-9A-F]{6}$", settings.Current.AnnotationColor);

            window.Close();

            settings.Flush();
            AppSettings reloaded = SettingsService.Load(Path.Combine(_dir, "settings.json")).Current;
            Assert.Equal("Shot_{yyyy}", reloaded.FileNameTemplate);
            Assert.Equal(ImageFileFormat.Jpeg, reloaded.SaveFormat);
            Assert.False(reloaded.CloseAfterCopy);
            Assert.True(reloaded.CaptureSound);
        });
    }
}
