using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HolyScreen.Interop;

namespace HolyScreen.E2E.Driver;

/// <summary>
/// Makes sure injected test input only ever reaches the app under test. The run may start only while the terminal
/// that launched it is in front. After that:
/// <list type="bullet">
/// <item>pointer moves (and global shortcuts, which Windows hands to the registered app before any window sees them)
/// are allowed while the terminal, this runner or HolyScreen is in front;</item>
/// <item>key presses are sent only while a HolyScreen window (or one of this runner's fixture windows) has the
/// keyboard focus — never into the terminal or an editor that happens to be in front;</item>
/// <item>mouse buttons and the wheel are sent only while the pointer is over such a window, since that window is
/// where the click lands.</item>
/// </list>
/// If any other application comes to the front (the user switched to a game or a chat), the whole run stops, and
/// every later press is refused too. Key and button releases are never blocked, so nothing is ever left held.
/// </summary>
internal static class InputGuard
{
    private static readonly HashSet<int> Allowed = new(); // may be in front without stopping the run
    private static readonly HashSet<int> Targets = new(); // may receive keys and clicks
    private static bool _armed;
    private static bool _tripped;

    /// <summary>Returns a reason not to start, or null when it is safe.</summary>
    public static string? Arm()
    {
        Allowed.Clear();
        Targets.Clear();
        Allowed.Add(Environment.ProcessId);
        Targets.Add(Environment.ProcessId);
        foreach (int ancestor in Ancestors(Environment.ProcessId)) Allowed.Add(ancestor);

        int foreground = ForegroundProcessId();
        if (foreground != 0 && !Allowed.Contains(foreground))
        {
            return $"'{ProcessName(foreground)}' is in the foreground. The E2E run injects real mouse and keyboard input, so it only " +
                   "starts while the terminal that launched it is in front — and stops if anything else comes forward.";
        }

        // The terminal's own window may belong to a sibling process (e.g. VS Code's main process vs. its pty host).
        if (foreground != 0) Allowed.Add(foreground);
        _armed = true;
        _tripped = false;
        return null;
    }

    /// <summary>The app under test: it may be in front and receive keys and clicks.</summary>
    public static void Allow(int processId)
    {
        Allowed.Add(processId);
        Targets.Add(processId);
    }

    /// <summary>For pointer moves and global shortcuts. Stops the run if an unrelated application is in front.</summary>
    public static void Check()
    {
        if (!_armed) return;
        if (_tripped) throw new E2EFailure("input was stopped earlier in this run");
        int foreground = ForegroundProcessId();
        if (foreground == 0 || Allowed.Contains(foreground)) return;
        _tripped = true;
        throw new E2EFailure($"aborted: '{ProcessName(foreground)}' came to the foreground — not sending test input to it");
    }

    /// <summary>For key presses: the window with the keyboard focus must be HolyScreen's (or this runner's).</summary>
    public static void CheckKeyboard()
    {
        Check();
        if (!_armed) return;
        int foreground = ForegroundProcessId();
        if (foreground != 0 && !Targets.Contains(foreground))
        {
            throw new E2EFailure($"not typing: '{ProcessName(foreground)}' has the keyboard focus, not HolyScreen");
        }
    }

    /// <summary>For mouse buttons and the wheel: the window under the pointer must be HolyScreen's (or this runner's).</summary>
    public static void CheckPointerButton()
    {
        Check();
        if (!_armed) return;
        Win32.GetCursorPos(out Win32.POINT point);
        IntPtr root = Win32.GetAncestor(Win32.WindowFromPoint(point), Win32.GA_ROOT);
        Win32.GetWindowThreadProcessId(root, out uint pid);
        if (!Targets.Contains((int)pid))
        {
            throw new E2EFailure($"not clicking: the pointer is over '{ProcessName((int)pid)}', not HolyScreen");
        }
    }

    public static bool Tripped => _tripped;

    private static int ForegroundProcessId()
    {
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint pid);
        return (int)pid;
    }

    private static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return $"process {pid}"; }
    }

    private static IEnumerable<int> Ancestors(int pid)
    {
        var seen = new HashSet<int>();
        int current = pid;
        for (int depth = 0; depth < 16; depth++)
        {
            int parent = ParentOf(current);
            if (parent <= 0 || !seen.Add(parent)) yield break;
            yield return parent;
            current = parent;
        }
    }

    private static int ParentOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            var info = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);
}
