using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HolyScreen.Output;

/// <summary>
/// Turns a file-name template such as <c>Screenshot_{yyyy-MM-dd_HH-mm-ss}</c> into a safe file name. Everything
/// inside braces is a .NET custom date/time format applied to the capture time; text outside braces is kept as is.
/// </summary>
internal static class FileNameTemplate
{
    public const string Default = "Screenshot_{yyyy-MM-dd_HH-mm-ss}";
    private const int MaxLength = 150;

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Expands <paramref name="template"/> for <paramref name="time"/>. Never returns an empty or unsafe name.</summary>
    public static string Expand(string? template, DateTime time)
    {
        string source = string.IsNullOrWhiteSpace(template) ? Default : template.Trim();
        var sb = new StringBuilder(source.Length + 16);

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '{')
            {
                int end = source.IndexOf('}', i + 1);
                if (end > i + 1)
                {
                    sb.Append(FormatTime(time, source.Substring(i + 1, end - i - 1)));
                    i = end;
                    continue;
                }
            }

            sb.Append(c);
        }

        return Sanitize(sb.ToString());
    }

    private static string FormatTime(DateTime time, string format)
    {
        // A single letter would be a *standard* format ("d" = short date); "%d" forces the custom meaning.
        string effective = format.Length == 1 ? "%" + format : format;
        try
        {
            return time.ToString(effective, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        string result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > MaxLength) result = result[..MaxLength].TrimEnd('.', ' ');
        if (result.Length == 0) return "Screenshot";

        string stem = result.Split('.')[0];
        foreach (string reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return "_" + result;
        }

        return result;
    }
}
