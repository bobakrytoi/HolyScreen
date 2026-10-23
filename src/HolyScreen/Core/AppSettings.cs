using System;
using System.Globalization;
using HolyScreen.Input;

namespace HolyScreen.Core;

internal enum ImageFileFormat
{
    Png,
    Jpeg,
}

/// <summary>
/// User preferences, stored as plain JSON. "Launch at Windows startup" is not stored here: the registry Run entry
/// is the single source of truth.
/// </summary>
internal sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultCaptureHotkey = "Alt+Shift+S";
    public const string DefaultFileNameTemplate = "Screenshot_{yyyy-MM-dd_HH-mm-ss}";
    public const string DefaultAnnotationColor = "#FF3B30";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Shortcuts. An empty string means "not assigned"; every assigned one must contain a real key
    // (Windows' RegisterHotKey cannot express a modifier-only chord).
    public string HotkeyCaptureRegion { get; set; } = DefaultCaptureHotkey;
    public string HotkeyCaptureFullScreen { get; set; } = string.Empty;
    public string HotkeyRepeatLastRegion { get; set; } = string.Empty;
    public string HotkeyCaptureText { get; set; } = string.Empty;
    public string HotkeyPickColor { get; set; } = string.Empty;

    // After capture
    public bool CloseAfterCopy { get; set; } = true;
    public bool CloseAfterSave { get; set; } = true;
    public bool AutoCopyOnSelection { get; set; }
    public bool ShowMagnifier { get; set; } = true;
    public bool CaptureSound { get; set; }
    public double SoundVolume { get; set; } = 0.5;
    public bool CaptureFlash { get; set; } = true;

    // Output. An empty folder means the Windows "Screenshots" known folder (Pictures\Screenshots, OneDrive-aware).
    public string SaveFolder { get; set; } = string.Empty;
    public ImageFileFormat SaveFormat { get; set; } = ImageFileFormat.Png;
    public int JpegQuality { get; set; } = 90;
    public string FileNameTemplate { get; set; } = DefaultFileNameTemplate;

    // Annotation defaults
    public string AnnotationColor { get; set; } = DefaultAnnotationColor;
    public double AnnotationThickness { get; set; } = 4;
    public double TextSize { get; set; } = 22;

    // History
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryLimit { get; set; } = 20;

    // Appearance
    public bool AnimationsEnabled { get; set; } = true;

    // Beautify (remembered between uses)
    public int BeautifyBackground { get; set; }
    public int BeautifyPadding { get; set; } = 64;
    public int BeautifyRadius { get; set; } = 12;
    public bool BeautifyShadow { get; set; } = true;

    /// <summary>Last confirmed selection as x, y, width, height in virtual-screen pixels (for "repeat last region").</summary>
    public int[]? LastRegion { get; set; }

    public string GetHotkey(HotkeyAction action) => action switch
    {
        HotkeyAction.CaptureRegion => HotkeyCaptureRegion,
        HotkeyAction.CaptureFullScreen => HotkeyCaptureFullScreen,
        HotkeyAction.RepeatLastRegion => HotkeyRepeatLastRegion,
        HotkeyAction.CaptureText => HotkeyCaptureText,
        HotkeyAction.PickColor => HotkeyPickColor,
        _ => string.Empty,
    };

    public void SetHotkey(HotkeyAction action, string value)
    {
        switch (action)
        {
            case HotkeyAction.CaptureRegion: HotkeyCaptureRegion = value; break;
            case HotkeyAction.CaptureFullScreen: HotkeyCaptureFullScreen = value; break;
            case HotkeyAction.RepeatLastRegion: HotkeyRepeatLastRegion = value; break;
            case HotkeyAction.CaptureText: HotkeyCaptureText = value; break;
            case HotkeyAction.PickColor: HotkeyPickColor = value; break;
        }
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.LastRegion = LastRegion is null ? null : (int[])LastRegion.Clone();
        return copy;
    }

    /// <summary>Clamps every value into its supported range so a hand-edited file cannot break the app.</summary>
    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;

        HotkeyCaptureRegion = HotkeyCaptureRegion?.Trim() ?? string.Empty;
        HotkeyCaptureFullScreen = HotkeyCaptureFullScreen?.Trim() ?? string.Empty;
        HotkeyRepeatLastRegion = HotkeyRepeatLastRegion?.Trim() ?? string.Empty;
        HotkeyCaptureText = HotkeyCaptureText?.Trim() ?? string.Empty;
        HotkeyPickColor = HotkeyPickColor?.Trim() ?? string.Empty;

        SaveFolder = SaveFolder?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(SaveFormat)) SaveFormat = ImageFileFormat.Png;
        JpegQuality = Math.Clamp(JpegQuality, 30, 100);
        if (string.IsNullOrWhiteSpace(FileNameTemplate)) FileNameTemplate = DefaultFileNameTemplate;
        FileNameTemplate = FileNameTemplate.Trim();
        if (FileNameTemplate.Length > 120) FileNameTemplate = FileNameTemplate[..120];

        if (!TryParseColor(AnnotationColor, out _)) AnnotationColor = DefaultAnnotationColor;
        AnnotationThickness = Math.Clamp(Finite(AnnotationThickness, 4), 1, 40);
        TextSize = Math.Clamp(Finite(TextSize, 22), 8, 160);
        SoundVolume = Math.Clamp(Finite(SoundVolume, 0.5), 0, 1);
        HistoryLimit = Math.Clamp(HistoryLimit, 0, 200);
        BeautifyBackground = Math.Clamp(BeautifyBackground, 0, 31);
        BeautifyPadding = Math.Clamp(BeautifyPadding, 0, 400);
        BeautifyRadius = Math.Clamp(BeautifyRadius, 0, 80);

        if (LastRegion is not { Length: 4 } || LastRegion[2] <= 0 || LastRegion[3] <= 0)
        {
            LastRegion = null;
        }
    }

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB" into ARGB components.</summary>
    public static bool TryParseColor(string? text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length is not (6 or 8)) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return false;
        argb = s.Length == 6 ? 0xFF000000u | value : value;
        return true;
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
