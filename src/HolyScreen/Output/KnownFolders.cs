using System;
using System.IO;
using System.Runtime.InteropServices;
using HolyScreen.Interop;

namespace HolyScreen.Output;

internal static class KnownFolders
{
    /// <summary>
    /// The Windows "Screenshots" known folder (normally Pictures\Screenshots). Resolved through the shell so it follows
    /// OneDrive "back up Pictures" redirection and user relocation; created if missing.
    /// </summary>
    public static string GetScreenshotsFolder()
    {
        IntPtr path = IntPtr.Zero;
        try
        {
            if (Win32.SHGetKnownFolderPath(Win32.FOLDERID_Screenshots, Win32.KF_FLAG_CREATE, IntPtr.Zero, out path) == 0 &&
                path != IntPtr.Zero)
            {
                string? resolved = Marshal.PtrToStringUni(path);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }
        }
        catch (Exception)
        {
            // Fall through to the conventional location.
        }
        finally
        {
            // Must be freed whether or not the call succeeded.
            if (path != IntPtr.Zero) Win32.CoTaskMemFree(path);
        }

        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures))
        {
            pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        }

        return Path.Combine(pictures, "Screenshots");
    }
}
