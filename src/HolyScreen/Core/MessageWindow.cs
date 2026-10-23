using System;
using System.Runtime.InteropServices;
using HolyScreen.Interop;

namespace HolyScreen.Core;

/// <summary>
/// A hidden, never-shown top-level window that owns the app's Win32 message plumbing: registered hotkeys, the tray
/// icon callback, power resume, display/setting changes and Explorer restarts. It also owns the clipboard while we
/// write images to it. Created on and only used from the UI thread, where WPF already pumps messages.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const string ClassName = "HolyScreen.MessageWindow";

    private readonly Win32.WndProc _wndProc; // kept alive for the window's lifetime
    private readonly IntPtr _instance;
    private readonly uint _taskbarCreatedMessage;
    private bool _disposed;

    public MessageWindow()
    {
        _instance = Win32.GetModuleHandle(null);
        _wndProc = WndProc;
        _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");

        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _instance,
            lpszClassName = ClassName,
        };

        if (Win32.RegisterClassEx(ref wc) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != Win32.ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new InvalidOperationException($"Could not register the message window class (0x{error:X8}).");
            }
        }

        // A hidden top-level tool window (never shown, off-screen, non-activating). Unlike a message-only window
        // it still receives the broadcasts we need: TaskbarCreated, WM_POWERBROADCAST, WM_DISPLAYCHANGE, WM_SETTINGCHANGE.
        const int WS_POPUP = unchecked((int)0x80000000);
        Handle = Win32.CreateWindowEx(
            (int)(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE),
            ClassName, "HolyScreen", WS_POPUP, -32000, -32000, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not create the message window (0x{Marshal.GetLastPInvokeError():X8}).");
        }
    }

    public IntPtr Handle { get; private set; }

    public event Action<int>? HotkeyPressed;
    public event Action? SystemResumed;
    public event Action? ExplorerRestarted;
    public event Action? SettingChanged;
    public event Action? DisplayChanged;

    /// <summary>Lets the tray icon receive its callback message through this window's proc.</summary>
    public Func<uint, IntPtr, IntPtr, bool>? TrayCallback { get; set; }

    /// <summary>The message the tray icon is told to post back to this window.</summary>
    public uint TrayCallbackMessage { get; } = Win32.WM_APP + 1;

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case Win32.WM_HOTKEY:
                    HotkeyPressed?.Invoke((int)wParam);
                    return IntPtr.Zero;

                case Win32.WM_POWERBROADCAST:
                    if ((int)wParam is Win32.PBT_APMRESUMESUSPEND or Win32.PBT_APMRESUMEAUTOMATIC)
                    {
                        SystemResumed?.Invoke();
                    }

                    return new IntPtr(1);

                case Win32.WM_SETTINGCHANGE:
                    SettingChanged?.Invoke();
                    return IntPtr.Zero;

                case Win32.WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke();
                    return IntPtr.Zero;

                default:
                    if (msg == TrayCallbackMessage && TrayCallback is { } callback && callback(msg, wParam, lParam))
                    {
                        return IntPtr.Zero;
                    }

                    if (msg == _taskbarCreatedMessage)
                    {
                        ExplorerRestarted?.Invoke();
                        return IntPtr.Zero;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Unhandled error while processing window message 0x{msg:X4}", ex);
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            Win32.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        Win32.UnregisterClass(ClassName, _instance);
    }
}
