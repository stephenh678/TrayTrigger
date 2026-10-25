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

/// <summary>Scan locations and the launcher imports: Steam, GOG, EA, Epic, Ubisoft, Xbox and Battle.net.</summary>
public partial class ImportCoordinator
{
    public bool IsScanLocation(string path)
    {
        string normalized = path.TrimEnd('\\', '/');
        return _settings.ScanLocations.Any(l => string.Equals(l.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds a folder as a manual scan location if it isn't tracked already - used when the user
    /// checks "remember this folder" in the batch-import dialog after Add Folder turned out to be
    /// a multi-game library, so future "Scan for Games" runs pick up new installs there too.
    /// </summary>
    public void AddManualScanLocationIfNew(string path)
    {
        if (IsScanLocation(path)) return;

        _settings.ScanLocations.Add(new ScanLocation { Path = path.TrimEnd('\\', '/'), Source = ScanLocationSource.Manual, IsEnabled = true });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Added manual scan location: '{path}'.");
    }

    /// <summary>
    /// Imports both halves of a "Scan for Games" selection. Awaits the Steam import fully before
    /// starting the folder one rather than firing both at once - ImportSteamGamesAsync and
    /// ImportBatchGamesAsync share the same _isImportInProgress reentrancy guard, so kicking them
    /// off back-to-back (fire-and-forget) makes the second one see the first still in flight and
    /// silently no-op instead of importing anything.
    /// </summary>
    public async Task ImportScanResultsAsync(List<DiscoveredSteamGame> steamGames, List<DiscoveredGogGame> gogGames, List<DiscoveredEaGame> eaGames, List<DiscoveredEpicGame> epicGames, List<DiscoveredUbisoftGame> ubisoftGames, List<DiscoveredXboxGame> xboxGames, List<DiscoveredBattleNetGame> battleNetGames, List<GameCandidate> folderCandidates)
    {
        int total = steamGames.Count + gogGames.Count + eaGames.Count + epicGames.Count + ubisoftGames.Count + xboxGames.Count + battleNetGames.Count + folderCandidates.Count;
        if (total == 0) return;

        // announceProgress: false on every leg - each one's own "Importing N..."/"Imported N!"
        // status would otherwise stomp over the others' as they run one after another (e.g.
        // "Importing 1..." followed by "Importing 2...") instead of one steady running total.
        _library.StatusMessage = $"Importing {total} game(s)...";

        int steamAdded, gogAdded, eaAdded, epicAdded, ubisoftAdded, xboxAdded, battleNetAdded, folderAdded;

        // One save and one hotkey rebuild for the whole selection instead of one per platform.
        using (BeginCommitBatch())
        {
            steamAdded = steamGames.Count > 0 ? await ImportSteamGamesAsync(steamGames, announceProgress: false) : 0;
            gogAdded = gogGames.Count > 0 ? await ImportGogGamesAsync(gogGames, announceProgress: false) : 0;
            eaAdded = eaGames.Count > 0 ? await ImportEaGamesAsync(eaGames, announceProgress: false) : 0;
            epicAdded = epicGames.Count > 0 ? await ImportEpicGamesAsync(epicGames, announceProgress: false) : 0;
            ubisoftAdded = ubisoftGames.Count > 0 ? await ImportUbisoftGamesAsync(ubisoftGames, announceProgress: false) : 0;
            xboxAdded = xboxGames.Count > 0 ? await ImportXboxGamesAsync(xboxGames, announceProgress: false) : 0;
            battleNetAdded = battleNetGames.Count > 0 ? await ImportBattleNetGamesAsync(battleNetGames, announceProgress: false) : 0;
            folderAdded = folderCandidates.Count > 0 ? await ImportBatchGamesAsync(folderCandidates, announceProgress: false) : 0;
        }

        // -1 means that leg's import threw (already logged) rather than everything just being a
        // duplicate - don't let a real failure hide behind the same reassuring "already in your
        // library" text a legitimate all-duplicates result gets.
        if (steamAdded < 0 || gogAdded < 0 || eaAdded < 0 || epicAdded < 0 || ubisoftAdded < 0 || xboxAdded < 0 || battleNetAdded < 0 || folderAdded < 0)
        {
            int addedSoFar = Math.Max(steamAdded, 0) + Math.Max(gogAdded, 0) + Math.Max(eaAdded, 0) + Math.Max(epicAdded, 0) + Math.Max(ubisoftAdded, 0) + Math.Max(xboxAdded, 0) + Math.Max(battleNetAdded, 0) + Math.Max(folderAdded, 0);
            _library.AnnounceImportResult(addedSoFar > 0
                ? $"Imported {addedSoFar} game(s), but part of the import failed - check the log for details."
                : "Import failed - check the log for details.");
            return;
        }

        int addedTotal = steamAdded + gogAdded + eaAdded + epicAdded + ubisoftAdded + xboxAdded + battleNetAdded + folderAdded;
        _library.AnnounceImportResult(addedTotal > 0
            ? $"Imported {addedTotal} game(s)!"
            : "All selected games are already in your library.");
    }

    /// <summary>
    /// Call once at startup so newly-detected Steam libraries are already in the scan-location
    /// list before the user ever opens Scan for Games. No-ops if Steam integration is off - the
    /// scan-location list is only ever synced from Steam while that's enabled.
    /// </summary>
    public void SyncSteamScanLocationsOnStartup()
    {
        if (!_settings.SteamIntegrationEnabled) return;

        try
        {
            if (ScanLocationService.SyncSteamLocations(_settings, _steamScannerService))
            {
                _storageService.SaveSettings(_settings);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("ImportCoordinator", $"Failed to sync Steam scan locations at startup: {ex.Message}");
        }
    }

    /// <summary>
    /// The import every launcher shares: skip what the library already has, build and finish an entry for
    /// each of the rest (a few at a time), then add them together. Each launcher says only how it recognises
    /// a game it has, and how a discovered game becomes an entry.
    /// </summary>
    /// <param name="label">How the launcher reads in messages ("GOG", "Battle.net").</param>
    /// <param name="logName">The public method's name, for the log line when the whole import throws.</param>
    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    private async Task<int> ImportDiscoveredAsync<T>(List<T> discoveredGames, string label, string logName,
        Func<GameEntry, T, bool> isSameGame, Func<T, (GameEntry Entry, string IconSource)> prepare, Func<T, string> nameOf, bool announceProgress)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already running. Try again when it finishes.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !_library.Games.Any(g => isSameGame(g.Game, d)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = $"All selected {label} games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} {label} game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var (entry, iconSource) = prepare(d);
                    await FinalizeNewEntryAsync(entry, iconSource);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import {label} game '{nameOf(d)}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Every item threw (logged above) - that's a failure, not "all duplicates". Without
            // this the caller would print "already in your library" for e.g. an unwritable
            // icon-cache directory, and the user would never retry.
            if (preparedEntries.IsEmpty && failed > 0)
            {
                return -1;
            }

            if (!preparedEntries.IsEmpty)
            {
                string? status = null;
                if (announceProgress)
                {
                    status = skippedDuplicates > 0
                        ? $"Imported {preparedEntries.Count} {label} game(s)! ({skippedDuplicates} already in library, skipped)"
                        : $"Imported {preparedEntries.Count} {label} game(s)!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", $"Error in {logName}", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    private static bool SameId(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string IconSourceOf(string? iconPath, string? exePath) => !string.IsNullOrEmpty(iconPath) ? iconPath : (exePath ?? string.Empty);

    public void ImportSteamGames(List<DiscoveredSteamGame> discoveredGames) => _ = ImportSteamGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportSteamGamesAsync(List<DiscoveredSteamGame> discoveredGames, bool announceProgress = true) =>
        await ImportDiscoveredAsync(discoveredGames, "Steam", "ImportSteamGames",
            (g, d) => SameId(g.SteamAppId, d.AppId),
            d => (new GameEntry
            {
                Name = d.Name,
                ExecutablePath = $"steam://rungameid/{d.AppId}",
                IsSteamGame = true,
                ImportedFrom = LauncherPlatform.Steam,
                SteamAppId = d.AppId,
                Category = LibraryConstants.SteamCategory,
                WorkingDirectory = d.InstallDir
            }, IconSourceOf(d.IconPath, d.ExePath)),
            d => d.Name, announceProgress);

    public void ImportGogGames(List<DiscoveredGogGame> discoveredGames) => _ = ImportGogGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportGogGamesAsync(List<DiscoveredGogGame> discoveredGames, bool announceProgress = true) =>
        await ImportDiscoveredAsync(discoveredGames, "GOG", "ImportGogGames",
            (g, d) => SameId(g.GogGameId, d.GameId),
            d => (new GameEntry
            {
                Name = d.Name,
                ExecutablePath = d.ExePath ?? string.Empty,
                // Some GOG games need extra launch flags (e.g. Ghostrunner's "-GOG") -
                // carried here so a direct-exe fallback launch (GOG Galaxy not installed;
                // see ProcessLauncherService) still passes them.
                Arguments = d.LaunchParam ?? string.Empty,
                IsGogGame = true,
                ImportedFrom = LauncherPlatform.Gog,
                GogGameId = d.GameId,
                Category = LibraryConstants.GogCategory,
                WorkingDirectory = d.InstallDir
            }, IconSourceOf(d.IconPath, d.ExePath)),
            d => d.Name, announceProgress);

    public void ImportEaGames(List<DiscoveredEaGame> discoveredGames) => _ = ImportEaGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportEaGamesAsync(List<DiscoveredEaGame> discoveredGames, bool announceProgress = true) =>
        await ImportDiscoveredAsync(discoveredGames, "EA", "ImportEaGames",
            (g, d) => SameId(g.EaContentId, d.ContentId),
            d => (new GameEntry
            {
                Name = d.Name,
                ExecutablePath = d.ExePath ?? string.Empty,
                IsEaGame = true,
                ImportedFrom = LauncherPlatform.Ea,
                EaContentId = d.ContentId,
                Category = LibraryConstants.EaCategory,
                WorkingDirectory = d.InstallDir
            }, IconSourceOf(d.IconPath, d.ExePath)),
            d => d.Name, announceProgress);

    public void ImportEpicGames(List<DiscoveredEpicGame> discoveredGames) => _ = ImportEpicGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportEpicGamesAsync(List<DiscoveredEpicGame> discoveredGames, bool announceProgress = true) =>
        await ImportDiscoveredAsync(discoveredGames, "Epic", "ImportEpicGames",
            (g, d) => SameId(g.EpicAppName, d.AppName),
            d => (new GameEntry
            {
                Name = d.Name,
                ExecutablePath = d.ExePath ?? string.Empty,
                IsEpicGame = true,
                ImportedFrom = LauncherPlatform.Epic,
                EpicAppName = d.AppName,
                Category = LibraryConstants.EpicCategory,
                WorkingDirectory = d.InstallDir
            }, IconSourceOf(d.IconPath, d.ExePath)),
            d => d.Name, announceProgress);

    public void ImportUbisoftGames(List<DiscoveredUbisoftGame> discoveredGames) => _ = ImportUbisoftGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportUbisoftGamesAsync(List<DiscoveredUbisoftGame> discoveredGames, bool announceProgress = true) =>
        await ImportDiscoveredAsync(discoveredGames, "Ubisoft", "ImportUbisoftGames",
            (g, d) => SameId(g.UbisoftGameId, d.GameId),
            d => (new GameEntry
            {
                Name = d.Name,
                ExecutablePath = d.ExePath ?? string.Empty,
                IsUbisoftGame = true,
                ImportedFrom = LauncherPlatform.Ubisoft,
                UbisoftGameId = d.GameId,
                Category = LibraryConstants.UbisoftCategory,
                WorkingDirectory = d.InstallDir
            }, IconSourceOf(d.IconPath, d.ExePath)),
            d => d.Name, announceProgress);

    public void ImportXboxGames(List<DiscoveredXboxGame> discoveredGames) => _ = ImportXboxGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportXboxGamesAsync(List<DiscoveredXboxGame> discoveredGames, bool announceProgress = true) =>
        await ImportDiscoveredAsync(discoveredGames, "Xbox", "ImportXboxGames",
            (g, d) => SameId(g.XboxAumid, d.Aumid),
            d => (new GameEntry
            {
                Name = d.Name,
                // Informational only - launch goes through the AUMID (see GameEntry.IsXboxGame).
                ExecutablePath = d.ExePath ?? string.Empty,
                IsXboxGame = true,
                ImportedFrom = LauncherPlatform.Xbox,
                XboxAumid = d.Aumid,
                // Unlike the other platforms, Xbox games get no placeholder category:
                // Game Pass exclusives (Fortnite, Roblox, first-party) aren't on Steam, so
                // a "Xbox" placeholder would never upgrade to a genre and would harden into
                // a permanent Xbox category tab. Left Uncategorized; Steam enrichment still
                // fills a real genre for the few Xbox games that are also on Steam.
                Category = LibraryConstants.Uncategorized,
                WorkingDirectory = d.InstallDir
            }, IconSourceOf(d.IconPath, d.ExePath)),
            d => d.Name, announceProgress);

    public void ImportBattleNetGames(List<DiscoveredBattleNetGame> discoveredGames) => _ = ImportBattleNetGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportBattleNetGamesAsync(List<DiscoveredBattleNetGame> discoveredGames, bool announceProgress = true) =>
        await ImportDiscoveredAsync(discoveredGames, "Battle.net", "ImportBattleNetGames",
            (g, d) => SameId(g.BattleNetUid, d.Uid),
            d => (new GameEntry
            {
                Name = d.Name,
                // Informational for launching (the client starts the game), but used for
                // the icon, dedupe and "open install folder".
                ExecutablePath = d.ExePath ?? string.Empty,
                IsBattleNetGame = true,
                ImportedFrom = LauncherPlatform.BattleNet,
                BattleNetUid = d.Uid,
                // May be null when Battle.net's catalog had no code yet - the launcher looks again.
                BattleNetProgramId = d.ProgramId,
                Category = LibraryConstants.BattleNetCategory,
                WorkingDirectory = d.InstallDir
            }, IconSourceOf(d.IconPath, d.ExePath)),
            d => d.Name, announceProgress);
}
