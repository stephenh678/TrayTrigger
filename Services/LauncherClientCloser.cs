using System;
using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// "Close the launcher after the game exits" - the per-game <see cref="GameEntry.CloseLauncherOnExit"/>
/// option. Platform clients stay resident (tray icon, background services, overlay hosts) after
/// a game closes and there is no user-facing way to tell them to quit from outside; this asks
/// nicely where a client supports it (Steam's "-shutdown" switch) and otherwise ends the client's
/// own UI processes. Background services that belong to the platform (GalaxyClientService,
/// EABackgroundService) are left alone: they are what re-launches the client, and killing them
/// breaks the next launch.
/// </summary>
public static class LauncherClientCloser
{
    /// <summary>Process names (without .exe) per platform that constitute "the client is open".</summary>
    private static readonly string[] GogProcesses = ["GalaxyClient", "GalaxyClient Helper"];
    private static readonly string[] EaProcesses = ["EADesktop", "EALocalHostSvc", "EACefSubProcess"];
    private static readonly string[] EpicProcesses = ["EpicGamesLauncher", "EpicWebHelper"];
    private static readonly string[] UbisoftProcesses = ["upc", "UplayWebCore", "UbisoftConnect"];

    /// <summary>True if any of the platform's client processes are currently running.</summary>
    public static bool IsClientRunning(LauncherPlatform platform)
    {
        string[] names = platform switch
        {
            LauncherPlatform.Steam => ["steam"],
            LauncherPlatform.Gog => GogProcesses,
            LauncherPlatform.Ea => EaProcesses,
            LauncherPlatform.Epic => EpicProcesses,
            LauncherPlatform.Ubisoft => UbisoftProcesses,
            _ => []
        };
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(name);
            try { if (procs.Length > 0) return true; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return false;
    }

    public static void Close(LauncherPlatform platform, string? steamInstallPath)
    {
        try
        {
            switch (platform)
            {
                case LauncherPlatform.Steam:
                    CloseSteam(steamInstallPath);
                    break;
                case LauncherPlatform.Gog:
                    KillByName(GogProcesses, "GOG Galaxy");
                    break;
                case LauncherPlatform.Ea:
                    KillByName(EaProcesses, "EA App");
                    break;
                case LauncherPlatform.Epic:
                    KillByName(EpicProcesses, "Epic Games Launcher");
                    break;
                case LauncherPlatform.Ubisoft:
                    KillByName(UbisoftProcesses, "Ubisoft Connect");
                    break;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Launcher", $"Close launcher ({platform}) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Steam has a documented graceful shutdown switch: running "steam.exe -shutdown" asks the
    /// running instance to exit cleanly (cloud sync flush, overlay teardown). Falls back to ending
    /// the process only if Steam's own exe can't be found.
    /// </summary>
    private static void CloseSteam(string? steamInstallPath)
    {
        string? steamExe = string.IsNullOrWhiteSpace(steamInstallPath) ? null : Path.Combine(steamInstallPath, "steam.exe");
        if (steamExe != null && File.Exists(steamExe))
        {
            var psi = new ProcessStartInfo { FileName = steamExe, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-shutdown");
            using var proc = Process.Start(psi);
            LoggingService.Info("Launcher", "Asked Steam to shut down (steam.exe -shutdown).");
            return;
        }

        KillByName(["steam"], "Steam");
    }

    private static void KillByName(string[] names, string label)
    {
        int killed = 0;
        foreach (var name in names)
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var proc in procs)
            {
                try
                {
                    // Give the client a chance to close its window normally first; most launchers
                    // only minimize to tray on WM_CLOSE, so follow up with a real Kill.
                    proc.CloseMainWindow();
                    if (!proc.WaitForExit(1500))
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    killed++;
                }
                catch (Exception ex)
                {
                    LoggingService.Verbose("Launcher", $"Could not end {name}.exe: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        if (killed > 0)
        {
            LoggingService.Info("Launcher", $"Closed {label} after the game exited ({killed} process(es)).");
        }
    }
}
