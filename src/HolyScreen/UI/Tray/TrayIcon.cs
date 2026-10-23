using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Interop;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Tray;

/// <summary>One entry of the tray menu; a null entry in a menu list is a separator.</summary>
internal sealed record TrayMenuEntry(string Header, string Icon, Action OnClick, string? Gesture = null, bool Enabled = true);

/// <summary>
/// The system-tray presence: a notify icon plus a themed right-click menu. Left-click starts a capture; right-click
/// opens the menu. Survives Explorer restarts by re-adding the icon when it receives the TaskbarCreated broadcast.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    private readonly MessageWindow _window;
    private Win32.NOTIFYICONDATA _data;
    private IntPtr _hIcon;
    private ContextMenu? _menu;
    private string _tooltip = "HolyScreen";
    private bool _added;
    private bool _disposed;

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        _window.TrayCallback = OnTrayCallback;
        _window.ExplorerRestarted += ReAdd;

        _hIcon = IconLoader.LoadTrayIcon();
        AddIcon();
    }

    /// <summary>Left click (or Enter/Space on the focused icon).</summary>
    public event Action? Activated;

    /// <summary>Builds the menu entries each time the menu opens, so they reflect the current state.</summary>
    public Func<IEnumerable<TrayMenuEntry?>>? MenuProvider { get; set; }

    public void SetMenu(IEnumerable<TrayMenuEntry?> entries)
    {
        if (_menu is not null) _menu.IsOpen = false;
        _menu = new ContextMenu();
        _menu.Items.Add(new MenuItem
        {
            Header = "HolyScreen",
            Style = (Style)Application.Current.FindResource("MenuHeaderItem"),
        });
        _menu.Items.Add(new Separator());

        foreach (TrayMenuEntry? entry in entries)
        {
            if (entry is null)
            {
                _menu.Items.Add(new Separator());
                continue;
            }

            var item = new MenuItem
            {
                Header = entry.Header,
                Icon = MenuGlyph(entry.Icon),
                InputGestureText = entry.Gesture ?? string.Empty,
                IsEnabled = entry.Enabled,
            };
            Action onClick = entry.OnClick;
            item.Click += (_, _) => onClick();
            _menu.Items.Add(item);
        }
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (!_added) return;
        Win32.NOTIFYICONDATA data = _data;
        data.uFlags = Win32.NIF_TIP | Win32.NIF_SHOWTIP;
        data.szTip = _tooltip;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
        _data.szTip = _tooltip;
    }

    private void AddIcon()
    {
        _data = new Win32.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP,
            uCallbackMessage = _window.TrayCallbackMessage,
            hIcon = _hIcon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            uVersion = Win32.NOTIFYICON_VERSION_4,
        };

        if (!Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref _data))
        {
            Log.Error("Could not add the system tray icon.");
            return;
        }

        _added = true;
        Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref _data);
    }

    private void ReAdd()
    {
        if (_disposed) return;
        Log.Info("Explorer restarted; re-adding the tray icon.");
        _added = false;
        AddIcon();
    }

    /// <summary>Shows a system balloon notification (used for things the user must know, e.g. a hotkey conflict).</summary>
    public void ShowBalloon(string title, string message, bool warning = false)
    {
        if (!_added) return;
        Win32.NOTIFYICONDATA data = _data;
        data.uFlags = Win32.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = warning ? Win32.NIIF_WARNING : Win32.NIIF_INFO;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    private bool OnTrayCallback(uint msg, IntPtr wParam, IntPtr lParam)
    {
        uint evt = (uint)Win32.LoWord(lParam);
        switch (evt)
        {
            case Win32.NIN_SELECT:
            case Win32.NIN_KEYSELECT:
                Activated?.Invoke();
                return true;
            case Win32.WM_CONTEXTMENU:
                ShowMenu();
                return true;
            default:
                return false;
        }
    }

    private void ShowMenu()
    {
        if (MenuProvider is not null) SetMenu(MenuProvider());
        if (_menu is null) return;

        // Foreground ourselves first so the menu dismisses correctly when the user clicks elsewhere.
        Win32.SetForegroundWindow(_window.Handle);

        Win32.POINT cursor = DisplayLayout.GetCursorPosition();
        MonitorInfo monitor = DisplayLayout.FromPoint(DisplayLayout.GetMonitors(), cursor.X, cursor.Y);
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        _menu.HorizontalOffset = cursor.X / monitor.Scale;
        _menu.VerticalOffset = cursor.Y / monitor.Scale;
        _menu.IsOpen = true;
    }

    private static FrameworkElement MenuGlyph(string iconPath)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xA2, 0xA7, 0xB0));
        brush.Freeze();
        return new Path
        {
            Data = Icons.Get(iconPath),
            Stroke = brush,
            StrokeThickness = 1.6,
            Stretch = Stretch.Uniform,
            Width = 15,
            Height = 15,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_menu is not null) _menu.IsOpen = false;

        if (_added)
        {
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref _data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _window.TrayCallback = null;
    }
}
