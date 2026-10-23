using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Editor;
using HolyScreen.Interop;
using HolyScreen.Output;
using HolyScreen.UI.Beautify;
using HolyScreen.UI.Common;
using HolyScreen.UI.Overlay;
using HolyScreen.UI.Pin;
using HolyScreen.UI.Toast;

namespace HolyScreen;

/// <summary>
/// Runs captures: freezes the desktop, opens the overlay session, and performs what the user asked for (copy,
/// save, pin, OCR, beautify, pick colour) — mostly after the overlay has gone, so the work never stalls an
/// animation and focus is already back in the user's app. Also keeps the capture history.
/// </summary>
internal sealed class CaptureController : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ToastService _toast;
    private readonly MessageWindow _messageWindow;
    private readonly HistoryService _history;
    private readonly PinManager _pins;
    private readonly OverlayWindowPool _pool = new();
    private OverlaySession? _session;
    private OverlaySession? _warmUp;
    private CountdownWindow? _countdown;
    private PendingAction? _pending;
    private bool _displaysChanged;

    private sealed record PendingAction(OverlayCommand Command, BitmapSource Image, BitmapSource Original, PixelRect Region,
        IReadOnlyList<Annotation> Annotations, DateTime CapturedAt, Color? Color = null, bool Rgb = false);

    public CaptureController(SettingsService settings, ToastService toast, MessageWindow messageWindow, HistoryService history, PinManager pins)
    {
        _settings = settings;
        _toast = toast;
        _messageWindow = messageWindow;
        _history = history;
        _pins = pins;
    }

    public bool IsOverlayOpen => _session is not null;

    // ------------------------------------------------------------------ warm-up / displays

    /// <summary>
    /// Runs one invisible capture after startup so the first real hotkey press does not pay for JIT compilation,
    /// render-thread start-up and window creation.
    /// </summary>
    public void WarmUp()
    {
        if (_session is not null || _warmUp is not null) return;
        try
        {
            var timer = Stopwatch.StartNew();
            CapturedFrame? frame = ScreenCapture.CaptureDesktop();
            if (frame is null) return;

            var session = new OverlaySession(frame, _settings.Current.Clone(), _pool, IntPtr.Zero, animate: true, timer, warmUp: true);
            session.Closed += closed =>
            {
                _warmUp = null;
                OnSessionClosed(closed);
            };
            _warmUp = session;
            session.Open();
        }
        catch (Exception ex)
        {
            Log.Warn("Overlay warm-up failed (the first capture will just be slower)", ex);
            _warmUp?.ForceClose();
            _warmUp = null;
        }
    }

    /// <summary>Monitors were added, removed, moved or rescaled: drop pooled windows built for the old layout.</summary>
    public void OnDisplayChanged()
    {
        if (_session is null && _warmUp is null) _pool.Clear();
        else _displaysChanged = true;
    }

    // ------------------------------------------------------------------ captures

    public void StartRegionCapture(OverlayIntent intent = OverlayIntent.Capture) => OpenOverlay(intent, null);

    /// <summary>"Capture in N seconds": a countdown pill, then a normal region capture (menus left open stay in the shot).</summary>
    public void StartDelayedCapture(int seconds)
    {
        if (_session is not null) return;
        _countdown?.Cancel();
        _countdown = new CountdownWindow(seconds, () =>
        {
            _countdown = null;
            StartRegionCapture();
        });
        _countdown.Start();
    }

    /// <summary>All monitors at once: saved to the screenshots folder and copied to the clipboard.</summary>
    public void CaptureFullScreen()
    {
        if (_session is not null) return;
        _warmUp?.ForceClose();
        _toast.DismissCurrent();

        CapturedFrame? frame = ScreenCapture.CaptureDesktop();
        if (frame is null)
        {
            _toast.Show("The screen could not be captured", ToastKind.Warning);
            return;
        }

        PlayShutter();
        BitmapSource image = frame.CreateBitmap(frame.Bounds);
        string? path = TrySave(image, frame.CapturedAt);
        bool copied = ClipboardImage.TrySet(image, _messageWindow.Handle, out _);
        RecordHistory(image, image, frame.Bounds, Array.Empty<Annotation>(), frame.CapturedAt, path);
        if (path is not null)
        {
            _toast.Show(copied ? $"Saved and copied · {Path.GetFileName(path)}" : $"Saved {Path.GetFileName(path)}",
                ToastKind.Success, 3200, () => ShellActions.ShowInFolder(path));
        }
        else if (copied)
        {
            _toast.Show("Copied to clipboard (saving failed)", ToastKind.Warning);
        }
    }

    /// <summary>Captures the last confirmed region again, straight to the clipboard (no overlay).</summary>
    public void RepeatLastRegion()
    {
        if (_session is not null) return;
        if (PixelRect.FromArray(_settings.Current.LastRegion) is not PixelRect last)
        {
            _toast.Show("No previous region yet — make a selection first", ToastKind.Info);
            return;
        }

        _warmUp?.ForceClose();
        _toast.DismissCurrent();
        CapturedFrame? frame = ScreenCapture.CaptureDesktop();
        PixelRect region = frame is null ? PixelRect.Empty : last.Intersect(frame.Bounds);
        if (frame is null || region.IsEmpty)
        {
            _toast.Show("The last region is not on screen any more", ToastKind.Warning);
            return;
        }

        BitmapSource image = frame.CreateBitmap(region);
        if (ClipboardImage.TrySet(image, _messageWindow.Handle, out string? error))
        {
            PlayShutter();
            RecordHistory(image, image, region, Array.Empty<Annotation>(), frame.CapturedAt, null);
            _toast.Show($"Copied the last region ({region.Width} × {region.Height})");
        }
        else
        {
            _toast.Show(error ?? "The image could not be copied", ToastKind.Warning);
        }
    }

    private void OpenOverlay(OverlayIntent intent, Action<OverlaySession, CapturedFrame>? prepare)
    {
        if (_session is not null) return; // already selecting
        _warmUp?.ForceClose();
        _countdown?.Cancel();
        _countdown = null;

        var sinceHotkey = Stopwatch.StartNew();
        IntPtr previousForeground = Win32.GetForegroundWindow();
        _toast.DismissCurrent(); // never freeze our own toast into the screenshot

        CapturedFrame? frame = ScreenCapture.CaptureDesktop();
        if (frame is null)
        {
            _toast.Show("The screen could not be captured", ToastKind.Warning);
            return;
        }

        var session = new OverlaySession(frame, _settings.Current.Clone(), _pool, previousForeground,
            Motion.Enabled(_settings.Current), sinceHotkey, intent: intent);
        session.ExtraCommands.UnionWith(new[] { OverlayCommand.Pin, OverlayCommand.Ocr, OverlayCommand.Beautify });
        session.CommandRequested += OnCommandRequested;
        session.Closed += OnSessionClosed;
        prepare?.Invoke(session, frame);
        _session = session;

        try
        {
            session.Open();
        }
        catch (Exception ex)
        {
            Log.Error("The capture overlay could not be opened", ex);
            _session = null;
            session.ForceClose();
            _toast.Show("The capture overlay could not be opened", ToastKind.Warning);
        }
    }

    // ------------------------------------------------------------------ overlay results

    private void OnCommandRequested(OverlaySession session, OverlayCommand command, bool keepOpen)
    {
        if (command == OverlayCommand.PickColor)
        {
            if (session.PickedColor is Color color)
            {
                _pending = new PendingAction(command, null!, null!, PixelRect.Empty, Array.Empty<Annotation>(), session.Frame.CapturedAt,
                    color, session.PickedAsRgb);
            }

            return;
        }

        if (session.Selection is not PixelRect region) return;
        _settings.Update(s => s.LastRegion = region.ToArray());
        if (command is OverlayCommand.Copy or OverlayCommand.Save or OverlayCommand.Pin) PlayShutter();

        BitmapSource original = session.Frame.CreateBitmap(region);
        BitmapSource image = command == OverlayCommand.Ocr ? original : session.RenderResult();
        IReadOnlyList<Annotation> annotations = session.Editor?.Document.Items.Select(a => a.Clone()).ToList() ?? new List<Annotation>();
        var action = new PendingAction(command, image, original, region, annotations, session.Frame.CapturedAt);

        if (keepOpen) Perform(action);
        else _pending = action;
    }

    private void OnSessionClosed(OverlaySession session)
    {
        if (ReferenceEquals(_session, session)) _session = null;
        if (_displaysChanged)
        {
            _displaysChanged = false;
            _pool.Clear();
        }

        if (_pending is { } pending)
        {
            _pending = null;
            Perform(pending);
        }

        // The frozen frame and per-monitor bitmaps are tens of MB: release them now rather than whenever the GC next
        // feels like it, and let the GC hand the freed memory back to Windows (Aggressive also decommits), so the
        // tray app stays small between captures. The heap is tiny by then, so this takes a few milliseconds.
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Log.Info("Memory after the overlay closed: " + SystemInfo.DescribeMemory());
        }), DispatcherPriority.ApplicationIdle);
    }

    private void PlayShutter()
    {
        if (_settings.Current.CaptureSound) ShutterSound.Play(_settings.Current.SoundVolume);
    }

    private void Perform(PendingAction action)
    {
        switch (action.Command)
        {
            case OverlayCommand.Copy:
                if (CopyImage(action.Image)) RecordHistory(action, savedPath: null);
                break;

            case OverlayCommand.Save:
                if (SaveImage(action.Image, action.CapturedAt) is { } path) RecordHistory(action, path);
                break;

            case OverlayCommand.Pin:
                _pins.Open(action.Image, action.Region, CopyImage, image => SaveImage(image, DateTime.Now) is not null);
                RecordHistory(action, savedPath: null);
                break;

            case OverlayCommand.Beautify:
                OpenBeautify(action.Image);
                RecordHistory(action, savedPath: null);
                break;

            case OverlayCommand.Ocr:
                _ = RecognizeAsync(action.Original);
                break;

            case OverlayCommand.PickColor when action.Color is Color color:
                string text = action.Rgb ? $"rgb({color.R}, {color.G}, {color.B})" : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                if (ClipboardText.TrySet(text, _messageWindow.Handle, out string? error)) _toast.Show($"Copied {text}", ToastKind.Success, 2400, swatch: color);
                else _toast.Show(error ?? "The colour could not be copied", ToastKind.Warning);
                break;
        }
    }

    private async Task RecognizeAsync(BitmapSource image)
    {
        _toast.Show("Reading text…", ToastKind.Info, 6000);
        try
        {
            OcrText? text = await OcrService.RecognizeAsync(image);
            if (text is null)
            {
                _toast.Show("No text found in the selection", ToastKind.Warning);
                return;
            }

            if (ClipboardText.TrySet(text.Text, _messageWindow.Handle, out string? error))
            {
                Log.Info($"OCR ({text.Language}) copied {text.Lines} line(s).");
                _toast.Show($"Copied text · {OcrService.Describe(text)}");
            }
            else
            {
                _toast.Show(error ?? "The text could not be copied", ToastKind.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Text recognition failed", ex);
            _toast.Show(OcrService.AvailableLanguages.Count == 0
                ? "Windows has no OCR language installed (Settings › Time & language)"
                : "Text recognition failed", ToastKind.Warning);
        }
    }

    // ------------------------------------------------------------------ shared actions

    public bool CopyImage(BitmapSource image)
    {
        if (ClipboardImage.TrySet(image, _messageWindow.Handle, out string? error))
        {
            Log.Info($"Copied {image.PixelWidth}x{image.PixelHeight} to the clipboard.");
            _toast.Show("Copied to clipboard");
            return true;
        }

        _toast.Show(error ?? "The image could not be copied", ToastKind.Warning);
        return false;
    }

    public string? SaveImage(BitmapSource image, DateTime capturedAt)
    {
        if (TrySave(image, capturedAt) is not { } path) return null;
        _toast.Show($"Saved {Path.GetFileName(path)}", ToastKind.Success, 3200, () => ShellActions.ShowInFolder(path));
        return path;
    }

    private void OpenBeautify(BitmapSource image)
    {
        AppSettings s = _settings.Current;
        var window = new BeautifyWindow(image, new BeautifyOptions(s.BeautifyBackground, s.BeautifyPadding, s.BeautifyRadius, s.BeautifyShadow),
            options => _settings.Update(x =>
            {
                x.BeautifyBackground = options.Background;
                x.BeautifyPadding = options.Padding;
                x.BeautifyRadius = options.Radius;
                x.BeautifyShadow = options.Shadow;
            }),
            CopyImage,
            result => SaveImage(result, DateTime.Now) is not null);
        window.CenterOnCursorMonitor();
        window.Show();
        window.Activate();
    }

    private string? TrySave(BitmapSource image, DateTime capturedAt)
    {
        try
        {
            string path = ScreenshotSaver.Save(image, _settings.Current, capturedAt);
            Log.Info($"Saved {image.PixelWidth}x{image.PixelHeight}.");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Saving the screenshot failed", ex);
            _toast.Show("The screenshot could not be saved", ToastKind.Warning);
            return null;
        }
    }

    // ------------------------------------------------------------------ history

    private void RecordHistory(PendingAction action, string? savedPath) =>
        RecordHistory(action.Original, action.Image, action.Region, action.Annotations, action.CapturedAt, savedPath);

    private void RecordHistory(BitmapSource original, BitmapSource image, PixelRect region, IReadOnlyList<Annotation> annotations,
        DateTime capturedAt, string? savedPath)
    {
        // Encoding a few PNGs takes a moment; do it once the UI is idle so the toast and focus hand-over stay instant.
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _history.Add(original, image, region, annotations, capturedAt, savedPath);
            }
            catch (Exception ex)
            {
                Log.Warn("Adding to the history failed", ex);
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    public void CopyFromHistory(HistoryEntry entry)
    {
        try
        {
            CopyImage(HistoryService.LoadBitmap(entry.ResultPath));
        }
        catch (Exception ex)
        {
            Log.Warn("Copying from history failed", ex);
            _toast.Show("That capture could not be loaded", ToastKind.Warning);
        }
    }

    public void PinFromHistory(HistoryEntry entry)
    {
        try
        {
            BitmapSource image = HistoryService.LoadBitmap(entry.ResultPath);
            PixelRect where = PlaceOnScreen(entry.Region, DisplayLayout.GetMonitors());
            _pins.Open(image, where, CopyImage, img => SaveImage(img, DateTime.Now) is not null);
        }
        catch (Exception ex)
        {
            Log.Warn("Pinning from history failed", ex);
            _toast.Show("That capture could not be loaded", ToastKind.Warning);
        }
    }

    /// <summary>
    /// Opens a past capture for editing: its original pixels are put back where they were (or centred on the main
    /// screen if that place no longer exists), its annotations are restored and remain editable.
    /// </summary>
    public void EditFromHistory(HistoryEntry entry)
    {
        BitmapSource original;
        List<Annotation> annotations;
        try
        {
            original = HistoryService.LoadBitmap(entry.OriginalPath);
            annotations = _history.LoadAnnotations(entry);
        }
        catch (Exception ex)
        {
            Log.Warn("Loading a capture for editing failed", ex);
            _toast.Show("That capture could not be loaded", ToastKind.Warning);
            return;
        }

        OpenOverlay(OverlayIntent.Capture, (session, frame) =>
        {
            PixelRect where = PlaceOnScreen(new PixelRect(entry.Region.X, entry.Region.Y, original.PixelWidth, original.PixelHeight), frame.Monitors);
            frame.Paste(original, where.X, where.Y);
            var offset = new Vector(where.X - entry.Region.X, where.Y - entry.Region.Y);
            foreach (Annotation annotation in annotations) AnnotationGeometry.Offset(annotation, offset);
            session.Preload(where, annotations);
        });
    }

    /// <summary>Keeps a rectangle where it was if that is fully on a monitor, else centres it on the primary monitor.</summary>
    internal static PixelRect PlaceOnScreen(PixelRect rect, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Any(m => m.Bounds.Contains(rect))) return rect;
        MonitorInfo primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        int width = Math.Min(rect.Width, primary.Bounds.Width);
        int height = Math.Min(rect.Height, primary.Bounds.Height);
        return new PixelRect(primary.Bounds.X + (primary.Bounds.Width - width) / 2, primary.Bounds.Y + (primary.Bounds.Height - height) / 2, width, height);
    }

    public void Dispose()
    {
        _countdown?.Cancel();
        _warmUp?.ForceClose();
        _session?.ForceClose();
        _pins.CloseAll();
        _pool.Dispose();
    }
}
