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

/// <summary>Following a started game to its end: the process exit, a launcher stub handing off to the real game, and polling for one that can't be watched.</summary>
public partial class ProcessLauncherService
{
    /// <summary>
    /// Shared tail of every launch path that ends up with a real Process handle to watch: applies
    /// the post-start tweaks, focuses the window, and finishes the session exactly once when the
    /// process exits. With <paramref name="allowStubHandoff"/>, an exit inside
    /// <see cref="StubHandoffWindow"/> is treated as a bootstrap stub and the session moves to the
    /// real game found under the install folder instead of ending.
    /// </summary>
    private void AttachExitTracking(ActiveGameSession session, Process process, bool allowStubHandoff)
    {
        var game = session.Game;
        session.Process = process;
        DateTime startedAt;
        try { startedAt = process.StartTime; } catch (Exception ex) { LoggingService.Swallowed("Launcher", ex, "reading the game's start time; playtime counts from now"); startedAt = DateTime.Now; }
        MarkGameStarted(session, startedAt);

        // A .bat or .cmd that stays alive for the whole game (start /wait) is only its host: priority, CPU cores,
        // the DLSS check and the window belong to the game it starts, found under the install folder, not to cmd.
        string scriptInstallDir = allowStubHandoff && IsScriptHost(game.ExecutablePath) ? ResolveInstallDir(game) : string.Empty;
        bool followRealGame = !string.IsNullOrWhiteSpace(scriptInstallDir);
        if (!followRealGame) StartDlssObservation(session, process);

        int exitHandled = 0;

        void OnExited(object? s, EventArgs e)
        {
            if (Interlocked.Exchange(ref exitHandled, 1) != 0) return;

            TimeSpan ranFor = DateTime.UtcNow - session.StartedAtUtc;
            if (allowStubHandoff && ranFor < StubHandoffWindow && !session.CloseRequested)
            {
                string installDir = ResolveInstallDir(game);
                if (!string.IsNullOrWhiteSpace(installDir))
                {
                    LoggingService.Info("Launcher", $"'{game.Name}' exited {ranFor.TotalSeconds:0.0}s after starting - treating it as a launcher stub and looking for the real game under '{installDir}'.");
                    try { process.Dispose(); } catch { /* already disposed */ }
                    if (ReferenceEquals(session.Process, process)) session.Process = null;
                    TrackInstallDirSession(session, installDir, "direct launch", StubHandoffSearchTimeout, ownsProcessAlready: true);
                    return;
                }
            }

            FinishSession(session, gameRan: true, "process exited");
        }

        bool exitHandlerAttached = false;
        try
        {
            if (followRealGame)
            {
                StartRealGameDiscovery(session, process, scriptInstallDir);
            }
            else
            {
                _performanceProfileService.OnGameProcessStarted(game, process);
                TrackGameWindow(session, process.Id);
                // Only a direct launch hands over the process it started (allowStubHandoff); a process
                // found under the install folder was opened by id, and has no launch handle.
                CpuTopologyService.ApplyCpuCores(process, game, startedByTrayTrigger: allowStubHandoff);
            }
            _scriptService.TrackPostExit(game, session.PlaytimeStartUtc);

            // Subscribe before enabling, not after: EnableRaisingEvents can fire Exited on a
            // thread-pool thread almost immediately for a process that already exited.
            process.Exited += OnExited;
            process.EnableRaisingEvents = true;
            exitHandlerAttached = true;

            if (!followRealGame) WaitForWindowAndActivate(session, process);

            // Handle it inline too in case the process had already exited before the line
            // above and the async callback hasn't run yet - exitHandled guards against
            // double-processing if it fires anyway.
            try
            {
                if (process.HasExited)
                {
                    OnExited(process, EventArgs.Empty);
                }
            }
            catch (ObjectDisposedException)
            {
                // Already handled by the async Exited callback in the tiny window above.
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Launcher", $"Note: Could not attach Exited event to process: {ex.Message}");
        }
        finally
        {
            if (!exitHandlerAttached)
            {
                // No Exited handler means nothing would ever end the session. A game running as
                // administrator refuses the handle the event needs, but it can still be seen by
                // its process id, so watch for that instead; ending the session here would put
                // the Performance Profile back while the game is still running.
                if (!TryWatchByPolling(session, process))
                {
                    FinishSession(session, gameRan: true, "could not watch the process");
                }
            }
        }
    }

    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long the game a script starts is looked for: a driver install and anti-cheat start-up come first.</summary>
    private static readonly TimeSpan RealGameSearchTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A .bat or .cmd: it starts the game, it is not the game.</summary>
    internal static bool IsScriptHost(string? path) =>
        Path.GetExtension(path ?? string.Empty) is { } ext
        && (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The session is watching a script that stays alive while the game runs. Finds the game it starts - a process under
    /// the install folder, seen on several polls in a row - and gives it what the profile and the game's settings do to a
    /// process: priority, power throttling, CPU cores, the DLSS check, its window. The session still ends when the script
    /// does, which for start /wait is when the game does. If the script instead hands off and exits, the stub handoff
    /// takes over and this stops.
    /// </summary>
    private void StartRealGameDiscovery(ActiveGameSession session, Process host, string installDir)
    {
        var game = session.Game;
        string normalizedInstallDir = ProcessPathResolver.NormalizeDirectory(installDir);
        int hostPid;
        try { hostPid = host.Id; } catch (InvalidOperationException) { return; /* the script has already exited */ }

        DateTime startedUtc = DateTime.UtcNow;
        int? pendingPid = null;
        int sightings = 0;
        Poller? poller = null;
        poller = new Poller(InstallDirLaunchPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0 || !ReferenceEquals(session.Process, host)) return false;

            var candidate = ProcessPathResolver.FindProcessesUnderDirectory(normalizedInstallDir)
                .Where(c => c.Pid != hostPid)
                .Select(c => (ProcessPathResolver.ProcessUnderDirectory?)c)
                .FirstOrDefault();
            if (candidate is { } best)
            {
                sightings = pendingPid == best.Pid ? sightings + 1 : 1;
                pendingPid = best.Pid;
                if (sightings >= RequiredStableSightings)
                {
                    Process? real = null;
                    try { real = Process.GetProcessById(best.Pid); }
                    catch (ArgumentException) { /* gone again */ }
                    if (real != null)
                    {
                        LoggingService.Info("Launcher", $"'{game.Name}' is started by a script; following the game itself (PID {best.Pid}, {Path.GetFileName(best.Path)}) for its priority, CPU cores and window.");
                        ApplyToRealGame(session, real);
                        return false;
                    }
                    sightings = 0;
                    pendingPid = null;
                }
            }
            else
            {
                sightings = 0;
                pendingPid = null;
            }

            if (DateTime.UtcNow - startedUtc > RealGameSearchTimeout)
            {
                LoggingService.Info("Launcher", $"No game process appeared under '{installDir}' within {RealGameSearchTimeout.TotalMinutes:0} minutes of its script starting; its priority and CPU cores were not changed.");
                LaunchRecord.Note(game.Id, LaunchRecord.Profile, LaunchRecord.SkippedPrefix + "process settings, the game's own process wasn't found");
                return false;
            }
            return true;
        });
        var previousCancel = session.CancelTracking;
        session.CancelTracking = () => { previousCancel?.Invoke(); poller?.Stop(); };
    }

