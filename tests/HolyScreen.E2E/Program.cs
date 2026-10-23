using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HolyScreen.E2E.Scenarios;

namespace HolyScreen.E2E;

internal sealed record Scenario(string Name, Action<E2EContext> Run);

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string config = "Debug";
        string? exe = null;
        var only = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    config = args[++i];
                    break;
                case "--only" when i + 1 < args.Length:
                    only.Add(args[++i]);
                    break;
                case "--exe" when i + 1 < args.Length: // e.g. the published dist\HolyScreen.exe
                    exe = Path.GetFullPath(args[++i]);
                    break;
            }
        }

        if (Process.GetProcessesByName("HolyScreen").Length > 0)
        {
            Console.WriteLine("HolyScreen is already running. Exit it first: the E2E run needs the global shortcuts.");
            return 2;
        }

        Driver.DpiScaling.RepairInterruptedRun();
        if (Driver.InputGuard.Arm() is string unsafeToRun)
        {
            Console.WriteLine("Not starting: " + unsafeToRun);
            return 3;
        }

        // Live self-check: with the terminal in front, keys must be refused (they would land in the terminal/editor).
        if (Driver.WindowQuery.ForegroundProcessId() is int front && front != 0 && front != Environment.ProcessId)
        {
            try
            {
                Driver.InputGuard.CheckKeyboard();
                Console.WriteLine("Not starting: the input guard would let keys through to the terminal.");
                return 4;
            }
            catch (Driver.E2EFailure)
            {
                // expected
            }
        }

        Interop.Win32.GetCursorPos(out Interop.Win32.POINT cursorAtStart);
        E2EContext context = E2EContext.Create(config, exe);
        List<Scenario> scenarios = AllScenarios()
            .Where(s => only.Count == 0 || only.Any(o => s.Name.Contains(o, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Console.WriteLine($"HolyScreen E2E — {scenarios.Count} scenario(s), {context.AppExe}");
        Console.WriteLine($"Artifacts: {context.RunDir}");

        // The scenarios copy dozens of images; put the user's clipboard text back afterwards.
        string? clipboardText = Driver.ClipboardReader.ReadText();
        int failed = 0;
        try
        {
            foreach (Scenario scenario in scenarios)
            {
                if (Driver.InputGuard.Tripped)
                {
                    Console.WriteLine($"• {scenario.Name}  SKIPPED (input guard tripped — another app took the foreground)");
                    failed++;
                    continue;
                }

                Console.WriteLine($"• {scenario.Name}");
                var timer = Stopwatch.StartNew();
                try
                {
                    scenario.Run(context);
                    Console.WriteLine($"  PASS ({timer.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  FAIL: {ex.Message}");
                    File.AppendAllText(Path.Combine(context.RunDir, "failures.log"), $"{scenario.Name}:{Environment.NewLine}{ex}{Environment.NewLine}");
                    try { context.Snapshot(scenario.Name, "failure-screen"); } catch { /* best effort */ }
                }
                finally
                {
                    KillStrays();
                }
            }
        }
        finally
        {
            if (clipboardText is not null)
            {
                try { System.Windows.Clipboard.SetText(clipboardText); }
                catch (Exception ex) { Console.WriteLine("Could not restore the clipboard text: " + ex.Message); }
            }
        }

        Interop.Win32.SetCursorPos(cursorAtStart.X, cursorAtStart.Y);
        Console.WriteLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        return failed == 0 ? 0 : 1;
    }

    private static IEnumerable<Scenario> AllScenarios()
    {
        yield return new Scenario("fullscreen-capture", Stage1.FullScreenCapture);
        yield return new Scenario("drag-copy", Stage2.DragCopy);
        yield return new Scenario("drag-copy-secondary", Stage2.DragCopyOnSecondary);
        yield return new Scenario("cross-monitor-save", Stage2.CrossMonitorSave);
        yield return new Scenario("window-click", Stage2.WindowClick);
        yield return new Scenario("keyboard-nudge", Stage2.KeyboardNudge);
        yield return new Scenario("cancel", Stage2.CancelPaths);
        yield return new Scenario("panels", Stage3.Panels);
        yield return new Scenario("annotate", Stage4.Annotate);
        yield return new Scenario("annotate-edit", Stage4.EditExisting);
        yield return new Scenario("palette", Stage4.Palette);
        yield return new Scenario("pin", Stage5.Pin);
        yield return new Scenario("ocr", Stage5.Ocr);
        yield return new Scenario("pick-color", Stage5.PickColor);
        yield return new Scenario("beautify", Stage5.Beautify);
        yield return new Scenario("history-edit", Stage5.HistoryEdit);
        yield return new Scenario("repeat-region", Stage5.RepeatRegion);
        yield return new Scenario("delayed", Stage5.Delayed);
        yield return new Scenario("settings", Stage6.Settings);
        yield return new Scenario("memory", Stage6.MemorySteady);
        yield return new Scenario("mixed-dpi", MixedDpi.Run);
    }

    /// <summary>Stops instances this run started that are still alive — never a HolyScreen the user started.</summary>
    private static void KillStrays()
    {
        List<(int Pid, DateTime Started)> started;
        lock (Driver.AppProcess.Started) started = Driver.AppProcess.Started.ToList();
        foreach ((int pid, DateTime startedAt) in started)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.StartTime != startedAt) continue; // the pid was reused by something else
                if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
        }
    }
}
