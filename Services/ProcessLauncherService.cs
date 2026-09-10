using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

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
    private readonly GogScannerService _gogScannerService;
    private readonly EaScannerService _eaScannerService;
    private readonly EpicScannerService _epicScannerService;
    private readonly UbisoftScannerService _ubisoftScannerService;
    public event Action<GameEntry>? GameUpdated;

    /// <summary>
    /// Fired once a freshly-launched game's window has been found and given foreground focus (or,
    /// failing that, once a bounded wait for it gives up) - the signal LibraryViewModel uses to
    /// minimize TrayTrigger to tray instead of guessing with a fixed delay. See
    /// <see cref="WaitForWindowAndActivate"/>.
    /// </summary>
    public event Action<GameEntry>? GameWindowReady;

    public ProcessLauncherService(StorageService storageService, PerformanceProfileService performanceProfileService, GameScriptService scriptService, GogScannerService gogScannerService, EaScannerService eaScannerService, EpicScannerService epicScannerService, UbisoftScannerService ubisoftScannerService)
    {
        _storageService = storageService;
        _performanceProfileService = performanceProfileService;
        _scriptService = scriptService;
        _gogScannerService = gogScannerService;
        _eaScannerService = eaScannerService;
        _epicScannerService = epicScannerService;
        _ubisoftScannerService = ubisoftScannerService;
    }

    /// <summary>
    /// Confirms a running process actually corresponds to the given executable path, rather
    /// than just sharing its file name with an unrelated program.
    /// </summary>
    private static bool IsSameExecutable(Process process, string executablePath)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // MainModule can throw (e.g. access denied for an elevated process, or a
            // 32/64-bit mismatch). Fall back to matching on name alone rather than
            // silently excluding a process we simply couldn't inspect.
            return true;
        }
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

    public bool LaunchGame(GameEntry game, out string? errorMessage)
    {
        return LaunchGame(game, out errorMessage, out _);
    }

    public bool LaunchGame(GameEntry game, out string? errorMessage, out bool isMissing)
    {
        errorMessage = null;
        isMissing = false;

        try
        {
            LoggingService.Info("Launcher", $"Attempting to launch '{game.Name}' (ID: {game.Id}).");

            // Steam game handling. A Steam-tagged entry whose user asked for a direct launch and
            // whose ExecutablePath is a real file (not a steam:// URL) is treated as a plain exe
            // below instead - Edit Game's "launch this executable directly" option.
            bool directSteamExe = game.LaunchDirectly && !game.ExecutablePath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) && File.Exists(game.ExecutablePath);
            if ((game.IsSteamGame || game.ExecutablePath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) && !directSteamExe)
            {
                string launchUrl = !string.IsNullOrEmpty(game.SteamAppId)
                    ? $"steam://rungameid/{game.SteamAppId}"
                    : game.ExecutablePath;

                // Order: profile → user's pre-launch script → game. See PerformanceProfileService
                // for why the profile goes first. TrackSteamSession ends the session if Steam
                // never reports the game running.
                _performanceProfileService.BeginGameSession(game);
                _scriptService.RunPreLaunch(game);

                LoggingService.Verbose("Launcher", $"Launching Steam URL: {launchUrl}");
                Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });

                game.LastPlayed = DateTime.Now;
                GameUpdated?.Invoke(game);
                LoggingService.Info("Launcher", $"Dispatched Steam launch for '{game.Name}'.");
                TrackSteamSession(game);
                return true;
            }

            // GOG game handling: launch through Galaxy when it's installed, so its overlay,
            // achievement sync, and cloud saves engage the same way Steam's do for a steam://
            // launch. Falls through to the normal executable handling below (direct exe launch)
            // when Galaxy isn't installed - covers DRM-free/offline-installer users - and also
            // when Galaxy is already running: Galaxy's command-line launch only starts the game
            // silently on a cold start, confirmed on GOG's own forums - if Galaxy is already open
            // (the common case once a user has logged in once), the exact same command line just
            // brings its window to the game's page with a Play button instead, requiring a manual
            // click. Galaxy running in the background is enough for achievements/cloud saves
            // regardless of what actually started the game process, so a direct exe launch gets
            // the same features without the extra click.
            if (game.IsGogGame && !string.IsNullOrWhiteSpace(game.GogGameId))
            {
                string? galaxyClientPath = game.LaunchDirectly ? null : _gogScannerService.GetGalaxyClientPath();
                if (galaxyClientPath != null && !IsGalaxyClientRunning())
                {
                    return LaunchGogGameViaGalaxy(game, galaxyClientPath, out errorMessage);
                }
                LoggingService.Verbose("Launcher", game.LaunchDirectly
                    ? $"'{game.Name}' is set to launch directly; skipping GOG Galaxy."
                    : galaxyClientPath == null
                        ? $"GOG Galaxy not found; launching '{game.Name}' directly instead."
                        : $"GOG Galaxy already running; launching '{game.Name}' directly instead to skip its Play-button prompt.");
                // Not the generic "normal executable handling" below - GOG's registered exe is
                // often a short-lived prelauncher stub (e.g. Cyberpunk 2077's REDprelauncher.exe)
                // that spawns the real game and exits within milliseconds, so this needs the same
                // install-dir polling/debounce LaunchGogGameViaGalaxy uses, not a single-PID watch.
                return LaunchGogGameDirectly(game, out errorMessage);
            }

            // EA game handling: launch through EA App when it's installed, for the same reasons
            // as the GOG branch above - some EA titles even enforce this at the DRM level and
            // will fail (or try to launch EA App themselves) if only run directly. Falls through
            // to direct exe when EA App isn't installed.
            if (game.IsEaGame && !string.IsNullOrWhiteSpace(game.EaContentId))
            {
                if (!game.LaunchDirectly && _eaScannerService.IsEaAppInstalled())
                {
                    return LaunchEaGameViaClient(game, out errorMessage);
                }
                LoggingService.Verbose("Launcher", game.LaunchDirectly
                    ? $"'{game.Name}' is set to launch directly; skipping EA App."
                    : $"EA App not found; launching '{game.Name}' directly instead.");
                // Not the generic "normal executable handling" below - like GOG, EA's registered
                // exe can be a short-lived prelauncher/anti-cheat stub that spawns the real game
                // and exits within milliseconds, so this needs install-dir polling, not a
                // single-PID watch.
                return LaunchEaGameDirectly(game, out errorMessage);
            }

            // Epic Games Store handling: launch through Epic Games Launcher when it's installed,
            // for the same reasons as the GOG/EA branches above. Falls through to direct exe when
            // the launcher isn't installed.
            if (game.IsEpicGame && !string.IsNullOrWhiteSpace(game.EpicAppName))
            {
                if (!game.LaunchDirectly && _epicScannerService.IsEpicLauncherInstalled())
                {
                    return LaunchEpicGameViaClient(game, out errorMessage);
                }
                LoggingService.Verbose("Launcher", game.LaunchDirectly
                    ? $"'{game.Name}' is set to launch directly; skipping Epic Games Launcher."
                    : $"Epic Games Launcher not found; launching '{game.Name}' directly instead.");
                // See the EA branch above for why this needs install-dir polling instead of the
                // generic single-PID handling below.
                return LaunchEpicGameDirectly(game, out errorMessage);
            }

            // Ubisoft Connect handling: launch through Ubisoft Connect when it's installed, for
            // the same reasons as the GOG/EA/Epic branches above. Falls through to direct exe when
            // it isn't installed.
            if (game.IsUbisoftGame && !string.IsNullOrWhiteSpace(game.UbisoftGameId))
            {
                if (!game.LaunchDirectly && _ubisoftScannerService.IsUbisoftConnectInstalled())
                {
                    return LaunchUbisoftGameViaClient(game, out errorMessage);
                }
                LoggingService.Verbose("Launcher", game.LaunchDirectly
                    ? $"'{game.Name}' is set to launch directly; skipping Ubisoft Connect."
                    : $"Ubisoft Connect not found; launching '{game.Name}' directly instead.");
                // See the EA branch above for why this needs install-dir polling instead of the
                // generic single-PID handling below.
                return LaunchUbisoftGameDirectly(game, out errorMessage);
            }

            // Normal executable handling
            if (string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                errorMessage = "Executable path is not configured.";
                isMissing = true;
                LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}': Executable path is blank.");
                return false;
            }

            // Non-Steam protocol shortcut (Epic, GOG Galaxy, Ubisoft Connect, etc. desktop
            // shortcuts resolve to a "scheme://..." URL, not a file). Hand it to the shell the
            // same way as the Steam branch above instead of treating it as a missing exe.
            if (IsNonFileProtocolUrl(game.ExecutablePath))
            {
                // Import already refuses these, but games.json is user-editable and older
                // libraries predate the check - never hand an unknown scheme to ShellExecute.
                if (!UrlProtocolHelper.IsAllowedLaunchUrl(game.ExecutablePath))
                {
                    errorMessage = $"\"{game.Name}\" points at a URL type TrayTrigger won't launch ({game.ExecutablePath}). Edit the game and set a real executable or a launcher link.";
                    LoggingService.Warn("Launcher", $"Refused to launch '{game.Name}': URL scheme not in the allow-list ({game.ExecutablePath}).");
                    return false;
                }

                // Pre-launch only: there's no process handle or running flag to detect the exit,
                // so a post-exit script can't be honoured for protocol launches.
                _scriptService.RunPreLaunch(game);

                LoggingService.Verbose("Launcher", $"Launching protocol URL: {game.ExecutablePath}");
                Process.Start(new ProcessStartInfo(game.ExecutablePath) { UseShellExecute = true });

                game.LastPlayed = DateTime.Now;
                GameUpdated?.Invoke(game);
                LoggingService.Info("Launcher", $"Dispatched protocol launch for '{game.Name}'.");
                return true;
            }

            if (!File.Exists(game.ExecutablePath))
            {
                errorMessage = $"Executable not found at:\n{game.ExecutablePath}";
                isMissing = true;
                LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}': Executable missing at '{game.ExecutablePath}'.");
                return false;
            }

            // If game is already running, bring window to foreground instead of duplicate launch
            Process[]? existingProcesses = null;
            try
            {
                string procName = Path.GetFileNameWithoutExtension(game.ExecutablePath);
                existingProcesses = Process.GetProcessesByName(procName);

                // Matching by process name alone is ambiguous: unrelated games/tools can share
                // a generic exe name (e.g. "Game.exe"). Confirm the running process actually
                // points at this game's executable before treating it as "already running".
                var matchingProcesses = existingProcesses
                    .Where(p => IsSameExecutable(p, game.ExecutablePath))
                    .ToList();

                if (matchingProcesses.Count > 0)
                {
                    var activeProc = matchingProcesses.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                                     ?? matchingProcesses[0];

                    IntPtr hWnd = activeProc.MainWindowHandle;
                    if (hWnd != IntPtr.Zero)
                    {
                        ActivateWindow(hWnd);
                    }

                    game.LastPlayed = DateTime.Now;
                    GameUpdated?.Invoke(game);
                    LoggingService.Info("Launcher", $"'{game.Name}' already running (PID {activeProc.Id}). Activated existing window.");
                    GameWindowReady?.Invoke(game);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Launcher", $"Note: Could not check or activate running process: {ex.Message}");
            }
            finally
            {
                if (existingProcesses != null)
                {
                    foreach (var p in existingProcesses)
                    {
                        p.Dispose();
                    }
                }
            }

            string workingDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
                ? game.WorkingDirectory
                : (Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty);

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

            // Order: profile → user's pre-launch script → game → process-bound tweaks.
            // See PerformanceProfileService for why the profile goes first.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Spawning process: '{game.ExecutablePath}', Args='{game.Arguments}', WorkDir='{workingDir}', RunAsAdmin={game.RunAsAdmin}");
            var process = Process.Start(startInfo);
            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);

            if (process == null)
            {
                // Shell reused an existing instance or gave us nothing to track: nothing will
                // ever signal an exit, so undo the pre-launch profile now.
                _performanceProfileService.EndGameSession(game.Id);
            }

            if (process != null)
            {
                LoggingService.Info("Launcher", $"Started '{game.Name}' (PID {process.Id}).");
                AttachExitTracking(game, process, DateTime.Now);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}': {ex.Message}", ex);
            // The profile is applied before the process starts, so a failed start (UAC cancelled,
            // missing DLL, etc.) must roll it back. No-op if nothing was applied.
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Shared tail of every launch path that ends up with a real Process handle to watch (direct
    /// exe launches, and a GOG game's process once <see cref="TrackGogGalaxySession"/> finds it) -
    /// records playtime and restores Performance Profile tweaks/runs the post-exit script exactly
    /// once, whenever that process exits.
    /// </summary>
    private void AttachExitTracking(GameEntry game, Process process, DateTime startTime)
    {
        bool exitHandlerAttached = false;
        int exitHandled = 0; // guards against running the handler twice (see below)

        void OnExited(object? s, EventArgs e)
        {
            if (Interlocked.Exchange(ref exitHandled, 1) != 0) return;
            long minutes = 0;
            try
            {
                TimeSpan playedDuration = DateTime.Now - startTime;
                minutes = (long)Math.Max(0, Math.Round(playedDuration.TotalMinutes));
                if (minutes > 0)
                {
                    game.CumulativePlaytimeMinutes += minutes;
                    GameUpdated?.Invoke(game);
                    LoggingService.Info("Launcher", $"'{game.Name}' session ended. +{minutes}m playtime recorded.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error("Launcher", $"Error updating playtime for '{game.Name}': {ex.Message}");
            }
            finally
            {
                // Built-in tweaks restore first so the user's script sees the machine
                // back in its normal state.
                _performanceProfileService.EndGameSession(game.Id);
                _scriptService.RunPostExit(game, minutes);
                process.Dispose();
            }
        }

        try
        {
            // OnGameProcessStarted/TrackPostExit run inside this try (not before it) so a
            // failure in either one is caught by the same catch/finally rollback below - this
            // matters for the GOG-Galaxy path, where AttachExitTracking is called from a Timer
            // callback rather than from inside LaunchGame's own outer try/catch.
            _performanceProfileService.OnGameProcessStarted(game, process);
            _scriptService.TrackPostExit(game);

            // Subscribe before enabling, not after: EnableRaisingEvents can fire Exited
            // on a thread-pool thread almost immediately for a process that already exited,
            // and doing it the other way around left a real window where that fire found
            // no subscriber yet and playtime for the session was silently never recorded.
            process.Exited += OnExited;
            process.EnableRaisingEvents = true;
            exitHandlerAttached = true;

            WaitForWindowAndActivate(game, process);

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
                // No Exited handler means OnExited (and its EndGameSession call) will
                // never run for this launch - end the session immediately instead of
                // leaving a Performance Profile applied with no way to know when to undo it.
                _performanceProfileService.EndGameSession(game.Id);
                _scriptService.RunPostExit(game, playedMinutes: 0);
                process.Dispose();
            }
        }
    }

    private static readonly TimeSpan WindowActivationPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WindowActivationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Polls briefly for a freshly-launched game process to create its main window, then gives it
    /// foreground focus and fires <see cref="GameWindowReady"/>. A process existing and having a
    /// window are two different moments - confirmed via real launches where the gap was several
    /// seconds - and nothing else in the launch pipeline tracks the second one, which is what
    /// LibraryViewModel actually needs before it's safe to minimize TrayTrigger without other
    /// windows (or the taskbar) ending up in front of the game. Bounded so a console-only process,
    /// or a game slow enough to blow past the window, still eventually fires the event instead of
    /// leaving LibraryViewModel waiting forever.
    /// </summary>
    private void WaitForWindowAndActivate(GameEntry game, Process process)
    {
        DateTime waitStartedUtc = DateTime.UtcNow;
        int handled = 0;
        Timer? timer = null;

        timer = new Timer(_ =>
        {
            try
            {
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
                    return;
                }

                if (Interlocked.Exchange(ref handled, 1) != 0) return;
                timer?.Dispose();

                if (hWnd != IntPtr.Zero)
                {
                    ActivateWindow(hWnd);
                    LoggingService.Verbose("Launcher", $"Activated window for '{game.Name}'.");
                }
                else
                {
                    LoggingService.Verbose("Launcher", $"No window found to activate for '{game.Name}' ({(exited ? "process exited" : "timed out")}).");
                }

                GameWindowReady?.Invoke(game);
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Launcher", $"Window activation error for '{game.Name}': {ex.Message}");
                if (Interlocked.Exchange(ref handled, 1) == 0)
                {
                    timer?.Dispose();
                    GameWindowReady?.Invoke(game);
                }
            }
        }, null, WindowActivationPollInterval, WindowActivationPollInterval);
    }

    /// <summary>
    /// Gives a window foreground focus using the same AttachThreadInput technique tools like
    /// AutoIt's WinActivate use. A plain SetForegroundWindow call is silently denied by Windows'
    /// foreground-lock heuristic once meaningful time/input has passed since the calling process
    /// itself had focus - exactly the situation here, since TrayTrigger hides itself and the real
    /// game window can appear many seconds later. Attaching to the current foreground thread's
    /// input queue makes the OS treat the call as if it came from that already-foregrounded
    /// thread, which it always allows.
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
            if (IsIconic(hWnd))
            {
                ShowWindowAsync(hWnd, SW_RESTORE);
            }
            else
            {
                ShowWindowAsync(hWnd, SW_SHOW);
            }
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

    private const string SteamRunningKeyRoot = @"Software\Valve\Steam\Apps\";
    private static readonly TimeSpan SteamSessionPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SteamSessionStartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A Steam launch is a fire-and-forget "steam://" dispatch with no Process handle - the
    /// Performance Profile can't be tied to the launcher exiting (Steam itself keeps running).
    /// Steam maintains its own "Running" flag per AppId while a game is actually in session, so
    /// poll that: the profile was already applied pre-dispatch, so this just waits for the flag to
    /// flip on and then ends the session (and fires the post-exit script) once it flips back off.
    /// If it never turns on (user cancels the Steam launch, game isn't actually installed, etc.),
    /// give up after a few minutes and roll the profile back rather than leaving it applied.
    /// </summary>
    private void TrackSteamSession(GameEntry game)
    {
        bool hasPostExitScript = !string.IsNullOrWhiteSpace(game.PostExitScriptPath);
        bool hasProfile = game.PerformanceProfile != PerformanceProfileMode.Off;
        if (!hasProfile && !hasPostExitScript)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(game.SteamAppId))
        {
            // No AppId means no "Running" flag to watch, so the session can never be ended.
            // Roll back whatever BeginGameSession applied instead of leaving it on forever.
            _performanceProfileService.EndGameSession(game.Id);
            LoggingService.Verbose("Launcher", $"'{game.Name}' has no Steam AppId; cannot track its session, so no Performance Profile or post-exit script will run for it.");
            return;
        }

        string runningKeyPath = SteamRunningKeyRoot + game.SteamAppId;
        string gameId = game.Id;
        DateTime waitStartedUtc = DateTime.UtcNow;
        bool sessionBegun = false;
        // Timer callbacks are re-entrant: EndGameSession can block for a while (e.g. an elevated
        // Remove-MpPreference for a Defender exclusion), during which further ticks would otherwise
        // re-enter the same "ended"/"timed out" branch and run EndGameSession/RunPostExit again.
        // Guards both terminal branches below so each fires at most once.
        int sessionHandled = 0;
        Timer? timer = null;

        timer = new Timer(_ =>
        {
            try
            {
                bool isRunning;
                using (var key = Registry.CurrentUser.OpenSubKey(runningKeyPath))
                {
                    isRunning = key?.GetValue("Running") is int i && i != 0;
                }

                if (!sessionBegun)
                {
                    if (isRunning)
                    {
                        sessionBegun = true;
                        _scriptService.TrackPostExit(game);
                        LoggingService.Verbose("Launcher", $"Steam reports '{game.Name}' now running.");
                    }
                    else if (DateTime.UtcNow - waitStartedUtc > SteamSessionStartTimeout)
                    {
                        if (Interlocked.Exchange(ref sessionHandled, 1) != 0) return;
                        timer?.Dispose();
                        LoggingService.Verbose("Launcher", $"'{game.Name}' never reported running via Steam within {SteamSessionStartTimeout.TotalMinutes:0}m; rolling back its Performance Profile.");
                        _performanceProfileService.EndGameSession(gameId);
                    }
                }
                else if (!isRunning)
                {
                    if (Interlocked.Exchange(ref sessionHandled, 1) != 0) return;
                    timer?.Dispose();
                    _performanceProfileService.EndGameSession(gameId);
                    _scriptService.RunPostExit(game, playedMinutes: 0);
                    LoggingService.Verbose("Launcher", $"Steam reports '{game.Name}' session ended.");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Launcher", $"Steam session tracking error for '{game.Name}': {ex.Message}");
            }
        }, null, SteamSessionPollInterval, SteamSessionPollInterval);
    }

    private static readonly TimeSpan InstallDirLaunchPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InstallDirLaunchTimeout = TimeSpan.FromMinutes(3);

    // How many consecutive polls (see InstallDirLaunchPollInterval) the same PID must be seen
    // under the install directory before it's trusted as the real game and handed off to
    // AttachExitTracking. 3 was chosen over the original 2 after a real Ubisoft title
    // (R6-Extraction_Plus.exe) turned out to itself be a handoff stub that outlived a 2-poll
    // (~2s) window but still exited (~1.85s later) before the real game took over - see
    // docs/adding-a-platform-integration.md.
    private const int RequiredStableSightings = 3;

    /// <summary>
    /// Resolves a client-launched game's install directory (GOG/EA), for use as the process-match
    /// key by <see cref="TryActivateRunningProcessUnderDirectory"/> and <see cref="TrackInstallDirSession"/>.
    /// </summary>
    private static string ResolveInstallDir(GameEntry game) =>
        !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
            ? game.WorkingDirectory
            : (Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty);

    /// <summary>
    /// If a process is already running under the game's install directory, brings its window to
    /// the foreground and returns true - callers should skip dispatching a new client launch in
    /// that case. Matches by directory rather than exe name/PID because the client-registered exe
    /// is often a short-lived prelauncher stub, not the real game (see
    /// <see cref="TrackInstallDirSession"/>), so a name-based check (as the plain direct-exe path
    /// below uses) would almost always miss an already-running game.
    /// </summary>
    private bool TryActivateRunningProcessUnderDirectory(GameEntry game, string normalizedInstallDir, string platformLabel)
    {
        var alreadyRunning = FindRunningProcessUnderDirectory(normalizedInstallDir);
        if (alreadyRunning == null) return false;

        using (alreadyRunning)
        {
            IntPtr hWnd = alreadyRunning.MainWindowHandle;
            if (hWnd != IntPtr.Zero)
            {
                ActivateWindow(hWnd);
            }

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"'{game.Name}' already running via {platformLabel} (PID {alreadyRunning.Id}). Activated existing window.");
            GameWindowReady?.Invoke(game);
            return true;
        }
    }

    /// <summary>
    /// True if GalaxyClient.exe is already running - callers should launch the game's exe
    /// directly instead of going through Galaxy's command line in that case, since the launch-args
    /// invocation only starts the game silently on a cold start (see the GOG branch of LaunchGame).
    /// </summary>
    private static bool IsGalaxyClientRunning()
    {
        var processes = Process.GetProcessesByName("GalaxyClient");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>
    /// Launches a GOG game through GalaxyClient.exe (the same "/command=runGame /gameId=&lt;id&gt;
    /// /path=&lt;installDir&gt;" invocation Galaxy's own shortcuts use - there's no public URL scheme
    /// like Steam's steam://). Only reached when Galaxy isn't already running - see the GOG branch
    /// of LaunchGame. GalaxyClient.exe is only a launcher stub, not the game itself, so this hands
    /// off to <see cref="TrackInstallDirSession"/> to find the real game process before
    /// playtime/Performance Profile/post-exit-script tracking can begin.
    /// </summary>
    private bool LaunchGogGameViaGalaxy(GameEntry game, string galaxyClientPath, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "GOG Galaxy"))
                {
                    return true;
                }
            }

            var galaxyStartInfo = new ProcessStartInfo
            {
                FileName = galaxyClientPath,
                UseShellExecute = false
            };
            // Each flag must be a single "/name=value" token, not "/name" and "value" as separate
            // arguments - Galaxy's parser silently ignores anything it doesn't recognize instead of
            // erroring, so a malformed invocation just opens the client to its normal UI rather than
            // running the game, with no indication anything was wrong.
            galaxyStartInfo.ArgumentList.Add("/command=runGame");
            galaxyStartInfo.ArgumentList.Add($"/gameId={game.GogGameId}");
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                galaxyStartInfo.ArgumentList.Add($"/path={installDir}");
            }

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' via GOG Galaxy (gameId {game.GogGameId}).");
            Process.Start(galaxyStartInfo);

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched GOG Galaxy launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "GOG Galaxy");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' via GOG Galaxy: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Launches a GOG game by spawning its own registered exe directly - used when Galaxy isn't
    /// installed, or is already running (see the GOG branch of LaunchGame). The registered exe is
    /// often a short-lived prelauncher stub (e.g. Cyberpunk 2077's REDprelauncher.exe) that spawns
    /// the real game and exits within milliseconds, so - like <see cref="LaunchGogGameViaGalaxy"/> -
    /// this hands off to <see cref="TrackInstallDirSession"/> instead of watching the spawned PID
    /// directly; the generic "normal executable handling" path assumes a 1:1 process/session
    /// relationship that doesn't hold here, and would restore the Performance Profile the instant
    /// the stub exits, well before the real game has actually started.
    /// </summary>
    private bool LaunchGogGameDirectly(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "GOG"))
                {
                    return true;
                }
            }

            string workingDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
                ? game.WorkingDirectory
                : (Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty);

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

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' directly (GOG, no Galaxy dispatch): '{game.ExecutablePath}'.");
            Process.Start(startInfo);

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched direct GOG launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "GOG");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' directly: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Launches an EA game through EA App's "origin2://" launch protocol (a real registered URL
    /// scheme, unlike GOG - see the origin2\shell\open\command registry key EA App installs).
    /// Some EA titles enforce this at the DRM level and will fail if launched directly. Like GOG,
    /// the dispatch is fire-and-forget with no Process handle, and the real game process (not
    /// necessarily the registered exe, which can itself be a stub) has to be found separately -
    /// reuses the exact same install-directory polling as the GOG path.
    /// </summary>
    private bool LaunchEaGameViaClient(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "EA App"))
                {
                    return true;
                }
            }

            string launchUrl = $"origin2://game/launch/?offerIds={Uri.EscapeDataString(game.EaContentId ?? string.Empty)}";

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching EA URL: {launchUrl}");
            Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched EA App launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "EA App");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' via EA App: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Launches an EA game by spawning its own registered exe directly - used when EA App isn't
    /// installed. Like GOG's registered exe, EA's can be a short-lived prelauncher/anti-cheat stub
    /// that spawns the real game and exits within milliseconds, so - like
    /// <see cref="LaunchGogGameDirectly"/> - this hands off to <see cref="TrackInstallDirSession"/>
    /// instead of watching the spawned PID directly.
    /// </summary>
    private bool LaunchEaGameDirectly(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "EA"))
                {
                    return true;
                }
            }

            string workingDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
                ? game.WorkingDirectory
                : (Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty);

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

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' directly (EA, no EA App dispatch): '{game.ExecutablePath}'.");
            Process.Start(startInfo);

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched direct EA launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "EA");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' directly: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Launches an Epic Games Store title through Epic Games Launcher's "com.epicgames.launcher://"
    /// protocol (a real registered URL scheme, confirmed via the HKEY_CLASSES_ROOT registry entry
    /// the launcher installs - same shape as EA's origin2://). Like GOG/EA, the dispatch is
    /// fire-and-forget with no Process handle, so this reuses the same install-directory polling.
    /// </summary>
    private bool LaunchEpicGameViaClient(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "Epic Games"))
                {
                    return true;
                }
            }

            string launchUrl = $"com.epicgames.launcher://apps/{Uri.EscapeDataString(game.EpicAppName ?? string.Empty)}?action=launch&silent=true";

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching Epic URL: {launchUrl}");
            Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched Epic Games launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "Epic Games");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' via Epic Games Launcher: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Launches an Epic Games Store title by spawning its own registered exe directly - used when
    /// Epic Games Launcher isn't installed. See <see cref="LaunchEaGameDirectly"/> for why this
    /// hands off to <see cref="TrackInstallDirSession"/> instead of watching the spawned PID
    /// directly.
    /// </summary>
    private bool LaunchEpicGameDirectly(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "Epic Games"))
                {
                    return true;
                }
            }

            string workingDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
                ? game.WorkingDirectory
                : (Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty);

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

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' directly (Epic, no launcher dispatch): '{game.ExecutablePath}'.");
            Process.Start(startInfo);

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched direct Epic launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "Epic Games");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' directly: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Launches a Ubisoft Connect game through the "uplay://launch/&lt;gameId&gt;/0" protocol (a
    /// real registered URL scheme, confirmed via the HKEY_CLASSES_ROOT registry entry Ubisoft
    /// Connect installs - same shape as EA's origin2:// and Epic's com.epicgames.launcher://).
    /// Like GOG/EA/Epic, the dispatch is fire-and-forget with no Process handle, so this reuses the
    /// same install-directory polling.
    /// </summary>
    private bool LaunchUbisoftGameViaClient(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "Ubisoft Connect"))
                {
                    return true;
                }
            }

            string launchUrl = $"uplay://launch/{Uri.EscapeDataString(game.UbisoftGameId ?? string.Empty)}/0";

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching Ubisoft URL: {launchUrl}");
            Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched Ubisoft Connect launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "Ubisoft Connect");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' via Ubisoft Connect: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Launches a Ubisoft Connect game by spawning its own registered exe directly - used when
    /// Ubisoft Connect isn't installed. See <see cref="LaunchEaGameDirectly"/> for why this hands
    /// off to <see cref="TrackInstallDirSession"/> instead of watching the spawned PID directly.
    /// </summary>
    private bool LaunchUbisoftGameDirectly(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            string installDir = ResolveInstallDir(game);

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (TryActivateRunningProcessUnderDirectory(game, normalizedInstallDir, "Ubisoft Connect"))
                {
                    return true;
                }
            }

            string workingDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
                ? game.WorkingDirectory
                : (Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty);

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

            // Order: profile → user's pre-launch script → game. See PerformanceProfileService
            // for why the profile goes first. TrackInstallDirSession ends the session if the
            // game's process never actually appears.
            _performanceProfileService.BeginGameSession(game);
            _scriptService.RunPreLaunch(game);

            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' directly (Ubisoft, no Connect dispatch): '{game.ExecutablePath}'.");
            Process.Start(startInfo);

            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);
            LoggingService.Info("Launcher", $"Dispatched direct Ubisoft launch for '{game.Name}'.");

            TrackInstallDirSession(game, installDir, "Ubisoft Connect");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            LoggingService.Error("Launcher", $"Failed to launch '{game.Name}' directly: {ex.Message}", ex);
            _performanceProfileService.EndGameSession(game.Id);
            return false;
        }
    }

    /// <summary>
    /// Reads a process's own executable path, falling back to a WMI query when direct access
    /// throws. MainModule opens a handle at the caller's privilege level and throws for a process
    /// running at higher integrity (elevated/anti-cheat/DRM-protected games) - WMI's process query
    /// goes through a broker service and can often see the path even when a direct handle can't,
    /// so it's worth a second attempt rather than silently treating an inaccessible process as
    /// "not a match" (the same failure mode <see cref="IsSameExecutable"/> avoids by falling back
    /// to a lenient match instead of exclusion).
    /// </summary>
    private static string? TryGetExecutablePath(Process proc)
    {
        try
        {
            return proc.MainModule?.FileName;
        }
        catch
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {proc.Id}");
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        if (obj["ExecutablePath"] is string wmiPath && !string.IsNullOrWhiteSpace(wmiPath))
                        {
                            return wmiPath;
                        }
                    }
                }
            }
            catch
            {
                // WMI can also fail (service unavailable, permissions) - give up on this process.
            }
            return null;
        }
    }

    /// <summary>
    /// Finds the first running process whose executable lives under the given directory (already
    /// normalized with a trailing separator). Used for GOG/EA client-launched games, whose
    /// registered exe is often a short-lived prelauncher rather than the real game, so matching by
    /// install directory is the only reliable identity both before launch (is it already running?)
    /// and while polling for it to appear (see <see cref="TrackInstallDirSession"/>).
    /// </summary>
    private static Process? FindRunningProcessUnderDirectory(string normalizedDir)
    {
        Process? found = null;
        foreach (var proc in Process.GetProcesses())
        {
            if (found != null)
            {
                proc.Dispose();
                continue;
            }

            string? path = TryGetExecutablePath(proc);
            if (path != null && path.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            {
                found = proc;
                continue;
            }

            proc.Dispose();
        }
        return found;
    }

    /// <summary>
    /// Polls for the actual game process to appear under the game's install directory, then hands
    /// off to <see cref="AttachExitTracking"/> once found. Matches by install directory rather than
    /// the registered/launched exe's filename because some client-launched games (e.g. GOG's
    /// Cyberpunk 2077, via REDprelauncher.exe) run a prelauncher stub that starts the real game
    /// process and exits - GOGWrapper (a third-party GOG launch tool) uses the same "search running
    /// processes under the game's path" approach for this reason, and EA's own client has the
    /// identical handoff ambiguity (Playnite has open issues about EA-launched games appearing to
    /// stop seconds after actually starting). A single sighting isn't enough to commit to, though:
    /// the prelauncher itself also lives under the install directory, so this requires the same
    /// process to be seen on <see cref="RequiredStableSightings"/> consecutive polls before
    /// treating it as the real game - a prelauncher that's already exited by then never gets
    /// attached to. Gives up and rolls back the Performance Profile if nothing stable appears
    /// within a few minutes (e.g. the user cancels a first-run EULA/verify prompt without
    /// actually starting the game).
    /// </summary>
    private void TrackInstallDirSession(GameEntry game, string installDir, string platformLabel)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            _performanceProfileService.EndGameSession(game.Id);
            LoggingService.Verbose("Launcher", $"'{game.Name}' has no resolvable install directory; cannot track its {platformLabel} session.");
            return;
        }

        // Nothing to gain from polling if there's no profile to restore and no script to run -
        // same reasoning as TrackSteamSession.
        bool hasPostExitScript = !string.IsNullOrWhiteSpace(game.PostExitScriptPath);
        bool hasProfile = game.PerformanceProfile != PerformanceProfileMode.Off;
        if (!hasProfile && !hasPostExitScript)
        {
            return;
        }

        string normalizedInstallDir = installDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        DateTime waitStartedUtc = DateTime.UtcNow;
        int sessionHandled = 0; // guards against a re-entrant tick redoing this (see TrackSteamSession)
        int? pendingCandidatePid = null; // debounce: require the same PID on RequiredStableSightings consecutive ticks
        int pendingCandidateSightings = 0;
        Timer? timer = null;

        timer = new Timer(_ =>
        {
            try
            {
                var candidate = FindRunningProcessUnderDirectory(normalizedInstallDir);

                if (candidate != null)
                {
                    if (pendingCandidatePid == candidate.Id)
                    {
                        pendingCandidateSightings++;
                        if (pendingCandidateSightings < RequiredStableSightings)
                        {
                            candidate.Dispose();
                            return;
                        }

                        if (Interlocked.Exchange(ref sessionHandled, 1) != 0)
                        {
                            candidate.Dispose();
                            return;
                        }
                        timer?.Dispose();
                        LoggingService.Verbose("Launcher", $"Found running process for '{game.Name}' (PID {candidate.Id}) after launching via {platformLabel}.");

                        DateTime startTime;
                        try { startTime = candidate.StartTime; }
                        catch { startTime = DateTime.Now; }

                        AttachExitTracking(game, candidate, startTime);
                        return;
                    }

                    pendingCandidatePid = candidate.Id;
                    pendingCandidateSightings = 1;
                    candidate.Dispose();
                }
                else
                {
                    pendingCandidatePid = null;
                    pendingCandidateSightings = 0;
                }

                if (DateTime.UtcNow - waitStartedUtc > InstallDirLaunchTimeout)
                {
                    if (Interlocked.Exchange(ref sessionHandled, 1) != 0) return;
                    timer?.Dispose();
                    LoggingService.Verbose("Launcher", $"'{game.Name}' never appeared as a running process within {InstallDirLaunchTimeout.TotalMinutes:0}m of launching via {platformLabel}; rolling back its Performance Profile.");
                    _performanceProfileService.EndGameSession(game.Id);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("Launcher", $"{platformLabel} session tracking error for '{game.Name}': {ex.Message}");
            }
        }, null, InstallDirLaunchPollInterval, InstallDirLaunchPollInterval);
    }
}
