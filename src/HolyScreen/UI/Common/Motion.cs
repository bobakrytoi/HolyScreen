using HolyScreen.Core;

namespace HolyScreen.UI.Common;

/// <summary>Whether to animate: both the app's own switch and Windows' "Animation effects" must allow it.</summary>
internal static class Motion
{
    public static bool Enabled(AppSettings settings) => settings.AnimationsEnabled && SystemInfo.AnimationsEnabled;
}