    private void ApplyToRealGame(ActiveGameSession session, Process real)
    {
        var game = session.Game;
        try
        {
            _performanceProfileService.OnGameProcessStarted(game, real);
            TrackGameWindow(session, real.Id);
            CpuTopologyService.ApplyCpuCores(real, game, startedByTrayTrigger: false);
            StartDlssObservation(session, real);
            WaitForWindowAndActivate(session, real);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Launcher", $"Applying the process settings to '{game.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the process with this id is still running, without needing a handle to it, which
    /// an elevated or protected game refuses. When its image path can be read it must still be the
    /// one first seen, so a new process that took the id after the game quit doesn't keep the
    /// session open.
    /// </summary>
    internal static bool IsStillRunning(int pid, string? expectedPath)
    {
        try
        {
            using var running = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return false; // no process has this id any more
        }
        catch (InvalidOperationException)
        {
            return false; // it exited while being looked at
        }

        if (string.IsNullOrWhiteSpace(expectedPath)) return true;
        string? now = ProcessPathResolver.GetProcessPath(pid);
        return now == null || ProcessPathResolver.IsSamePath(now, expectedPath);
    }

    /// <summary>
    /// Ends the session when the process is gone, for a process whose exit can't be subscribed to.
    /// False when it isn't running to begin with, so the caller ends the session at once.
    /// </summary>
    private bool TryWatchByPolling(ActiveGameSession session, Process process)
    {
        int pid;
        try { pid = process.Id; }
        catch (InvalidOperationException) { return false; /* no id: it has already exited */ }

        string? exePath = ProcessPathResolver.GetProcessPath(pid);
        if (!IsStillRunning(pid, exePath)) return false;

        LoggingService.Info("Launcher", $"'{session.Game.Name}' (PID {pid}) can't be watched for its exit - it is probably running as administrator - so TrayTrigger checks every {ExitPollInterval.TotalSeconds:0} seconds whether it is still running.");
        Poller? poller = null;
        poller = new Poller(ExitPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0) return false;
            if (IsStillRunning(pid, exePath)) return true;
            FinishSession(session, gameRan: true, "process exited");
            return false;
        });
        session.CancelTracking = () => poller?.Stop();
        return true;
    }
}
