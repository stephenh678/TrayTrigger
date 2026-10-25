using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Launching a Steam game, and following its session through Steam's Running flag.</summary>
public partial class ProcessLauncherService
{
    private static bool ReadSteamRunningFlag(string appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SteamRunningKeyRoot + appId);
            return key?.GetValue("Running") is int i && i != 0;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Launcher", ex, "asking Steam whether the game is running");
            return false;
        }
    }

    private bool LaunchSteamGame(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;

        // The AppId is interpolated into a URL and a registry path: only a digits-only value
        // (the shape Edit Game and every scanner already enforce) is trusted from games.json.
        string? appId = UrlProtocolHelper.IsValidSteamAppId(game.SteamAppId) ? game.SteamAppId : null;
        if (!string.IsNullOrWhiteSpace(game.SteamAppId) && appId == null)
        {
            LoggingService.Warn("Launcher", $"'{game.Name}' has a malformed Steam AppId '{game.SteamAppId}'; ignoring it.");
        }

        // Already running (launched by TrayTrigger or by Steam itself)? Every other route checks
        // this before dispatching; without it a double-click re-ran the pre-launch script and
        // started a second tracker whose post-exit script fired a second time.
        if (appId != null && ReadSteamRunningFlag(appId))
        {
            var existing = GetSession(game.Id);
            IntPtr hWnd = IntPtr.Zero;
            hWnd = SafeProcess.MainWindow(existing?.Process);
            if (hWnd == IntPtr.Zero)
            {
                string? installDir = _steamScannerService.FindInstallDirForAppId(appId);
                if (installDir != null)
                {
                    using var proc = ProcessPathResolver.FindBestProcessUnderDirectory(ProcessPathResolver.NormalizeDirectory(installDir));
                    hWnd = SafeProcess.MainWindow(proc);
                }
            }
            if (hWnd != IntPtr.Zero) ActivateWindow(hWnd);

            MarkLaunched(game);
            LoggingService.Info("Launcher", $"'{game.Name}' is already running according to Steam{(existing != null ? " (tracked session)" : "")}; activated its window instead of relaunching.");
            return true;
        }

        var session = BeginSession(game, LaunchRoute.Steam, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            if (!TryLaunchSteamSilently(game, appId))
            {
                string launchUrl = appId != null
                    ? LaunchRouter.BuildSteamLaunchUrl(appId, game.Arguments)
                    : game.ExecutablePath;

                LoggingService.Verbose("Launcher", $"Launching Steam URL: {launchUrl}");
                Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true })?.Dispose();
            }

            MarkLaunched(game);
            LoggingService.Info("Launcher", $"Dispatched Steam launch for '{game.Name}'.");

            if (appId == null)
            {
                // No AppId means no "Running" flag to watch, so the session can never be ended.
                LoggingService.Verbose("Launcher", $"'{game.Name}' has no Steam AppId; cannot track its session, so no Performance Profile or post-exit script will run for it.");
                FinishSession(session, gameRan: false, "no Steam AppId to track");
                return true;
            }

            TrackSteamSession(session, appId);
            return true;
        }
        catch
        {
            RollbackSession(session);
            throw;
        }
    }

    /// <summary>
    /// "Keep game launchers minimized when launching a game" (Settings), or this game's "Close the launcher
    /// after this game exits" (the user treats Steam as incidental to the game): when either is on
    /// and Steam is not already running, cold-start it with "steam.exe -silent -applaunch &lt;id&gt;
    /// [args]" (Valve's documented switches) instead of a steam:// URL, so the client comes up
    /// minimized to the tray and only the game shows. When Steam is already open the URL is used as
    /// before - "-silent" only affects client startup, and an open Steam doesn't raise its window
    /// for a steam:// launch. Returns false when the URL path should be taken (both options off,
    /// Steam running, no AppId, steam.exe not found, or the start failed), so the caller can fall back.
    /// </summary>
    private bool TryLaunchSteamSilently(GameEntry game, string? appId)
    {
        if (appId == null || !(game.CloseLauncherOnExit || ShouldKeepLaunchersMinimized)) return false;
        if (LauncherClientCloser.IsClientRunning(LauncherPlatform.Steam)) return false;

        string? steamPath = _steamScannerService.GetSteamInstallPath();
        string? steamExe = string.IsNullOrWhiteSpace(steamPath) ? null : Path.Combine(steamPath, "steam.exe");
        if (steamExe == null || !File.Exists(steamExe))
        {
            LoggingService.Verbose("Launcher", $"steam.exe not found under '{steamPath ?? "(unknown)"}'; launching '{game.Name}' via steam:// URL instead of a silent client start.");
            return false;
        }

        // Everything after "-applaunch <id>" is handed to the game as its launch options, the
        // same as Properties > Launch Options in Steam - keep the user's own quoting intact.
        string arguments = string.IsNullOrWhiteSpace(game.Arguments)
            ? $"-silent -applaunch {appId}"
            : $"-silent -applaunch {appId} {game.Arguments.Trim()}";

        try
        {
            LoggingService.Verbose("Launcher", $"Steam is not running; starting it minimized: \"{steamExe}\" {arguments}");
            Process.Start(new ProcessStartInfo(steamExe, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = steamPath
            })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Launcher", $"Silent Steam start for '{game.Name}' failed ({ex.Message}); falling back to the steam:// URL.");
            return false;
        }
    }

    /// <summary>
    /// A Steam launch is a fire-and-forget "steam://" dispatch with no Process handle. Steam
    /// maintains its own "Running" flag per AppId while a game is actually in session, so poll
    /// that: wait for it to flip on (the real start of playtime), then end the session once it
    /// flips back off. While it's on, also try to locate the game's own process under its Steam
    /// install folder so the window can be focused, Above Normal priority applied, and a hung game
    /// force-closed. If the flag never turns on (user cancels the Steam launch, game isn't
    /// actually installed, etc.), give up after a few minutes and roll the profile back.
    /// </summary>
    private void TrackSteamSession(ActiveGameSession session, string appId)
    {
        var game = session.Game;
        string? installDir = null;
        bool installDirResolved = false;
        int processSearchTicks = 0;

        // Registered up front (not on the Running flip) so a TrayTrigger exit during Steam's
        // own startup still runs the post-exit script on shutdown.
        _scriptService.TrackPostExit(game, session.PlaytimeStartUtc);

        Poller? poller = null;
        poller = new Poller(SteamSessionPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0) return false;

            bool isRunning = ReadSteamRunningFlag(appId);

            if (!session.GameStarted)
            {
                if (isRunning)
                {
                    MarkGameStarted(session, DateTime.Now);
                    LoggingService.Verbose("Launcher", $"Steam reports '{game.Name}' now running.");
                    return true;
                }

                if (DateTime.UtcNow - session.LaunchedAtUtc > SteamSessionStartTimeout)
                {
                    LoggingService.Verbose("Launcher", $"'{game.Name}' never reported running via Steam within {SteamSessionStartTimeout.TotalMinutes:0}m; rolling back its Performance Profile.");
                    FinishSession(session, gameRan: false, "Steam never reported the game running");
                    return false;
                }
                return true;
            }

            if (!isRunning)
            {
                if (session.Process == null && game.DlssSettings.Count > 0)
                    LoggingService.Info("Dlss", $"'{game.Name}': its process was never found under the Steam install folder, so what DLSS it loaded could not be read.");
                LoggingService.Verbose("Launcher", $"Steam reports '{game.Name}' session ended.");
                FinishSession(session, gameRan: true, "Steam reports the game closed");
                return false;
            }

            if (session.Process == null && processSearchTicks < SteamProcessSearchTicks)
            {
                processSearchTicks++;
                if (!installDirResolved)
                {
                    installDirResolved = true;
                    installDir = _steamScannerService.FindInstallDirForAppId(appId);
                    if (installDir == null)
                    {
                        LoggingService.Verbose("Launcher", $"No Steam install folder found for AppId {appId}; window focus and priority are unavailable for '{game.Name}'.");
                    }
                }

                if (installDir != null)
                {
                    var proc = ProcessPathResolver.FindBestProcessUnderDirectory(ProcessPathResolver.NormalizeDirectory(installDir));
                    if (proc != null)
                    {
                        session.Process = proc;
                        LoggingService.Verbose("Launcher", $"Found Steam game process for '{game.Name}' (PID {proc.Id}).");
                        _performanceProfileService.OnGameProcessStarted(game, proc);
                        TrackGameWindow(session, proc.Id);
                        CpuTopologyService.ApplyCpuCores(proc, game);
                        WaitForWindowAndActivate(session, proc);
                        StartDlssObservation(session, proc);
                    }
                }
            }
            return true;
        });
        session.CancelTracking = () => poller?.Stop();
    }

    // ==========================================================================================
    // Plain local executable
    // ==========================================================================================

    // A launched exe that exits this quickly is treated as a bootstrap stub (Rockstar/Bethesda/
    // Paradox launchers, some Unity and UE bootstraps) and the session hands off to whatever it
    // spawned under the install folder instead of ending.
    private static readonly TimeSpan StubHandoffWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StubHandoffSearchTimeout = TimeSpan.FromSeconds(30);
}
