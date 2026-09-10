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
        LaunchedAt = DateTime.Now;
        StartedAt = LaunchedAt;
    }

    public GameEntry Game { get; }
    public string GameId => Game.Id;
    public LaunchRoute Route { get; }
    public string PlatformLabel => LaunchRouter.PlatformLabelFor(Route);
    /// <summary>When TrayTrigger dispatched the launch.</summary>
    public DateTime LaunchedAt { get; }
    /// <summary>When the real game was first seen running (playtime starts here, not at dispatch).</summary>
    public DateTime StartedAt { get; internal set; }
    /// <summary>True once the real game process (or Steam's Running flag) has been observed.</summary>
    public bool GameStarted { get; internal set; }
    /// <summary>The game's process when TrayTrigger has a handle to it; null for a Steam session whose
    /// process hasn't been located (yet).</summary>
    public Process? Process { get; internal set; }
    public bool CanForceClose => Process != null || GameStarted;

    internal Action? CancelTracking;
    internal int Finished;
    internal int WindowReadySignalled;
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

    private readonly Lock _sessionsLock = new();
    private readonly Dictionary<string, ActiveGameSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>LastPlayed/playtime changed - the library should refresh and save.</summary>
    public event Action<GameEntry>? GameUpdated;

    /// <summary>
    /// Fired once a freshly-launched game's window has been found and given foreground focus (or,
    /// failing that, once a bounded wait for it gives up) - the signal LibraryViewModel uses to
    /// minimize TrayTrigger to tray instead of guessing with a fixed delay.
    /// </summary>
    public event Action<GameEntry>? GameWindowReady;

    /// <summary>A tracked session began (profile applied / launch dispatched). Raised on a background thread.</summary>
    public event Action<ActiveGameSession>? SessionStarted;
    /// <summary>A tracked session ended (tweaks restored, post-exit script dispatched). Raised on a background thread.</summary>
    public event Action<ActiveGameSession>? SessionEnded;

    public ProcessLauncherService(
        StorageService storageService,
        PerformanceProfileService performanceProfileService,
        GameScriptService scriptService,
        SteamScannerService steamScannerService,
        GogScannerService gogScannerService,
        EaScannerService eaScannerService,
        EpicScannerService epicScannerService,
        UbisoftScannerService ubisoftScannerService)
    {
        _storageService = storageService;
        _performanceProfileService = performanceProfileService;
        _scriptService = scriptService;
        _steamScannerService = steamScannerService;
        _gogScannerService = gogScannerService;
        _eaScannerService = eaScannerService;
        _epicScannerService = epicScannerService;
        _ubisoftScannerService = ubisoftScannerService;
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
        if (session == null) return false;

        if (forceCloseGame)
        {
            TerminateSessionProcesses(session);
        }

        FinishSession(session, gameRan: session.GameStarted, forceCloseGame ? "force-closed by user" : "ended by user");
        return true;
    }

    private void TerminateSessionProcesses(ActiveGameSession session)
    {
        try
        {
            if (session.Process != null)
            {
                try
                {
                    if (!session.Process.HasExited)
                    {
                        session.Process.Kill(entireProcessTree: true);
                        LoggingService.Info("Launcher", $"Force-closed '{session.Game.Name}' (PID {session.Process.Id}).");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("Launcher", $"Could not kill '{session.Game.Name}' by handle: {ex.Message}; trying by install folder.");
                }
            }

            string? installDir = session.Route == LaunchRoute.Steam
                ? (UrlProtocolHelper.IsValidSteamAppId(session.Game.SteamAppId) ? _steamScannerService.FindInstallDirForAppId(session.Game.SteamAppId!) : null)
                : ResolveInstallDir(session.Game);
            if (string.IsNullOrWhiteSpace(installDir)) return;

            string normalized = ProcessPathResolver.NormalizeDirectory(installDir);
            foreach (var candidate in ProcessPathResolver.FindProcessesUnderDirectory(normalized))
            {
                try
                {
                    using var proc = Process.GetProcessById(candidate.Pid);
                    proc.Kill(entireProcessTree: true);
                    LoggingService.Info("Launcher", $"Force-closed '{Path.GetFileName(candidate.Path)}' (PID {candidate.Pid}) for '{session.Game.Name}'.");
                }
                catch (Exception ex)
                {
                    LoggingService.Verbose("Launcher", $"Could not kill PID {candidate.Pid}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Launcher", $"Force-close failed for '{session.Game.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the profile, runs the pre-launch script, and registers the session. Returns null
    /// (with <paramref name="abortReason"/> set) if the pre-launch script asked to cancel the launch,
    /// in which case everything already applied has been rolled back.
    /// Order: profile → pre-launch script → game. See PerformanceProfileService for why the
    /// profile goes first.
    /// </summary>
    private ActiveGameSession? BeginSession(GameEntry game, LaunchRoute route, out string? abortReason)
    {
        abortReason = null;
        var session = new ActiveGameSession(game, route);
        lock (_sessionsLock)
        {
            _sessions[game.Id] = session;
        }

        _performanceProfileService.BeginGameSession(game);

        var scriptResult = _scriptService.RunPreLaunch(game);
        if (!scriptResult.ProceedWithLaunch)
        {
            abortReason = $"Launch of \"{game.Name}\" was cancelled because {scriptResult.AbortReason}.";
            LoggingService.Warn("Launcher", abortReason);
            RollbackSession(session);
            return null;
        }

        SessionStarted?.Invoke(session);
        return session;
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
    private void FinishSession(ActiveGameSession session, bool gameRan, string reason)
    {
        if (Interlocked.Exchange(ref session.Finished, 1) != 0) return;

        try { session.CancelTracking?.Invoke(); } catch { }

        lock (_sessionsLock)
        {
            if (_sessions.TryGetValue(session.GameId, out var current) && ReferenceEquals(current, session))
            {
                _sessions.Remove(session.GameId);
            }
        }

        var game = session.Game;
        long minutes = 0;
        try
        {
            if (gameRan)
            {
                TimeSpan played = DateTime.Now - session.StartedAt;
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
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("Launcher", $"Error updating playtime for '{game.Name}': {ex.Message}");
        }

        try
        {
            _performanceProfileService.EndGameSession(game.Id);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Launcher", $"Error restoring profile for '{game.Name}': {ex.Message}", ex);
        }

        if (gameRan)
        {
            _scriptService.RunPostExit(game, minutes);
        }
        else
        {
            _scriptService.UntrackPostExit(game);
        }

        // Make sure a window-ready waiter never hangs on a session that ended before a window appeared.
        SignalWindowReady(session);

        try { session.Process?.Dispose(); } catch { }
        session.Process = null;

        try { SessionEnded?.Invoke(session); } catch (Exception ex) { LoggingService.Verbose("Launcher", $"SessionEnded handler failed: {ex.Message}"); }

        if (gameRan && game.CloseLauncherOnExit && LaunchRouter.ClientPlatformFor(session.Route) is LauncherPlatform platform)
        {
            LauncherClientCloser.Close(platform, _steamScannerService.GetSteamInstallPath());
        }
    }

    private void SignalWindowReady(ActiveGameSession session)
    {
        if (Interlocked.Exchange(ref session.WindowReadySignalled, 1) != 0) return;
        GameWindowReady?.Invoke(session.Game);
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
                catch (ObjectDisposedException) { }
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

        try
        {
            LoggingService.Info("Launcher", $"Attempting to launch '{game.Name}' (ID: {game.Id}).");

            // A session already tracked for this game means a launch is in flight (or the game is
            // running). Re-dispatching would re-run the pre-launch script and start a second
            // tracker; focus what's there instead. "End session" in the tray clears a stuck one.
            var inFlight = GetSession(game.Id);
            if (inFlight != null)
            {
                IntPtr hWnd = IntPtr.Zero;
                try { hWnd = inFlight.Process?.MainWindowHandle ?? IntPtr.Zero; } catch { }
                if (hWnd != IntPtr.Zero) ActivateWindow(hWnd);
                LoggingService.Info("Launcher", inFlight.GameStarted
                    ? $"'{game.Name}' is already running (tracked session); activated its window instead of relaunching."
                    : $"'{game.Name}' launch is still in progress (waiting for {inFlight.PlatformLabel}); not dispatching again.");
                GameWindowReady?.Invoke(game);
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
                        $"com.epicgames.launcher://apps/{Uri.EscapeDataString(game.EpicAppName ?? string.Empty)}?action=launch&silent=true", out errorMessage);

                case LaunchRoute.UbisoftClient:
                    return LaunchViaClientUrl(game, route,
                        $"uplay://launch/{Uri.EscapeDataString(game.UbisoftGameId ?? string.Empty)}/0", out errorMessage);

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

        // Pre-launch only: there's no process handle or running flag to detect the exit, so
        // neither a Performance Profile nor a post-exit script can be honoured for these.
        var scriptResult = _scriptService.RunPreLaunch(game);
        if (!scriptResult.ProceedWithLaunch)
        {
            errorMessage = $"Launch of \"{game.Name}\" was cancelled because {scriptResult.AbortReason}.";
            return false;
        }

        LoggingService.Verbose("Launcher", $"Launching protocol URL: {game.ExecutablePath}");
        Process.Start(new ProcessStartInfo(game.ExecutablePath) { UseShellExecute = true });

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

    private static bool ReadSteamRunningFlag(string appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SteamRunningKeyRoot + appId);
            return key?.GetValue("Running") is int i && i != 0;
        }
        catch
        {
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
            try { hWnd = existing?.Process?.MainWindowHandle ?? IntPtr.Zero; } catch { }
            if (hWnd == IntPtr.Zero)
            {
                string? installDir = _steamScannerService.FindInstallDirForAppId(appId);
                if (installDir != null)
                {
                    using var proc = ProcessPathResolver.FindBestProcessUnderDirectory(ProcessPathResolver.NormalizeDirectory(installDir));
                    try { hWnd = proc?.MainWindowHandle ?? IntPtr.Zero; } catch { }
                }
            }
            if (hWnd != IntPtr.Zero) ActivateWindow(hWnd);

            MarkLaunched(game);
            LoggingService.Info("Launcher", $"'{game.Name}' is already running according to Steam{(existing != null ? " (tracked session)" : "")}; activated its window instead of relaunching.");
            GameWindowReady?.Invoke(game);
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
            string launchUrl = appId != null
                ? LaunchRouter.BuildSteamLaunchUrl(appId, game.Arguments)
                : game.ExecutablePath;

            LoggingService.Verbose("Launcher", $"Launching Steam URL: {launchUrl}");
            Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });

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
        _scriptService.TrackPostExit(game);

        Poller? poller = null;
        poller = new Poller(SteamSessionPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0) return false;

            bool isRunning = ReadSteamRunningFlag(appId);

            if (!session.GameStarted)
            {
                if (isRunning)
                {
                    session.GameStarted = true;
                    session.StartedAt = DateTime.Now;
                    LoggingService.Verbose("Launcher", $"Steam reports '{game.Name}' now running.");
                    return true;
                }

                if (DateTime.Now - session.LaunchedAt > SteamSessionStartTimeout)
                {
                    LoggingService.Verbose("Launcher", $"'{game.Name}' never reported running via Steam within {SteamSessionStartTimeout.TotalMinutes:0}m; rolling back its Performance Profile.");
                    FinishSession(session, gameRan: false, "Steam never reported the game running");
                    return false;
                }
                return true;
            }

            if (!isRunning)
            {
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
                        CpuTopologyService.ApplyAffinity(proc, game);
                        WaitForWindowAndActivate(session, proc);
                    }
                }

                if (session.Process == null && (installDir == null || processSearchTicks >= SteamProcessSearchTicks))
                {
                    // Nothing to focus - let the library minimize anyway rather than wait forever.
                    SignalWindowReady(session);
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
        Process[]? candidates = null;
        try
        {
            string procName = Path.GetFileNameWithoutExtension(game.ExecutablePath);
            candidates = Process.GetProcessesByName(procName);

            var matching = candidates
                .Where(p => ProcessPathResolver.IsSamePath(ProcessPathResolver.GetProcessPath(p.Id), game.ExecutablePath))
                .ToList();
            if (matching.Count == 0) return false;

            var activeProc = matching.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? matching[0];
            IntPtr hWnd = activeProc.MainWindowHandle;
            if (hWnd != IntPtr.Zero)
            {
                ActivateWindow(hWnd);
            }

            MarkLaunched(game);
            LoggingService.Info("Launcher", $"'{game.Name}' already running (PID {activeProc.Id}). Activated existing window.");
            GameWindowReady?.Invoke(game);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Launcher", $"Note: Could not check or activate running process: {ex.Message}");
            return false;
        }
        finally
        {
            if (candidates != null)
            {
                foreach (var p in candidates) p.Dispose();
            }
        }
    }

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
        session.GameStarted = true;
        try { session.StartedAt = process.StartTime; } catch { session.StartedAt = DateTime.Now; }

        int exitHandled = 0;

        void OnExited(object? s, EventArgs e)
        {
            if (Interlocked.Exchange(ref exitHandled, 1) != 0) return;

            if (allowStubHandoff && DateTime.Now - session.StartedAt < StubHandoffWindow)
            {
                string installDir = ResolveInstallDir(game);
                if (!string.IsNullOrWhiteSpace(installDir))
                {
                    LoggingService.Info("Launcher", $"'{game.Name}' exited {(DateTime.Now - session.StartedAt).TotalSeconds:0.0}s after starting - treating it as a launcher stub and looking for the real game under '{installDir}'.");
                    try { process.Dispose(); } catch { }
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
            _performanceProfileService.OnGameProcessStarted(game, process);
            CpuTopologyService.ApplyAffinity(process, game);
            _scriptService.TrackPostExit(game);

            // Subscribe before enabling, not after: EnableRaisingEvents can fire Exited on a
            // thread-pool thread almost immediately for a process that already exited.
            process.Exited += OnExited;
            process.EnableRaisingEvents = true;
            exitHandlerAttached = true;

            WaitForWindowAndActivate(session, process);

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
                // No Exited handler means the session would never end - end it now instead of
                // leaving a Performance Profile applied with no way to know when to undo it.
                FinishSession(session, gameRan: true, "could not watch the process");
            }
        }
    }

    private static readonly TimeSpan WindowActivationPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowActivationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Polls briefly for a freshly-launched game process to create its main window, then gives it
    /// foreground focus and signals <see cref="GameWindowReady"/>. A process existing and having a
    /// window are two different moments - confirmed via real launches where the gap was several
    /// seconds - and this is what LibraryViewModel needs before it's safe to minimize TrayTrigger
    /// without other windows ending up in front of the game. Bounded so a console-only process,
    /// or a game slow enough to blow past the window, still eventually signals.
    /// </summary>
    private void WaitForWindowAndActivate(ActiveGameSession session, Process process)
    {
        DateTime waitStartedUtc = DateTime.UtcNow;
        var game = session.Game;

        _ = new Poller(WindowActivationPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0 || Volatile.Read(ref session.WindowReadySignalled) != 0) return false;

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

            SignalWindowReady(session);
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
    private static void ActivateWindow(IntPtr hWnd)
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
        try { hWnd = alreadyRunning.MainWindowHandle; } catch { }
        if (hWnd != IntPtr.Zero)
        {
            ActivateWindow(hWnd);
        }

        MarkLaunched(game);
        LoggingService.Info("Launcher", $"'{game.Name}' already running via {platformLabel} (PID {alreadyRunning.Id}{(existing != null ? ", tracked session" : "")}). Activated existing window.");
        GameWindowReady?.Invoke(game);
        return true;
    }

    private static bool IsGalaxyClientRunning()
    {
        var processes = Process.GetProcessesByName("GalaxyClient");
        try { return processes.Length > 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    /// <summary>
    /// Launches a GOG game through GalaxyClient.exe (the same "/command=runGame /gameId=&lt;id&gt;
    /// /path=&lt;installDir&gt;" invocation Galaxy's own shortcuts use - there's no public URL scheme
    /// like Steam's steam://). Only reached when Galaxy isn't already running (see LaunchRouter).
    /// </summary>
    private bool LaunchGogGameViaGalaxy(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        string? galaxyClientPath = _gogScannerService.GetGalaxyClientPath();
        if (galaxyClientPath == null)
        {
            return LaunchPlatformExeDirectly(game, LaunchRoute.GogDirect, out errorMessage, out _);
        }

        string installDir = ResolveInstallDir(game);
        if (TryActivateRunningProcessUnderDirectory(game, installDir, "GOG Galaxy"))
        {
            return true;
        }

        var galaxyStartInfo = new ProcessStartInfo { FileName = galaxyClientPath, UseShellExecute = false };
        // Each flag must be a single "/name=value" token - Galaxy's parser silently ignores
        // anything it doesn't recognize instead of erroring.
        galaxyStartInfo.ArgumentList.Add("/command=runGame");
        galaxyStartInfo.ArgumentList.Add($"/gameId={game.GogGameId}");
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            galaxyStartInfo.ArgumentList.Add($"/path={installDir}");
        }

        var session = BeginSession(game, LaunchRoute.GogGalaxy, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' via GOG Galaxy (gameId {game.GogGameId}).");
            Process.Start(galaxyStartInfo);
            MarkLaunched(game);
            LoggingService.Info("Launcher", $"Dispatched GOG Galaxy launch for '{game.Name}'.");

            TrackInstallDirSession(session, installDir, "GOG Galaxy", InstallDirLaunchTimeout);
            return true;
        }
        catch
        {
            RollbackSession(session);
            throw;
        }
    }

    /// <summary>
    /// EA App ("origin2://"), Epic Games Launcher ("com.epicgames.launcher://") and Ubisoft
    /// Connect ("uplay://") all expose a real registered URL scheme. The dispatch is
    /// fire-and-forget with no Process handle, and the real game process (not necessarily the
    /// registered exe, which can itself be a stub) is found by install-directory polling.
    /// </summary>
    private bool LaunchViaClientUrl(GameEntry game, LaunchRoute route, string launchUrl, out string? errorMessage)
    {
        errorMessage = null;
        string label = LaunchRouter.PlatformLabelFor(route);
        string installDir = ResolveInstallDir(game);

        if (TryActivateRunningProcessUnderDirectory(game, installDir, label))
        {
            return true;
        }

        var session = BeginSession(game, route, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            LoggingService.Verbose("Launcher", $"Launching {label} URL: {launchUrl}");
            Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });
            MarkLaunched(game);
            LoggingService.Info("Launcher", $"Dispatched {label} launch for '{game.Name}'.");

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
            Process.Start(startInfo);
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

        Poller? poller = null;
        poller = new Poller(InstallDirLaunchPollInterval, () =>
        {
            if (Volatile.Read(ref session.Finished) != 0) return false;

            var candidates = ProcessPathResolver.FindProcessesUnderDirectory(normalizedInstallDir);
            if (candidates.Count > 0)
            {
                var best = candidates[0];
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
                    catch { proc = null; }

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
