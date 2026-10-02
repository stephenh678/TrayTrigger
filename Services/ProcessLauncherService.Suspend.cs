using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Suspend and resume a running game: its whole process tree frozen with NtSuspendProcess, its
/// sound muted, and its playtime clock stopped until it is resumed. For a cutscene that can't be
/// paused, or stepping away from a game with no pause. Reached from the suspend hotkey, the tray's
/// Now Playing menu, and the game's right-click menu.
///
/// <para>Refused for a game protected by a kernel anti-cheat (see <see cref="AntiCheatDetector"/>):
/// a game that stops answering its anti-cheat is disconnected at best. An online game without one
/// still loses its connection while it is frozen; the Settings text says so.</para>
///
/// <para>A frozen game can't be switched to, answer its close button, or be resumed by anything
/// but TrayTrigger, so every way out of a session resumes it first: Close Game, Play, a session
/// that ends, TrayTrigger exiting - and the next start, for a TrayTrigger that stopped without
/// exiting (suspended-games.json).</para>
/// </summary>
public partial class ProcessLauncherService
{
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hWnd, uint command);

    private const uint GW_OWNER = 4;
    private const int SW_MINIMIZE = 6;
    /// <summary>Minimizes a window whose thread isn't answering - one that is frozen, here.</summary>
    private const int SW_FORCEMINIMIZE = 11;

    /// <summary>
    /// How long a game gets to minimize itself before it is frozen. A full-screen game has to switch
    /// the display back as it goes, which can take a second or two; frozen first, it couldn't.
    /// </summary>
    private static readonly TimeSpan MinimizeWait = TimeSpan.FromSeconds(3);

    private readonly Lock _suspendLock = new();

    /// <summary>A session was suspended or resumed. Raised on a background thread.</summary>
    public event Action<ActiveGameSession>? SessionSuspendChanged;

    /// <summary>A session is tracked for this game and Suspend has it frozen.</summary>
    public bool IsGameSuspended(string gameId) => GetSession(gameId)?.IsSuspended == true;

    public enum SuspendResult
    {
        Suspended,
        Resumed,
        /// <summary>No session is tracked for the game, or the hotkey found no game to act on.</summary>
        NoSession,
        /// <summary>The game is still starting (its launcher is up, the game isn't yet).</summary>
        NotStarted,
        AlreadySuspended,
        NotSuspended,
        /// <summary>The game is protected by a kernel anti-cheat.</summary>
        AntiCheat,
        /// <summary>None of the game's processes could be found.</summary>
        NoProcess,
        /// <summary>Windows refused - usually a game running as administrator, from a TrayTrigger that isn't.</summary>
        Refused,
        /// <summary>The hotkey was pressed with several games running and none of them in front.</summary>
        Ambiguous,
    }

    /// <summary>What Suspend or Resume did, with the sentence to show for it.</summary>
    public sealed record SuspendOutcome(SuspendResult Result, string Message, string? GameId = null)
    {
        public bool Succeeded => Result is SuspendResult.Suspended or SuspendResult.Resumed;
    }

    /// <summary>
    /// Suspends the game: every process of its session and every process those started, muted
    /// first so an old audio path doesn't loop its last buffer. Off the UI thread: it lists
    /// processes and talks to the audio engine.
    /// </summary>
    public SuspendOutcome SuspendGame(string gameId)
    {
        var session = GetSession(gameId);
        if (session == null) return new(SuspendResult.NoSession, "That game isn't running.", gameId);
        string name = session.Game.Name;

        lock (_suspendLock)
        {
            if (session.IsSuspended) return new(SuspendResult.AlreadySuspended, $"{name} is already suspended.", gameId);
            if (!session.GameStarted) return new(SuspendResult.NotStarted, $"{name} is still starting; it can be suspended once it's running.", gameId);

            string? installDir = SessionInstallDir(session);
            string? antiCheat = AntiCheatDetector.Find(installDir, session.Game.ExecutablePath, session.LaunchedAtUtc);
            if (antiCheat != null)
            {
                LoggingService.Info("Suspend", $"Not suspending '{name}': it uses {antiCheat}.");
                return new(SuspendResult.AntiCheat, $"{name} wasn't suspended: it uses {antiCheat}, which can disconnect or ban a game that stops responding.", gameId);
            }

            var targets = SessionProcessTree(session);
            if (targets.Count == 0)
            {
                LoggingService.Warn("Suspend", $"Not suspending '{name}': none of its processes could be found.");
                return new(SuspendResult.NoProcess, $"{name} wasn't suspended: TrayTrigger couldn't find its process.", gameId);
            }

            // Out of the way first, while the game can still answer: minimized, a full-screen game
            // hands the screen back, and the desktop and taskbar are there to step away to.
            var windows = MinimizeGameWindows(targets.Select(t => t.Pid));

            var muted = ProcessAudioMuter.Mute(targets.Select(t => t.Pid).ToHashSet());

            var suspended = new List<SuspendedProcessRecord>();
            string? refusedBy = null;
            string? refusal = null;
            foreach (var (pid, processName, startedUtc) in targets)
            {
                var result = ProcessSuspender.Suspend(pid, startedUtc, out string? error);
                if (result == ProcessSuspender.Outcome.Done)
                {
                    suspended.Add(new SuspendedProcessRecord
                    {
                        GameId = gameId,
                        GameName = name,
                        ProcessId = pid,
                        StartedUtc = startedUtc,
                        Muted = muted.Contains(pid)
                    });
                }
                else if (result == ProcessSuspender.Outcome.Refused)
                {
                    // All or nothing: a game half frozen - its launcher stopped and the game itself
                    // still running, say - is no use to anyone, and would read as suspended.
                    refusedBy = $"{processName} (PID {pid})";
                    refusal = error;
                    break;
                }
            }

            // Ended while it was being suspended: nothing will resume it after this, so don't leave it frozen.
            bool sessionEnded = Volatile.Read(ref session.Finished) != 0;
            if (refusedBy != null || suspended.Count == 0 || sessionEnded)
            {
                foreach (var record in suspended) ProcessSuspender.Resume(record.ProcessId, record.StartedUtc, out _);
                if (muted.Count > 0) ProcessAudioMuter.Unmute(muted);
                // Not suspended after all: the game goes back where it was, in front.
                RestoreGameWindows(windows, bringToFront: !sessionEnded);
                if (sessionEnded)
                {
                    LoggingService.Info("Suspend", $"'{name}' closed while it was being suspended; left running.");
                    return new(SuspendResult.NoSession, $"{name} has closed.", gameId);
                }
                string why = refusal ?? "its processes had exited";
                LoggingService.Warn("Suspend", $"Could not suspend '{name}': {refusedBy ?? "its processes"} refused ({why}); nothing was left suspended.");
                return new(SuspendResult.Refused,
                    $"{name} wasn't suspended: Windows refused ({why}). A game running as administrator can only be suspended when TrayTrigger runs as administrator too.",
                    gameId);
            }

            // A window that didn't minimize in time - a game that ignores the request, or one still
            // switching the display - is minimized by Windows itself, which works on a frozen window.
            int forced = 0;
            foreach (var hWnd in windows)
            {
                if (IsWindow(hWnd) && !IsIconic(hWnd) && ShowWindowAsync(hWnd, SW_FORCEMINIMIZE)) forced++;
            }

            session.SuspendedProcesses.Clear();
            session.SuspendedProcesses.AddRange(suspended);
            session.MinimizedWindows.Clear();
            session.MinimizedWindows.AddRange(windows);
            session.SuspendedAtUtc = DateTime.UtcNow;
            session.IsSuspended = true;
            PersistSuspended();

            string minimizedNote = windows.Count == 0 ? ", no window to minimize" : forced > 0 ? $", {forced} window(s) minimized by force" : ", minimized";
            LoggingService.Info("Suspend", $"Suspended '{name}' ({suspended.Count} process(es){minimizedNote}{(muted.Count > 0 ? ", sound muted" : "")}); its playtime clock is stopped.");
        }

        RaiseSuspendChanged(session);
        return new(SuspendResult.Suspended, $"{name} is suspended. Online games disconnect while suspended.", gameId);
    }

    /// <summary>Resumes what <see cref="SuspendGame"/> froze, unmutes it, and brings the game back to the front.</summary>
    public SuspendOutcome ResumeGame(string gameId, bool bringToFront = true)
    {
        var session = GetSession(gameId);
        if (session == null) return new(SuspendResult.NoSession, "That game isn't running.", gameId);
        string name = session.Game.Name;

        List<IntPtr> windows;
        lock (_suspendLock)
        {
            if (!session.IsSuspended) return new(SuspendResult.NotSuspended, $"{name} isn't suspended.", gameId);
            windows = session.MinimizedWindows.ToList();
            ResumeProcesses(session, "resumed");
        }

        RaiseSuspendChanged(session);
        if (bringToFront)
        {
            // The windows Suspend minimized, put back; a game whose window couldn't be found then
            // is brought forward by its main window, as before.
            if (!RestoreGameWindows(windows, bringToFront: true)) BringSessionToFront(session);
        }
        return new(SuspendResult.Resumed, $"{name} resumed.", gameId);
    }

    /// <summary>
    /// Minimizes every visible top-level window of these processes, and waits up to
    /// <see cref="MinimizeWait"/> for them to go. Returns the windows asked, minimized or not yet.
    /// </summary>
    private static List<IntPtr> MinimizeGameWindows(IEnumerable<int> pids)
    {
        var windows = new List<IntPtr>();
        foreach (int pid in pids)
        {
            foreach (var hWnd in ProcessPathResolver.TopLevelWindows(pid))
            {
                // Owned windows (a tooltip, a dialog) go with their owner.
                if (IsWindowVisible(hWnd) && !IsIconic(hWnd) && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero) windows.Add(hWnd);
            }
        }
        if (windows.Count == 0) return windows;

        // Asynchronous, so a game slow to answer can't hold the hotkey up past the wait below.
        foreach (var hWnd in windows) ShowWindowAsync(hWnd, SW_MINIMIZE);
        var deadline = DateTime.UtcNow + MinimizeWait;
        while (DateTime.UtcNow < deadline && windows.Any(w => IsWindow(w) && !IsIconic(w)))
        {
            Thread.Sleep(50);
        }
        return windows;
    }

    /// <summary>
    /// Restores windows <see cref="MinimizeGameWindows"/> minimized, and with
    /// <paramref name="bringToFront"/> gives the first focus. False when none is left to restore.
    /// </summary>
    private static bool RestoreGameWindows(List<IntPtr> windows, bool bringToFront)
    {
        var live = windows.Where(IsWindow).ToList();
        if (live.Count == 0) return false;
        foreach (var hWnd in live)
        {
            if (IsIconic(hWnd)) ShowWindowAsync(hWnd, SW_RESTORE);
        }
        if (bringToFront) ActivateWindow(live[0]);
        return true;
    }

    /// <summary>
    /// The suspend hotkey. Resumes the game it suspended last, if any game is suspended; otherwise
    /// suspends the game in front - or the only game running, when TrayTrigger is following just one.
    /// </summary>
    public SuspendOutcome ToggleSuspendFromHotkey()
    {
        // One press at a time: a second press while the first is still freezing the game would find
        // nothing suspended yet, and try to suspend it again rather than resume it. Its own lock, not
        // the suspend lock, which must never be held while the events reach the UI thread.
        lock (_hotkeyLock)
        {
            return ToggleSuspendFromHotkeyCore();
        }
    }

    private readonly Lock _hotkeyLock = new();

    private SuspendOutcome ToggleSuspendFromHotkeyCore()
    {
        var sessions = GetActiveSessions();
        var suspended = sessions.Where(s => s.IsSuspended).OrderByDescending(s => s.SuspendedAtUtc).FirstOrDefault();
        if (suspended != null) return ResumeGame(suspended.GameId);

        var running = sessions.Where(s => s.GameStarted).ToList();
        if (running.Count == 0)
        {
            LoggingService.Verbose("Suspend", "Suspend hotkey pressed with no game running.");
            return new(SuspendResult.NoSession, "No game launched from TrayTrigger is running.");
        }

        var target = running.Count == 1 ? running[0] : ForegroundSession(running);
        if (target == null)
        {
            LoggingService.Verbose("Suspend", $"Suspend hotkey pressed with {running.Count} games running and none of them in front.");
            return new(SuspendResult.Ambiguous, $"{running.Count} games are running. Switch to the one to suspend and press the hotkey again, or use the tray menu.");
        }
        return SuspendGame(target.GameId);
    }

    /// <summary>The session whose process owns the foreground window, or null.</summary>
    private ActiveGameSession? ForegroundSession(IReadOnlyList<ActiveGameSession> sessions)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;
        GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == 0) return null;
        return sessions.FirstOrDefault(s => SessionProcessTree(s).Any(t => t.Pid == pid));
    }

    /// <summary>
    /// TrayTrigger is exiting: resume every suspended game, since nothing else will. Playtime is
    /// recorded after this by <see cref="RecordPlaytimeOnShutdown"/>, which leaves the paused time out.
    /// </summary>
    public void ResumeAllOnShutdown()
    {
        foreach (var session in GetActiveSessions().Where(s => s.IsSuspended))
        {
            lock (_suspendLock)
            {
                if (session.IsSuspended) ResumeProcesses(session, "resumed because TrayTrigger is exiting");
            }
        }
    }

    /// <summary>
    /// At startup, before anything else runs a game: resumes whatever a previous TrayTrigger left
    /// suspended - it stopped without exiting (a crash, killed from Task Manager, an update), and
    /// its games are still frozen with no session left to resume them. Returns the games resumed.
    /// </summary>
    public IReadOnlyList<string> ResumeLeftSuspended()
    {
        var records = _storageService.LoadSuspendedProcesses();
        if (records.Count == 0) return [];

        var games = new List<string>();
        var unmute = new HashSet<int>();
        foreach (var group in records.GroupBy(r => r.GameId))
        {
            int resumed = 0;
            foreach (var record in group)
            {
                var result = ProcessSuspender.Resume(record.ProcessId, record.StartedUtc, out string? error);
                if (result == ProcessSuspender.Outcome.Done) resumed++;
                else if (result == ProcessSuspender.Outcome.Refused) LoggingService.Warn("Suspend", $"Could not resume PID {record.ProcessId} of '{record.GameName}', left suspended when TrayTrigger last stopped: {error}.");
                if (record.Muted && result == ProcessSuspender.Outcome.Done) unmute.Add(record.ProcessId);
            }
            string name = group.First().GameName;
            if (resumed > 0)
            {
                games.Add(name);
                LoggingService.Info("Suspend", $"Resumed '{name}' ({resumed} process(es)), left suspended when TrayTrigger last stopped.");
            }
            else
            {
                LoggingService.Verbose("Suspend", $"'{name}' was left suspended when TrayTrigger last stopped, but has exited since.");
            }
        }
        if (unmute.Count > 0) ProcessAudioMuter.Unmute(unmute);
        _storageService.SaveSuspendedProcesses([]);
        return games;
    }

    /// <summary>
    /// The session is ending: if its game is frozen, thaw it and leave it where it is. Asked under the
    /// suspend lock on every session end, so a suspend still under way when the game exits is either
    /// seen here or sees the session finished itself (<see cref="SuspendGame"/>) - never neither.
    /// </summary>
    private void ResumeForSessionEnd(ActiveGameSession session)
    {
        lock (_suspendLock)
        {
            if (!session.IsSuspended) return;
            ResumeProcesses(session, "resumed because its session ended");
        }
        RaiseSuspendChanged(session);
    }

    /// <summary>Unmutes a suspended game without resuming it - Force Close, just before the kill.</summary>
    private void UnmuteSuspended(ActiveGameSession session)
    {
        HashSet<int> pids;
        lock (_suspendLock)
        {
            pids = session.SuspendedProcesses.Where(p => p.Muted).Select(p => p.ProcessId).ToHashSet();
            foreach (var record in session.SuspendedProcesses) record.Muted = false;
        }
        if (pids.Count > 0) ProcessAudioMuter.Unmute(pids);
    }

    /// <summary>Resumes and unmutes the session's processes and restarts its playtime clock. Holds <see cref="_suspendLock"/>.</summary>
    private void ResumeProcesses(ActiveGameSession session, string why)
    {
        int resumed = 0;
        var unmute = new HashSet<int>();
        foreach (var record in session.SuspendedProcesses)
        {
            var result = ProcessSuspender.Resume(record.ProcessId, record.StartedUtc, out string? error);
            if (result == ProcessSuspender.Outcome.Done) resumed++;
            else if (result == ProcessSuspender.Outcome.Refused) LoggingService.Warn("Suspend", $"Could not resume PID {record.ProcessId} of '{session.Game.Name}': {error}.");
            if (record.Muted) unmute.Add(record.ProcessId);
        }
        if (unmute.Count > 0) ProcessAudioMuter.Unmute(unmute);

        var paused = DateTime.UtcNow - session.SuspendedAtUtc;
        if (paused > TimeSpan.Zero) session.SuspendedTotal += paused;
        session.IsSuspended = false;
        session.SuspendedProcesses.Clear();
        session.MinimizedWindows.Clear();
        PersistSuspended();
        string howLong = paused.TotalMinutes < 1 ? $"{Math.Max(0, paused.TotalSeconds):0}s" : $"{paused.TotalMinutes:0}m";
        LoggingService.Info("Suspend", $"'{session.Game.Name}' {why} ({resumed} process(es)) after {howLong} suspended.");
    }

    /// <summary>Writes every session's suspended processes to disk, for a start after a crash. Holds <see cref="_suspendLock"/>.</summary>
    private void PersistSuspended()
    {
        var all = GetActiveSessions().SelectMany(s => s.SuspendedProcesses).ToList();
        _storageService.SaveSuspendedProcesses(all);
    }

    private void RaiseSuspendChanged(ActiveGameSession session)
    {
        try { SessionSuspendChanged?.Invoke(session); }
        catch (Exception ex) { LoggingService.Verbose("Suspend", $"SessionSuspendChanged handler failed: {ex.Message}"); }
    }

    private void BringSessionToFront(ActiveGameSession session)
    {
        try
        {
            foreach (var process in CollectSessionProcesses(session))
            {
                using (process)
                {
                    process.Refresh();
                    IntPtr hWnd = process.MainWindowHandle;
                    if (hWnd == IntPtr.Zero) continue;
                    ActivateWindow(hWnd);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Suspend", $"Could not bring '{session.Game.Name}' to the front after resuming it: {ex.Message}");
        }
    }

    /// <summary>The folder the session's game is installed in, where Force Close, Suspend and anti-cheat detection look.</summary>
    private string? SessionInstallDir(ActiveGameSession session) => session.Route == LaunchRoute.Steam
        ? (UrlProtocolHelper.IsValidSteamAppId(session.Game.SteamAppId) ? _steamScannerService.FindInstallDirForAppId(session.Game.SteamAppId!) : null)
        : ResolveTrackedInstallDir(session.Game);

    /// <summary>
    /// Every process of the session - the one TrayTrigger holds, or those under the install folder -
    /// and every process each of them started, whenever it started it. Never TrayTrigger itself, and
    /// never anything run from the Windows folder (a console game's conhost, Windows' crash reporter).
    /// </summary>
    private List<(int Pid, string Name, DateTime StartedUtc)> SessionProcessTree(ActiveGameSession session)
    {
        var tree = new List<(int Pid, string Name, DateTime StartedUtc)>();
        var seen = new HashSet<int>();
        int self = Environment.ProcessId;
        string windows = ProcessPathResolver.NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        foreach (var process in CollectSessionProcesses(session))
        {
            using (process)
            {
                int pid;
                DateTime startedUtc;
                string name;
                try
                {
                    pid = process.Id;
                    startedUtc = process.StartTime.ToUniversalTime();
                    name = process.ProcessName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    LoggingService.Verbose("Suspend", $"'{session.Game.Name}': a process could not be read ({ex.Message}); skipped.");
                    continue;
                }
                if (pid == self || !seen.Add(pid)) continue;
                tree.Add((pid, name, startedUtc));

                foreach (var child in ProcessTree.Descendants(pid, startedUtc, "Suspend"))
                {
                    if (child.Pid == self || !seen.Add(child.Pid)) continue;
                    string? path = ProcessPathResolver.GetProcessPath(child.Pid);
                    if (path != null && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    tree.Add(child);
                }
            }
        }
        return tree;
    }
}
