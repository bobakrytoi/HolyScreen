using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyScreen.Core;

/// <summary>
/// Minimal rolling file logger for diagnostics. Call sites log fixed messages, sizes, error codes and exception
/// types — never screenshot contents, recognised text or clipboard data.
/// </summary>
internal static class Log
{
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _file;

    public static string? FilePath => _file;

    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _file = Path.Combine(directory, "app.log");
        }
        catch
        {
            _file = null; // logging is best effort
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        string? file = _file;
        if (file is null) return;

        var sb = new StringBuilder(256);
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append("Z [").Append(level).Append("] ").Append(message);

        if (ex is not null)
        {
            sb.AppendLine();
            AppendException(sb, ex);
        }

        sb.AppendLine();

        lock (Gate)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Copy(file, file + ".1", overwrite: true);
                    File.Delete(file);
                }

                File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the application down.
            }
        }
    }

    private static void AppendException(StringBuilder sb, Exception ex)
    {
        int depth = 0;
        for (Exception? current = ex; current is not null && depth < 5; current = current.InnerException, depth++)
        {
            if (depth > 0) sb.Append("  ---> ");
            sb.Append("    ").Append(current.GetType().FullName)
              .Append(" (0x").Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture)).Append("): ")
              .Append(current.Message);
            sb.AppendLine();
            if (current.StackTrace is { } trace) sb.AppendLine(trace);
        }
    }
}
