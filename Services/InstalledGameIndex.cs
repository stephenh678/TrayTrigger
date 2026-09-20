using System;
using System.Collections.Generic;
using System.IO;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Whether a library entry's game is still there to play, as far as this PC can tell.</summary>
public enum GameAvailability
{
    /// <summary>There, or nothing said otherwise. The ordinary state, and the only one a card shows plainly.</summary>
    Available,
    /// <summary>A launcher game its own launcher no longer lists. Undone by reinstalling it there.</summary>
    NotInstalled,
    /// <summary>
    /// A game added by hand, by drop or by folder scan whose executable is not on disk. Unlike
    /// <see cref="NotInstalled"/> this equally means "moved", which no launcher can answer for -
    /// hence "Locate Executable..." rather than a reinstall.
    /// </summary>
    ExecutableMissing
}

/// <summary>
/// One snapshot of what each launcher currently has installed, taken once and then asked about
/// every entry in the library. The launchers are the only ones who can answer for a Steam, Xbox or
/// Battle.net game: those entries' ExecutablePath is a steam:// URL, a package folder re-resolved
/// on every launch, or an exe that is never run at all, so File.Exists says nothing about them.
///
/// <para>The rule throughout is that a game is only called <see cref="GameAvailability.NotInstalled"/>
/// when its launcher was read in full and does not list it. A launcher that isn't installed, a
/// registry key that won't open, a Steam library on a drive that is asleep - each leaves that
/// launcher's games exactly as they were. Greying out half a library because a drive is unplugged
/// is a worse failure than saying nothing, and it is the one a user would report as a bug.</para>
/// </summary>
public sealed class InstalledGameIndex
{
    /// <summary>An index that read nothing, so it calls nothing uninstalled. The starting value.</summary>
    public static InstalledGameIndex Unknown { get; } = new();

    /// <summary>
    /// The most recent snapshot, shared by every card. One card refreshed on its own (a rename, a
    /// poster arriving from enrichment) reuses it instead of re-reading three launchers on the UI
    /// thread; <see cref="ViewModels.LibraryViewModel"/> replaces it on each full pass - at startup
    /// and after a scan.
    /// </summary>
    public static InstalledGameIndex Current { get; set; } = Unknown;

    /// <summary>
    /// The scanners a capture asks. Shared, because a capture runs at startup, after every import
    /// batch and after every scan, and BattleNetScannerService builds a file-backed code store
    /// (and a folder scanner) with each instance. All three are read-only here and safe to share.
    /// </summary>
    private static readonly SteamScannerService SteamScanner = new();
    private static readonly XboxScannerService XboxScanner = new();
    private static readonly BattleNetScannerService BattleNetScanner = new();

    private readonly HashSet<string> _steamAppIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _xboxAumids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _battleNetUids = new(StringComparer.OrdinalIgnoreCase);

    private bool _steamRead;
    private bool _xboxRead;
    private bool _battleNetRead;

    private InstalledGameIndex() { }

    /// <summary>
    /// Reads each launcher's own record of what is installed. Registry and manifest reads only -
    /// no exe or icon resolution - but still disk and registry work, so it belongs on a background
    /// thread. Never throws.
    /// </summary>
    public static InstalledGameIndex Capture()
    {
        var index = new InstalledGameIndex();
        index.CaptureSteam(SteamScanner);
        index.CaptureXbox(XboxScanner);
        index.CaptureBattleNet(BattleNetScanner);
        return index;
    }

    /// <summary>
    /// Asks the owning launcher about one game right now, instead of from a snapshot. A snapshot
    /// is only replaced at startup and after a scan, so by the time the user presses Play on a
    /// marked card it can be days old and the game reinstalled since - and a card that is only
    /// dimmed must never be a card that cannot be launched. Each branch uses the same lookup the
    /// launch itself makes, so this and the launch can never disagree.
    /// </summary>
    public static GameAvailability Recheck(GameEntry game)
    {
        var index = new InstalledGameIndex();

        if (game.IsSteamGame)
        {
            if (!UrlProtocolHelper.IsValidSteamAppId(game.SteamAppId)) return GameAvailability.Available;
            // A manifest read, not the folder: the same evidence CaptureSteam uses.
            var appIds = SteamScanner.TryListInstalledAppIds();
            if (appIds == null) return GameAvailability.Available;
            index._steamAppIds.UnionWith(appIds);
            index._steamRead = true;
        }
        else if (game.IsXboxGame)
        {
            if (string.IsNullOrWhiteSpace(game.XboxAumid)) return GameAvailability.Available;
            if (XboxScanner.FindByAumid(game.XboxAumid) == null) return GameAvailability.NotInstalled;
            return GameAvailability.Available;
        }
        else if (game.IsBattleNetGame)
        {
            if (string.IsNullOrWhiteSpace(game.BattleNetUid)) return GameAvailability.Available;
            if (!BattleNetScanner.IsClientInstalled()) return GameAvailability.Available;
            return BattleNetScanner.FindInstallDir(game.BattleNetUid) == null
                ? GameAvailability.NotInstalled
                : GameAvailability.Available;
        }

        // Steam falls through to the shared answer with its set filled in; every exe-backed
        // launcher and every local game is re-checked by AvailabilityOf on its own.
        return index.AvailabilityOf(game);
    }

    /// <summary>
    /// Test seam: an index that answers for exactly these IDs, as though every launcher had been
    /// read in full. Pass null for a launcher that could not be read.
    /// </summary>
    internal static InstalledGameIndex ForTest(
        IEnumerable<string>? steamAppIds = null,
        IEnumerable<string>? xboxAumids = null,
        IEnumerable<string>? battleNetUids = null)
    {
        var index = new InstalledGameIndex();
        if (steamAppIds != null) { index._steamAppIds.UnionWith(steamAppIds); index._steamRead = true; }
        if (xboxAumids != null) { index._xboxAumids.UnionWith(xboxAumids); index._xboxRead = true; }
        if (battleNetUids != null) { index._battleNetUids.UnionWith(battleNetUids); index._battleNetRead = true; }
        return index;
    }

