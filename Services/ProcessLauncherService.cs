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

/// <summary>
/// One game launched by TrayTrigger whose lifetime is still being tracked: from the moment its
/// Performance Profile is applied until its exit signal (process exit, Steam's Running flag,
/// install-directory tracking, or the user's "End session") finishes it. Exposed so the UI can show
/// a "Playing" state, restore tweaks on demand, and force-close a hung game.
/// </summary>
public sealed class ActiveGameSession
{
    internal ActiveGameSession(GameEntry game, LaunchRoute route)
    {
        Game = game;
        Route = route;
        LaunchedAtUtc = DateTime.UtcNow;
        LaunchedAt = LaunchedAtUtc.ToLocalTime();
        StartedAt = LaunchedAt;
    }

    public GameEntry Game { get; }
    public string GameId => Game.Id;
    public LaunchRoute Route { get; }
    public string PlatformLabel => LaunchRouter.PlatformLabelFor(Route);
    /// <summary>When TrayTrigger dispatched the launch.</summary>
    public DateTime LaunchedAt { get; }
    /// <summary>
    /// <see cref="LaunchedAt"/> in UTC. Every elapsed-time check uses the UTC pair: subtracting
    /// local times puts a daylight-saving change's hour into the result.
    /// </summary>
    internal DateTime LaunchedAtUtc { get; }
    /// <summary>When the real game was first seen running (playtime starts here, not at dispatch).</summary>
    public DateTime StartedAt
    {
        get => _startedAt;
        internal set
        {
            _startedAt = value;
            StartedAtUtc = value.ToUniversalTime();
        }
    }
    private DateTime _startedAt;
    /// <summary><see cref="StartedAt"/> in UTC - see <see cref="LaunchedAtUtc"/>.</summary>
    internal DateTime StartedAtUtc { get; private set; }
    /// <summary>True once the real game process (or Steam's Running flag) has been observed.</summary>
    public bool GameStarted { get; internal set; }
    /// <summary>The game's process when TrayTrigger has a handle to it; null for a Steam session whose
    /// process hasn't been located (yet).</summary>
    public Process? Process { get; internal set; }
    public bool CanForceClose => Process != null || GameStarted;

    /// <summary>True while Suspend has the game frozen. The playtime clock is stopped meanwhile.</summary>
    public bool IsSuspended { get; internal set; }
    /// <summary>When the current suspension began; meaningful only while <see cref="IsSuspended"/>.</summary>
    internal DateTime SuspendedAtUtc { get; set; }
    /// <summary>Every earlier suspension's length added up, so playtime leaves it out.</summary>
    internal TimeSpan SuspendedTotal { get; set; }
    /// <summary>The processes the current suspension froze, so exactly those are resumed. Guarded by the launcher's suspend lock.</summary>
    internal List<SuspendedProcessRecord> SuspendedProcesses { get; } = new();
    /// <summary>The windows the current suspension minimized, restored when Resume brings the game back. Guarded like <see cref="SuspendedProcesses"/>.</summary>
    internal List<IntPtr> MinimizedWindows { get; } = new();

    /// <summary>
    /// How long the game has been played at <paramref name="nowUtc"/>: since it started, less the
    /// time it spent suspended, the current suspension included.
    /// </summary>
    public TimeSpan PlayedTime(DateTime nowUtc)
    {
        if (!GameStarted) return TimeSpan.Zero;
        var paused = SuspendedTotal + (IsSuspended ? nowUtc - SuspendedAtUtc : TimeSpan.Zero);
        var played = nowUtc - StartedAtUtc - paused;
        return played < TimeSpan.Zero ? TimeSpan.Zero : played;
    }

    /// <summary>
    /// For a post-exit script run at TrayTrigger's shutdown, which works its playtime out from a
    /// start time: when the game would have started had it never been suspended. Null until it starts.
    /// </summary>
    internal DateTime? PlaytimeStartUtc()
    {
        if (!GameStarted) return null;
        var now = DateTime.UtcNow;
        return now - PlayedTime(now);
    }

    internal Action? CancelTracking;
    internal int Finished;
    /// <summary>Close Game or Force Close asked the game to quit, so its exit is final: not a
    /// launcher stub handing off to the real game, however soon after starting it comes.</summary>
    internal volatile bool CloseRequested;
    internal int GameStartedSignalled;
}

