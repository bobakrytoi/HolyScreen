using HolyScreen.Input;
using Xunit;

namespace HolyScreen.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Alt+Shift+S")]
    [InlineData("Ctrl+Alt+K")]
    [InlineData("Shift+Win+Space")]
    [InlineData("Ctrl+F12")]
    [InlineData("PrintScreen")]
    [InlineData("Ctrl+PrintScreen")]
    public void ParseThenToString_RoundTrips(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture));
        Assert.Equal(text, gesture.ToString());
    }

    [Fact]
    public void Parse_IsCaseInsensitiveAndTrims()
    {
        Assert.True(HotkeyGesture.TryParse(" shift + alt + s ", out HotkeyGesture gesture));
        Assert.Equal("Alt+Shift+S", gesture.ToString());
    }

    [Theory]
    [InlineData("PrtSc")]
    [InlineData("PrintScrn")]
    public void Parse_AcceptsPrintScreenAliases(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture));
        Assert.Equal("PrintScreen", gesture.ToString());
    }

    [Fact]
    public void Default_IsAltShiftS_AndValid()
    {
        Assert.Equal("Alt+Shift+S", HotkeyGesture.Default.ToString());
        Assert.Null(HotkeyGesture.Default.Validate());
    }

    [Theory]
    [InlineData("PrintScreen")]
    [InlineData("Shift+PrintScreen")]
    [InlineData("F13")]
    [InlineData("Shift+F5")]
    public void Validate_AllowsKeysThatNeverProduceText(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture));
        Assert.Null(gesture.Validate());
    }

    [Theory]
    [InlineData("Ctrl+Shift")]   // modifier-only
    [InlineData("Ctrl+C")]       // everyday editing
    [InlineData("Win+Shift+S")]  // Windows' own snipping shortcut
    [InlineData("Shift+A")]      // would break typing
    [InlineData("S")]
    public void Validate_RejectsUnusableShortcuts(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture));
        Assert.NotNull(gesture.Validate());
    }

    [Fact]
    public void ToNativeModifiers_MapsAllFlags()
    {
        HotkeyGesture.TryParse("Ctrl+Alt+Shift+Win+P", out HotkeyGesture gesture);
        Assert.Equal(0x0001u | 0x0002u | 0x0004u | 0x0008u, gesture.ToNativeModifiers());
    }

    [Fact]
    public void Parse_RejectsTwoKeys()
    {
        Assert.False(HotkeyGesture.TryParse("Ctrl+A+B", out _));
    }
}
