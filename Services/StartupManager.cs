using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TrayTrigger";

    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;

            var val = key.GetValue(AppName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("StartupManager", $"Error checking startup status: {ex.Message}");
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
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
}