public partial class ProcessLauncherService
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr hWnd);

    private readonly StorageService _storageService;
    private readonly PerformanceProfileService _performanceProfileService;
    private readonly GameScriptService _scriptService;
    private readonly SteamScannerService _steamScannerService;
    private readonly GogScannerService _gogScannerService;
    private readonly EaScannerService _eaScannerService;
    private readonly EpicScannerService _epicScannerService;
    private readonly UbisoftScannerService _ubisoftScannerService;
    private readonly XboxScannerService _xboxScannerService;
    private readonly BattleNetScannerService _battleNetScannerService;

    private readonly Lock _sessionsLock = new();
    private readonly Dictionary<string, ActiveGameSession> _sessions = new(StringComparer.Ordinal);
    /// <summary>Games whose LaunchGame call is still deciding its route, before any session exists. Guarded by <see cref="_sessionsLock"/>.</summary>
    private readonly HashSet<string> _launchesInProgress = new(StringComparer.Ordinal);

    /// <summary>LastPlayed/playtime changed - the library should refresh and save.</summary>
    public event Action<GameEntry>? GameUpdated;

    /// <summary>A tracked session began (profile applied / launch dispatched). Raised on a background thread.</summary>
    public event Action<ActiveGameSession>? SessionStarted;
    /// <summary>
    /// The game itself is now running, as opposed to merely dispatched - the process handle was
    /// attached, or Steam flipped its Running flag. Raised at most once per session, on a
    /// background thread.
    ///
    /// SessionStarted fires at dispatch, when <see cref="ActiveGameSession.GameStarted"/> is still
    /// false, and nothing raised anything when it later turned true - so anything rendered from
    /// that flag (the tray tooltip, the tray menu's Now Playing section) was painted once as
    /// "Starting ..." and stayed that way for the whole session.
    /// </summary>
    public event Action<ActiveGameSession>? SessionGameStarted;
    /// <summary>A tracked session ended (tweaks restored, post-exit script dispatched). Raised on a background thread.</summary>
    public event Action<ActiveGameSession>? SessionEnded;

    /// <summary>
    /// The single place <see cref="ActiveGameSession.GameStarted"/> turns on: sets the flag and the
    /// playtime clock, then raises <see cref="SessionGameStarted"/> exactly once. Every launch route
    /// funnels through here - Steam via its Running flag, everything else via
    /// <see cref="AttachExitTracking"/>.
    /// </summary>
    internal void MarkGameStarted(ActiveGameSession session, DateTime startedAt)
    {
        session.StartedAt = startedAt;
        session.GameStarted = true;
        if (Interlocked.Exchange(ref session.GameStartedSignalled, 1) != 0) return;
        try { SessionGameStarted?.Invoke(session); }
        catch (Exception ex) { LoggingService.Verbose("Launcher", $"SessionGameStarted handler failed: {ex.Message}"); }
        _ = Task.Run(() => NoteAntiCheat(session));
    }

    /// <summary>
    /// Looks for an anti-cheat once the game has had time to start its services, remembers it on the
    /// game (for the Game Details tag) and notes it in the Activity & History row. Never throws.
    /// </summary>
    private async Task NoteAntiCheat(ActiveGameSession session)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            string? found = AntiCheatDetector.Find(SessionInstallDir(session), session.Game.ExecutablePath, session.LaunchedAtUtc);
            // A check that finds nothing never clears what an earlier launch saw: the game may have
            // exited before its anti-cheat service started, or the service may start late.
            if (found == null) return;
            LaunchRecord.Note(session.GameId, LaunchRecord.Game, $"{found} detected");
            if (string.Equals(found, session.Game.DetectedAntiCheat, StringComparison.Ordinal)) return;
            session.Game.DetectedAntiCheat = found;
            GameUpdated?.Invoke(session.Game);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Launcher", $"Anti-cheat check failed: {ex.Message}");
        }
    }

    public ProcessLauncherService(
        StorageService storageService,
        PerformanceProfileService performanceProfileService,
        GameScriptService scriptService,
        SteamScannerService steamScannerService,
        GogScannerService gogScannerService,
        EaScannerService eaScannerService,
        EpicScannerService epicScannerService,
        UbisoftScannerService ubisoftScannerService,
        XboxScannerService xboxScannerService,
        BattleNetScannerService battleNetScannerService)
    {
        _storageService = storageService;
        _performanceProfileService = performanceProfileService;
        _scriptService = scriptService;
        _steamScannerService = steamScannerService;
        _gogScannerService = gogScannerService;
        _eaScannerService = eaScannerService;
        _epicScannerService = epicScannerService;
        _ubisoftScannerService = ubisoftScannerService;
        _xboxScannerService = xboxScannerService;
        _battleNetScannerService = battleNetScannerService;
    }

    /// <summary>
    /// Set by App from Settings: "Keep game launchers minimized when launching a game", honored by
    /// the Steam, Epic and GOG Galaxy launches with each client's own start-quietly switch. Unset means never.
    /// </summary>
    public Func<bool>? KeepLaunchersMinimized { get; set; }

    private bool ShouldKeepLaunchersMinimized => KeepLaunchersMinimized?.Invoke() == true;

    /// <summary>
    /// Saves the library after a launch changed a game record. Set by App, like
    /// <see cref="KeepLaunchersMinimized"/>; unset in tests, where nothing needs persisting.
    /// </summary>
    public Action? PersistLibrary { get; set; }

    /// <summary>
    /// The DLSS override service. A property rather than a constructor argument so the eight
    /// existing test call sites keep working; tests that care substitute a fake backend.
    /// </summary>
    public DlssOverrideService DlssOverrides { get; set; } = new();

    /// <summary>Tools that start with games or close for them (Edit Tool). Set by App; unset in tests, where none do.</summary>
    public CompanionToolService? CompanionTools { get; set; }

    /// <summary>
    /// The Played row written for each game's last session, so a note that arrives after it (CPU Cores
    /// after its wait, DLSS seen late in a short session) is added to the row's line rather than lost.
    /// </summary>
    private readonly Dictionary<string, ActivityEntry> _playedRows = new(StringComparer.Ordinal);
    private readonly Lock _playedRowsLock = new();

    /// <summary>Set as <see cref="LaunchRecord.LateNote"/> by App: adds a late note to the game's Played row.</summary>
    public void NoteOnPlayedRow(string gameId, string section, string text)
    {
        ActivityEntry? row;
        lock (_playedRowsLock) { if (!_playedRows.TryGetValue(gameId, out row)) return; }
        ActivityService.Current?.AppendToLine(row, section, text);
    }

    /// <summary>The Epic launch link. "silent=true" asks the launcher not to show its window.</summary>
    internal static string BuildEpicLaunchUrl(string appName, bool silent) =>
        $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch{(silent ? "&silent=true" : string.Empty)}";

    /// <summary>
    /// GalaxyClient.exe's arguments for a game launch with Galaxy closed (the only case the Galaxy
    /// route is used - see LaunchRouter). Each flag must be a single "/name=value" token: Galaxy's
    /// parser silently ignores anything it doesn't recognize instead of erroring.
    /// "/launchViaAutostart" is the switch Galaxy writes into its own Run entry to start in the
    /// background. Playnite's GOG plugin sends it in front of the same runGame command, and it is
    /// reported to keep Galaxy down only when Galaxy isn't already running.
    /// </summary>
    internal static List<string> BuildGalaxyRunGameArguments(string gameId, string? installDir, bool launchMinimized)
    {
        var arguments = new List<string>(4);
        if (launchMinimized) arguments.Add("/launchViaAutostart");
        arguments.Add("/command=runGame");
        arguments.Add($"/gameId={gameId}");
        if (!string.IsNullOrWhiteSpace(installDir)) arguments.Add($"/path={installDir}");
        return arguments;
    }

    /// <summary>
    /// True for a launcher protocol URL (e.g. "com.epicgames.launcher://...", "goggalaxy://...")
    /// as opposed to a filesystem path - including a plain "C:\..." path, which Uri also parses
    /// successfully but as the "file" scheme.
    /// </summary>
    internal static bool IsNonFileProtocolUrl(string path)
    {
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
    }

    // ==========================================================================================
    // Session registry
    // ==========================================================================================

    public IReadOnlyList<ActiveGameSession> GetActiveSessions()
    {
        lock (_sessionsLock) { return _sessions.Values.OrderBy(s => s.LaunchedAt).ToList(); }
    }

    public bool IsSessionActive(string gameId)
    {
        lock (_sessionsLock) { return _sessions.ContainsKey(gameId); }
    }

    /// <summary>A session is tracked for this game and its game hasn't started yet.</summary>
    public bool IsWaitingForGame(string gameId)
    {
        lock (_sessionsLock) { return _sessions.TryGetValue(gameId, out var session) && !session.GameStarted; }
    }

    private ActiveGameSession? GetSession(string gameId)
    {
        lock (_sessionsLock) { return _sessions.GetValueOrDefault(gameId); }
    }

    /// <summary>
    /// Ends a tracked session on the user's request: stops tracking, restores the Performance
    /// Profile, runs the post-exit script if the game actually ran. With
    /// <paramref name="forceCloseGame"/> the game process (or every non-helper process under its
    /// install folder, when no handle is held) is killed first.
    /// </summary>
    public bool EndSessionNow(string gameId, bool forceCloseGame)
    {
        var session = GetSession(gameId);
        if (session == null)
        {
            LoggingService.Verbose("Launcher", $"End session asked for game {gameId}, but no session is being tracked for it.");
            return false;
        }

        if (forceCloseGame)
        {
            // Final, like Close Game's: the exit must not be taken for a launcher stub, whose
            // hand-off would also dispose the handle while Kill is still using it.
            session.CloseRequested = true;
            // Unmuted before it dies: Windows keeps an app's mute in the Volume Mixer for its next
            // run, and a game killed while TrayTrigger had it muted would start silent next time.
            if (session.IsSuspended) UnmuteSuspended(session);
            TerminateSessionProcesses(session);
        }

        // The game keeps running when it isn't force-closed, so its launcher stays open: closing the
        // client under a running game breaks its DRM, cloud saves and achievements.
        FinishSession(session, gameRan: session.GameStarted, forceCloseGame ? "force-closed by user" : "ended by user", closeLauncher: forceCloseGame);
        return true;
    }

    /// <summary>What <see cref="CloseGameNow"/> managed.</summary>
    public enum CloseGameResult
    {
        /// <summary>Nothing is tracked for the game.</summary>
        NoSession,
        /// <summary>The game had not started yet (a launcher still loading or stuck), so there was
        /// nothing to close: the session ended and the tweaks were restored at once.</summary>
        EndedBeforeStart,
        /// <summary>The game quit when asked, and its session ended as after any normal exit.</summary>
        Closed,
        /// <summary>Asked, but still running when the wait ran out: often a "save before quitting?"
        /// prompt. The session stays, and ends when the game does.</summary>
        StillRunning,
        /// <summary>No window of the game's would take the request (none found, or the game runs as
        /// administrator and TrayTrigger doesn't). The session stays.</summary>
        CouldNotAsk,
    }

    /// <summary>How long <see cref="CloseGameNow"/> waits for the game to quit after asking.</summary>
    public static readonly TimeSpan CloseGameWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// "Close Game": asks the game to quit the way its own close button does, then lets the
    /// normal exit path end the session - profile restored, post-exit script, playtime - so it is
    /// exactly as if the player had quit. Blocks for up to <see cref="CloseGameWait"/>; call off the
    /// UI thread. A game that hasn't started yet has nothing to close, so its session ends at once.
    /// </summary>
    public CloseGameResult CloseGameNow(string gameId)
    {
        var session = GetSession(gameId);
        if (session == null)
        {
            LoggingService.Verbose("Launcher", $"Close Game asked for game {gameId}, but no session is being tracked for it.");
            return CloseGameResult.NoSession;
        }

        if (!session.GameStarted)
        {
            FinishSession(session, gameRan: false, "closed by user before the game started");
            return CloseGameResult.EndedBeforeStart;
        }

        // A frozen game can't answer its close button. Thawed first, it quits the usual way.
        if (session.IsSuspended) ResumeGame(gameId, bringToFront: false);

        session.CloseRequested = true;
        bool asked = false;
        foreach (var process in CollectSessionProcesses(session))
        {
            try
            {
                process.Refresh();
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                {
                    asked = true;
                    LoggingService.Info("Launcher", $"Asked '{session.Game.Name}' to close (PID {process.Id}).");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Launcher", $"Could not ask PID {SafeProcess.Id(process)} to close: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (!asked)
        {
            session.CloseRequested = false;
            LoggingService.Warn("Launcher", $"Close Game: no window of '{session.Game.Name}' took the request.");
            return CloseGameResult.CouldNotAsk;
        }

        var deadline = DateTime.UtcNow + CloseGameWait;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref session.Finished) != 0) return CloseGameResult.Closed;
            Thread.Sleep(250);
        }
        LoggingService.Info("Launcher", $"Close Game: '{session.Game.Name}' was still running after {CloseGameWait.TotalSeconds:0}s.");
        return CloseGameResult.StillRunning;
    }

    /// <summary>Kills each process's tree; false when one could not be killed.</summary>
    private bool KillAll(List<Process> processes)
    {
        bool allKilled = true;
        foreach (var process in processes)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                LoggingService.Info("Launcher", $"Force-closed PID {process.Id}.");
            }
            catch (Exception ex)
            {
                allKilled = false;
                LoggingService.Warn("Launcher", $"Could not kill PID {SafeProcess.Id(process)}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
        return allKilled;
    }

    /// <summary>
    /// The session's running game processes: the one TrayTrigger holds a handle to, or, when it
    /// holds none, every non-helper process under the game's install folder (never a Windows or
    /// system folder - see <see cref="ProcessPathResolver.IsUnsafeProcessFolder"/>). Each is a
    /// Process object opened here, which the caller disposes.
    /// </summary>
    private List<Process> CollectSessionProcesses(ActiveGameSession session, bool skipHandle = false)
    {
        var result = new List<Process>();
        if (session.Process != null && !skipHandle)
        {
            try
            {
                var held = session.Process;
                if (!held.HasExited)
                {
                    // An object of our own: the session's is disposed by its exit handler the moment
                    // the game dies, which can be in the middle of Kill walking its process tree.
                    // Same PID and start time, so not a new process that reused the number.
                    var own = Process.GetProcessById(held.Id);
                    if (own.StartTime == held.StartTime)
                    {
                        result.Add(own);
                        return result;
                    }
                    own.Dispose();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Launcher", $"Could not use the handle to '{session.Game.Name}': {ex.Message}; trying by install folder.");
            }
        }

        string? installDir = SessionInstallDir(session);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            LoggingService.Verbose("Launcher", $"'{session.Game.Name}': no install folder is known, so its processes can only be found through the one that was started.");
            return result;
        }

        string normalized = ProcessPathResolver.NormalizeDirectory(installDir);
        foreach (var candidate in ProcessPathResolver.FindProcessesUnderDirectory(normalized))
        {
            try
            {
                result.Add(Process.GetProcessById(candidate.Pid));
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Launcher", $"PID {candidate.Pid} is gone: {ex.Message}");
            }
        }
        return result;
    }

    private void TerminateSessionProcesses(ActiveGameSession session)
    {
        try
        {
            if (KillAll(CollectSessionProcesses(session))) return;

            var held = session.Process;
            if (held == null) return;

            // Our own Process object opens the game afresh, and a game started "as administrator"
            // refuses that to a TrayTrigger that isn't elevated. The handle from its launch was
            // granted full rights, so use it for this one attempt.
            try
            {
                if (!held.HasExited)
                {
                    held.Kill(entireProcessTree: true);
                    LoggingService.Info("Launcher", $"Force-closed '{session.Game.Name}' through its launch handle.");
                }
                return;
            }
            catch (Exception ex)
            {
                LoggingService.Warn("Launcher", $"Could not kill '{session.Game.Name}' through its launch handle: {ex.Message}; trying by install folder.");
            }
            KillAll(CollectSessionProcesses(session, skipHandle: true));
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Launcher", $"Force-close failed for '{session.Game.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the profile, runs the pre-launch script, starts the tools that start with games, and
    /// registers the session. Returns null (with <paramref name="abortReason"/> set) if the pre-launch
    /// script asked to cancel the launch, in which case everything already applied has been rolled back.
    /// Order: profile → pre-launch script → tools → game. See PerformanceProfileService for why the
    /// profile goes first.
    /// </summary>
    private ActiveGameSession? BeginSession(GameEntry game, LaunchRoute route, out string? abortReason)
    {
        abortReason = null;
        // The Played row's record starts here: everything done for this launch is noted under the game's id.
        LaunchRecord.Begin(game.Id);
        LaunchRecord.Note(game.Id, LaunchRecord.Launch, LaunchRouter.ClientPlatformFor(route) == null
            ? "launched directly"
            : $"launched through {LaunchRouter.PlatformLabelFor(route)}");
        var session = new ActiveGameSession(game, route);
        lock (_sessionsLock)
        {
            _sessions[game.Id] = session;
        }

        // Before the profile, and deliberately outside it: DLSS settings are persistent driver
        // state, not a session tweak, so they apply even when the performance profile is Off.
        ReapplyDlssSettings(game);

        // Before the profile, which goes by the default playback device: its Unmute audio has to act on the device the
        // game will play through, and put the mute back on that same one. FinishSession ends the profile first and this last.
        AudioOutputService.Shared.Begin(game);
        AudioOutputService.SharedInput.Begin(game);

        _performanceProfileService.BeginGameSession(game);

        // The game's own display mode, if it has one: set before the game exists, put back in FinishSession. After the
        // profile, whose HDR change comes first; the profile's frame cap asks the display setting for the refresh rate to expect.
        DisplayModeService.Shared.Begin(game);

        var scriptResult = _scriptService.RunPreLaunch(game);
        if (!scriptResult.ProceedWithLaunch)
        {
            abortReason = $"Launch of \"{game.Name}\" was cancelled because {scriptResult.AbortReason}.";
            LoggingService.Warn("Launcher", abortReason);
            RollbackSession(session);
            return null;
        }

        // After the script, so a launch it cancels closes and starts nothing; before the game, so a
        // tool's administrator prompt is answered before the game can go full screen over it.
        CompanionTools?.StartForGame(game, remember: true);

        // Guarded like SessionGameStarted and SessionEnded: a subscriber's failure must not turn
        // into a rolled-back session and a "launch failed" for a game that was about to start.
        try { SessionStarted?.Invoke(session); }
        catch (Exception ex) { LoggingService.Verbose("Launcher", $"SessionStarted handler failed: {ex.Message}"); }
        return session;
    }

    // Each session's DLSS poller, so a stub handoff can stop the one watching the process that
    // has gone before starting the next.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Poller> _dlssObservers = new();

    private static readonly TimeSpan DlssObserveInterval = TimeSpan.FromSeconds(30);
    private const int DlssObserveMaxTicks = 3;   // ~90 seconds

    /// <summary>
    /// Records what DLSS actually loaded, while the game is running - the only time it can be
    /// known. Costs nothing for a game TrayTrigger has never applied to, which is almost all of them.
    ///
    /// <para><b>Polled, not sampled once.</b> A game loads its DLSS runtime when it first builds
    /// the renderer, which can be a minute or more after the process starts - at a launcher, a
    /// shader compile, a main menu. Looking once at process start would almost always see nothing
    /// and record "unable to verify" for a game that was about to work perfectly. It stops at the
    /// first runtime seen, when the process exits, or after three ticks.</para>
    ///
    /// <para><b>Three ticks, and no more.</b> Enumerating another process's modules is what a
    /// cheat does when it goes looking for a game's memory layout. It is a documented API and
    /// anti-cheat refuses it cleanly rather than punishing it, but the window is still time spent
    /// reading a live game, so it is as short as it can be while covering the usual case. A game
    /// that has not built its renderer within 90 seconds records nothing, which costs a missing
    /// line on the card and no more.</para>
    ///
    /// <para>Failure is normal: anti-cheat titles refuse module enumeration, and then too there is
    /// simply nothing to record.</para>
    /// </summary>
    /// <summary>
    /// The running process of the executable the override was written for, or null. The process a
    /// session tracks can be a launcher - DOOM Eternal's is idTechLauncher - and a launcher never
    /// loads DLSS. The caller disposes what this returns.
    /// </summary>
    private static Process? FindRendererProcess(string rendererPath)
    {
        if (!DlssProbeService.IsFilePath(rendererPath)) return null;

        Process? found = null;
        foreach (var candidate in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(rendererPath)))
        {
            if (found == null) found = candidate;
            else candidate.Dispose();
        }
        return found;
    }

    /// <summary>Undo a session that never got as far as dispatching the game (or whose dispatch threw).</summary>
    private void RollbackSession(ActiveGameSession session)
    {
        FinishSession(session, gameRan: false, "launch did not complete");
    }

    /// <summary>
    /// The single exit path for every session. Idempotent: timers, the Exited handler, the
    /// shutdown path and the user's "End session" can all race here and only the first wins.
    /// Built-in tweaks restore first so the user's post-exit script sees the machine back in its
    /// normal state.
    /// </summary>
    private void FinishSession(ActiveGameSession session, bool gameRan, string reason, bool closeLauncher = true)
    {
        if (Interlocked.Exchange(ref session.Finished, 1) != 0) return;

        try { session.CancelTracking?.Invoke(); } catch (Exception ex) { LoggingService.Swallowed("Launcher", ex, "cancelling the session's tracking"); }

        lock (_sessionsLock)
        {
            if (_sessions.TryGetValue(session.GameId, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.GameId);
            }
        }

        // A session can end with its game still frozen: the user chose Close Game or Force Close,
        // or "ended" a session whose game is still running. Nothing would ever resume it after this.
        // Always asked, under the suspend lock: a suspend still finishing on another thread is seen.
        ResumeForSessionEnd(session);

        var game = session.Game;
        long minutes = 0;
        try
        {
            if (gameRan)
            {
                TimeSpan played = session.PlayedTime(DateTime.UtcNow);
                minutes = (long)Math.Max(0, Math.Round(played.TotalMinutes));
                if (minutes > 0)
                {
                    game.CumulativePlaytimeMinutes += minutes;
                    GameUpdated?.Invoke(game);
                }
                LoggingService.Info("Launcher", $"'{game.Name}' session ended ({reason}). +{minutes}m playtime recorded.");
            }
            else
            {
                LoggingService.Verbose("Launcher", $"'{game.Name}' session closed without the game running ({reason}); rolling back its Performance Profile.");
                if (LaunchProblems.DescribeUnseen(game.Name, reason) is var (unseenText, unseenDetail))
                {
                    ActivityService.Add(ActivityLevel.Problem, unseenText, subject: game.Name, detail: unseenDetail,
                        groupKey: LaunchProblems.UnseenGroupKey(game.Id));
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Launcher", $"Error updating playtime for '{game.Name}': {ex.Message}");
        }

        PerformanceProfileMode? profilePutBack = null;
        try
        {
            _performanceProfileService.EndGameSession(game.Id, out profilePutBack);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Launcher", $"Error restoring profile for '{game.Name}': {ex.Message}", ex);
        }
        // Never throws; a game that set no display mode is ignored.
        DisplayModeService.Shared.End(game.Id);
        AudioOutputService.SharedInput.End(game.Id);
        AudioOutputService.Shared.End(game.Id);
        CursorLockService.Shared.Untrack(game.Id);
        ScreenDimService.Shared.Untrack(game.Id);
        if (gameRan)
        {
            _scriptService.RunPostExit(game, minutes);
        }
        else
        {
            _scriptService.UntrackPostExit(game);
        }

        try { session.Process?.Dispose(); } catch { /* already disposed */ }
        session.Process = null;

        try { SessionEnded?.Invoke(session); } catch (Exception ex) { LoggingService.Verbose("Launcher", $"SessionEnded handler failed: {ex.Message}"); }

        if (closeLauncher && gameRan && game.CloseLauncherOnExit && LaunchRouter.ClientPlatformFor(session.Route) is LauncherPlatform platform)
        {
            LauncherClientCloser.Close(platform, _steamScannerService.GetSteamInstallPath());
            LaunchRecord.Note(game.Id, LaunchRecord.Launch, $"{LaunchRouter.PlatformLabelFor(session.Route)} closed after");
        }

        // The row goes last, once the post-exit script and the launcher have had their say. Taken
        // whether or not the game ran, so a failed launch's notes don't end up on the next one's row.
        var record = LaunchRecord.Take(game.Id);
        if (gameRan)
        {
            var (text, detail) = DescribePlaySession(game.Name, minutes, game.PerformanceProfile, session.StartedAtUtc, DateTime.UtcNow, record);
            // One row per game, its times listed when opened: a history, not a log of every launch.
            var row = ActivityService.Add(profilePutBack != null ? ActivityLevel.Change : ActivityLevel.Activity, text,
                subject: game.Name, detail: detail, groupKey: $"played|{game.Id}");
            // Before CloseCompanionToolsIfIdle below: what the Tools line still foretells is written on this row once it's known.
            CompanionTools?.AttachPlayedRow(game.Id, row);
            if (row != null) lock (_playedRowsLock) _playedRows[game.Id] = row;
        }

        CloseCompanionToolsIfIdle();
    }

    /// <summary>
    /// The game's Played row: a one-line title with the time played, and a detail that is the record
    /// of the launch, one line per section - when and through what, what the profile changed and put
    /// back (and skipped), what was done to the game's own process, the scripts, the tools - each
    /// only when it has something to say. The transparency promise, on the row people open.
    /// </summary>
    /// <param name="record">What was noted for this launch, from <see cref="LaunchRecord.Take"/>.</param>
    internal static (string Text, string Detail) DescribePlaySession(string name, long minutes,
        PerformanceProfileMode profile, DateTime startedUtc, DateTime endedUtc,
        IReadOnlyList<(string Section, IReadOnlyList<string> Notes)>? record = null)
    {
        string length = minutes < 1 ? "under a minute"
            : minutes < 60 ? $"{minutes}m"
            : minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m";
        string text = $"Played {name} · {length}";

        var notes = (record ?? []).ToDictionary(r => r.Section, r => r.Notes, StringComparer.Ordinal);
        var lines = new List<string>(5);

        // "Played: from 09:47 to 10:10 · launched through Steam · Steam closed after." Each line starts
        // with its label and a colon, which the page sets in bold.
        var launch = new List<string>(3);
        if (startedUtc != default) launch.Add($"from {startedUtc.ToLocalTime():HH:mm} to {endedUtc.ToLocalTime():HH:mm}");
        if (notes.TryGetValue(LaunchRecord.Launch, out var launchNotes)) launch.AddRange(launchNotes);
        if (launch.Count > 0) lines.Add("Played: " + string.Join(" · ", launch) + ".");

        // What changed, what didn't and whether it was undone, each on its own line:
        // "Profile: Aggressive · changed: Ultimate Performance power plan · HDR on."
        // "Skipped: Resizable BAR, off in the BIOS · speakers already unmuted."
        // "Put back: everything."
        var profileNotes = notes.TryGetValue(LaunchRecord.Profile, out var p) ? p : [];
        var changedNotes = profileNotes.Where(n => !n.StartsWith(LaunchRecord.SkippedPrefix, StringComparison.Ordinal) && !n.StartsWith(LaunchRecord.PutBackPrefix, StringComparison.Ordinal) && n != "nothing changed").ToList();
        var skipped = profileNotes.Where(n => n.StartsWith(LaunchRecord.SkippedPrefix, StringComparison.Ordinal)).Select(n => n[LaunchRecord.SkippedPrefix.Length..]).ToList();
        var putBack = profileNotes.Where(n => n.StartsWith(LaunchRecord.PutBackPrefix, StringComparison.Ordinal)).Select(n => n[LaunchRecord.PutBackPrefix.Length..]).ToList();
        lines.Add(changedNotes.Count > 0
            ? $"Profile: {profile} · changed: {string.Join(" · ", changedNotes)}."
            : $"Profile: {profile} · nothing changed.");
        if (skipped.Count > 0) lines.Add($"Skipped: {string.Join(" · ", skipped)}.");
        if (putBack.Count > 0) lines.Add($"Put back: {string.Join(" · ", putBack)}.");

        foreach (string section in new[] { LaunchRecord.Game, LaunchRecord.Scripts, LaunchRecord.Tools })
        {
            if (notes.TryGetValue(section, out var items) && items.Count > 0) lines.Add($"{section}: {string.Join(" · ", items)}.");
        }
        return (text, string.Join("\n", lines));
    }

    /// <summary>No game is running or being launched: the time to close the tools started with games, and open again the ones closed for them.</summary>
    private bool IsIdle()
    {
        lock (_sessionsLock) { return _sessions.Count == 0 && _launchesInProgress.Count == 0; }
    }

    /// <summary>
    /// After the last game, closes the tools started with games that are set to close, and opens again
    /// the tools closed for games that are set to come back. Called when a session ends and when a
    /// launch attempt ends, since a launch that failed after starting its tools ends its session while
    /// its own launch still counts as in progress.
    /// </summary>
    private void CloseCompanionToolsIfIdle()
    {
        if (CompanionTools is { } tools && IsIdle()) _ = tools.AfterLastGameAsync(IsIdle);
    }

    /// <summary>
    /// TrayTrigger is exiting while games it launched may still be running. Nothing will see them
    /// exit, so the time played so far is recorded now; without this a session that outlived
    /// TrayTrigger added nothing to the game's playtime. Each session is marked finished so a
    /// tracker that fires during shutdown can't count it a second time. The profile restore and
    /// post-exit scripts have their own shutdown paths in App.
    /// </summary>
    public void RecordPlaytimeOnShutdown()
    {
        List<ActiveGameSession> sessions;
        lock (_sessionsLock) { sessions = _sessions.Values.ToList(); }

        foreach (var session in sessions)
        {
            if (!session.GameStarted) continue;
            if (Interlocked.Exchange(ref session.Finished, 1) != 0) continue;

            try { session.CancelTracking?.Invoke(); } catch (Exception ex) { LoggingService.Swallowed("Launcher", ex, "cancelling the session's tracking"); }

            long minutes = (long)Math.Max(0, Math.Round(session.PlayedTime(DateTime.UtcNow).TotalMinutes));
            if (minutes <= 0) continue;

            session.Game.CumulativePlaytimeMinutes += minutes;
            LoggingService.Info("Launcher", $"'{session.Game.Name}' is still running at exit. +{minutes}m playtime recorded.");
            var (text, _) = DescribePlaySession(session.Game.Name, minutes, session.Game.PerformanceProfile, default, default);
            ActivityService.Add(ActivityLevel.Activity, $"{text} · still running when TrayTrigger closed", subject: session.Game.Name,
                groupKey: $"played|{session.GameId}");
            try { GameUpdated?.Invoke(session.Game); }
            catch (Exception ex) { LoggingService.Warn("Launcher", $"Could not save playtime for '{session.Game.Name}' at exit: {ex.Message}"); }
        }
    }

    /// <summary>
    /// A <see cref="Timer"/> that never overlaps its own callback: the period is infinite and the
    /// timer is re-armed only after the callback returns. The old pattern (periodic timer,
    /// re-entrant callbacks) let a slow tick - process enumeration, an elevated restore - overlap
    /// with the next one and corrupt the debounce counters.
    /// </summary>
    private sealed class Poller
    {
        private readonly TimeSpan _interval;
        private readonly Func<bool> _tick;
        private readonly Timer _timer;
        private int _stopped;

        /// <param name="tick">Return true to be called again after <paramref name="interval"/>, false to stop.</param>
        public Poller(TimeSpan interval, Func<bool> tick)
        {
            _interval = interval;
            _tick = tick;
            _timer = new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(interval, Timeout.InfiniteTimeSpan);
        }

        private void OnTick(object? _)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            bool again = true;
            try
            {
                again = _tick();
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Launcher", $"Session tracking tick failed: {ex.Message}");
            }

            if (again && Volatile.Read(ref _stopped) == 0)
            {
                try { _timer.Change(_interval, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { /* the poller was disposed between the tick and this re-arm */ }
            }
            else
            {
                Stop();
            }
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            _timer.Dispose();
        }
    }

    // ==========================================================================================
    // Launch entry point
    // ==========================================================================================

    public bool LaunchGame(GameEntry game, out string? errorMessage)
    {
        return LaunchGame(game, out errorMessage, out _);
    }

    /// <summary>
    /// Launches a game. Safe to call from a background thread - and it should be, since applying
    /// a profile (UAC prompts, Defender cmdlets) and waiting for a pre-launch script can take
    /// anywhere from milliseconds to minutes. All events are raised on whatever thread ends up
    /// observing the change; subscribers marshal to the UI thread themselves.
    /// </summary>
    public bool LaunchGame(GameEntry game, out string? errorMessage, out bool isMissing)
    {
        errorMessage = null;
        isMissing = false;
        bool claimed = false;

        try
        {
            LoggingService.Info("Launcher", $"Attempting to launch '{game.Name}' (ID: {game.Id}).");

            // A session already tracked for this game means a launch is in flight (or the game is
            // running). Re-dispatching would re-run the pre-launch script and start a second
            // tracker; focus what's there instead. "End session" in the tray clears a stuck one.
            // The claim covers the time before a route registers its session: two launches that
            // arrive together (a hotkey pressed twice) would otherwise both find nothing here.
            ActiveGameSession? inFlight;
            lock (_sessionsLock)
            {
                inFlight = _sessions.GetValueOrDefault(game.Id);
                if (inFlight == null)
                {
                    claimed = _launchesInProgress.Add(game.Id);
                    if (!claimed)
                    {
                        LoggingService.Info("Launcher", $"'{game.Name}' is already being launched; not dispatching again.");
                        return true;
                    }
                }
            }
            if (inFlight != null)
            {
                // Play on a game that is suspended means "back to it": resumed, then brought forward.
                if (inFlight.IsSuspended)
                {
                    LoggingService.Info("Launcher", $"'{game.Name}' is suspended; Play resumes it.");
                    ResumeGame(game.Id);
                }
                IntPtr hWnd = IntPtr.Zero;
                hWnd = SafeProcess.MainWindow(inFlight.Process);
                if (hWnd != IntPtr.Zero) ActivateWindow(hWnd);
                LoggingService.Info("Launcher", inFlight.GameStarted
                    ? $"'{game.Name}' is already running (tracked session); activated its window instead of relaunching."
                    : $"'{game.Name}' launch is still in progress (waiting for {inFlight.PlatformLabel}); not dispatching again.");
                return true;
            }

            var route = LaunchRouter.Resolve(game, GetClientAvailability(game));
            LoggingService.Verbose("Launcher", $"Launch route for '{game.Name}': {route}.");

            switch (route)
            {
                case LaunchRoute.MissingPath:
                    errorMessage = "Executable path is not configured.";
                    isMissing = true;
                    LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}': Executable path is blank.");
                    return false;

                case LaunchRoute.RefusedUrl:
                    // Import already refuses these, but games.json is user-editable and older
                    // libraries predate the check - never hand an unknown scheme to ShellExecute.
                    errorMessage = $"\"{game.Name}\" points at a URL type TrayTrigger won't launch ({game.ExecutablePath}). Edit the game and set a real executable or a launcher link.";
                    LoggingService.Warn("Launcher", $"Refused to launch '{game.Name}': URL scheme not in the allow-list ({game.ExecutablePath}).");
                    return false;

                case LaunchRoute.ProtocolUrl:
                    return LaunchProtocolUrl(game, out errorMessage);

                case LaunchRoute.Steam:
                    return LaunchSteamGame(game, out errorMessage);

                case LaunchRoute.DirectExe:
                    return LaunchDirectExe(game, out errorMessage, out isMissing);

                case LaunchRoute.GogGalaxy:
                    return LaunchGogGameViaGalaxy(game, out errorMessage);

                case LaunchRoute.EaClient:
                    return LaunchViaClientUrl(game, route,
                        $"origin2://game/launch/?offerIds={Uri.EscapeDataString(game.EaContentId ?? string.Empty)}", out errorMessage);

                case LaunchRoute.EpicClient:
                    return LaunchViaClientUrl(game, route,
                        BuildEpicLaunchUrl(game.EpicAppName ?? string.Empty, ShouldKeepLaunchersMinimized), out errorMessage);

                case LaunchRoute.UbisoftClient:
                    return LaunchViaClientUrl(game, route,
                        $"uplay://launch/{Uri.EscapeDataString(game.UbisoftGameId ?? string.Empty)}/0", out errorMessage);

                case LaunchRoute.Xbox:
                    return LaunchXboxGame(game, out errorMessage);

                case LaunchRoute.BattleNet:
                    return LaunchBattleNetGame(game, out errorMessage);

                case LaunchRoute.GogDirect:
                case LaunchRoute.EaDirect:
                case LaunchRoute.EpicDirect:
                case LaunchRoute.UbisoftDirect:
                    LogDirectFallback(game, route);
                    return LaunchPlatformExeDirectly(game, route, out errorMessage, out isMissing);

                default:
                    errorMessage = $"Unsupported launch route {route}.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}': {ex.Message}", ex);
            var stray = GetSession(game.Id);
            if (stray != null) RollbackSession(stray);
            return false;
        }
        finally
        {
            if (claimed)
            {
                lock (_sessionsLock) { _launchesInProgress.Remove(game.Id); }
                CloseCompanionToolsIfIdle();
            }
        }
    }

    private LaunchClientAvailability GetClientAvailability(GameEntry game)
    {
        return new LaunchClientAvailability(
            GogGalaxyInstalled: game.IsGogGame && _gogScannerService.GetGalaxyClientPath() != null,
            GogGalaxyRunning: game.IsGogGame && IsGalaxyClientRunning(),
            EaAppInstalled: game.IsEaGame && _eaScannerService.IsEaAppInstalled(),
            EpicLauncherInstalled: game.IsEpicGame && _epicScannerService.IsEpicLauncherInstalled(),
            UbisoftConnectInstalled: game.IsUbisoftGame && _ubisoftScannerService.IsUbisoftConnectInstalled());
    }

    private static void LogDirectFallback(GameEntry game, LaunchRoute route)
    {
        string client = route switch
        {
            LaunchRoute.GogDirect => "GOG Galaxy",
            LaunchRoute.EaDirect => "EA App",
            LaunchRoute.EpicDirect => "Epic Games Launcher",
            _ => "Ubisoft Connect"
        };
        LoggingService.Verbose("Launcher", game.LaunchDirectly
            ? $"'{game.Name}' is set to launch directly; skipping {client}."
            : $"{client} not available for a silent launch; launching '{game.Name}' directly instead.");
    }

    private void MarkLaunched(GameEntry game)
    {
        game.LastPlayed = DateTime.Now;
        GameUpdated?.Invoke(game);
    }

    // ==========================================================================================
    // Protocol shortcut (http/https/goggalaxy:// etc. with no platform ID): fire-and-forget
    // ==========================================================================================

    private bool LaunchProtocolUrl(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        // No session, so no Played row takes these notes; begun anyway so they don't land on the next launch's.
        LaunchRecord.Begin(game.Id);

        // Pre-launch only: there's no process handle or running flag to detect the exit, so
        // neither a Performance Profile nor a post-exit script can be honoured for these.
        var scriptResult = _scriptService.RunPreLaunch(game);
        if (!scriptResult.ProceedWithLaunch)
        {
            errorMessage = $"Launch of \"{game.Name}\" was cancelled because {scriptResult.AbortReason}.";
            return false;
        }

        // No exit to wait for either, so what starts here is left running, and what closes stays closed.
        CompanionTools?.StartForGame(game, remember: false);

        LoggingService.Verbose("Launcher", $"Launching protocol URL: {game.ExecutablePath}");
        Process.Start(new ProcessStartInfo(game.ExecutablePath) { UseShellExecute = true })?.Dispose();

        MarkLaunched(game);
        LoggingService.Info("Launcher", $"Dispatched protocol launch for '{game.Name}'.");
        return true;
    }

    // ==========================================================================================
    // Steam
    // ==========================================================================================

    private const string SteamRunningKeyRoot = @"Software\Valve\Steam\Apps\";
    private static readonly TimeSpan SteamSessionPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SteamSessionStartTimeout = TimeSpan.FromMinutes(3);
    // How many polls after Steam reports "running" to keep looking for the game's own process
    // (for window focus / priority / force-close) before giving up on finding one.
    private const int SteamProcessSearchTicks = 15;

    private bool LaunchDirectExe(GameEntry game, out string? errorMessage, out bool isMissing)
    {
        errorMessage = null;
        isMissing = false;

        if (!File.Exists(game.ExecutablePath))
        {
            errorMessage = $"Executable not found at:\n{game.ExecutablePath}";
            isMissing = true;
            LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}': Executable missing at '{game.ExecutablePath}'.");
            return false;
        }

        // If the game is already running, bring its window to the foreground instead of a
        // duplicate launch. Matching by process name alone is ambiguous (unrelated tools share
        // generic names like "Game.exe"), so the running process's real image path must match.
        if (TryActivateRunningExecutable(game))
        {
            return true;
        }

        string workingDir = ResolveInstallDir(game);

        var startInfo = new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            Arguments = game.Arguments ?? string.Empty,
            WorkingDirectory = workingDir,
            UseShellExecute = true
        };
        if (game.RunAsAdmin)
        {
            startInfo.Verb = "runas";
        }

        var session = BeginSession(game, LaunchRoute.DirectExe, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            LoggingService.Verbose("Launcher", $"Spawning process: '{game.ExecutablePath}', Args='{game.Arguments}', WorkDir='{workingDir}', RunAsAdmin={game.RunAsAdmin}");
            var process = Process.Start(startInfo);
            MarkLaunched(game);

            if (process == null)
            {
                // Shell reused an existing instance or gave us nothing to track: nothing will
                // ever signal an exit, so undo the pre-launch profile now.
                LoggingService.Warn("Launcher", $"'{game.Name}' started but the shell returned no process to track.");
                FinishSession(session, gameRan: false, "no process handle returned");
                return true;
            }

            LoggingService.Info("Launcher", $"Started '{game.Name}' (PID {process.Id}).");
            AttachExitTracking(session, process, allowStubHandoff: true);
            return true;
        }
        catch
        {
            // The profile is applied before the process starts, so a failed start (UAC
            // cancelled, missing DLL, etc.) must roll it back.
            RollbackSession(session);
            throw;
        }
    }

    private bool TryActivateRunningExecutable(GameEntry game)
    {
        var matching = ProcessPathResolver.FindRunningCopies(game.ExecutablePath);
        try
        {
            if (matching.Count == 0) return false;

            var activeProc = matching.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? matching[0];
            IntPtr hWnd = activeProc.MainWindowHandle;
            if (hWnd != IntPtr.Zero)
            {
                ActivateWindow(hWnd);
            }

            MarkLaunched(game);
            LoggingService.Info("Launcher", $"'{game.Name}' already running (PID {activeProc.Id}). Activated existing window.");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Launcher", $"Note: Could not check or activate running process: {ex.Message}");
            return false;
        }
        finally
        {
            foreach (var p in matching) p.Dispose();
        }
    }

    /// <summary>
    /// The game's process is known: the mouse is held to its screen and the other screens dimmed (for a game that has
    /// either on) whenever its window is in front. If the session ended while this ran, FinishSession has already looked
    /// and found nothing to lift, so it is lifted here: nothing else would.
    /// </summary>
    private static void TrackGameWindow(ActiveGameSession session, int processId)
    {
        CursorLockService.Shared.Track(session.Game, processId);
        ScreenDimService.Shared.Track(session.Game, processId);
        if (Volatile.Read(ref session.Finished) != 0)
        {
            CursorLockService.Shared.Untrack(session.Game.Id);
            ScreenDimService.Shared.Untrack(session.Game.Id);
        }
    }

    private static readonly TimeSpan WindowActivationPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowActivationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Polls briefly for a freshly-launched game process to create its main window, then gives it
    /// foreground focus. A process existing and having a window are two different moments -
    /// confirmed via real launches where the gap was several seconds. TrayTrigger has usually
    /// hidden itself by then, so whatever window took focus in the meantime is replaced by the
    /// game. Bounded so a console-only process, or a game slow enough to blow past the window,
    /// still ends the wait.
    /// </summary>
    private void WaitForWindowAndActivate(ActiveGameSession session, Process process)
    {
        DateTime waitStartedUtc = DateTime.UtcNow;
        var game = session.Game;

        _ = new Poller(WindowActivationPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0) return false;

            IntPtr hWnd = IntPtr.Zero;
            bool exited;
            try
            {
                exited = process.HasExited;
                if (!exited)
                {
                    process.Refresh();
                    hWnd = process.MainWindowHandle;
                }
            }
            catch (InvalidOperationException)
            {
                // The process has already exited.
                exited = true;
            }

            bool timedOut = DateTime.UtcNow - waitStartedUtc > WindowActivationTimeout;
            if (hWnd == IntPtr.Zero && !exited && !timedOut)
            {
                return true;
            }

            if (hWnd != IntPtr.Zero)
            {
                ActivateWindow(hWnd);
                LoggingService.Verbose("Launcher", $"Activated window for '{game.Name}'.");
            }
            else
            {
                LoggingService.Verbose("Launcher", $"No window found to activate for '{game.Name}' ({(exited ? "process exited" : "timed out")}).");
            }

            return false;
        });
    }

    /// <summary>
    /// Gives a window foreground focus using the same AttachThreadInput technique tools like
    /// AutoIt's WinActivate use. A plain SetForegroundWindow call is silently denied by Windows'
    /// foreground-lock heuristic once meaningful time/input has passed since the calling process
    /// itself had focus - exactly the situation here, since TrayTrigger hides itself and the real
    /// game window can appear many seconds later.
    /// </summary>
    internal static void ActivateWindow(IntPtr hWnd)
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
        uint currentThreadId = GetCurrentThreadId();

        bool attached = foregroundThreadId != 0 && foregroundThreadId != currentThreadId
            && AttachThreadInput(currentThreadId, foregroundThreadId, true);

        try
        {
            ShowWindowAsync(hWnd, IsIconic(hWnd) ? SW_RESTORE : SW_SHOW);
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    // ==========================================================================================
    // GOG / EA / Epic / Ubisoft - client launches and platform-direct launches, tracked by
    // install directory
    // ==========================================================================================

    private static readonly TimeSpan InstallDirLaunchPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InstallDirLaunchTimeout = TimeSpan.FromMinutes(3);

    // How many consecutive polls the same PID must be seen under the install directory before
    // it's trusted as the real game. 3 was chosen over the original 2 after a real Ubisoft title
    // (R6-Extraction_Plus.exe) turned out to itself be a handoff stub that outlived a 2-poll
    // window but still exited (~1.85s later) before the real game took over - see
    // docs/adding-a-platform-integration.md.
    private const int RequiredStableSightings = 3;

    /// <summary>The game's install directory: its configured working directory, else the exe's folder.</summary>
    private static string ResolveInstallDir(GameEntry game) =>
        !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
            ? game.WorkingDirectory
            : (Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty);

    /// <summary>
    /// The folder a session's processes are watched (and force-closed) under. For a Battle.net
    /// game that is the folder Blizzard's uninstall entry records now - a "Move install" leaves the
    /// saved entry pointing at the old place - falling back to <see cref="ResolveInstallDir"/>.
    /// Launch and force-close must agree on this, or a kill by folder looks in the wrong one.
    /// </summary>
    private string ResolveTrackedInstallDir(GameEntry game) =>
        game.IsBattleNetGame
            ? _battleNetScannerService.FindInstallDir(game.BattleNetUid) ?? ResolveInstallDir(game)
            : ResolveInstallDir(game);

    /// <summary>
    /// If a (non-helper) process is already running under the game's install directory, brings
    /// its window to the foreground and returns true - callers should skip dispatching a new
    /// launch in that case. Matches by directory rather than exe name because the client-registered
    /// exe is often a short-lived prelauncher stub, not the real game.
    /// </summary>
    private bool TryActivateRunningProcessUnderDirectory(GameEntry game, string installDir, string platformLabel)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return false;

        var existing = GetSession(game.Id);
        using var alreadyRunning = ProcessPathResolver.FindBestProcessUnderDirectory(ProcessPathResolver.NormalizeDirectory(installDir));
        if (alreadyRunning == null)
        {
            return false;
        }

        IntPtr hWnd = IntPtr.Zero;
        hWnd = SafeProcess.MainWindow(alreadyRunning);
        if (hWnd != IntPtr.Zero)
        {
            ActivateWindow(hWnd);
        }

        MarkLaunched(game);
        LoggingService.Info("Launcher", $"'{game.Name}' already running via {platformLabel} (PID {alreadyRunning.Id}{(existing != null ? ", tracked session" : "")}). Activated existing window.");
        return true;
    }

    /// <summary>
    /// Launches a platform game by spawning its own registered exe directly - used when the client
    /// isn't installed, or the user ticked "launch directly". The registered exe is often a
    /// short-lived prelauncher stub (e.g. Cyberpunk 2077's REDprelauncher.exe) that spawns the
    /// real game and exits within milliseconds, so this hands off to install-directory polling
    /// instead of watching the spawned PID.
    /// </summary>
    private bool LaunchPlatformExeDirectly(GameEntry game, LaunchRoute route, out string? errorMessage, out bool isMissing)
    {
        errorMessage = null;
        isMissing = false;
        string label = LaunchRouter.PlatformLabelFor(route);

        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
        {
            errorMessage = $"Executable not found at:\n{game.ExecutablePath}";
            isMissing = true;
            LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}' ({label}): Executable missing at '{game.ExecutablePath}'.");
            return false;
        }

        string installDir = ResolveInstallDir(game);
        if (TryActivateRunningProcessUnderDirectory(game, installDir, label))
        {
            return true;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = game.ExecutablePath,
            Arguments = game.Arguments ?? string.Empty,
            WorkingDirectory = installDir,
            UseShellExecute = true
        };
        if (game.RunAsAdmin)
        {
            startInfo.Verb = "runas";
        }

        var session = BeginSession(game, route, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' directly ({label}, no client dispatch): '{game.ExecutablePath}'.");
            Process.Start(startInfo)?.Dispose();
            MarkLaunched(game);
            LoggingService.Info("Launcher", $"Dispatched direct {label} launch for '{game.Name}'.");

            TrackInstallDirSession(session, installDir, label, InstallDirLaunchTimeout);
            return true;
        }
        catch
        {
            RollbackSession(session);
            throw;
        }
    }

    /// <summary>
    /// Polls for the actual game process to appear under the game's install directory, then hands
    /// off to <see cref="AttachExitTracking"/> once found. Requires the same PID on
    /// <see cref="RequiredStableSightings"/> consecutive polls before trusting it, so a prelauncher
    /// that's already exited by then never gets attached to; known helper processes (crash
    /// handlers, anti-cheat services) are never candidates. Gives up and rolls the profile back if
    /// nothing stable appears within <paramref name="timeout"/> - unless
    /// <paramref name="ownsProcessAlready"/> (a stub handoff), where the game did run and the
    /// session ends normally instead.
    /// </summary>
    private void TrackInstallDirSession(ActiveGameSession session, string installDir, string platformLabel, TimeSpan timeout, bool ownsProcessAlready = false)
    {
        var game = session.Game;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            LoggingService.Verbose("Launcher", $"'{game.Name}' has no resolvable install directory; cannot track its {platformLabel} session.");
            FinishSession(session, gameRan: ownsProcessAlready, "no install directory to track");
            return;
        }

        string normalizedInstallDir = ProcessPathResolver.NormalizeDirectory(installDir);
        DateTime waitStartedUtc = DateTime.UtcNow;
        int? pendingCandidatePid = null;
        int pendingCandidateSightings = 0;

        // Focus at first sight. A game started by its launcher can't take the foreground from the
        // window the user pressed Play in, so it sat behind TrayTrigger until the stable-sightings
        // check below confirmed it - 4-6 s, and seen at ~4 s with the EA app (2026-09-13). The
        // first window-owning process under the install folder gets focus straight away; the
        // session still only attaches after RequiredStableSightings. If that window was a stub's,
        // the real game's window gets focus when it shows up. That includes a stub handoff
        // (ownsProcessAlready): the successor is the real game starting, and nothing else focuses it.
        IntPtr earlyFocusedWindow = IntPtr.Zero;
        void GiveEarlyFocus(int pid, bool hasMainWindow)
        {
            if (!hasMainWindow) return;
            IntPtr hWnd;
            string exeName;
            try
            {
                using var process = Process.GetProcessById(pid);
                hWnd = process.MainWindowHandle;
                exeName = process.ProcessName;
            }
            catch
            {
                // The process has already exited.
                return;
            }
            if (hWnd == IntPtr.Zero || hWnd == earlyFocusedWindow) return;

            earlyFocusedWindow = hWnd;
            ActivateWindow(hWnd);
            LoggingService.Verbose("Launcher", $"Gave '{game.Name}' focus as soon as a window appeared under its install folder (PID {pid}, {exeName}), {(DateTime.UtcNow - waitStartedUtc).TotalSeconds:0.0}s after launching via {platformLabel}; still confirming it's the game.");
        }

        Poller? poller = null;
        poller = new Poller(InstallDirLaunchPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0) return false;

            var candidates = ProcessPathResolver.FindProcessesUnderDirectory(normalizedInstallDir);
            if (candidates.Count > 0)
            {
                var best = candidates[0];
                GiveEarlyFocus(best.Pid, best.HasMainWindow);
                if (pendingCandidatePid == best.Pid)
                {
                    pendingCandidateSightings++;
                }
                else
                {
                    pendingCandidatePid = best.Pid;
                    pendingCandidateSightings = 1;
                }

                if (pendingCandidateSightings >= RequiredStableSightings)
                {
                    Process? proc = null;
                    try
                    {
                        proc = Process.GetProcessById(best.Pid);
                        if (proc.HasExited) { proc.Dispose(); proc = null; }
                    }
                    catch { /* the process has already exited */ proc = null; }

                    if (proc != null)
                    {
                        LoggingService.Verbose("Launcher", $"Found running process for '{game.Name}' (PID {proc.Id}, {Path.GetFileName(best.Path)}) after launching via {platformLabel}.");
                        AttachExitTracking(session, proc, allowStubHandoff: false);
                        return false;
                    }

                    pendingCandidatePid = null;
                    pendingCandidateSightings = 0;
                }
            }
            else
            {
                pendingCandidatePid = null;
                pendingCandidateSightings = 0;
            }

            if (DateTime.UtcNow - waitStartedUtc > timeout)
            {
                if (ownsProcessAlready)
                {
                    LoggingService.Verbose("Launcher", $"No successor process appeared under '{installDir}' within {timeout.TotalSeconds:0}s; treating '{game.Name}' as finished.");
                    FinishSession(session, gameRan: true, "process exited (no successor found)");
                }
                else
                {
                    LoggingService.Verbose("Launcher", $"'{game.Name}' never appeared as a running process within {timeout.TotalMinutes:0}m of launching via {platformLabel}; rolling back its Performance Profile.");
                    FinishSession(session, gameRan: false, "game process never appeared");
                }
                return false;
            }
            return true;
        });
        session.CancelTracking = () => poller?.Stop();
    }
}
