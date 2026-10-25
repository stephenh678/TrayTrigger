using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>Games and folders the player has told TrayTrigger to leave out, and the platform matching that runs beside them.</summary>
public partial class ImportCoordinator
{
    /// <summary>Drops any candidate whose exe path the user has permanently ignored (see <see cref="IgnoreGamePath"/>).</summary>
    private IEnumerable<GameCandidate> FilterIgnored(IEnumerable<GameCandidate> candidates)
        => FilterIgnored(candidates, BuildIgnoredExePathSet(), BuildIgnoredPlatformKeySet());

    /// <summary>
    /// Same as the single-arg overload, but takes pre-built sets - for a caller that filters
    /// multiple lists (or scan locations) in one pass. A candidate that resolved to a platform
    /// (see <see cref="ResolvePlatforms"/>) is judged by its platform ID, not its exe path: the
    /// user ignoring a misdetected exe inside a Steam install must not block importing that
    /// game through Steam's own record, and ignoring a Steam AppId in the scan picker must also
    /// apply when the same game arrives via Add Folder.
    /// </summary>
    private IEnumerable<GameCandidate> FilterIgnored(IEnumerable<GameCandidate> candidates, HashSet<string> ignoredExePaths, HashSet<string> ignoredPlatformKeys)
        => candidates.Where(c =>
        {
            bool keep = c.Platform != null
                ? !ignoredPlatformKeys.Contains(PlatformKey(c.Platform))
                // The scanner already prunes ignored folders; this catches a candidate that reached
                // here another way (a folder dropped or added that is itself under an ignored one).
                : !ignoredExePaths.Contains(c.ExePath) && !IsUnderIgnoredFolder(c.ExePath);
            if (!keep) LoggingService.Verbose("ScanForGames", $"Folders: '{c.Name}' ({c.ExePath}): on the ignore list");
            return keep;
        });

    private bool IsUnderIgnoredFolder(string exePath)
    {
        string? dir = Path.GetDirectoryName(exePath);
        return !string.IsNullOrEmpty(dir) && _folderScannerService.IsIgnoredFolder(dir);
    }