    // ------------------------------------------------------------------ the question

    /// <summary>What this index can say about one library entry.</summary>
    public GameAvailability AvailabilityOf(GameEntry game)
    {
        // IsSteamGame, not HasSteamOverlay: a local exe wearing a forced Steam badge for its
        // overlay is still a local game, and Steam has never heard of it.
        if (game.IsSteamGame)
        {
            return _steamRead
                && UrlProtocolHelper.IsValidSteamAppId(game.SteamAppId)
                && !_steamAppIds.Contains(game.SteamAppId!)
                ? GameAvailability.NotInstalled
                : GameAvailability.Available;
        }

        if (game.IsXboxGame)
        {
            return _xboxRead
                && !string.IsNullOrWhiteSpace(game.XboxAumid)
                && !_xboxAumids.Contains(game.XboxAumid!)
                ? GameAvailability.NotInstalled
                : GameAvailability.Available;
        }

        if (game.IsBattleNetGame)
        {
            return _battleNetRead
                && !string.IsNullOrWhiteSpace(game.BattleNetUid)
                && !_battleNetUids.Contains(game.BattleNetUid!)
                ? GameAvailability.NotInstalled
                : GameAvailability.Available;
        }

        if (!IsExecutableOffDisk(game)) return GameAvailability.Available;

        // GOG, EA, Epic and Ubisoft install into a folder they own and the entry points at a real
        // exe inside it, so the exe going means the launcher removed the game - "not installed",
        // with the launcher to reinstall from, rather than the "locate it" a moved local game gets.
        // Unless the drive itself is away, in which case nothing was uninstalled.
        bool fromLauncher = game.IsGogGame || game.IsEaGame || game.IsEpicGame || game.IsUbisoftGame;
        if (fromLauncher)
        {
            return DriveIsAttached(game.ExecutablePath) ? GameAvailability.NotInstalled : GameAvailability.Available;
        }

        return GameAvailability.ExecutableMissing;
    }

    /// <summary>
    /// The check the library has always made: the entry names a real file and that file is gone.
    /// A launcher protocol shortcut ("com.epicgames.launcher://...") is not a path, so File.Exists
    /// would call every one of them missing.
    /// </summary>
    private static bool IsExecutableOffDisk(GameEntry game)
        => !string.IsNullOrWhiteSpace(game.ExecutablePath)
           && !ProcessLauncherService.IsNonFileProtocolUrl(game.ExecutablePath)
           && !File.Exists(game.ExecutablePath);

    /// <summary>
    /// Whether the drive a path sits on is present at all. False also when the question can't be
    /// answered, because every caller uses it to decide whether to keep quiet.
    /// </summary>
    private static bool DriveIsAttached(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("InstallCheck", ex, $"checking whether the drive of '{path}' is attached");
            return false;
        }
    }

    // ------------------------------------------------------------------ capture, per launcher

    /// <summary>
    /// Steam's installed set is its app manifests, read by the scanner that owns that knowledge.
    /// Null back from it means a library folder could not be read, and its games must be left
    /// exactly as they are.
    /// </summary>
    private void CaptureSteam(SteamScannerService steam)
    {
        var appIds = steam.TryListInstalledAppIds();
        if (appIds == null)
        {
            LoggingService.Verbose("InstallCheck", "Steam could not be read in full, so no Steam entry is judged installed or not.");
            return;
        }

        _steamAppIds.UnionWith(appIds);
        _steamRead = true;
        LoggingService.Verbose("InstallCheck", $"Steam: {_steamAppIds.Count} game(s) installed.");
    }

    /// <summary>
    /// Xbox games come from Windows' own Gaming Services registry, which is also what a launch
    /// re-resolves the package folder from - so a game missing here is exactly the one a launch
    /// would refuse with "no longer installed through the Xbox app".
    /// </summary>
    private void CaptureXbox(XboxScannerService xbox)
    {
        var aumids = xbox.TryListInstalledAumids();
        if (aumids == null)
        {
            LoggingService.Verbose("InstallCheck", "Windows' Gaming Services could not be read (the Xbox app is not installed here?), so no Xbox entry is judged installed or not.");
            return;
        }

        _xboxAumids.UnionWith(aumids);
        _xboxRead = true;
        LoggingService.Verbose("InstallCheck", $"Xbox: {_xboxAumids.Count} game(s) installed.");
    }

    /// <summary>
    /// Battle.net games come from Blizzard's uninstall entries, and the install folder has to be
    /// there too - the same pair a launch checks before it refuses with "isn't installed in
    /// Battle.net anymore".
    /// </summary>
    private void CaptureBattleNet(BattleNetScannerService battleNet)
    {
        if (!battleNet.IsClientInstalled())
        {
            LoggingService.Verbose("InstallCheck", "Battle.net is not installed on this PC, so no Battle.net entry is judged installed or not.");
            return;
        }

        try
        {
            foreach (var entry in BattleNetScannerService.ReadUninstallEntries())
            {
                if (Directory.Exists(entry.InstallDir)) _battleNetUids.Add(entry.Uid);
            }
            _battleNetRead = true;
            LoggingService.Verbose("InstallCheck", $"Battle.net: {_battleNetUids.Count} game(s) installed.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("InstallCheck", $"Could not read Battle.net's uninstall entries, so its games are left as they are: {ex.Message}");
        }
    }
}
