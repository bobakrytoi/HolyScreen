using System;
using Microsoft.Win32;

namespace HolyScreen.Core;

/// <summary>"Launch at Windows startup" via the per-user Run key (no administrator rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "HolyScreen";

    public static string BuildCommand()
    {
        string command = $"\"{AppPaths.ExecutablePath}\" --startup";
        if (AppPaths.IsCustomDataDirectory)
        {
            command += $" --data-dir \"{AppPaths.DataDirectory}\"";
        }

        return command;
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string command &&
                   command.Contains(AppPaths.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the startup registration", ex);
            return false;
        }
    }

    /// <summary>True when the user disabled the entry in Task Manager's Startup apps page.</summary>
    public static bool IsDisabledInTaskManager()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            if (key?.GetValue(ValueName) is byte[] data && data.Length > 0)
            {
                // Explorer stores 0x02/0x06 for enabled and 0x03/0x07 for disabled entries.
                return (data[0] & 0x01) != 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read StartupApproved state", ex);
        }

        return false;
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not change the startup registration", ex);
            return false;
        }
    }
}
