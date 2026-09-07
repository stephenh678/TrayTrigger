using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
    public event Action<GameEntry>? GameUpdated;

    public ProcessLauncherService(StorageService storageService)
    {
        _storageService = storageService;
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

                LoggingService.Verbose("Launcher", $"Launching Steam URL: {launchUrl}");
                Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true });

                game.LastPlayed = DateTime.Now;
                GameUpdated?.Invoke(game);
                LoggingService.Info("Launcher", $"Dispatched Steam launch for '{game.Name}'.");
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

            LoggingService.Verbose("Launcher", $"Spawning process: '{game.ExecutablePath}', Args='{game.Arguments}', WorkDir='{workingDir}', RunAsAdmin={game.RunAsAdmin}");
            var process = Process.Start(startInfo);
            game.LastPlayed = DateTime.Now;
            GameUpdated?.Invoke(game);

            if (process != null)
            {
                LoggingService.Info("Launcher", $"Started '{game.Name}' (PID {process.Id}).");
                DateTime startTime = DateTime.Now;
                bool exitHandlerAttached = false;
                int exitHandled = 0; // guards against running the handler twice (see below)

                void OnExited(object? s, EventArgs e)
                {
                    if (Interlocked.Exchange(ref exitHandled, 1) != 0) return;
                    try
                    {
                        TimeSpan playedDuration = DateTime.Now - startTime;
                        long minutes = (long)Math.Max(0, Math.Round(playedDuration.TotalMinutes));
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
            return false;
        }
    }
}
