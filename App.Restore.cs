using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// The restart a restore from backup needs, and what it says after (see <see cref="BackupService"/>).
/// The running TrayTrigger unpacks the backup and starts a new copy of itself with
/// "--after-restart &lt;its id&gt;", then exits as usual; the new one waits for it to be gone,
/// takes the single-instance mutex, and puts the files in place before reading anything.
/// </summary>
public partial class App
{
    private const string AfterRestartArgument = "--after-restart";

    /// <summary>What this start's restore did, until the window is up to say so.</summary>
    private RestoreResult? _restoreResult;

    private static void WaitForPreviousInstanceToExit(string[] args)
    {
        int at = Array.FindIndex(args, a => a.Equals(AfterRestartArgument, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out int pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            // Once it has gone its id can belong to anything; only a TrayTrigger is worth waiting for.
            using var self = Process.GetCurrentProcess();
            if (!string.Equals(previous.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase)) return;
            if (!previous.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                LoggingService.Warn("App", $"The TrayTrigger being restarted (PID {pid}) was still running after 30 seconds; starting anyway.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // It has already exited, which is what was being waited for.
        }
    }

    /// <summary>Starts a new TrayTrigger to take over, then exits this one the usual way.</summary>
    private void RestartApplication()
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("TrayTrigger's own path is unknown");
            Process.Start(new ProcessStartInfo(exe, $"{AfterRestartArgument} {Environment.ProcessId}") { UseShellExecute = false });
            LoggingService.Info("App", "Restarting TrayTrigger to finish restoring from a backup.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("App", $"Could not restart TrayTrigger ({ex.Message}); the restore finishes the next time it starts.");
            ModernDialog.ShowInfo(null, "Restart TrayTrigger", "TrayTrigger couldn't restart itself.", "Start it again to finish restoring from the backup.");
        }
        ExitApplication();
    }

    private void ShowRestoreResult(RestoreResult result)
    {
        var owner = _mainWindow is { IsVisible: true } ? _mainWindow : null;
        string safety = string.IsNullOrWhiteSpace(result.SafetyBackupPath)
            ? string.Empty
            : $" What you had before the restore is in \"{result.SafetyBackupPath}\"; restore that file to go back.";

        if (!result.Succeeded)
        {
            ModernDialog.ShowWarning(owner, "Restore from Backup", "The restore didn't finish.",
                $"{result.Error?.TrimEnd('.')}. Some of your data may be from the backup and some from before.{safety}");
            return;
        }

        var manifest = result.Manifest;
        int games = _mainViewModel?.Games.Count ?? manifest?.Games ?? 0;
        int tools = _mainViewModel?.Tools.ToolsSnapshot.Count ?? manifest?.Tools ?? 0;
        string made = manifest == null ? "" : $" made {manifest.CreatedUtc.ToLocalTime():MMM d, yyyy h:mm tt}{(string.IsNullOrWhiteSpace(manifest.MachineName) ? "" : $" on {manifest.MachineName}")}";

        var detail = new StringBuilder();
        if (result.Paths is { } paths)
        {
            int found = paths.Relinked + paths.Moved;
            if (found > 0) detail.Append($"{found} path(s) were found in a new place - another drive letter, user folder or launcher folder. ");
            if (paths.StillMissing.Count > 0)
            {
                var names = paths.StillMissing.Take(5).ToList();
                string more = paths.StillMissing.Count > names.Count ? $" and {paths.StillMissing.Count - names.Count} more" : "";
                detail.Append($"{paths.StillMissing.Count} game(s) in the backup aren't installed on this PC ({string.Join(", ", names)}{more}). They're kept, greyed out in the library: reinstall them, or use Locate Executable. ");
            }
        }
        if (result.ApiKeysNeedReentry)
        {
            detail.Append("Your SteamGridDB or RAWG API key was saved for another Windows account and can't be read here; enter it again in Settings > Library & Art. ");
        }
        detail.Append(safety.TrimStart());

        ModernDialog.ShowInfo(owner, "Restore Complete",
            $"Restored {games} game(s) and {tools} tool(s) from the backup{made}.",
            detail.ToString().Trim());
    }
}
