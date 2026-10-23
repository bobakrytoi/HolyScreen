using System;
using System.IO;
using HolyScreen.Core;
using HolyScreen.Input;
using HolyScreen.UI.Common;
using Xunit;

namespace HolyScreen.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var s = new AppSettings();
        Assert.Equal("Alt+Shift+S", s.HotkeyCaptureRegion);
        Assert.True(s.CloseAfterCopy);
        Assert.Equal(ImageFileFormat.Png, s.SaveFormat);
        Assert.Equal("Screenshot_{yyyy-MM-dd_HH-mm-ss}", s.FileNameTemplate);
        Assert.Equal(string.Empty, s.SaveFolder);
    }

    [Fact]
    public void Normalize_RepairsHandEditedValues()
    {
        var s = new AppSettings
        {
            JpegQuality = 5000,
            FileNameTemplate = "  ",
            AnnotationColor = "red",
            AnnotationThickness = double.NaN,
            TextSize = -3,
            HistoryLimit = -1,
            LastRegion = new[] { 1, 2, 0, 0 },
        };

        s.Normalize();

        Assert.Equal(100, s.JpegQuality);
        Assert.Equal(AppSettings.DefaultFileNameTemplate, s.FileNameTemplate);
        Assert.Equal(AppSettings.DefaultAnnotationColor, s.AnnotationColor);
        Assert.Equal(4, s.AnnotationThickness);
        Assert.Equal(8, s.TextSize);
        Assert.Equal(0, s.HistoryLimit);
        Assert.Null(s.LastRegion);
    }

    [Fact]
    public void Json_RoundTripsWithEnumsAsText()
    {
        var s = new AppSettings { SaveFormat = ImageFileFormat.Jpeg, HotkeyPickColor = "Ctrl+Alt+C", LastRegion = new[] { -10, 5, 300, 200 } };
        string json = SettingsService.Serialize(s);
        Assert.Contains("\"Jpeg\"", json);

        AppSettings back = SettingsService.Deserialize(json)!;
        Assert.Equal(ImageFileFormat.Jpeg, back.SaveFormat);
        Assert.Equal("Ctrl+Alt+C", back.GetHotkey(HotkeyAction.PickColor));
        Assert.Equal(new[] { -10, 5, 300, 200 }, back.LastRegion);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var s = new AppSettings { LastRegion = new[] { 1, 2, 3, 4 } };
        AppSettings copy = s.Clone();
        copy.LastRegion![0] = 99;
        Assert.Equal(1, s.LastRegion[0]);
    }

    [Theory]
    [InlineData("#FF3B30", 0xFFFF3B30u)]
    [InlineData("80FF3B30", 0x80FF3B30u)]
    public void TryParseColor_AcceptsRgbAndArgb(string text, uint expected)
    {
        Assert.True(AppSettings.TryParseColor(text, out uint argb));
        Assert.Equal(expected, argb);
    }

    [Fact]
    public void LoadingMissingFile_GivesDefaults_AndDamagedFileIsPreserved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HolyScreenTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            Sta.Run(() => Assert.Equal("Alt+Shift+S", SettingsService.Load(path).Current.HotkeyCaptureRegion));

            File.WriteAllText(path, "{ not json");
            Sta.Run(() => Assert.Equal("Alt+Shift+S", SettingsService.Load(path).Current.HotkeyCaptureRegion));
            Assert.Single(Directory.GetFiles(dir, "settings.json.damaged-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class IconTests
{
    private static byte[] LoadAppIcon()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "HolyScreen.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return File.ReadAllBytes(Path.Combine(dir!, "src", "HolyScreen", "Assets", "AppIcon.ico"));
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(18, 20)]
    [InlineData(24, 24)]
    [InlineData(30, 32)]
    [InlineData(48, 48)]
    [InlineData(300, 256)]
    public void TryPickEntry_ChoosesSmallestEntryAtLeastTheRequestedSize(int requested, int expected)
    {
        byte[] ico = LoadAppIcon();
        Assert.True(IconLoader.TryPickEntry(ico, requested, out int offset, out int length));
        Assert.True(length > 0);

        int actual = IsPng(ico, offset) ? ReadPngWidth(ico, offset) : BitConverter.ToInt32(ico, offset + 4);
        Assert.Equal(expected, actual);
    }

    private static bool IsPng(byte[] data, int offset) => data[offset] == 0x89 && data[offset + 1] == (byte)'P';

    private static int ReadPngWidth(byte[] data, int offset) =>
        (data[offset + 16] << 24) | (data[offset + 17] << 16) | (data[offset + 18] << 8) | data[offset + 19];
}
