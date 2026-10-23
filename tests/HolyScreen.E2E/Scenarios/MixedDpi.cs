using System.Linq;
using HolyScreen.Capture;
using HolyScreen.E2E.Driver;

namespace HolyScreen.E2E.Scenarios;

/// <summary>
/// Re-runs the overlay scenarios with the second monitor temporarily at a different scale than the primary
/// (100 % + 150 %), which is where screenshot tools usually get coordinates, sizes or sharpness wrong.
/// The original scale is restored afterwards, even if a check fails.
/// </summary>
internal static class MixedDpi
{
    public static void Run(E2EContext ctx)
    {
        MonitorInfo? secondary = DisplayLayout.GetMonitors().FirstOrDefault(m => !m.IsPrimary);
        if (secondary is null)
        {
            ctx.Note("single monitor — skipped");
            return;
        }

        int original = DpiScaling.GetPercent(secondary.DeviceName);
        int target = original == 150 ? 125 : 150;
        ctx.Note($"{secondary.DeviceName}: {original}% → {target}% for this test");

        using (DpiScaling.Override(secondary.DeviceName, target))
        {
            ctx.Note("monitors now: " + string.Join(", ", DisplayLayout.GetMonitors().Select(m => $"{m.DeviceName} {m.Bounds} @{m.Dpi} dpi")));
            MonitorInfo scaled = DisplayLayout.GetMonitors().First(m => m.DeviceName == secondary.DeviceName);
            MonitorInfo primary = DisplayLayout.GetMonitors().First(m => m.IsPrimary);

            Stage2.DragCopyOn(ctx, "mixed-dpi-scaled", scaled);
            Stage2.DragCopyOn(ctx, "mixed-dpi-primary", primary);
            Stage2.CrossMonitorSaveNamed(ctx, "mixed-dpi-cross");
        }

        Check.Equal(original, DpiScaling.GetPercent(secondary.DeviceName), "scale after restoring");
        ctx.Note($"{secondary.DeviceName} restored to {original}%");
    }
}
