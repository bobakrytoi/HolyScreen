using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Input;
using HolyScreen.Output;
using HolyScreen.UI.Common;
using HolyScreen.UI.History;
using HolyScreen.UI.Overlay;
using HolyScreen.UI.Pin;
using HolyScreen.UI.Settings;
using HolyScreen.UI.Toast;
using HolyScreen.UI.Tray;

namespace HolyScreen;

/// <summary>
/// Owns every long-lived component and wires them together. There is no main window: the application lives in the
/// tray and opens the capture overlay on demand. Created and used only on the WPF UI thread.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private static readonly HotkeyAction[] AllActions =
    {
        HotkeyAction.CaptureRegion, HotkeyAction.CaptureFullScreen, HotkeyAction.RepeatLastRegion,
        HotkeyAction.CaptureText, HotkeyAction.PickColor,
    };

    private readonly App _app;
    private readonly CommandLineArgs _args;

    private SettingsService _settings = null!;
    private MessageWindow _messageWindow = null!;
    private ToastService _toast = null!;
    private TrayIcon _tray = null!;
    private HotkeyManager _hotkeys = null!;
    private HistoryService _history = null!;
    private PinManager _pins = null!;
    private CaptureController _capture = null!;
    private HistoryWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public AppHost(App app, CommandLineArgs args)
    {
        _app = app;
        _args = args;
    }

    public void Start()
    {
        bool firstRun = !File.Exists(AppPaths.SettingsFile);

        _settings = SettingsService.Load(AppPaths.SettingsFile);
        _messageWindow = new MessageWindow();
        _toast = new ToastService(() => Motion.Enabled(_settings.Current));
        _history = new HistoryService(AppPaths.HistoryDirectory, () => _settings.Current);
        _pins = new PinManager();
        _capture = new CaptureController(_settings, _toast, _messageWindow, _history, _pins);

        _tray = new TrayIcon(_messageWindow) { MenuProvider = BuildTrayMenu };
        _tray.Activated += () => Execute(HotkeyAction.CaptureRegion);

        _hotkeys = new HotkeyManager(_messageWindow);
        _hotkeys.Activated += Execute;

        _messageWindow.SystemResumed += () => _hotkeys.Reapply();
        _messageWindow.DisplayChanged += () => _capture.OnDisplayChanged();

        RegisterConfiguredHotkeys();
        UpdateTooltip();

        // Once the app is idle, run one invisible capture so the first real one opens instantly.
        var warmUp = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(600) };
        warmUp.Tick += (_, _) =>
        {
            warmUp.Stop();
            _capture.WarmUp();
        };
        warmUp.Start();

        if (firstRun)
        {
            string gesture = _hotkeys.GetCurrent(HotkeyAction.CaptureRegion)?.ToDisplayString() ?? AppSettings.DefaultCaptureHotkey;
            _tray.ShowBalloon("HolyScreen is running",
                $"Press {gesture} to take a screenshot. Right-click the tray icon for more.");
        }

        if (_args.Command is { } command && command != InstanceCommand.Exit)
        {
            _app.Dispatcher.BeginInvoke(new Action(() => HandleInstanceCommand(command)), DispatcherPriority.ApplicationIdle);
        }

        Log.Info("Startup complete; running in the system tray.");
    }

    // ------------------------------------------------------------------ hotkeys & tray

    private void RegisterConfiguredHotkeys()
    {
        foreach (HotkeyAction action in AllActions)
        {
            string text = _settings.Current.GetHotkey(action);
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (!HotkeyGesture.TryParse(text, out HotkeyGesture gesture))
            {
                Log.Warn($"Ignoring unreadable shortcut '{text}' for {action}.");
                continue;
            }

            HotkeyRegistrationResult result = _hotkeys.Register(action, gesture);
            if (!result.Success && action == HotkeyAction.CaptureRegion && gesture != HotkeyGesture.Default)
            {
                Log.Warn($"Configured capture shortcut '{gesture}' failed: {result.Message} Trying the default.");
                result = _hotkeys.Register(action, HotkeyGesture.Default);
            }

            if (!result.Success)
            {
                Log.Error($"Shortcut for {action} is not registered: {result.Message}");
                if (action == HotkeyAction.CaptureRegion)
                {
                    _tray.ShowBalloon("Shortcut unavailable",
                        $"{result.Message} You can still capture from the tray icon.", warning: true);
                }
            }
        }
    }

    private IEnumerable<TrayMenuEntry?> BuildTrayMenu()
    {
        string? Gesture(HotkeyAction action) => _hotkeys.GetCurrent(action)?.ToDisplayString();

        return new TrayMenuEntry?[]
        {
            new("Capture region", Icons.Scan, () => Execute(HotkeyAction.CaptureRegion), Gesture(HotkeyAction.CaptureRegion)),
            new("Capture full screen", Icons.Monitor, () => Execute(HotkeyAction.CaptureFullScreen), Gesture(HotkeyAction.CaptureFullScreen)),
            new("Capture text (OCR)", Icons.ScanText, () => Execute(HotkeyAction.CaptureText), Gesture(HotkeyAction.CaptureText)),
            new("Pick colour", Icons.Pipette, () => Execute(HotkeyAction.PickColor), Gesture(HotkeyAction.PickColor)),
            new("Repeat last region", Icons.Refresh, () => Execute(HotkeyAction.RepeatLastRegion), Gesture(HotkeyAction.RepeatLastRegion),
                Enabled: _settings.Current.LastRegion is not null),
            new("Capture in 3 seconds", Icons.Timer, () => _capture.StartDelayedCapture(3)),
            new("Capture in 5 seconds", Icons.Timer, () => _capture.StartDelayedCapture(5)),
            null,
            new("History", Icons.History, ShowHistory),
            new("Open screenshots folder", Icons.Folder, () => ShellActions.OpenFolder(ScreenshotSaver.ResolveFolder(_settings.Current))),
            new($"Close pinned screenshots ({_pins.Count})", Icons.Pin, _pins.CloseAll, Enabled: _pins.Count > 0),
            null,
            new("Settings", Icons.Settings, ShowSettings),
            new("Exit", Icons.Power, () => _app.Shutdown()),
        };
    }

    private void UpdateTooltip()
    {
        string? regionGesture = _hotkeys.GetCurrent(HotkeyAction.CaptureRegion)?.ToDisplayString();
        _tray.SetTooltip(regionGesture is null ? "HolyScreen" : $"HolyScreen — {regionGesture} to capture");
    }

    private void Execute(HotkeyAction action)
    {
        try
        {
            switch (action)
            {
                case HotkeyAction.CaptureRegion:
                    _capture.StartRegionCapture();
                    break;
                case HotkeyAction.CaptureFullScreen:
                    _capture.CaptureFullScreen();
                    break;
                case HotkeyAction.CaptureText:
                    _capture.StartRegionCapture(OverlayIntent.ExtractText);
                    break;
                case HotkeyAction.PickColor:
                    _capture.StartRegionCapture(OverlayIntent.PickColor);
                    break;
                case HotkeyAction.RepeatLastRegion:
                    _capture.RepeatLastRegion();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Action {action} failed", ex);
            _toast.Show("Something went wrong — see the log for details", ToastKind.Warning);
        }
    }

    private void ShowHistory()
    {
        if (_historyWindow is not null)
        {
            _historyWindow.Activate();
            return;
        }

        _historyWindow = new HistoryWindow(_history, new HistoryActions
        {
            Copy = _capture.CopyFromHistory,
            Edit = entry =>
            {
                _historyWindow?.Close();
                _capture.EditFromHistory(entry);
            },
            Pin = _capture.PinFromHistory,
            Delete = _history.Delete,
            OpenFolder = () => ShellActions.OpenFolder(_history.Root),
            ClearAll = _history.Clear,
        });
        _historyWindow.CenterOnCursorMonitor();
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, new SettingsHost
        {
            ApplyHotkey = ApplyHotkey,
            CurrentHotkey = _hotkeys.GetCurrent,
            SuspendHotkeys = _hotkeys.SetSuspended,
            ClearHistory = _history.Clear,
            OpenHistoryFolder = () => ShellActions.OpenFolder(_history.Root),
        });
        _settingsWindow.CenterOnCursorMonitor();
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _settings.Flush();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Registers <paramref name="gesture"/> for <paramref name="action"/> (null clears it) and saves it. The settings
    /// file only changes once Windows has accepted the shortcut, so a failed attempt keeps the working one.
    /// </summary>
    private string? ApplyHotkey(HotkeyAction action, HotkeyGesture? gesture)
    {
        if (gesture is not HotkeyGesture value)
        {
            if (action == HotkeyAction.CaptureRegion) return "The capture shortcut cannot be removed.";
            _hotkeys.Unregister(action);
            _settings.Update(s => s.SetHotkey(action, string.Empty));
            UpdateTooltip();
            return null;
        }

        HotkeyRegistrationResult result = _hotkeys.Register(action, value);
        if (!result.Success) return result.Message ?? "The shortcut could not be registered.";

        _settings.Update(s => s.SetHotkey(action, value.ToString()));
        UpdateTooltip();
        return null;
    }

    public void HandleInstanceCommand(InstanceCommand command)
    {
        switch (command)
        {
            case InstanceCommand.CaptureRegion:
                Execute(HotkeyAction.CaptureRegion);
                break;
            case InstanceCommand.CaptureFullScreen:
                Execute(HotkeyAction.CaptureFullScreen);
                break;
            case InstanceCommand.History:
                ShowHistory();
                break;
            case InstanceCommand.Settings:
                ShowSettings();
                break;
            case InstanceCommand.CaptureDelayed:
                _capture.StartDelayedCapture(3);
                break;
            case InstanceCommand.Exit:
                _app.Shutdown();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _settings?.Flush(); } catch (Exception ex) { Log.Warn("Flushing settings failed", ex); }

        _historyWindow?.Close();
        _settingsWindow?.Close();
        _capture?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _toast?.Dispose();
        _messageWindow?.Dispose();
    }
}
