using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string AppName = "TrayTrigger";

    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;

            var val = key.GetValue(AppName) as string;
            return !string.IsNullOrWhiteSpace(val) && !IsDisabledViaTaskManager();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StartupManager", $"Error checking startup status: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Task Manager's Startup tab disables an entry without touching the Run value itself - it
    /// writes a binary flag under StartupApproved\Run instead, so a plain Run-key check never
    /// notices a Task-Manager-disabled entry. The exact format is undocumented by Microsoft, but
    /// is consistently reported (and matches Sysinternals Autoruns' interpretation): the first
    /// byte is 0x02 or 0x06 when enabled, anything else when disabled. Missing/unreadable/
    /// unrecognized data is treated as "not disabled" - the Run key's own presence is the more
    /// reliable signal in that case.
    /// </summary>
    private bool IsDisabledViaTaskManager()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, false);
            if (key?.GetValue(AppName) is byte[] { Length: > 0 } data)
            {
                return data[0] != 0x02 && data[0] != 0x06;
            }
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StartupManager", $"Error checking StartupApproved flag: {ex.Message}");
            return false;
        }
    }

    public bool IsStartupMinimized()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return true;

            var val = key.GetValue(AppName) as string;
            if (string.IsNullOrWhiteSpace(val)) return true;

            return val.Contains("--minimized", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StartupManager", $"Error checking startup minimized flag: {ex.Message}");
            return true;
        }
    }

    public void SetStartupEnabled(bool enabled, bool startMinimized = true)
    {
        try
        {
            // CreateSubKey (vs. OpenSubKey(path, true)) also creates RunKeyPath if it's somehow
            // missing, instead of silently no-oping when `key` comes back null.
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string command = startMinimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";
                    key.SetValue(AppName, command);
                }
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StartupManager", $"Error setting startup: {ex.Message}");
        }
    }

    /// <summary>
    /// If startup is enabled but the stored command's exe no longer exists on disk (a portable
    /// copy moved to another folder), rewrites the Run value to point at this running instance's
    /// current path. Deliberately does NOT rewrite merely because the stored path differs from
    /// this process's path while the stored exe still exists - a user can legitimately run an
    /// installed copy at startup and a separate portable/dev copy by hand sometimes, and that
    /// must not silently retarget their real startup entry to whichever copy happened to run last.
    /// </summary>
    public void ReconcilePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var val = key?.GetValue(AppName) as string;
            if (string.IsNullOrWhiteSpace(val)) return;

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            if (val.Contains(exePath, StringComparison.OrdinalIgnoreCase)) return;

            string storedPath = ExtractExecutablePath(val);
            if (string.IsNullOrEmpty(storedPath) || File.Exists(storedPath)) return;

            bool startMinimized = val.Contains("--minimized", StringComparison.OrdinalIgnoreCase);
            LoggingService.Info("StartupManager", $"Startup entry's exe no longer exists at '{storedPath}'; rewriting to current path '{exePath}'.");
            SetStartupEnabled(true, startMinimized);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StartupManager", $"Error reconciling startup path: {ex.Message}");
        }
    }

    /// <summary>Pulls the quoted (or bare) executable path out of a Run-key command string.</summary>
    private static string ExtractExecutablePath(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 0 ? trimmed.Substring(1, closingQuote - 1) : string.Empty;
        }

        int spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex > 0 ? trimmed[..spaceIndex] : trimmed;
    }
}
