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

/// <summary>Launching a Battle.net game: the program id, the client, and the dispatch to it.</summary>
public partial class ProcessLauncherService
{
    /// <summary>
    /// Battle.net game: the client must start it, because it hands the game a sign-in token
    /// (<c>-launcherlogin</c>, <c>-sso=1</c>) nothing else can. The launch code is the one saved on
    /// the entry; with none saved, Battle.net's catalog (then TrayTrigger's saved code map) is
    /// asked, starting the client if needed so it rebuilds its catalog. With no code at all, the
    /// client's Play tab is opened instead and tracking still runs, so the profile applies if the
    /// user clicks Play.
    /// </summary>
    private bool LaunchBattleNetGame(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        const string label = "Battle.net";

        string? clientPath = _battleNetScannerService.GetClientPath();
        if (clientPath == null)
        {
            errorMessage = $"\"{game.Name}\" launches through Battle.net, which isn't installed.";
            LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}': the Battle.net client wasn't found.");
            return false;
        }

        // Track the folder Battle.net records now, not the saved one: a moved install, or a Local
        // entry linked from a versioned subfolder (StarCraft II's Versions\BaseNNNNN), would
        // otherwise be watched where the game no longer runs.
        string installDir = ResolveTrackedInstallDir(game);
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            // Uninstalled in Battle.net (the card never shows a Battle.net game as missing, since
            // its exe is never run). Without this, the profile was applied, the launch command
            // sent, and the session rolled back a moment later with nothing shown to the user.
            errorMessage = $"\"{game.Name}\" isn't installed in Battle.net anymore - its install folder wasn't found. Reinstall it in Battle.net, then try again.";
            LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}': no install folder for Battle.net uid '{game.BattleNetUid}' (saved folder '{installDir}').");
            return false;
        }

        if (TryActivateRunningProcessUnderDirectory(game, installDir, label))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(game.Arguments))
        {
            LoggingService.Verbose("Launcher", $"'{game.Name}' has launch arguments, but Battle.net's launch command can't carry them - set them in Battle.net's own game settings.");
        }

        // The session is registered before any wait for a launch code, so a second click during
        // that wait hits LaunchGame's in-flight check instead of starting a second session.
        var session = BeginSession(game, LaunchRoute.BattleNet, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            string? programId = BattleNetCatalog.IsValidProgramId(game.BattleNetProgramId)
                ? game.BattleNetProgramId
                : FindBattleNetProgramIdForLaunch(game, clientPath, session);

            // The user ended the session while the launch code was being looked for: they no
            // longer want the game, so send nothing and don't start tracking.
            if (Volatile.Read(ref session.Finished) != 0)
            {
                LoggingService.Info("Launcher", $"'{game.Name}' session ended while waiting for its launch code; the launch was not sent.");
                return true;
            }

            if (programId == null)
            {
                SendBattleNetCommand(clientPath, "focus play");
                MarkLaunched(game);
                LoggingService.Warn("Launcher", $"No launch code for '{game.Name}' (uid {game.BattleNetUid}); opened Battle.net's Play tab instead. Tracking continues in case the user clicks Play.");
                LaunchNotice?.Invoke(game, $"Battle.net couldn't be told to start \"{game.Name}\". Click Play in Battle.net - TrayTrigger is still watching for the game.");
            }
            else
            {
                LoggingService.Verbose("Launcher", $"Launching '{game.Name}' via Battle.net (uid {game.BattleNetUid}, launch code {programId}); client {(BattleNetScannerService.IsClientRunning() ? "running" : "not running")}.");
                StartBattleNetDispatch(session, clientPath, programId, installDir);
                MarkLaunched(game);
                LoggingService.Info("Launcher", $"Dispatched Battle.net launch for '{game.Name}'.");
            }

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
    /// The launch code for an entry that has none saved: Battle.net's catalog, then the saved code
    /// map. When neither has it, starts the client (a cleared cache is rebuilt at startup) and asks
    /// again for up to <see cref="BattleNetCatalogWait"/>. Saves what it finds on the entry.
    /// </summary>
    private string? FindBattleNetProgramIdForLaunch(GameEntry game, string clientPath, ActiveGameSession session)
    {
        string uid = game.BattleNetUid ?? string.Empty;
        var lookup = _battleNetScannerService.LookUpProgramId(uid);
        if (!lookup.Found)
        {
            LoggingService.Info("Launcher", $"'{game.Name}' has no launch code ({lookup.Detail}); starting Battle.net and waiting up to {BattleNetCatalogWait.TotalSeconds:0}s for its catalog.");
            if (!BattleNetScannerService.IsClientRunning())
            {
                StartBattleNetClient(clientPath);
            }

            // Stops early when the user ends the session during the wait (Close Game, Force Close).
            var deadline = DateTime.UtcNow + BattleNetCatalogWait;
            while (!lookup.Found && DateTime.UtcNow < deadline && Volatile.Read(ref session.Finished) == 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
                lookup = _battleNetScannerService.LookUpProgramId(uid);
            }

            if (!lookup.Found)
            {
                if (Volatile.Read(ref session.Finished) != 0) return null;
                LoggingService.Warn("Launcher", $"Still no launch code for '{game.Name}' (uid {uid}) after waiting: {lookup.Detail}.");
                return null;
            }
        }

        SaveBattleNetProgramId(game, lookup.ProgramId!, lookup.Source);
        return lookup.ProgramId;
    }

    private void SaveBattleNetProgramId(GameEntry game, string programId, string source)
    {
        if (string.Equals(game.BattleNetProgramId, programId, StringComparison.Ordinal)) return;

        LoggingService.Info("Launcher", string.IsNullOrEmpty(game.BattleNetProgramId)
            ? $"'{game.Name}' launch code set to {programId} (from the {source})."
            : $"'{game.Name}' launch code changed from {game.BattleNetProgramId} to {programId} (from the {source}).");
        game.BattleNetProgramId = programId;
        GameUpdated?.Invoke(game);
    }

    /// <summary>
    /// Sends the launch command now, then every <see cref="BattleNetResendInterval"/> until a
    /// non-helper process appears under the install folder, the session ends, or
    /// <see cref="BattleNetResendWindow"/> passes. At the end of the window the catalog is asked
    /// once more: a different code is saved and gets a fresh window; the same code means the
    /// problem is elsewhere (an update, a sign-in), so the Play tab is opened and the user told.
    /// </summary>
    private void StartBattleNetDispatch(ActiveGameSession session, string clientPath, string programId, string installDir)
    {
        var game = session.Game;
        string normalizedInstallDir = ProcessPathResolver.NormalizeDirectory(installDir);
        string currentCode = programId;
        int sends = 1;
        bool codeRechecked = false;
        DateTime windowStartedUtc = DateTime.UtcNow;

        SendBattleNetCommand(clientPath, $"launch {currentCode}");

        string key = session.GameId;
        Poller? poller = null;
        poller = new Poller(BattleNetResendInterval, () =>
        {
            bool again = Tick();
            if (!again && poller != null)
            {
                _battleNetDispatchers.TryRemove(new KeyValuePair<string, object>(key, poller));
            }
            return again;
        });
        _battleNetDispatchers[key] = poller;

        bool Tick()
        {
            if (Volatile.Read(ref session.Finished) != 0 || session.GameStarted) return false;

            if (ProcessPathResolver.FindProcessesUnderDirectory(normalizedInstallDir).Count > 0)
            {
                LoggingService.Verbose("Launcher", $"'{game.Name}' appeared under its install folder after {sends} Battle.net launch command(s).");
                return false;
            }

            if (DateTime.UtcNow - windowStartedUtc < BattleNetResendWindow)
            {
                sends++;
                LoggingService.Verbose("Launcher", $"'{game.Name}' hasn't started; re-sending Battle.net launch command #{sends} (a client still signing in drops it).");
                SendBattleNetCommand(clientPath, $"launch {currentCode}");
                return true;
            }

            if (!codeRechecked)
            {
                codeRechecked = true;
                var lookup = _battleNetScannerService.LookUpProgramId(game.BattleNetUid ?? string.Empty);
                if (lookup.Found && !string.Equals(lookup.ProgramId, currentCode, StringComparison.Ordinal))
                {
                    SaveBattleNetProgramId(game, lookup.ProgramId!, lookup.Source);
                    currentCode = lookup.ProgramId!;
                    windowStartedUtc = DateTime.UtcNow;
                    sends++;
                    LoggingService.Info("Launcher", $"'{game.Name}' didn't start with its saved launch code; retrying with {currentCode}.");
                    SendBattleNetCommand(clientPath, $"launch {currentCode}");
                    return true;
                }
            }

            LoggingService.Warn("Launcher", $"'{game.Name}' didn't start after {sends} Battle.net launch command(s) (launch code {currentCode}). Opening Battle.net's Play tab; tracking continues.");
            SendBattleNetCommand(clientPath, "focus play");
            LaunchNotice?.Invoke(game, $"\"{game.Name}\" hasn't started yet. Battle.net may need you - check for a sign-in or an update, then click Play. TrayTrigger is still watching for the game.");
            return false;
        }
    }

    /// <summary>
    /// Runs <c>Battle.net.exe --exec="&lt;command&gt;"</c> - exactly the form that was tested. Only
    /// "focus play" and "launch &lt;program id&gt;" are ever passed, and program IDs are validated
    /// (<see cref="BattleNetCatalog.IsValidProgramId"/>), so nothing else reaches the command line.
    /// A running client receives the command; a closed one starts and signs in.
    /// </summary>
    private static void SendBattleNetCommand(string clientPath, string command)
    {
        LoggingService.Verbose("Launcher", $"Battle.net command: --exec=\"{command}\"");
        Process.Start(new ProcessStartInfo
        {
            FileName = clientPath,
            Arguments = $"--exec=\"{command}\"",
            WorkingDirectory = Path.GetDirectoryName(clientPath) ?? string.Empty,
            UseShellExecute = true
        })?.Dispose();
    }

    private static void StartBattleNetClient(string clientPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = clientPath,
            WorkingDirectory = Path.GetDirectoryName(clientPath) ?? string.Empty,
            UseShellExecute = true
        })?.Dispose();
    }
}
