using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public class TrayPromotionService
{
    private const string NotifyIconSettingsPath = @"Control Panel\NotifyIconSettings";

    /// <summary>
    /// Purges stale or duplicate TrayTrigger registrations from Windows 11 NotifyIconSettings.
    /// Keeps only the entry for the currently running process path.
    /// </summary>
    public static int CleanStaleRegistrations()
    {
        string? currentExePath = Environment.ProcessPath;
        string currentExeName = !string.IsNullOrEmpty(currentExePath) ? Path.GetFileName(currentExePath) : "TrayTrigger.exe";

        int removedCount = 0;
        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsPath, true);
            if (rootKey == null) return 0;

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subKeyName, false);
                if (subKey == null) continue;

                string? storedPath = subKey.GetValue("ExecutablePath") as string;
                string? tooltip = subKey.GetValue("InitialTooltip") as string;

                bool isTrayTrigger = (!string.IsNullOrEmpty(storedPath) && storedPath.EndsWith(currentExeName, StringComparison.OrdinalIgnoreCase)) ||
                                     (!string.IsNullOrEmpty(tooltip) && tooltip.Contains("TrayTrigger", StringComparison.OrdinalIgnoreCase));

                if (isTrayTrigger)
                {
                    bool isCurrent = !string.IsNullOrEmpty(currentExePath) &&
                                     string.Equals(storedPath, currentExePath, StringComparison.OrdinalIgnoreCase);

                    // A different TrayTrigger install (e.g. portable copy vs. installed copy) is
                    // still live if its executable still exists on disk; only remove entries whose
                    // target is actually gone, so live installs don't repeatedly delete each other's
                    // "always show" promotion. See M-21.
                    bool targetStillExists = !string.IsNullOrEmpty(storedPath) && File.Exists(storedPath);

                    if (!isCurrent && !targetStillExists)
                    {
                        try
                        {
                            rootKey.DeleteSubKeyTree(subKeyName, false);
                            removedCount++;
                            LoggingService.Info("TrayPromotionService", $"Removed stale tray registration for '{storedPath}' (Key: {subKeyName})");
                        }
                        catch (Exception delEx)
                        {
                            LoggingService.Warn("TrayPromotionService", $"Could not delete stale key {subKeyName}: {delEx.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("TrayPromotionService", $"Error cleaning stale tray registrations: {ex.Message}");
        }

        return removedCount;
    }

    public bool TrySetAlwaysShow(bool enable, out string statusMessage)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            statusMessage = "Cannot determine process path.";
            return false;
        }

        CleanStaleRegistrations();

        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsPath, true);
            if (rootKey == null)
            {
                statusMessage = "NotifyIconSettings registry key not found on this Windows version.";
                return false;
            }

            string[] subKeyNames = rootKey.GetSubKeyNames();
            string? targetSubKey = null;

            // 1. Prioritize exact executable path match
            foreach (var subKeyName in subKeyNames)
            {
                using var subKey = rootKey.OpenSubKey(subKeyName, false);
                if (subKey?.GetValue("ExecutablePath") is string storedPath &&
                    storedPath.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                {
                    targetSubKey = subKeyName;
                    break;
                }
            }

            // 2. Fallback: match by file name in the application directory
            if (targetSubKey == null)
            {
                string exeName = Path.GetFileName(exePath);
                string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

                foreach (var subKeyName in subKeyNames)
                {
                    using var subKey = rootKey.OpenSubKey(subKeyName, false);
                    if (subKey?.GetValue("ExecutablePath") is string storedPath &&
                        storedPath.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) &&
                        storedPath.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
                    {
                        targetSubKey = subKeyName;
                        break;
                    }
                }
            }

            if (targetSubKey == null)
            {
                statusMessage = "Windows has not registered this application icon yet. The icon must appear in the tray overflow first before Windows creates its settings entry.";
                return false;
            }

            using (var writeSubKey = rootKey.OpenSubKey(targetSubKey, true))
            {
                if (writeSubKey != null)
                {
                    writeSubKey.SetValue("IsPromoted", enable ? 1 : 0, RegistryValueKind.DWord);
                }
            }

            statusMessage = enable
                ? "Requested Windows to keep icon always visible on taskbar."
                : "Icon reset to default Windows overflow behavior.";
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("TrayPromotionService", $"Error writing to NotifyIconSettings: {ex.Message}");
            statusMessage = $"Registry access error: {ex.Message}";
            return false;
        }
    }

    public static void OpenWindowsTaskbarSettings()
    {
        CleanStaleRegistrations();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:taskbar",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("TrayPromotionService", $"Error opening taskbar settings: {ex.Message}");
        }
    }

    public bool? IsCurrentlyPromoted()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return null;

        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsPath, false);
            if (rootKey == null) return null;

            // Prioritize exact match first
            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                using var subKey = rootKey.OpenSubKey(subKeyName, false);
                if (subKey?.GetValue("ExecutablePath") is string storedPath &&
                    storedPath.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (subKey.GetValue("IsPromoted") is int intVal)
                    {
                        return intVal == 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("TrayPromotionService", $"Error reading promotion status: {ex.Message}");
        }

        return null;
    }
}
