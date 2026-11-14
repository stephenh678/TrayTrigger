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
    private void AttachExitTracking(ActiveGameSession session, Process process, bool allowStubHandoff, TimeSpan? standInWithin = null)
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
        // Read now: an exited process can no longer be asked its name.
        string standInName = string.Empty;
        if (standInWithin != null) { try { standInName = process.ProcessName; } catch (Exception) { /* already gone */ } }

        void OnExited(object? s, EventArgs e)
        {
            // Taken already when the session moved on to the game's own program (WatchForRecordedGame).
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
                    TrackInstallDirSession(session, installDir, "direct launch", StubHandoffSearchTimeout, ownsProcessAlready: true, preferRecordedExe: false);
                    return;
                }
            }

            // The process followed was not the game's recorded executable (an updater, a patcher): the game
            // is still to come, so look for it again rather than ending the session with the stand-in.
            if (standInWithin is { } within && ranFor < within && !session.CloseRequested)
            {
                string installDir = ResolveInstallDir(game);
                if (!string.IsNullOrWhiteSpace(installDir))
                {
                    LoggingService.Info("Launcher", $"'{game.Name}': the process followed ({standInName}) was not the game's own program and has exited after {ranFor.TotalSeconds:0}s - looking for '{ProcessPathResolver.RecordedExeName(game.ExecutablePath)}' under '{installDir}'.");
                    try { process.Dispose(); } catch { /* already disposed */ }
                    if (ReferenceEquals(session.Process, process)) session.Process = null;
                    TrackInstallDirSession(session, installDir, "its install folder", InstallDirLaunchTimeout, ownsProcessAlready: true);
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

            if (standInWithin != null) WatchForRecordedGame(session, process, () => Interlocked.Exchange(ref exitHandled, 1) == 0);

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
                // the Performance Profile back while the game is still running. Its end goes through
                // OnExited, as the event's would: a stand-in that runs as administrator (an updater)
                // still has the game looked for again, and is still swapped for the game's own program.
                if (!TryWatchByPolling(session, process, () => OnExited(process, EventArgs.Empty)))
                {
                    OnExited(process, EventArgs.Empty);
                }
                else if (standInWithin != null)
                {
                    WatchForRecordedGame(session, process, () => Interlocked.Exchange(ref exitHandled, 1) == 0);
                }
            }
        }
    }

    /// <summary>The game's own program usually follows its stand-in within seconds. After this long the watch for it
    /// scans only every <see cref="SlowRecordedGameWatchTicks"/> polls: a full process scan every 2 s for a whole
    /// session is time taken from the game.</summary>
    private static readonly TimeSpan FastRecordedGameWatch = TimeSpan.FromMinutes(2);
    private const int SlowRecordedGameWatchTicks = 15;

    /// <summary>Whether the watch for the game's own program scans on this poll: every poll at first and while a
    /// sighting is being confirmed, then one in <see cref="SlowRecordedGameWatchTicks"/>.</summary>
    internal static bool ScanForRecordedGameThisTick(TimeSpan watchedFor, int tick, bool sightingPending) =>
        sightingPending || watchedFor <= FastRecordedGameWatch || tick % SlowRecordedGameWatchTicks == 0;

    /// <summary>
    /// The session is following a stand-in (a process under the install folder that is not the game's recorded
    /// executable, such as Red Dead Redemption 2's PlayRDR2.exe). Watches the install folder for the recorded
    /// executable and, once it is seen on several polls in a row, follows that instead, so the session ends
    /// when the game does. Without this, a stand-in that stays open as long as the game (PlayRDR2.exe does)
    /// closed with it, and the session then spent minutes looking for a game that had already quit.
    /// <paramref name="claimStandIn"/> takes the stand-in's exit handling for the switch; false means the
    /// stand-in has already exited and its own handling has run.
    /// </summary>
    private void WatchForRecordedGame(ActiveGameSession session, Process standIn, Func<bool> claimStandIn)
    {
        var game = session.Game;
        string? recordedExe = ProcessPathResolver.RecordedExeName(game.ExecutablePath);
        string installDir = ResolveInstallDir(game);
        if (recordedExe == null || string.IsNullOrWhiteSpace(installDir)) return;

        int standInPid;
        try { standInPid = standIn.Id; } catch (InvalidOperationException) { return; /* already gone */ }
        string normalizedInstallDir = ProcessPathResolver.NormalizeDirectory(installDir);
        DateTime playtimeFrom = session.StartedAt;
        DateTime watchStartedUtc = DateTime.UtcNow;

        int? pendingPid = null;
        int sightings = 0;
        int ticks = 0;
        Poller? poller = null;
        poller = new Poller(InstallDirLaunchPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0 || !ReferenceEquals(session.Process, standIn)) return false;
            if (!ScanForRecordedGameThisTick(DateTime.UtcNow - watchStartedUtc, ++ticks, pendingPid != null)) return true;

            if (FindRecordedGame(ProcessPathResolver.FindProcessesUnderDirectory(normalizedInstallDir), recordedExe, standInPid) is not { } found)
            {
                pendingPid = null;
                sightings = 0;
                return true;
            }

            sightings = pendingPid == found.Pid ? sightings + 1 : 1;
            pendingPid = found.Pid;
            if (sightings < RequiredStableSightings) return true;

            Process? real = null;
            try
            {
                real = Process.GetProcessById(found.Pid);
                if (real.HasExited) { real.Dispose(); real = null; }
            }
            catch { real = null; /* gone again */ }
            if (real == null)
            {
                pendingPid = null;
                sightings = 0;
                return true;
            }

            if (!claimStandIn())
            {
                // The stand-in exited in the same moment; its exit handling is already looking for the game.
                real.Dispose();
                return false;
            }

            string standInName = string.Empty;
            try { standInName = standIn.ProcessName; } catch (Exception) { /* exited */ }
            LoggingService.Info("Launcher", $"'{game.Name}': its own program is running (PID {found.Pid}, {Path.GetFileName(found.Path)}); following it instead of {standInName} (PID {standInPid}).");
            AttachExitTracking(session, real, allowStubHandoff: false);
            session.StartedAt = playtimeFrom; // playtime still counts from the first process
            try { standIn.Dispose(); } catch (Exception) { /* already disposed */ }
            return false;
        });
        var previousCancel = session.CancelTracking;
        session.CancelTracking = () => { previousCancel?.Invoke(); poller?.Stop(); };
    }

    /// <summary>The game's recorded executable among <paramref name="candidates"/>, other than the stand-in itself.</summary>
    internal static ProcessPathResolver.ProcessUnderDirectory? FindRecordedGame(
        IReadOnlyList<ProcessPathResolver.ProcessUnderDirectory> candidates, string recordedExeName, int standInPid)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Pid != standInPid && ProcessPathResolver.IsRecordedExe(candidate.Path, recordedExeName)) return candidate;
        }
        return null;
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
        // The session's own process is the script, so nothing else would release this handle. What still
        // holds it when the session ends (the window and DLSS pollers) treats a disposed process as exited.
        var previousCancel = session.CancelTracking;
        session.CancelTracking = () =>
        {
            previousCancel?.Invoke();
            try { real.Dispose(); } catch (Exception) { /* already gone */ }
        };
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
    /// Calls <paramref name="onGone"/> when the process is gone, for a process whose exit can't be subscribed to.
    /// False when it isn't running to begin with, so the caller handles its end at once.
    /// </summary>
    private bool TryWatchByPolling(ActiveGameSession session, Process process, Action onGone)
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
            // The session has moved on to the game's own program: this process ending ends nothing.
            if (session.Process != null && !ReferenceEquals(session.Process, process)) return false;
            if (IsStillRunning(pid, exePath)) return true;
            onGone();
            return false;
        });
        session.CancelTracking = () => poller?.Stop();
        return true;
    }
}
