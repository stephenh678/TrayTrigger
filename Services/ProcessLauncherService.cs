using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    private readonly StorageService _storageService;
    private readonly PerformanceProfileService _performanceProfileService;
    private readonly GameScriptService _scriptService;
    public event Action<GameEntry>? GameUpdated;

    public ProcessLauncherService(StorageService storageService, PerformanceProfileService performanceProfileService, GameScriptService scriptService)
    {
        _storageService = storageService;
        _performanceProfileService = performanceProfileService;
        _scriptService = scriptService;
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

            // Steam game handling
            if (game.IsSteamGame || game.ExecutablePath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
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
                        if (IsIconic(hWnd))
                        {
                            ShowWindowAsync(hWnd, SW_RESTORE);
                        }
                        else
                        {
                            ShowWindowAsync(hWnd, SW_SHOW);
                        }
                        SetForegroundWindow(hWnd);
                    }

                    game.LastPlayed = DateTime.Now;
                    GameUpdated?.Invoke(game);
                    LoggingService.Info("Launcher", $"'{game.Name}' already running (PID {activeProc.Id}). Activated existing window.");
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
                _performanceProfileService.OnGameProcessStarted(game, process);
                _scriptService.TrackPostExit(game);
                LoggingService.Info("Launcher", $"Started '{game.Name}' (PID {process.Id}).");
                DateTime startTime = DateTime.Now;
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
                    // Subscribe before enabling, not after: EnableRaisingEvents can fire Exited
                    // on a thread-pool thread almost immediately for a process that already exited,
                    // and doing it the other way around left a real window where that fire found
                    // no subscriber yet and playtime for the session was silently never recorded.
                    process.Exited += OnExited;
                    process.EnableRaisingEvents = true;
                    exitHandlerAttached = true;

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
}
