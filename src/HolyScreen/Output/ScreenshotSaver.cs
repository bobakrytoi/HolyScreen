using System;
using System.IO;
using System.Windows.Media.Imaging;
using HolyScreen.Core;

namespace HolyScreen.Output;

/// <summary>Writes finished screenshots to disk without ever overwriting an existing file.</summary>
internal static class ScreenshotSaver
{
    private const int MaxSuffix = 9999;

    /// <summary>The configured folder, or the Windows Screenshots known folder when none is configured.</summary>
    public static string ResolveFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.SaveFolder)
            ? KnownFolders.GetScreenshotsFolder()
            : Environment.ExpandEnvironmentVariables(settings.SaveFolder);

    /// <summary>Encodes and saves <paramref name="image"/>; returns the full path of the new file.</summary>
    public static string Save(BitmapSource image, AppSettings settings, DateTime timestamp)
    {
        string folder = ResolveFolder(settings);
        byte[] data = ImageEncoding.Encode(image, settings.SaveFormat, settings.JpegQuality);
        string baseName = FileNameTemplate.Expand(settings.FileNameTemplate, timestamp);
        return WriteNew(folder, baseName, ImageEncoding.Extension(settings.SaveFormat), data);
    }

    /// <summary>Writes <paramref name="data"/> to the first free name: base.ext, base_2.ext, base_3.ext …</summary>
    public static string WriteNew(string folder, string baseName, string extension, byte[] data)
    {
        Directory.CreateDirectory(folder);
        for (int suffix = 1; suffix <= MaxSuffix; suffix++)
        {
            string name = suffix == 1 ? baseName + extension : $"{baseName}_{suffix}{extension}";
            string path = Path.Combine(folder, name);
            if (File.Exists(path)) continue;

            try
            {
                // CreateNew fails instead of overwriting if another writer took the name in the meantime.
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(data, 0, data.Length);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Lost a race for this name; try the next one.
            }
        }

        throw new IOException($"No free file name for \"{baseName}\" in {folder}.");
    }
}
