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

/// <summary>Launching through a store client: GOG Galaxy, the Xbox app, and the EA, Epic and Ubisoft launch URLs.</summary>
public partial class ProcessLauncherService
{
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

        bool keepMinimized = ShouldKeepLaunchersMinimized;
        var galaxyStartInfo = new ProcessStartInfo { FileName = galaxyClientPath, UseShellExecute = false };
        foreach (string argument in BuildGalaxyRunGameArguments(game.GogGameId ?? string.Empty, installDir, keepMinimized))
        {
            galaxyStartInfo.ArgumentList.Add(argument);
        }

        var session = BeginSession(game, LaunchRoute.GogGalaxy, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            LoggingService.Verbose("Launcher", $"Launching '{game.Name}' via GOG Galaxy (gameId {game.GogGameId}{(keepMinimized ? ", Galaxy started in the background" : string.Empty)}).");
            Process.Start(galaxyStartInfo)?.Dispose();
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
            Process.Start(new ProcessStartInfo(launchUrl) { UseShellExecute = true })?.Dispose();
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
    /// PC Game Pass / Store (GDK) title: shell-activates the package's AUMID (the only way a GDK
    /// exe can start - it refuses to run without package identity) and tracks by the game's
    /// resolved install directory. That is the junction <em>target</em> ("D:\XboxGames\&lt;Game&gt;\Content"),
    /// not the WindowsApps package root: the kernel names a process image by its final path, so
    /// <see cref="ProcessPathResolver.GetProcessPath"/> reports running GDK games under the
    /// target (Process.MainModule would say WindowsApps - verified both ways against a live
    /// Roblox). Both paths change on every game update, so the record is re-read from Gaming
    /// Services' registry on each launch rather than trusted from the entry; the entry's
    /// informational exe path is refreshed at the same time. Windows' gamelaunchhelper.exe stub
    /// is what activation starts first, so this relies on the same debounced polling every
    /// client-launch platform uses.
    /// </summary>
    private bool LaunchXboxGame(GameEntry game, out string? errorMessage)
    {
        errorMessage = null;
        const string label = "Xbox";

        var current = _xboxScannerService.FindByAumid(game.XboxAumid);
        if (current == null)
        {
            errorMessage = $"\"{game.Name}\" is no longer installed through the Xbox app.";
            LoggingService.Warn("Launcher", $"Cannot launch '{game.Name}': Xbox package '{game.XboxAumid}' is not registered for this user, or its install folder is missing.");
            return false;
        }

        // Self-heal the versioned paths (see the summary) so icon extraction, "open install
        // folder" and dedupe keep working after the Xbox app updates the game.
        if (current.ExePath != null && !string.Equals(game.ExecutablePath, current.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            LoggingService.Verbose("Launcher", $"'{game.Name}' exe path refreshed after a package update: '{game.ExecutablePath}' -> '{current.ExePath}'.");
            game.ExecutablePath = current.ExePath;
            game.WorkingDirectory = current.InstallDir;
            GameUpdated?.Invoke(game);
        }

        string installDir = current.InstallDir;
        if (TryActivateRunningProcessUnderDirectory(game, installDir, label))
        {
            return true;
        }

        var session = BeginSession(game, LaunchRoute.Xbox, out string? abortReason);
        if (session == null)
        {
            errorMessage = abortReason;
            return false;
        }

        try
        {
            LoggingService.Verbose("Launcher", $"Activating Xbox package: {current.Aumid}");
            uint pid = PackagedAppActivator.Activate(current.Aumid, game.Arguments);
            MarkLaunched(game);
            LoggingService.Info("Launcher", $"Dispatched {label} launch for '{game.Name}'{(pid != 0 ? $" (activation PID {pid})" : string.Empty)}.");

            TrackInstallDirSession(session, installDir, label, InstallDirLaunchTimeout);
            return true;
        }
        catch
        {
            RollbackSession(session);
            throw;
        }
    }

    // ==========================================================================================
    // Battle.net - "--exec=launch <program id>", re-sent until the game appears, tracked by
    // install directory. See docs/adding-a-platform-integration.md section 3.1 for the tests.
    // ==========================================================================================

    /// <summary>How often the launch command is re-sent until the game's process appears. A client
    /// still signing in drops the command instead of queueing it; a ready one starts the game on the
    /// first send. Extra sends were tested harmless - no second instance, even for StarCraft II,
    /// which allows several.</summary>
    private static readonly TimeSpan BattleNetResendInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long to keep re-sending. Past this, <see cref="TrackInstallDirSession"/>'s own
    /// window still catches the game if the user clicks Play (a sign-in or update that needs them).</summary>
    private static readonly TimeSpan BattleNetResendWindow = TimeSpan.FromSeconds(90);

    /// <summary>How long a launch waits for Battle.net's catalog when the game has no launch code at
    /// all. The client rebuilt a cleared cache about a second after starting, in testing.</summary>
    private static readonly TimeSpan BattleNetCatalogWait = TimeSpan.FromSeconds(20);

    // Holds each resend loop's poller until the loop ends: a Timer nobody references can be
    // collected mid-loop.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _battleNetDispatchers = new();

    /// <summary>
    /// A launch that was dispatched but needs the user - e.g. Battle.net couldn't be told which game
    /// to start. The message is written for a tray notification. Raised on a background thread.
    /// </summary>
    public event Action<GameEntry, string>? LaunchNotice;
}
