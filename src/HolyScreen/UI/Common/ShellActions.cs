using System;
using System.Diagnostics;
using System.IO;
using HolyScreen.Core;

namespace HolyScreen.UI.Common;

/// <summary>Opening folders and revealing files in Explorer.</summary>
internal static class ShellActions
{
    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open a folder in Explorer", ex);
        }
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not reveal a file in Explorer", ex);
        }
    }
}
