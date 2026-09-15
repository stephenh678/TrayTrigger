using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// Whether a Store (packaged) app is still installed for this Windows user, read from the per-user
/// package repository in the registry - the same records <see cref="XboxScannerService"/> reads. A
/// package counts as installed when a version of it is registered and its package folder is there.
/// </summary>
public static class PackagedApps
{
    /// <summary>
    /// True when <paramref name="appId"/>'s package is installed for this user. A malformed ID is never
    /// installed. When the registry can't be read the answer is true: unknown isn't missing, so the
    /// launch goes ahead and activation reports its own error.
    /// </summary>
    public static bool IsInstalled(string? appId)
    {
        string? family = ToolCatalog.PackageFamilyNameOf(appId);
        if (family == null) return false;
        try
        {
            using var repository = Registry.CurrentUser.OpenSubKey(XboxScannerService.PackageRepositoryKeyPath);
            if (repository == null) return false;
            foreach (string fullName in repository.GetSubKeyNames().Where(n => IsVersionOf(n, family)))
            {
                using var packageKey = repository.OpenSubKey(fullName);
                if (packageKey?.GetValue("PackageRootFolder") is string root && !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("PackagedApps", $"Could not check whether '{appId}' is installed: {ex.Message}");
            return true;
        }
    }

    /// <summary>Whether a package full name ("Name_Version_Arch_ResourceId_PublisherId") is a version of <paramref name="packageFamilyName"/> ("Name_PublisherId").</summary>
    internal static bool IsVersionOf(string packageFullName, string packageFamilyName) =>
        string.Equals(XboxScannerService.ToPackageFamilyName(packageFullName), packageFamilyName, StringComparison.OrdinalIgnoreCase);
}
