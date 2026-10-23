using System;
using System.Threading;
using HolyScreen.Interop;

namespace HolyScreen.E2E.Driver;

/// <summary>
/// Injects real keyboard and mouse input (SendInput / SetCursorPos) in physical pixels. Every press and move first
/// passes the <see cref="InputGuard"/>; releases always go through, so an aborted run never leaves a key held.
/// </summary>
internal static class Simulate
{
    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>
    /// Presses the keys in order and releases them in reverse, like a human chord (e.g. Ctrl+S), into the focused
    /// HolyScreen window — refused when anything else has the keyboard focus.
    /// </summary>
    public static void Chord(params int[] keys) => ChordCore(keys, global: false);

    /// <summary>
    /// A registered global shortcut. Windows hands it to HolyScreen before any window sees the keys, so it may be
    /// pressed while the terminal is in front (e.g. to open the overlay).
    /// </summary>
    public static void GlobalHotkey(params int[] keys) => ChordCore(keys, global: true);

    private static void ChordCore(int[] keys, bool global)
    {
        var pressed = new System.Collections.Generic.List<int>();
        try
        {
            foreach (int key in keys)
            {
                if (global) InputGuard.Check();
                else InputGuard.CheckKeyboard();
                SendKeyUnchecked(key, up: false);
                pressed.Add(key);
            }

            Thread.Sleep(30);
        }
        finally
        {
            for (int i = pressed.Count - 1; i >= 0; i--) SendKey(pressed[i], up: true);
            Thread.Sleep(30);
        }
    }

    public static void Press(int key) => Chord(key);

    public static void Type(string text)
    {
        foreach (char c in text)
        {
            InputGuard.CheckKeyboard();
            var down = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
            down.U.ki = new Win32.KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE };
            var up = down;
            up.U.ki.dwFlags = KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP;
            Win32.SendInput(2, new[] { down, up }, Win32.INPUT.Size);
            Thread.Sleep(8);
        }
    }

    public static void KeyDown(int key) => SendKey(key, up: false);

    public static void KeyUp(int key) => SendKey(key, up: true);

    private static void SendKey(int key, bool up)
    {
        if (!up) InputGuard.CheckKeyboard();
        SendKeyUnchecked(key, up);
    }

    private static void SendKeyUnchecked(int key, bool up)
    {
        var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        input.U.ki = new Win32.KEYBDINPUT
        {
            wVk = (ushort)key,
            dwFlags = (up ? Win32.KEYEVENTF_KEYUP : 0) | (IsExtended(key) ? Win32.KEYEVENTF_EXTENDEDKEY : 0),
        };
        if (Win32.SendInput(1, new[] { input }, Win32.INPUT.Size) != 1)
        {
            throw new InvalidOperationException($"SendInput failed for key 0x{key:X2}.");
        }
    }

    private static bool IsExtended(int key) => key is Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN
        or Win32.VK_INSERT or Win32.VK_DELETE or Win32.VK_HOME or Win32.VK_END or Win32.VK_PRIOR or Win32.VK_NEXT
        or Win32.VK_SNAPSHOT or Win32.VK_RCONTROL or Win32.VK_RMENU;

    /// <summary>Moves the cursor to an exact physical pixel (the system then sends WM_MOUSEMOVE to the window there).</summary>
    public static void MoveTo(int x, int y)
    {
        InputGuard.Check();
        Win32.SetCursorPos(x, y);
        Thread.Sleep(10);
    }

    public static void LeftDown() => SendMouse(Win32.MOUSEEVENTF_LEFTDOWN);

    public static void LeftUp() => SendMouse(Win32.MOUSEEVENTF_LEFTUP);

    public static void Click(int x, int y)
    {
        MoveTo(x, y);
        LeftDown();
        try
        {
            Thread.Sleep(40);
        }
        finally
        {
            LeftUp();
        }

        Thread.Sleep(40);
    }

    public static void DoubleClick(int x, int y)
    {
        Click(x, y);
        Click(x, y);
    }

    public static void RightClick(int x, int y)
    {
        MoveTo(x, y);
        SendMouse(Win32.MOUSEEVENTF_RIGHTDOWN);
        try
        {
            Thread.Sleep(40);
        }
        finally
        {
            SendMouse(Win32.MOUSEEVENTF_RIGHTUP);
        }

        Thread.Sleep(40);
    }

    public static void Wheel(int delta)
    {
        InputGuard.CheckPointerButton();
        var input = new Win32.INPUT { type = Win32.INPUT_MOUSE };
        input.U.mi = new Win32.MOUSEINPUT { dwFlags = Win32.MOUSEEVENTF_WHEEL, mouseData = unchecked((uint)delta) };
        Win32.SendInput(1, new[] { input }, Win32.INPUT.Size);
        Thread.Sleep(40);
    }

    /// <summary>Presses at the start point, moves in small steps (so the app sees a real drag) and releases.</summary>
    public static void Drag(int x0, int y0, int x1, int y1, int steps = 24, int stepDelayMs = 8)
    {
        MoveTo(x0, y0);
        LeftDown();
        try
        {
            Thread.Sleep(30);
            for (int i = 1; i <= steps; i++)
            {
                int x = x0 + (x1 - x0) * i / steps;
                int y = y0 + (y1 - y0) * i / steps;
                InputGuard.Check();
                Win32.SetCursorPos(x, y);
                Thread.Sleep(stepDelayMs);
            }

            Thread.Sleep(30);
        }
        finally
        {
            LeftUp();
        }

        Thread.Sleep(60);
    }

    private static void SendMouse(uint flags)
    {
        bool release = flags is Win32.MOUSEEVENTF_LEFTUP or Win32.MOUSEEVENTF_RIGHTUP;
        if (!release) InputGuard.CheckPointerButton();
        var input = new Win32.INPUT { type = Win32.INPUT_MOUSE };
        input.U.mi = new Win32.MOUSEINPUT { dwFlags = flags };
        if (Win32.SendInput(1, new[] { input }, Win32.INPUT.Size) != 1)
        {
            throw new InvalidOperationException("SendInput failed for a mouse event.");
        }
    }
}
