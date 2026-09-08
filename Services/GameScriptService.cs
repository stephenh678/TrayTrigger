using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Runs a game's optional user-supplied pre-launch and post-exit scripts. Sits alongside
/// <see cref="PerformanceProfileService"/> (the built-in, snapshot-and-restore tweaks) and
/// covers everything that service can't know about: killing a background app, switching an
/// audio device, toggling RGB, remapping a controller, and so on.
///
/// Supported script types: .bat/.cmd (via cmd.exe), .ps1 (via powershell.exe with
/// -ExecutionPolicy Bypass), .exe/.com (run directly). Anything else is handed to the shell.
///
/// Each script receives three positional arguments - phase ("prelaunch" or "postexit"), game
/// name, game executable path - and, when not elevated, the same data as TRAYTRIGGER_* environment
/// variables (see <see cref="BuildStartInfo"/>). Elevated launches go through ShellExecute, which
/// cannot carry a custom environment, so those scripts should read the arguments instead.
/// </summary>
public class GameScriptService
{
    public const string PhasePreLaunch = "prelaunch";
    public const string PhasePostExit = "postexit";

    /// <summary>Upper bound on how long a "wait for it" pre-launch script can hold up the game launch.</summary>
    public static readonly TimeSpan PreLaunchWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, GameEntry> _pendingPostExit = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs the game's pre-launch script, if configured. Never throws and never blocks the launch
    /// on failure - a broken script is logged and the game starts anyway. If the game asks to wait,
    /// this returns once the script exits or <see cref="PreLaunchWaitTimeout"/> elapses.
    /// </summary>
    public void RunPreLaunch(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.PreLaunchScriptPath)) return;

        var psi = BuildStartInfo(game.PreLaunchScriptPath, game, PhasePreLaunch, game.RunScriptsHidden, game.RunScriptsAsAdmin, playedMinutes: null);
        if (psi == null)
        {
            LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' not found: {game.PreLaunchScriptPath}");
            return;
        }

        try
        {
            LoggingService.Info("GameScript", $"Running pre-launch script for '{game.Name}': {game.PreLaunchScriptPath} (wait={game.WaitForPreLaunchScript}, hidden={game.RunScriptsHidden}, admin={game.RunScriptsAsAdmin})");
            using var process = Process.Start(psi);
            if (process == null)
            {
                LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' did not start.");
                return;
            }

            if (!game.WaitForPreLaunchScript) return;

            if (process.WaitForExit((int)PreLaunchWaitTimeout.TotalMilliseconds))
            {
                if (process.ExitCode != 0)
                {
                    LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' exited with code {process.ExitCode}; launching anyway.");
                }
                else
                {
                    LoggingService.Verbose("GameScript", $"Pre-launch script for '{game.Name}' completed.");
                }
            }
            else
            {
                LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' is still running after {PreLaunchWaitTimeout.TotalSeconds:0}s; launching the game without waiting further.");
            }
        }
        catch (Exception ex)
        {
            // Includes the user cancelling a UAC prompt (Win32Exception 1223) for elevated scripts.
            LoggingService.Warn("GameScript", $"Pre-launch script for '{game.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers that this game's session is being tracked, so its post-exit script can still run
    /// if TrayTrigger shuts down before the game does. Call only when a real exit signal exists
    /// (direct .exe launch or Steam session polling), never for fire-and-forget protocol launches.
    /// </summary>
    public void TrackPostExit(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.PostExitScriptPath)) return;
        lock (_lock)
        {
            _pendingPostExit[game.Id] = game;
        }
    }

    /// <summary>Runs the game's post-exit script, if configured. Fire-and-forget; never throws.</summary>
    public void RunPostExit(GameEntry game, long playedMinutes)
    {
        lock (_lock)
        {
            _pendingPostExit.Remove(game.Id);
        }

        if (string.IsNullOrWhiteSpace(game.PostExitScriptPath)) return;

        var psi = BuildStartInfo(game.PostExitScriptPath, game, PhasePostExit, game.RunScriptsHidden, game.RunScriptsAsAdmin, playedMinutes);
        if (psi == null)
        {
            LoggingService.Warn("GameScript", $"Post-exit script for '{game.Name}' not found: {game.PostExitScriptPath}");
            return;
        }

        try
        {
            LoggingService.Info("GameScript", $"Running post-exit script for '{game.Name}': {game.PostExitScriptPath}");
            using var process = Process.Start(psi);
            if (process == null)
            {
                LoggingService.Warn("GameScript", $"Post-exit script for '{game.Name}' did not start.");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameScript", $"Post-exit script for '{game.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// TrayTrigger is exiting while games with post-exit scripts may still be running. Run those
    /// scripts now rather than never, mirroring how Performance Profiles restore on shutdown.
    /// </summary>
    public void RunPendingPostExitScriptsOnShutdown()
    {
        List<GameEntry> pending;
        lock (_lock)
        {
            pending = new List<GameEntry>(_pendingPostExit.Values);
            _pendingPostExit.Clear();
        }

        foreach (var game in pending)
        {
            LoggingService.Info("GameScript", $"Running post-exit script for '{game.Name}' on application exit.");
            RunPostExit(game, playedMinutes: 0);
        }
    }

    /// <summary>
    /// Builds the process start info for a script. Returns null if the script file doesn't exist.
    /// Pure and side-effect free so it can be unit tested.
    /// </summary>
    internal static ProcessStartInfo? BuildStartInfo(
        string scriptPath,
        GameEntry game,
        string phase,
        bool hidden,
        bool elevated,
        long? playedMinutes)
    {
        string path = scriptPath.Trim().Trim('"');
        if (!File.Exists(path)) return null;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        string workDir = Path.GetDirectoryName(path) ?? string.Empty;

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            // ShellExecute is required for the "runas" verb; otherwise CreateProcess gives us a
            // real environment block and a reliable CreateNoWindow.
            UseShellExecute = elevated,
            CreateNoWindow = hidden && !elevated,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        if (elevated)
        {
            psi.Verb = "runas";
        }

        switch (ext)
        {
            case ".bat":
            case ".cmd":
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(path);
                break;

            case ".ps1":
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                if (hidden)
                {
                    psi.ArgumentList.Add("-WindowStyle");
                    psi.ArgumentList.Add("Hidden");
                }
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(path);
                break;

            case ".exe":
            case ".com":
                psi.FileName = path;
                break;

            default:
                // Unknown type (e.g. a .py or .ahk with a registered handler): let the shell decide.
                psi.FileName = path;
                psi.UseShellExecute = true;
                psi.CreateNoWindow = false;
                break;
        }

        // Positional arguments work for every script type and every privilege level.
        psi.ArgumentList.Add(phase);
        psi.ArgumentList.Add(game.Name);
        psi.ArgumentList.Add(game.ExecutablePath);

        if (!psi.UseShellExecute)
        {
            psi.Environment["TRAYTRIGGER_PHASE"] = phase;
            psi.Environment["TRAYTRIGGER_GAME_NAME"] = game.Name;
            psi.Environment["TRAYTRIGGER_GAME_ID"] = game.Id;
            psi.Environment["TRAYTRIGGER_GAME_EXE"] = game.ExecutablePath;
            if (playedMinutes.HasValue)
            {
                psi.Environment["TRAYTRIGGER_PLAYTIME_MINUTES"] = playedMinutes.Value.ToString();
            }
        }

        return psi;
    }
}
