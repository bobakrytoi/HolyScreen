using System;
using System.Globalization;
using System.IO;
using HolyScreen.Core;
using HolyScreen.E2E.Driver;

namespace HolyScreen.E2E;

/// <summary>Paths and helpers shared by all scenarios of one E2E run.</summary>
internal sealed class E2EContext
{
    private E2EContext(string repoRoot, string config, string appExe, string runDir)
    {
        RepoRoot = repoRoot;
        Config = config;
        AppExe = appExe;
        RunDir = runDir;
    }

    public string RepoRoot { get; }
    public string Config { get; }
    public string AppExe { get; }

    /// <summary>artifacts\e2e\&lt;timestamp&gt; — screenshots, data dirs and logs of this run (git-ignored).</summary>
    public string RunDir { get; }

    public static E2EContext Create(string config, string? exeOverride = null)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "HolyScreen.slnx"))) root = Path.GetDirectoryName(root);
        if (root is null) throw new InvalidOperationException("Repository root (HolyScreen.slnx) not found.");

        string exe = exeOverride ?? Path.Combine(root, "src", "HolyScreen", "bin", config, "net10.0-windows10.0.17763.0", "HolyScreen.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException($"App not built: {exe}");

        string run = Path.Combine(root, "artifacts", "e2e", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(run);
        return new E2EContext(root, config, exe, run);
    }

    public string ScenarioDir(string scenario)
    {
        string dir = Path.Combine(RunDir, scenario);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Starts the app with a fresh data directory whose settings are the defaults plus <paramref name="configure"/>.</summary>
    public AppProcess Launch(string scenario, Action<AppSettings>? configure = null)
    {
        var settings = new AppSettings { SaveFolder = Path.Combine(ScenarioDir(scenario), "shots") };
        configure?.Invoke(settings);
        settings.Normalize();
        AppProcess app = AppProcess.Launch(AppExe, Path.Combine(ScenarioDir(scenario), "data"), SettingsService.Serialize(settings));

        // The app warms its overlay up once after start; measure real captures against a warm app, like a user would.
        if (!app.WaitForLog("Overlay warm-up finished", TimeSpan.FromSeconds(6))) Note("warm-up did not report back");
        return app;
    }

    public string Snapshot(string scenario, string name) => Screen.Snapshot(ScenarioDir(scenario), name);

    /// <summary>Saves only the part of the screen a scenario is about (keeps unrelated windows out of the artifacts).</summary>
    public string SnapshotRegion(string scenario, string name, Capture.PixelRect region) =>
        Screen.Snapshot(ScenarioDir(scenario), name, region.Intersect(Screen.VirtualScreen));

    public void Note(string message)
    {
        Console.WriteLine("    " + message);
        File.AppendAllText(Path.Combine(RunDir, "run.log"), message + Environment.NewLine);
    }
}