    private HashSet<string> BuildIgnoredExePathSet()
        => new(
            _settings.IgnoredGamePaths.Where(p => p.ExePath != null).Select(p => p.ExePath!),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>"Steam|440", "GOG|1207658924", ... for every platform-ID ignore.</summary>
    private HashSet<string> BuildIgnoredPlatformKeySet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _settings.IgnoredGamePaths)
        {
            if (p.SteamAppId != null) set.Add($"Steam|{p.SteamAppId}");
            if (p.GogGameId != null) set.Add($"GOG|{p.GogGameId}");
            if (p.EaContentId != null) set.Add($"EA|{p.EaContentId}");
            if (p.EpicAppName != null) set.Add($"Epic|{p.EpicAppName}");
            if (p.UbisoftGameId != null) set.Add($"Ubisoft|{p.UbisoftGameId}");
            if (p.XboxAumid != null) set.Add($"Xbox|{p.XboxAumid}");
            if (p.BattleNetUid != null) set.Add($"Battle.net|{p.BattleNetUid}");
        }
        return set;
    }

    /// <summary>"Platform|ID" - the identity used for ignore and dedupe checks on a resolved match.</summary>
    private static string PlatformKey(PlatformMatch match) => match switch
    {
        { Steam: { } s } => $"Steam|{s.AppId}",
        { Gog: { } g } => $"GOG|{g.GameId}",
        { Ea: { } e } => $"EA|{e.ContentId}",
        { Epic: { } p } => $"Epic|{p.AppName}",
        { Ubisoft: { } u } => $"Ubisoft|{u.GameId}",
        { Xbox: { } x } => $"Xbox|{x.Aumid}",
        { BattleNet: { } b } => $"Battle.net|{b.Uid}",
        _ => string.Empty
    };

    /// <summary>
    /// Runs the platform lookup over folder-scan candidates up front, so everything downstream
    /// (ignore checks, the batch dialog's names/badges/"in library" state, the final import)
    /// sees the same answer. A matched candidate takes the platform's own title.
    /// </summary>
    private static List<GameCandidate> ResolvePlatforms(IEnumerable<GameCandidate> candidates, PlatformLookupService.Index index)
    {
        var resolved = new List<GameCandidate>();
        foreach (var c in candidates)
        {
            if (c.Platform != null) { resolved.Add(c); continue; }
            var match = index.Match(c.ExePath);
            resolved.Add(match == null ? c : c with { Platform = match, Name = match.Name });
        }
        return resolved;
    }

    /// <summary>Resolves platforms for both halves of a folder scan result and drops ignored
    /// candidates - the shared post-processing for every Add Folder / folder-drop route.</summary>
    private FolderScanResult ResolveAndFilterIgnored(FolderScanResult scan, PlatformLookupService.Index index)
    {
        var ignoredExe = BuildIgnoredExePathSet();
        var ignoredKeys = BuildIgnoredPlatformKeySet();
        scan.DiscoveredGames = FilterIgnored(ResolvePlatforms(scan.DiscoveredGames, index), ignoredExe, ignoredKeys).ToList();
        scan.SingleGameCandidates = FilterIgnored(ResolvePlatforms(scan.SingleGameCandidates, index), ignoredExe, ignoredKeys).ToList();
        return scan;
    }

    /// <summary>Ignores a candidate by whatever identity it has: platform ID when it resolved to a
    /// launcher game, exe path otherwise - so the batch dialog's "Ignore" matches the scan picker's.</summary>
    public void IgnoreCandidate(GameCandidate candidate)
    {
        switch (candidate.Platform)
        {
            case { Steam: { } s }: IgnoreSteamGame(s.AppId, candidate.Name); break;
            case { Gog: { } g }: IgnoreGogGame(g.GameId, candidate.Name); break;
            case { Ea: { } e }: IgnoreEaGame(e.ContentId, candidate.Name); break;
            case { Epic: { } p }: IgnoreEpicGame(p.AppName, candidate.Name); break;
            case { Ubisoft: { } u }: IgnoreUbisoftGame(u.GameId, candidate.Name); break;
            case { Xbox: { } x }: IgnoreXboxGame(x.Aumid, candidate.Name); break;
            case { BattleNet: { } b }: IgnoreBattleNetGame(b.Uid, candidate.Name); break;
            default: IgnoreGamePath(candidate.ExePath, candidate.Name); break;
        }
    }

    /// <summary>
    /// An explicitly dropped/browsed exe is not a "suggestion" the ignore list can veto - the
    /// user just told us they want it. Any ignore entry that would have hidden it (its exe path,
    /// or the platform ID it resolved to) is removed, so a later scan doesn't drop it either.
    /// </summary>
    private void UnignoreForExplicitDrop(string exePath, PlatformMatch? match)
    {
        string? key = match != null ? PlatformKey(match) : null;
        int removed = _settings.IgnoredGamePaths.RemoveAll(p =>
            (p.ExePath != null && string.Equals(p.ExePath, exePath, StringComparison.OrdinalIgnoreCase)) ||
            (key != null && (
                (p.SteamAppId != null && key.Equals($"Steam|{p.SteamAppId}", StringComparison.OrdinalIgnoreCase)) ||
                (p.GogGameId != null && key.Equals($"GOG|{p.GogGameId}", StringComparison.OrdinalIgnoreCase)) ||
                (p.EaContentId != null && key.Equals($"EA|{p.EaContentId}", StringComparison.OrdinalIgnoreCase)) ||
                (p.EpicAppName != null && key.Equals($"Epic|{p.EpicAppName}", StringComparison.OrdinalIgnoreCase)) ||
                (p.UbisoftGameId != null && key.Equals($"Ubisoft|{p.UbisoftGameId}", StringComparison.OrdinalIgnoreCase)) ||
                (p.XboxAumid != null && key.Equals($"Xbox|{p.XboxAumid}", StringComparison.OrdinalIgnoreCase)) ||
                (p.BattleNetUid != null && key.Equals($"Battle.net|{p.BattleNetUid}", StringComparison.OrdinalIgnoreCase)))));
        if (removed > 0)
        {
            _storageService.SaveSettings(_settings);
            LoggingService.Info("ImportCoordinator", $"Removed {removed} ignore entr{(removed == 1 ? "y" : "ies")} for '{exePath}' because it was dropped explicitly.");
        }
    }

    /// <summary>
    /// Permanently excludes an exe from future "Add Folder" and "Scan for Games" results - for a
    /// bundled non-game tool the folder scanner's heuristics keep mistaking for a game (e.g. an
    /// installer or utility living alongside real games in a scan location).
    /// </summary>
    public void IgnoreGamePath(string exePath, string name)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.ExePath, exePath, StringComparison.OrdinalIgnoreCase))) return;

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { ExePath = exePath, Name = name });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored scan candidate '{name}' ('{exePath}') - will be excluded from future scans.");
    }

    /// <summary>Permanently excludes a Steam AppId from future "Scan for Games" results - e.g. a tool/demo/soundtrack Steam also lists as an "app" that the user never wants suggested as a game.</summary>
    public void IgnoreSteamGame(string appId, string name) =>
        IgnoreLauncherGame("Steam", "AppId ", appId, name, p => p.SteamAppId, (p, v) => p.SteamAppId = v);

    /// <summary>Permanently excludes a GOG game ID from future "Scan for Games" results.</summary>
    public void IgnoreGogGame(string gameId, string name) =>
        IgnoreLauncherGame("GOG", "GameId ", gameId, name, p => p.GogGameId, (p, v) => p.GogGameId = v);

    /// <summary>Permanently excludes an EA content ID from future "Scan for Games" results.</summary>
    public void IgnoreEaGame(string contentId, string name) =>
        IgnoreLauncherGame("EA", "ContentId ", contentId, name, p => p.EaContentId, (p, v) => p.EaContentId = v);

    /// <summary>Permanently excludes an Epic AppName from future "Scan for Games" results.</summary>
    public void IgnoreEpicGame(string appName, string name) =>
        IgnoreLauncherGame("Epic", "AppName ", appName, name, p => p.EpicAppName, (p, v) => p.EpicAppName = v);

    /// <summary>Permanently excludes a Ubisoft GameId from future "Scan for Games" results.</summary>
    public void IgnoreUbisoftGame(string gameId, string name) =>
        IgnoreLauncherGame("Ubisoft", "GameId ", gameId, name, p => p.UbisoftGameId, (p, v) => p.UbisoftGameId = v);

    /// <summary>Permanently excludes an Xbox AUMID from future "Scan for Games" results.</summary>
    public void IgnoreXboxGame(string aumid, string name) =>
        IgnoreLauncherGame("Xbox", "", aumid, name, p => p.XboxAumid, (p, v) => p.XboxAumid = v);

    /// <summary>Permanently excludes a Battle.net install uid from future "Scan for Games" results.</summary>
    public void IgnoreBattleNetGame(string uid, string name) =>
        IgnoreLauncherGame("Battle.net", "uid ", uid, name, p => p.BattleNetUid, (p, v) => p.BattleNetUid = v);

    /// <summary>
    /// Leaves one launcher game out of future scans, once. Each launcher says only which id on the ignore entry
    /// it uses and how the log names it.
    /// </summary>
    private void IgnoreLauncherGame(string launcher, string idLabel, string id, string name,
        Func<IgnoredGamePath, string?> idOf, Action<IgnoredGamePath, string> setId)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(idOf(p), id, StringComparison.OrdinalIgnoreCase))) return;

        var entry = new IgnoredGamePath { Name = name };
        setId(entry, id);
        _settings.IgnoredGamePaths.Add(entry);
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored {launcher} scan candidate '{name}' ({idLabel}{id}) - will be excluded from future scans.");
    }

    public void RemoveIgnoredGamePath(string id)
    {
        var removed = _settings.IgnoredGamePaths.FirstOrDefault(p => p.Id == id);
        _settings.IgnoredGamePaths.RemoveAll(p => p.Id == id);
        _storageService.SaveSettings(_settings);
        if (removed != null)
        {
            LoggingService.Info("ImportCoordinator", $"Un-ignored '{removed.Name}' - will be considered again in future scans.");
        }
    }
}
