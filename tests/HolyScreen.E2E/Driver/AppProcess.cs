using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace HolyScreen.E2E.Driver;

/// <summary>A running HolyScreen instance with its own isolated data directory.</summary>
internal sealed class AppProcess : IDisposable
{
    private readonly string _exe;

    private AppProcess(string exe, string dataDir, Process process)
    {
        _exe = exe;
        DataDir = dataDir;
        Process = process;
    }

    /// <summary>Every instance this run started (pid + start time), so cleanup never touches a HolyScreen the user runs.</summary>
    public static readonly List<(int Pid, DateTime Started)> Started = new();

    public Process Process { get; }
    public string DataDir { get; }
    public string LogFile => Path.Combine(DataDir, "logs", "app.log");
    public int Id => Process.Id;

    public static AppProcess Launch(string exe, string dataDir, string settingsJson)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), settingsJson);

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(dataDir);

        Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start HolyScreen.");
        InputGuard.Allow(process.Id); // its overlay and windows may take the foreground
        lock (Started) Started.Add((process.Id, process.StartTime));
        var app = new AppProcess(exe, dataDir, process);
        Check.That(app.WaitForLog("Startup complete", TimeSpan.FromSeconds(15)), "app did not finish starting");
        return app;
    }

    public string ReadLog()
    {
        try
        {
            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    public bool WaitForLog(string text, TimeSpan timeout) => Wait.Until(() => ReadLog().Contains(text, StringComparison.Ordinal), timeout);

    public IReadOnlyList<string> LogErrors() =>
        ReadLog().Split('\n').Where(l => l.Contains("[ERROR]", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();

    /// <summary>Runs a second instance with <paramref name="argument"/> (it forwards the command and exits).</summary>
    public void Signal(string argument)
    {
        var info = new ProcessStartInfo(_exe) { UseShellExecute = false };
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(DataDir);
        info.ArgumentList.Add(argument);
        using Process signaller = Process.Start(info)!;
        Check.That(signaller.WaitForExit(10_000), $"signalling instance ({argument}) did not exit");
    }

    public bool Exit(TimeSpan timeout)
    {
        if (Process.HasExited) return true;
        Signal("--exit");
        return Process.WaitForExit((int)timeout.TotalMilliseconds);
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited && !Exit(TimeSpan.FromSeconds(5)))
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }

        Process.Dispose();
    }
}

internal static class Wait
{
    public static bool Until(Func<bool> condition, TimeSpan timeout, int pollMs = 50)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(pollMs);
        }

        return condition();
    }

    public static T? For<T>(Func<T?> probe, TimeSpan timeout, int pollMs = 50) where T : class
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            T? value = probe();
            if (value is not null) return value;
            Thread.Sleep(pollMs);
        }

        return probe();
    }
}

internal sealed class E2EFailure : Exception
{
    public E2EFailure(string message) : base(message) { }
}

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new E2EFailure(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new E2EFailure($"{what}: expected {expected}, got {actual}");
        }
    }
}
