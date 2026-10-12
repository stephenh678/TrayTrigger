using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TrayTrigger.Services.Theming;

/// <summary>
/// Whether Windows keeps a file deleted from this drive in the Recycle Bin. It does not when the bin is turned off for the
/// drive (Recycle Bin Properties: "Don't move files to the Recycle Bin. Remove files immediately when deleted") or when a
/// policy says so; then "send to the Recycle Bin" is a plain delete, and what the screen says has to say so.
/// </summary>
internal static class RecycleBinState
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    /// <summary>True when a file deleted at this path is kept in the Recycle Bin; true too when that cannot be told (the usual case).</summary>
    public static bool IsOnFor(string path)
    {
        try
        {
            if (PolicySaysNo(Registry.CurrentUser) || PolicySaysNo(Registry.LocalMachine)) return false;

            var root = new StringBuilder(260);
            if (!GetVolumePathName(path, root, root.Capacity)) return true;
            var volume = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root.ToString(), volume, volume.Capacity)) return true;

            // "\\?\Volume{guid}\" is the key's name without the prefix: "{guid}".
            string name = volume.ToString();
            int open = name.IndexOf('{'), close = name.IndexOf('}');
            if (open < 0 || close < open) return true;
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + name[open..(close + 1)]);
            return key?.GetValue("NukeOnDelete") is not int nuke || nuke == 0;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Theme", ex, "reading whether the Recycle Bin is on");
            return true;
        }
    }

    private static bool PolicySaysNo(RegistryKey hive)
    {
        using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
        return key?.GetValue("NoRecycleFiles") is int value && value != 0;
    }
}
