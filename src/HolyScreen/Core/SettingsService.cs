using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace HolyScreen.Core;

/// <summary>Loads and persists <see cref="AppSettings"/>. Changes are applied immediately and saved shortly after.</summary>
internal sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly DispatcherTimer _saveTimer;
    private readonly object _writeGate = new();

    private SettingsService(string path, AppSettings settings)
    {
        _path = path;
        Current = settings;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveInBackground();
        };
    }

    public AppSettings Current { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="Current"/> changed.</summary>
    public event EventHandler<AppSettings>? Changed;

    public static SettingsService Load(string path)
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep the unreadable file for inspection instead of silently overwriting it.
            Log.Warn("Settings file could not be read; using defaults", ex);
            TryPreserveDamagedFile(path);
            settings = new AppSettings();
        }

        settings.Normalize();
        return new SettingsService(path, settings);
    }

    /// <summary>Applies a change to a copy of the settings, publishes it and schedules a save.</summary>
    public void Update(Action<AppSettings> change)
    {
        AppSettings copy = Current.Clone();
        change(copy);
        copy.Normalize();
        Current = copy;
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(this, copy);
    }

    /// <summary>Writes pending changes synchronously (used on shutdown).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
        }

        WriteFile(JsonSerializer.Serialize(Current, JsonOptions));
    }

    internal static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    internal static AppSettings? Deserialize(string json) => JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

    private void SaveInBackground()
    {
        string json = JsonSerializer.Serialize(Current, JsonOptions);
        _ = Task.Run(() => WriteFile(json));
    }

    private void WriteFile(string json)
    {
        lock (_writeGate)
        {
            try
            {
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Settings could not be saved", ex);
            }
        }
    }

    private static void TryPreserveDamagedFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Damaged settings file could not be renamed", ex);
        }
    }
}
