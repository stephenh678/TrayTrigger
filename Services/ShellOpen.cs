using System;
using System.Diagnostics;

namespace TrayTrigger.Services;

/// <summary>
/// Hands a link, a file, a folder or a Windows page (ms-settings:, windowsdefender:, steam:) to the shell, the one
/// way the app does it: the process handle is disposed, and a failure is logged as a warning naming what was being
/// opened, then reported as false instead of thrown. Launching a game or a tool is not this: ProcessLauncherService
/// and ToolLauncherService need the process they start.
/// </summary>
internal static class ShellOpen
{
    /// <summary>Opens <paramref name="target"/> with whatever Windows has for it. <paramref name="what"/> reads in the log as "Could not open {what}".</summary>
    public static bool Open(string target, string category, string what) =>
        Start(new ProcessStartInfo(target) { UseShellExecute = true }, category, what);

    /// <summary>Opens Explorer on the folder holding <paramref name="filePath"/>, with the file selected.</summary>
    public static bool Reveal(string filePath, string category, string what) =>
        Start(new ProcessStartInfo(SystemExecutables.Explorer, $"/select,\"{filePath}\"") { UseShellExecute = true }, category, what);

    /// <summary>Opens Explorer on <paramref name="folder"/>, or on a shell: location such as shell:AppsFolder.</summary>
    public static bool OpenFolder(string folder, string category, string what) =>
        Start(new ProcessStartInfo(SystemExecutables.Explorer, $"\"{folder}\"") { UseShellExecute = true }, category, what);

    private static bool Start(ProcessStartInfo info, string category, string what)
    {
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn(category, $"Could not open {what}: {ex.Message}");
            return false;
        }
    }
}
