using System;
using System.Collections.Generic;
using System.Globalization;
using HolyScreen.Interop;

namespace HolyScreen.Input;

/// <summary>Modifier flags. Values match the MOD_* constants used by RegisterHotKey.</summary>
[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A keyboard shortcut expressed as modifiers plus a single virtual-key code. Every gesture must have a real key so
/// it can be registered with Windows' <c>RegisterHotKey</c>; modifier-only chords are rejected by <see cref="Validate"/>.
/// </summary>
internal readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    public static readonly HotkeyGesture Default = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'S');

    public bool IsModifierOnly => Key == 0;

    public uint ToNativeModifiers()
    {
        uint value = 0;
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) value |= Win32.MOD_ALT;
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) value |= Win32.MOD_CONTROL;
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) value |= Win32.MOD_SHIFT;
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) value |= Win32.MOD_WIN;
        return value;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Key != 0) parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Text for the UI: like <see cref="ToString"/> but OEM keys show the character of the current layout.</summary>
    public string ToDisplayString()
    {
        if (Key == 0 || !IsOemKey(Key)) return ToString();
        string text = ToString();
        int cut = text.LastIndexOf('+');
        string prefix = cut >= 0 ? text[..(cut + 1)] : string.Empty;
        uint ch = Win32.MapVirtualKeyEx((uint)Key, Win32.MAPVK_VK_TO_CHAR, Win32.GetKeyboardLayout(0)) & 0x7FFF;
        return ch > ' ' ? prefix + char.ToUpperInvariant((char)ch) : text;
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int key = 0;
        foreach (string rawPart in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    if (key != 0) return false;
                    key = ParseKeyName(rawPart);
                    if (key == 0) return false;
                    break;
            }
        }

        gesture = new HotkeyGesture(modifiers, key);
        return modifiers != HotkeyModifiers.None || key != 0;
    }

    /// <summary>Returns a user-facing reason why the gesture cannot be used, or null if it is acceptable.</summary>
    public string? Validate()
    {
        if (IsModifierOnly)
        {
            return "Add a normal key to the shortcut, for example Alt+Shift+S.";
        }

        if (IsModifierKey(Key))
        {
            return "Press a non-modifier key together with the modifiers.";
        }

        // Print Screen and F13–F24 never produce text, so they are fine on their own (or with only Shift).
        bool standalone = Key == Win32.VK_SNAPSHOT || Key is >= Win32.VK_F13 and <= Win32.VK_F24;
        bool isFunctionKey = Key is >= Win32.VK_F1 and <= Win32.VK_F24;
        bool hasStrongModifier = (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0;

        if (!hasStrongModifier && !standalone && !(isFunctionKey && Modifiers.HasFlag(HotkeyModifiers.Shift)))
        {
            return "Include Ctrl, Alt or Win so the shortcut does not interfere with normal typing.";
        }

        string name = ToString();
        foreach (string reserved in ReservedShortcuts)
        {
            if (string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase))
            {
                return $"{name} is reserved by Windows or used for everyday editing. Choose a different shortcut.";
            }
        }

        return null;
    }

    private static readonly string[] ReservedShortcuts =
    {
        "Alt+Tab", "Alt+Shift+Tab", "Alt+Esc", "Alt+F4", "Ctrl+Esc", "Ctrl+Alt+Delete", "Ctrl+Shift+Esc", "Win+L",
        "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Y", "Ctrl+Z", "Ctrl+S", "Alt+Space", "Win+D", "Win+E",
        "Shift+Win+S",
    };

    public static bool IsModifierKey(int vk) => vk is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU
        or Win32.VK_LSHIFT or Win32.VK_RSHIFT or Win32.VK_LCONTROL or Win32.VK_RCONTROL
        or Win32.VK_LMENU or Win32.VK_RMENU or Win32.VK_LWIN or Win32.VK_RWIN;

    private static bool IsOemKey(int vk) => vk is >= Win32.VK_OEM_1 and <= Win32.VK_OEM_8 || vk == Win32.VK_OEM_102;

    private static readonly Dictionary<int, string> Names = BuildNames();

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x1B] = "Esc", [0x20] = "Space",
            [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home", [0x25] = "Left", [0x26] = "Up",
            [0x27] = "Right", [0x28] = "Down", [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
            [0x5D] = "Menu", [0x6A] = "NumMultiply", [0x6B] = "NumAdd", [0x6D] = "NumSubtract", [0x6E] = "NumDecimal",
            [0x6F] = "NumDivide", [0x91] = "ScrollLock",
            [0xBA] = "Oem1", [0xBB] = "Plus", [0xBC] = "Comma", [0xBD] = "Minus", [0xBE] = "Period", [0xBF] = "Oem2",
            [0xC0] = "Oem3", [0xDB] = "Oem4", [0xDC] = "Oem5", [0xDD] = "Oem6", [0xDE] = "Oem7", [0xDF] = "Oem8",
            [0xE2] = "Oem102",
        };

        for (int c = 'A'; c <= 'Z'; c++) names[c] = ((char)c).ToString();
        for (int d = 0; d <= 9; d++)
        {
            names['0' + d] = d.ToString(CultureInfo.InvariantCulture);
            names[Win32.VK_NUMPAD0 + d] = "Num" + d.ToString(CultureInfo.InvariantCulture);
        }

        for (int f = 1; f <= 24; f++) names[Win32.VK_F1 + f - 1] = "F" + f.ToString(CultureInfo.InvariantCulture);
        return names;
    }

    public static string KeyName(int vk) =>
        Names.TryGetValue(vk, out string? name) ? name : "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);

    public static int ParseKeyName(string name)
    {
        if (string.Equals(name, "PrtSc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "PrintScrn", StringComparison.OrdinalIgnoreCase))
        {
            return Win32.VK_SNAPSHOT;
        }

        foreach (KeyValuePair<int, string> pair in Names)
        {
            if (string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        }

        if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int vk) &&
            vk is > 0 and < 0xFF)
        {
            return vk;
        }

        return 0;
    }
}
