using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.Editor;
using HolyScreen.Output;

namespace HolyScreen.Core;

/// <summary>One remembered capture. Files live in <see cref="Folder"/>.</summary>
internal sealed record HistoryEntry(string Id, string Folder, DateTime CapturedAt, PixelRect Region, int AnnotationCount, string? SavedPath)
{
    /// <summary>The untouched pixels of the selection (re-editing starts from these).</summary>
    public string OriginalPath => Path.Combine(Folder, "original.png");

    /// <summary>The finished picture as it was copied/saved, annotations included.</summary>
    public string ResultPath => Path.Combine(Folder, "result.png");

    public string ThumbnailPath => Path.Combine(Folder, "thumb.png");
    public string AnnotationsPath => Path.Combine(Folder, "annotations.json");
}

/// <summary>
/// The last N captures on disk under %LOCALAPPDATA%\HolyScreen\history, each with its original pixels, the finished
/// image, a thumbnail and the annotations as JSON — so a capture can be re-copied or opened again for editing.
/// </summary>
internal sealed class HistoryService
{
    private const string MetaFile = "meta.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _root;
    private readonly Func<AppSettings> _settings;

    public HistoryService(string root, Func<AppSettings> settings)
    {
        _root = root;
        _settings = settings;
    }

    public string Root => _root;

    /// <summary>Raised after an entry was added or removed.</summary>
    public event Action? Changed;

    /// <summary>Newest first. Entries whose files are damaged are skipped.</summary>
    public IReadOnlyList<HistoryEntry> Load()
    {
        var entries = new List<HistoryEntry>();
        if (!Directory.Exists(_root)) return entries;

        foreach (string folder in Directory.GetDirectories(_root))
        {
            try
            {
                string metaPath = Path.Combine(folder, MetaFile);
                if (!File.Exists(metaPath)) continue;
                Meta? meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath), Json);
                if (meta is null || PixelRect.FromArray(meta.Region) is not PixelRect region) continue;
                var entry = new HistoryEntry(Path.GetFileName(folder), folder, meta.CapturedAt, region, meta.Annotations, meta.SavedPath);
                if (File.Exists(entry.ResultPath)) entries.Add(entry);
            }
            catch (Exception ex)
            {
                Log.Warn("Skipping an unreadable history entry", ex);
            }
        }

        return entries.OrderByDescending(e => e.CapturedAt).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Stores a capture and trims the history to the configured size. Returns null when history is off.</summary>
    public HistoryEntry? Add(BitmapSource original, BitmapSource result, PixelRect region, IReadOnlyList<Annotation> annotations,
        DateTime capturedAt, string? savedPath)
    {
        AppSettings settings = _settings();
        if (!settings.HistoryEnabled || settings.HistoryLimit <= 0) return null;

        string id = capturedAt.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string folder = Path.Combine(_root, id);
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(_root, $"{id}-{n}");
        Directory.CreateDirectory(folder);

        var entry = new HistoryEntry(Path.GetFileName(folder), folder, capturedAt, region, annotations.Count, savedPath);
        try
        {
            File.WriteAllBytes(entry.OriginalPath, ImageEncoding.Encode(original, ImageFileFormat.Png));
            File.WriteAllBytes(entry.ResultPath, ImageEncoding.Encode(result, ImageFileFormat.Png));
            File.WriteAllBytes(entry.ThumbnailPath, ImageEncoding.Encode(Thumbnail(result), ImageFileFormat.Png));
            File.WriteAllText(entry.AnnotationsPath, JsonSerializer.Serialize(annotations.ToList(), Json));
            var meta = new Meta { CapturedAt = capturedAt, Region = region.ToArray(), Annotations = annotations.Count, SavedPath = savedPath };
            File.WriteAllText(Path.Combine(folder, MetaFile), JsonSerializer.Serialize(meta, Json)); // written last = complete
        }
        catch (Exception ex)
        {
            Log.Error("Writing a history entry failed", ex);
            TryDeleteFolder(folder);
            return null;
        }

        Trim(settings.HistoryLimit);
        Changed?.Invoke();
        return entry;
    }

    public List<Annotation> LoadAnnotations(HistoryEntry entry)
    {
        try
        {
            return File.Exists(entry.AnnotationsPath)
                ? JsonSerializer.Deserialize<List<Annotation>>(File.ReadAllText(entry.AnnotationsPath), Json) ?? new List<Annotation>()
                : new List<Annotation>();
        }
        catch (Exception ex)
        {
            Log.Warn("History annotations could not be read", ex);
            return new List<Annotation>();
        }
    }

    public void Delete(HistoryEntry entry)
    {
        TryDeleteFolder(entry.Folder);
        Changed?.Invoke();
    }

    public void Clear()
    {
        foreach (HistoryEntry entry in Load()) TryDeleteFolder(entry.Folder);
        Changed?.Invoke();
    }

    /// <summary>Removes the oldest entries beyond <paramref name="limit"/>.</summary>
    public void Trim(int limit)
    {
        foreach (HistoryEntry old in Load().Skip(Math.Max(0, limit))) TryDeleteFolder(old.Folder);
    }

    public static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // do not keep the file locked
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Thumbnail(BitmapSource image)
    {
        double scale = Math.Min(1, Math.Min(360.0 / image.PixelWidth, 240.0 / image.PixelHeight));
        if (scale >= 1) return image;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        int width = Math.Max(1, (int)Math.Round(image.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(image.PixelHeight * scale));
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn("A history folder could not be deleted", ex);
        }
    }

    private sealed class Meta
    {
        public DateTime CapturedAt { get; set; }
        public int[]? Region { get; set; }
        public int Annotations { get; set; }
        public string? SavedPath { get; set; }
    }
}
