using System;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// Reads one registry value for a check that only wants to know what is there. A key or value that is
/// missing, or a read that fails, comes back as null (a failure is noted in the verbose log), so the caller
/// states only what a missing value means for it.
/// </summary>
internal static class RegistryValues
{
    /// <param name="category">The log category of the caller.</param>
    /// <param name="doing">What was being read, when the name of the calling member does not say it.</param>
    public static object? Read(RegistryKey root, string path, string name, string category, string? doing = null,
        [CallerMemberName] string member = "")
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(category, ex, doing, member);
            return null;
        }
    }

    /// <summary>The value when it is a DWORD; null when it is missing, of another kind, or unreadable.</summary>
    public static int? Dword(RegistryKey root, string path, string name, string category, string? doing = null,
        [CallerMemberName] string member = "") =>
        Read(root, path, name, category, doing, member) is int value ? value : null;
}
