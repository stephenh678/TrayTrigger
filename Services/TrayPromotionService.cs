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

    /// <summary>
    /// Raised after <see cref="TrySetAlwaysShow"/> actually changed the stored value. Explorer
    /// reads an icon's NotifyIconSettings entry when the icon is registered and keeps the answer
    /// in memory, so a value written afterwards is ignored until the icon is added again: the
    /// owner of the tray icon subscribes and re-adds it.
    /// </summary>
    public event Action? PromotionApplied;

    public enum Outcome
    {
        /// <summary>The value was written; the tray icon should be re-added for Explorer to notice.</summary>
        Applied,
        /// <summary>Windows already had the requested value; nothing written.</summary>
        Unchanged,
        /// <summary>Windows has not created the icon's settings entry yet (it does so a moment after the icon first appears). Worth retrying.</summary>
        NotRegisteredYet,
        Failed
    }

    /// <summary>
    /// Writes the icon's "always show" flag. Writes only when the stored value differs, so a start
    /// with the setting on doesn't rewrite Windows' entry every time, and reports which of the
    /// four things happened so the caller can retry, re-add the icon, or show the reason.
    /// </summary>
    public Outcome TrySetAlwaysShow(bool enable, out string statusMessage)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            statusMessage = "Cannot determine process path.";
            return Outcome.Failed;
        }

        CleanStaleRegistrations();

        try
        {
            using var rootKey = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsPath, true);
            if (rootKey == null)
            {
                statusMessage = "This version of Windows has no per-icon tray settings (Windows 11 only). Use the Taskbar Settings button instead.";
                return Outcome.Failed;
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
                statusMessage = "Windows hasn't registered the TrayTrigger icon yet; it does that a moment after the icon first appears. Tick this again in a few seconds, or drag the icon from the overflow onto the taskbar.";
                return Outcome.NotRegisteredYet;
            }

            int wanted = enable ? 1 : 0;
            using (var writeSubKey = rootKey.OpenSubKey(targetSubKey, true))
            {
                if (writeSubKey == null)
                {
                    statusMessage = "Windows' entry for the icon couldn't be opened for writing.";
                    return Outcome.Failed;
                }

                if (writeSubKey.GetValue("IsPromoted") is int current && current == wanted)
                {
                    statusMessage = enable
                        ? "Windows already keeps the icon on the taskbar. If it's still in the overflow, drag it onto the taskbar once."
                        : "Windows already leaves the icon to its default overflow behaviour.";
                    return Outcome.Unchanged;
                }

                writeSubKey.SetValue("IsPromoted", wanted, RegistryValueKind.DWord);
            }

            statusMessage = enable
                ? "Applied. The icon was re-added so Windows picks the change up; if it's still in the overflow, drag it onto the taskbar once."
                : "Applied. The icon follows Windows' default overflow behaviour again.";
            try { PromotionApplied?.Invoke(); }
            catch (Exception ex) { LoggingService.Verbose("TrayPromotionService", $"PromotionApplied handler failed: {ex.Message}"); }
            return Outcome.Applied;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("TrayPromotionService", $"Error writing to NotifyIconSettings: {ex.Message}");
            statusMessage = $"Couldn't write the setting: {ex.Message}";
            return Outcome.Failed;
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

}
