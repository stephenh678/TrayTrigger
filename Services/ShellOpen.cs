using System;
using System.Diagnostics;

namespace TrayTrigger.Services;

/// <summary>
/// Hands a link, a file, a folder or a Windows page (ms-settings:, windowsdefender:, steam:) to the shell, the one
/// way the app does it: the process handle is disposed, and a failure is logged naming what was being opened, then
/// reported as false instead of thrown. Launching a game or a tool is not this: ProcessLauncherService and
/// ToolLauncherService need the process they start.
/// </summary>
internal static class ShellOpen
{
    /// <summary>
    /// Opens <paramref name="target"/> with whatever Windows has for it, or with its <paramref name="verb"/> (such as
    /// "edit"), passing <paramref name="arguments"/> when given. <paramref name="what"/> reads in the log as "Could not
    /// open {what}". A failure is a warning, except with <paramref name="quiet"/>: a first try that has a fallback after
    /// it is expected to fail on some PCs, and a warning there would only add noise to a bug report.
    /// </summary>
    public static bool Open(string target, string category, string what, string? verb = null, string? arguments = null, bool quiet = false) =>
        Start(new ProcessStartInfo(target, arguments ?? string.Empty) { UseShellExecute = true, Verb = verb ?? string.Empty }, category, what, quiet);

    /// <summary>Opens Explorer on the folder holding <paramref name="filePath"/>, with the file selected.</summary>
    public static bool Reveal(string filePath, string category, string what) =>
        Start(new ProcessStartInfo(SystemExecutables.Explorer, $"/select,\"{filePath}\"") { UseShellExecute = true }, category, what, quiet: false);

    /// <summary>
    /// Opens Explorer on <paramref name="folder"/>, or on a shell: location such as shell:AppsFolder. A folder path is
    /// quoted, so spaces and commas in it stay one path; a shell: location goes as it is, the way Explorer documents it.
    /// </summary>
    public static bool OpenFolder(string folder, string category, string what) =>
        Start(new ProcessStartInfo(SystemExecutables.Explorer, ExplorerTarget(folder)) { UseShellExecute = true }, category, what, quiet: false);

    internal static string ExplorerTarget(string folder) =>
        folder.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? folder : $"\"{folder}\"";

    private static bool Start(ProcessStartInfo info, string category, string what, bool quiet)
    {
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            string message = $"Could not open {what}: {ex.Message}";
            if (quiet) LoggingService.Verbose(category, message);
            else LoggingService.Warn(category, message);
            return false;
        }
    }
}
