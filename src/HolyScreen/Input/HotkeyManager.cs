using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HolyScreen.Core;
using HolyScreen.Interop;

namespace HolyScreen.Input;

/// <summary>Things a global shortcut can trigger.</summary>
internal enum HotkeyAction
{
    CaptureRegion = 0,
    CaptureFullScreen = 1,
    RepeatLastRegion = 2,
    CaptureText = 3,
    PickColor = 4,
}

internal enum HotkeyRegistrationStatus
{
    Registered,
    Conflict,
    Invalid,
    Failed,
}

internal sealed record HotkeyRegistrationResult(HotkeyRegistrationStatus Status, HotkeyGesture Gesture, string? Message)
{
    public bool Success => Status == HotkeyRegistrationStatus.Registered;
}

/// <summary>
/// Registers the global shortcuts with Windows' <c>RegisterHotKey</c>. This is a pull mechanism: Windows posts
/// <c>WM_HOTKEY</c> to the message window only when an exact registered combination is pressed, so the app never
/// observes any other keystroke (no keyboard hooks). <c>MOD_NOREPEAT</c> makes a held key fire exactly once.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int BaseId = 0xB100;
    private const int ProbeId = 0xB1FF;

    private readonly IntPtr _hwnd;
    private readonly Dictionary<HotkeyAction, HotkeyGesture> _registered = new();
    private bool _suspended;
    private bool _disposed;

    public HotkeyManager(MessageWindow window)
    {
        _hwnd = window.Handle;
        window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    public event Action<HotkeyAction>? Activated;

    public HotkeyGesture? GetCurrent(HotkeyAction action) =>
        _registered.TryGetValue(action, out HotkeyGesture gesture) ? gesture : null;

    public static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.CaptureRegion => "Capture region",
        HotkeyAction.CaptureFullScreen => "Capture full screen",
        HotkeyAction.RepeatLastRegion => "Repeat last region",
        HotkeyAction.CaptureText => "Capture text (OCR)",
        HotkeyAction.PickColor => "Pick color",
        _ => action.ToString(),
    };

    private void OnHotkeyPressed(int id)
    {
        int index = id - BaseId;
        if (index >= 0 && Enum.IsDefined(typeof(HotkeyAction), index))
        {
            Activated?.Invoke((HotkeyAction)index);
        }
    }

    /// <summary>
    /// Validates and registers <paramref name="gesture"/> for <paramref name="action"/>. On failure the previously
    /// working shortcut stays registered (the caller is told what went wrong and nothing is silently overwritten).
    /// </summary>
    public HotkeyRegistrationResult Register(HotkeyAction action, HotkeyGesture gesture)
    {
        string? problem = gesture.Validate();
        if (problem is not null)
        {
            return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Invalid, gesture, problem);
        }

        if (_registered.TryGetValue(action, out HotkeyGesture existing) && existing == gesture)
        {
            return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Registered, gesture, null);
        }

        foreach ((HotkeyAction other, HotkeyGesture otherGesture) in _registered)
        {
            if (other != action && otherGesture == gesture)
            {
                return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Conflict, gesture,
                    $"{gesture.ToDisplayString()} is already used for \"{Describe(other)}\".");
            }
        }

        // Register under a temporary id first, so a failure never leaves the action without its old shortcut.
        uint modifiers = gesture.ToNativeModifiers() | Win32.MOD_NOREPEAT;
        if (!Win32.RegisterHotKey(_hwnd, ProbeId, modifiers, (uint)gesture.Key))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == Win32.ERROR_HOTKEY_ALREADY_REGISTERED)
            {
                return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Conflict, gesture,
                    $"{gesture.ToDisplayString()} is already in use by another application. Choose a different shortcut.");
            }

            return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Failed, gesture,
                $"Windows could not register {gesture.ToDisplayString()} (error 0x{error:X8}).");
        }

        Win32.UnregisterHotKey(_hwnd, ProbeId);

        Unregister(action);
        if (!Win32.RegisterHotKey(_hwnd, BaseId + (int)action, modifiers, (uint)gesture.Key))
        {
            int error = Marshal.GetLastPInvokeError();
            Log.Error($"Hotkey registration failed after the probe succeeded (0x{error:X8})");
            return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Failed, gesture,
                "Windows could not register the shortcut.");
        }

        _registered[action] = gesture;
        Log.Info($"Hotkey registered: {Describe(action)} = {gesture}");
        return new HotkeyRegistrationResult(HotkeyRegistrationStatus.Registered, gesture, null);
    }

    public void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action))
        {
            Win32.UnregisterHotKey(_hwnd, BaseId + (int)action);
        }
    }

    /// <summary>
    /// Temporarily unregisters everything (while a shortcut is being recorded in Settings, so the keys reach the
    /// recorder instead of triggering a capture) or registers it all again.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        foreach ((HotkeyAction action, HotkeyGesture gesture) in _registered.ToList())
        {
            int id = BaseId + (int)action;
            if (suspended)
            {
                Win32.UnregisterHotKey(_hwnd, id);
            }
            else if (!Win32.RegisterHotKey(_hwnd, id, gesture.ToNativeModifiers() | Win32.MOD_NOREPEAT, (uint)gesture.Key))
            {
                Log.Warn($"Could not re-register {gesture} after recording (0x{Marshal.GetLastPInvokeError():X8})");
            }
        }
    }

    /// <summary>Re-applies every registration, e.g. defensively after the system resumes from sleep.</summary>
    public void Reapply()
    {
        foreach ((HotkeyAction action, HotkeyGesture gesture) in _registered.ToList())
        {
            int id = BaseId + (int)action;
            Win32.UnregisterHotKey(_hwnd, id);
            if (!Win32.RegisterHotKey(_hwnd, id, gesture.ToNativeModifiers() | Win32.MOD_NOREPEAT, (uint)gesture.Key))
            {
                Log.Warn($"Could not re-register {gesture} after resume (0x{Marshal.GetLastPInvokeError():X8})");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (HotkeyAction action in _registered.Keys.ToList())
        {
            Unregister(action);
        }
    }
}
