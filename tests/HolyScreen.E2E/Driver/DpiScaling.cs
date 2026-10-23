using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HolyScreen.Capture;

namespace HolyScreen.E2E.Driver;

/// <summary>
/// Changes a monitor's "Scale" setting (100 %, 125 %, 150 % …) live, the way Settings › Display does, through the
/// DisplayConfig API's undocumented DPI-scale requests (-3 = get, -4 = set; the same calls tools such as SetDpi use).
/// A marker file records the original value before any change so an interrupted run can be repaired on the next start.
/// </summary>
internal static class DpiScaling
{
    private static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };
    private static readonly string Marker = Path.Combine(Path.GetTempPath(), "HolyScreen.E2E.dpi-restore.txt");

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;

    /// <summary>Sets <paramref name="percent"/> on the monitor and returns a handle that restores the original scale.</summary>
    public static IDisposable Override(string gdiDeviceName, int percent)
    {
        int original = GetPercent(gdiDeviceName);
        File.WriteAllText(Marker, $"{gdiDeviceName}|{original}");
        SetPercent(gdiDeviceName, percent);
        return new Restore(gdiDeviceName, original);
    }

    /// <summary>Repairs a scale left changed by a run that was killed half-way.</summary>
    public static void RepairInterruptedRun()
    {
        if (!File.Exists(Marker)) return;
        string[] parts = File.ReadAllText(Marker).Split('|');
        if (parts.Length == 2 && int.TryParse(parts[1], out int percent))
        {
            Console.WriteLine($"Restoring {parts[0]} to {percent}% left over from an interrupted run.");
            SetPercent(parts[0], percent);
        }

        File.Delete(Marker);
    }

    public static int GetPercent(string gdiDeviceName)
    {
        (LUID adapter, uint source) = FindSource(gdiDeviceName);
        var get = new DPI_SCALE_GET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<DPI_SCALE_GET>(),
                adapterId = adapter,
                id = source,
            },
        };
        Throw(DisplayConfigGetDeviceInfo(ref get), "get DPI scale");
        int recommended = Math.Abs(get.minScaleRel);
        return Steps[Math.Clamp(recommended + get.curScaleRel, 0, Steps.Length - 1)];
    }

    public static void SetPercent(string gdiDeviceName, int percent)
    {
        int index = Array.IndexOf(Steps, percent);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(percent), "Unsupported scale step.");

        (LUID adapter, uint source) = FindSource(gdiDeviceName);
        var get = new DPI_SCALE_GET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<DPI_SCALE_GET>(),
                adapterId = adapter,
                id = source,
            },
        };
        Throw(DisplayConfigGetDeviceInfo(ref get), "get DPI scale");
        int recommended = Math.Abs(get.minScaleRel);
        int relative = Math.Clamp(index - recommended, get.minScaleRel, get.maxScaleRel);

        var set = new DPI_SCALE_SET
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<DPI_SCALE_SET>(),
                adapterId = adapter,
                id = source,
            },
            scaleRel = relative,
        };
        Throw(DisplayConfigSetDeviceInfo(ref set), "set DPI scale");

        uint expectedDpi = (uint)Math.Round(96 * percent / 100.0);
        bool applied = Wait.Until(() => DisplayLayout.GetMonitors().Any(m => m.DeviceName == gdiDeviceName && m.Dpi == expectedDpi),
            TimeSpan.FromSeconds(8), 100);
        if (!applied) throw new E2EFailure($"{gdiDeviceName} did not switch to {percent}%");
        Thread.Sleep(1200); // let Explorer and other apps finish re-laying out before anything is measured
    }

    private static (LUID Adapter, uint Source) FindSource(string gdiDeviceName)
    {
        Throw(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount), "query buffer sizes");
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        Throw(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero), "query display config");

        for (int i = 0; i < pathCount; i++)
        {
            var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref name) == 0 &&
                string.Equals(name.viewGdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return (paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
            }
        }

        throw new E2EFailure($"display source for {gdiDeviceName} not found");
    }

    private static void Throw(int status, string what)
    {
        if (status != 0) throw new E2EFailure($"DisplayConfig failed to {what} (error {status})");
    }

    private sealed class Restore : IDisposable
    {
        private readonly string _device;
        private readonly int _percent;
        private bool _done;

        public Restore(string device, int percent)
        {
            _device = device;
            _percent = percent;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            SetPercent(_device, _percent);
            File.Delete(Marker);
        }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SCALE_GET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DPI_SCALE_SET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DPI_SCALE_GET requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DPI_SCALE_SET setPacket);
}
