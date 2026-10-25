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

/// <summary>The first-run check for installed launchers, and what the player picks from it.</summary>
public partial class ImportCoordinator
{
    /// <summary>
    /// Probes each platform's own scanner (the same registry/filesystem footprint it would use
    /// for a real scan) purely to decide which ones are worth offering in the first-time picker -
    /// returns only the platforms that actually turned up a game. Uses the same shared scanner
    /// instances <see cref="ScanForGamesCoreAsync"/> uses, so this doubles up on that work when
    /// the user goes on to confirm the picker, but it's a one-time cost on the very first press.
    /// </summary>
    private List<Views.DetectedLauncherOption> DetectInstalledLaunchers()
    {
        var detected = new List<Views.DetectedLauncherOption>();
        var probed = new HashSet<Views.DetectedLauncher>();

        // Each probe is isolated: one platform's scanner throwing must not stop the others,
        // and - more importantly - must not make that platform look "not installed" to the
        // picker, which would silently turn its toggle off. LastProbedLaunchers records which
        // probes completed so ApplyDetectedLauncherChoices leaves the rest untouched.
        Probe(Views.DetectedLauncher.Steam, "Steam", () => _steamScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
        Probe(Views.DetectedLauncher.Gog, "GOG Galaxy", () => _gogScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
        Probe(Views.DetectedLauncher.Ea, "EA App", () => _eaScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
        Probe(Views.DetectedLauncher.Epic, "Epic Games", () => _epicScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
        Probe(Views.DetectedLauncher.Ubisoft, "Ubisoft Connect", () => _ubisoftScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
        Probe(Views.DetectedLauncher.Xbox, "Xbox / PC Game Pass", () => _xboxScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
        Probe(Views.DetectedLauncher.BattleNet, "Battle.net", () => _battleNetScannerService.ScanInstalledGames(Array.Empty<string>()).Count);

        LastProbedLaunchers = probed;
        LoggingService.Info("ImportCoordinator", detected.Count == 0
            ? "First 'Scan for Games' press: no supported launcher had any installed games."
            : $"First 'Scan for Games' press: detected {string.Join(", ", detected.Select(d => $"{d.DisplayName} ({d.GameCount})"))}.");
        return detected;

        void Probe(Views.DetectedLauncher launcher, string displayName, Func<int> countGames)
        {
            try
            {
                int count = countGames();
                probed.Add(launcher);
                if (count > 0)
                {
                    // Pre-ticked from the toggle's *current* value, so a platform the user has
                    // already turned off by hand in Settings isn't silently re-enabled by
                    // confirming the picker.
                    detected.Add(new Views.DetectedLauncherOption(launcher, displayName, count) { IsSelected = IsIntegrationEnabled(launcher) });
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("ImportCoordinator", $"Error probing {displayName} for installed games: {ex.Message}");
            }
        }
    }

    /// <summary>The launchers whose probe completed in the most recent
    /// <see cref="DetectInstalledLaunchers"/> run - see SettingsViewModel.ApplyDetectedLauncherChoices.</summary>
    public IReadOnlySet<Views.DetectedLauncher> LastProbedLaunchers { get; private set; } = new HashSet<Views.DetectedLauncher>();

    private bool IsIntegrationEnabled(Views.DetectedLauncher launcher) => launcher switch
    {
        Views.DetectedLauncher.Steam => _settings.SteamIntegrationEnabled,
        Views.DetectedLauncher.Gog => _settings.GogIntegrationEnabled,
        Views.DetectedLauncher.Ea => _settings.EaIntegrationEnabled,
        Views.DetectedLauncher.Epic => _settings.EpicIntegrationEnabled,
        Views.DetectedLauncher.Ubisoft => _settings.UbisoftIntegrationEnabled,
        Views.DetectedLauncher.Xbox => _settings.XboxIntegrationEnabled,
        Views.DetectedLauncher.BattleNet => _settings.BattleNetIntegrationEnabled,
        _ => false
    };

    /// <summary>
    /// Called after the user confirms <see cref="Views.LauncherDetectionDialog"/> with at least
    /// one platform checked. The caller (MainWindow) must already have applied the chosen
    /// toggles via SettingsViewModel.ApplyDetectedLauncherChoices *before* calling this - not
    /// here, because this class mutates the shared AppSettings object directly and has no way to
    /// raise the property-changed notification SettingsViewModel's own bindings need (setting
    /// _settings.UbisoftIntegrationEnabled directly leaves Settings > Library's checkbox showing
    /// its stale old value even though the underlying data, and therefore scanning, is correct -
    /// confusing regardless of being cosmetic). This method just re-syncs Steam's scan locations
    /// if Steam was turned on, then runs the real scan.
    /// </summary>
    public void CompleteFirstTimeLauncherDetection(bool anyLauncherEnabled = true)
    {
        if (!anyLauncherEnabled)
        {
            // "Enable Selected" with nothing ticked is a legitimate choice (the user wants no
            // launcher integrations) - honour it, but say so rather than falling through to a
            // scan that would only pop the "No Scan Locations" dialog at them.
            _library.AnnounceImportResult("No launcher integrations enabled. Turn them on any time in Settings → Library, or add a scan location.");
            return;
        }

        if (_settings.SteamIntegrationEnabled)
        {
            SyncSteamScanLocationsOnStartup();
        }

        _ = ScanForGamesCoreAsync(silent: false);
    }

    /// <summary>Called if the user closes/skips the first-time launcher-detection prompt without
    /// confirming. HasSeenLauncherDetectionPrompt is already marked seen and no toggle was
    /// touched; the next "Scan for Games" press just runs normally - which the status line says,
    /// since the user pressed Scan and would otherwise see nothing happen at all.</summary>
    public void SkipFirstTimeLauncherDetection()
    {
        _library.AnnounceImportResult("Launcher setup skipped - press Scan for Games again to scan with the current Settings.");
    }

    /// <summary>
    /// Does the actual scan: every enabled scan location (Steam libraries, if Steam integration
    /// is on, plus any manually-added folders from Settings &gt; Game Scanner) and, if it finds
    /// any game not already in the library, opens the bulk install prompt so the user can pick
    /// which to add - even a single result goes through that same prompt, since a scan can just
    /// as easily turn up several at once. If nothing new turns up, no dialog is shown at all.
    /// </summary>
    /// <param name="silent">
    /// True for the automatic startup run (see AppSettings.AutoScanForGamesOnStartup): skips the
    /// "No Scan Locations" prompt instead of greeting the user with a dialog the moment the app
    /// opens. The install prompt still opens normally if the scan actually finds something.
    /// </param>
    private async Task ScanForGamesCoreAsync(bool silent = false)
    {
        if (_isScanningForGames)
        {
            if (!silent) _library.StatusMessage = "A scan is already running. Try again when it finishes.";
            return;
        }

        var enabledLocations = _settings.ScanLocations.Where(l => l.IsEnabled).ToList();
        // GOG/EA/Epic/Ubisoft scanning isn't gated by ScanLocations at all (see the
        // gogEnabled/eaEnabled/epicEnabled/ubisoftEnabled comment below), so any one alone is
        // enough to proceed even with zero configured scan locations.
        // Steam library rows linger in settings while Steam integration is off (they're hidden
        // and skipped, not deleted), so they must not count towards "something to scan".
        bool anySteamLibrary = _settings.SteamIntegrationEnabled && enabledLocations.Any(l => l.Source == ScanLocationSource.Steam);
        bool anyManualFolder = enabledLocations.Any(l => l.Source != ScanLocationSource.Steam);
        if (!anySteamLibrary && !anyManualFolder && !_settings.GogIntegrationEnabled && !_settings.EaIntegrationEnabled && !_settings.EpicIntegrationEnabled && !_settings.UbisoftIntegrationEnabled && !_settings.XboxIntegrationEnabled && !_settings.BattleNetIntegrationEnabled)
        {
            if (!silent)
            {
                Window? owner = WindowHelper.ActiveOwner();
                bool steamOnButNoLibraries = _settings.SteamIntegrationEnabled;
                ModernDialog.ShowInfo(
                    owner,
                    "Nothing to Scan",
                    steamOnButNoLibraries
                        ? "Steam integration is on, but none of its library folders are enabled."
                        : "No launcher integrations are enabled and no scan locations have been added.",
                    steamOnButNoLibraries
                        ? "Tick a Steam library under the Steam toggle in Settings → Library (or click Refresh there if none are listed), then try again."
                        : "Turn on a launcher integration or add a folder under Scan Locations in Settings → Library, then try again.");
            }
            return;
        }

        _isScanningForGames = true;
        try
        {
            _library.StatusMessage = "Scanning for games...";

            // Any entry carrying the App ID, not only Steam-launched ones: ImportSteamGamesAsync
            // dedupes on the ID alone, so a Local entry linked by online matching was offered as
            // new on every scan and then silently skipped when imported.
            var existingAppIds = _library.Games
                .Where(g => !string.IsNullOrEmpty(g.Game.SteamAppId))
                .Select(g => g.Game.SteamAppId!)
                .ToList();
            var existingGogGameIds = _library.Games
                .Where(g => g.Game.IsGogGame && !string.IsNullOrEmpty(g.Game.GogGameId))
                .Select(g => g.Game.GogGameId!)
                .ToList();
            var existingEaContentIds = _library.Games
                .Where(g => g.Game.IsEaGame && !string.IsNullOrEmpty(g.Game.EaContentId))
                .Select(g => g.Game.EaContentId!)
                .ToList();
            var existingEpicAppNames = _library.Games
                .Where(g => g.Game.IsEpicGame && !string.IsNullOrEmpty(g.Game.EpicAppName))
                .Select(g => g.Game.EpicAppName!)
                .ToList();
            var existingUbisoftGameIds = _library.Games
                .Where(g => g.Game.IsUbisoftGame && !string.IsNullOrEmpty(g.Game.UbisoftGameId))
                .Select(g => g.Game.UbisoftGameId!)
                .ToList();
            var existingXboxAumids = _library.Games
                .Where(g => g.Game.IsXboxGame && !string.IsNullOrEmpty(g.Game.XboxAumid))
                .Select(g => g.Game.XboxAumid!)
                .ToList();
            var existingBattleNetUids = _library.Games
                .Where(g => g.Game.IsBattleNetGame && !string.IsNullOrEmpty(g.Game.BattleNetUid))
                .Select(g => g.Game.BattleNetUid!)
                .ToList();
            var existingExePaths = new HashSet<string>(_library.Games.Select(g => g.Game.ExecutablePath), StringComparer.OrdinalIgnoreCase);
            // Which entry holds each App ID, for the log only: "already in library" is no answer when
            // the user cannot see the game - it may be hidden, or a folder entry matched to that ID.
            var appIdHolders = LoggingService.IsVerboseEnabled
                ? _library.Games
                    .Where(g => !string.IsNullOrEmpty(g.Game.SteamAppId))
                    .GroupBy(g => g.Game.SteamAppId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => string.Join("; ", g.Select(c => $"'{c.Game.Name}' in '{c.Game.Category}'{(c.Game.IsHidden ? ", hidden" : string.Empty)}, launches '{c.Game.ExecutablePath}'")),
                        StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>();
            var ignoredAppIds = new HashSet<string>(
                _settings.IgnoredGamePaths.Where(p => p.SteamAppId != null).Select(p => p.SteamAppId!),
                StringComparer.OrdinalIgnoreCase);
            var ignoredGogGameIds = new HashSet<string>(
                _settings.IgnoredGamePaths.Where(p => p.GogGameId != null).Select(p => p.GogGameId!),
                StringComparer.OrdinalIgnoreCase);
            var ignoredEaContentIds = new HashSet<string>(
                _settings.IgnoredGamePaths.Where(p => p.EaContentId != null).Select(p => p.EaContentId!),
                StringComparer.OrdinalIgnoreCase);
            var ignoredEpicAppNames = new HashSet<string>(
                _settings.IgnoredGamePaths.Where(p => p.EpicAppName != null).Select(p => p.EpicAppName!),
                StringComparer.OrdinalIgnoreCase);
            var ignoredUbisoftGameIds = new HashSet<string>(
                _settings.IgnoredGamePaths.Where(p => p.UbisoftGameId != null).Select(p => p.UbisoftGameId!),
                StringComparer.OrdinalIgnoreCase);
            var ignoredXboxAumids = new HashSet<string>(
                _settings.IgnoredGamePaths.Where(p => p.XboxAumid != null).Select(p => p.XboxAumid!),
                StringComparer.OrdinalIgnoreCase);
            var ignoredBattleNetUids = new HashSet<string>(
                _settings.IgnoredGamePaths.Where(p => p.BattleNetUid != null).Select(p => p.BattleNetUid!),
                StringComparer.OrdinalIgnoreCase);

            var steamLocations = _settings.SteamIntegrationEnabled
                ? enabledLocations.Where(l => l.Source == ScanLocationSource.Steam).ToList()
                : new List<ScanLocation>();
            var folderLocations = enabledLocations.Where(l => l.Source != ScanLocationSource.Steam).ToList();
            bool gogEnabled = _settings.GogIntegrationEnabled;
            bool eaEnabled = _settings.EaIntegrationEnabled;
            bool epicEnabled = _settings.EpicIntegrationEnabled;
            bool ubisoftEnabled = _settings.UbisoftIntegrationEnabled;
            bool xboxEnabled = _settings.XboxIntegrationEnabled;
            bool battleNetEnabled = _settings.BattleNetIntegrationEnabled;

            // Each platform's scan hits its own independent registry/filesystem locations, so
            // they run concurrently rather than one after another.
            var steamTask = Task.Run(() =>
            {
                var steamResults = new List<DiscoveredSteamGame>();

                // This leg used to say nothing at all, so a Steam game that a scan did not offer
                // left a verbose log with no trace of why: integration off, no library enabled,
                // Steam not found, or the game filtered out below.
                if (!_settings.SteamIntegrationEnabled)
                    LoggingService.Verbose("SteamScanner", "Skipped: Steam integration is off.");
                else if (steamLocations.Count == 0)
                    LoggingService.Verbose("SteamScanner", $"Skipped: no enabled Steam library among {_settings.ScanLocations.Count(l => l.Source == ScanLocationSource.Steam)} known.");

                if (steamLocations.Count > 0)
                {
                    string? steamPath = _steamScannerService.GetSteamInstallPath();
                    LoggingService.Verbose("SteamScanner", $"Steam at '{steamPath ?? "(not found)"}'; libraries: {string.Join(", ", steamLocations.Select(l => $"'{l.Path}'"))}");
                    // A game in a library that is switched off is never offered, and looks exactly
                    // like a game Steam does not have.
                    foreach (var off in _settings.ScanLocations.Where(l => l.Source == ScanLocationSource.Steam && !l.IsEnabled))
                        LoggingService.Verbose("SteamScanner", $"Not scanned: Steam library '{off.Path}' is switched off in Settings.");
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        var found = _steamScannerService.ScanInstalledGames(steamLocations.Select(l => l.Path), steamPath, existingAppIds);
                        foreach (var g in LoggingService.IsVerboseEnabled ? found : [])
                        {
                            string verdict = g.IsAlreadyImported ? $"already in library as {appIdHolders.GetValueOrDefault(g.AppId, "(unknown entry)")}"
                                : ignoredAppIds.Contains(g.AppId) ? "on the ignore list"
                                : g.ExePath != null && existingExePaths.Contains(g.ExePath) ? $"already in library (exe '{g.ExePath}')"
                                : "offered";
                            LoggingService.Verbose("SteamScanner", $"'{g.Name}' [{g.AppId}]: {verdict}");
                        }

                        // The exe-path fallback mirrors the GOG/EA/Epic/Ubisoft tasks below: a
                        // Steam game that reached the library without a SteamAppId (added by
                        // some route the platform lookup couldn't resolve) must not be offered
                        // again as "new" - selecting it would create a second row for the same exe.
                        steamResults = found
                            .Where(g => !g.IsAlreadyImported && !ignoredAppIds.Contains(g.AppId) &&
                                        (g.ExePath == null || !existingExePaths.Contains(g.ExePath)))
                            .ToList();
                    }
                }
                return steamResults;
            });

            // GOG has no library-folder concept to enable/disable per-location (every game's
            // path is already pinned in its own registry entry - see GogScannerService), so
            // GogIntegrationEnabled alone gates it, the same way SteamIntegrationEnabled gates
            // Steam on top of the separate per-folder ScanLocations toggles above.
            var gogTask = Task.Run(() => OfferedBy("GOG", gogEnabled,
                () => _gogScannerService.ScanInstalledGames(existingGogGameIds),
                g => g.Name, g => g.GameId, g => g.IsAlreadyImported, g => ignoredGogGameIds.Contains(g.GameId), g => g.ExePath, existingExePaths));

            // Same reasoning as GOG above - EA has no scan-location concept either.
            var eaTask = Task.Run(() => OfferedBy("EA", eaEnabled,
                () => _eaScannerService.ScanInstalledGames(existingEaContentIds),
                g => g.Name, g => g.ContentId, g => g.IsAlreadyImported, g => ignoredEaContentIds.Contains(g.ContentId), g => g.ExePath, existingExePaths));

            // Same reasoning as GOG/EA above - Epic has no scan-location concept either.
            var epicTask = Task.Run(() => OfferedBy("Epic", epicEnabled,
                () => _epicScannerService.ScanInstalledGames(existingEpicAppNames),
                g => g.Name, g => g.AppName, g => g.IsAlreadyImported, g => ignoredEpicAppNames.Contains(g.AppName), g => g.ExePath, existingExePaths));

            // Same reasoning as GOG/EA/Epic above - Ubisoft has no scan-location concept either.
            var ubisoftTask = Task.Run(() => OfferedBy("Ubisoft", ubisoftEnabled,
                () => _ubisoftScannerService.ScanInstalledGames(existingUbisoftGameIds),
                g => g.Name, g => g.GameId, g => g.IsAlreadyImported, g => ignoredUbisoftGameIds.Contains(g.GameId), g => g.ExePath, existingExePaths));

            // Same reasoning again - Xbox games are wherever the Xbox app put them, and Gaming
            // Services' registry knows each one.
            var xboxTask = Task.Run(() => OfferedBy("Xbox", xboxEnabled,
                () => _xboxScannerService.ScanInstalledGames(existingXboxAumids),
                g => g.Name, g => g.Aumid, g => g.IsAlreadyImported, g => ignoredXboxAumids.Contains(g.Aumid), g => g.ExePath, existingExePaths));

            // Same again - Blizzard's uninstall entries pin every Battle.net game's folder.
            var battleNetTask = Task.Run(() => OfferedBy("Battle.net", battleNetEnabled,
                () => _battleNetScannerService.ScanInstalledGames(existingBattleNetUids),
                g => g.Name, g => g.Uid, g => g.IsAlreadyImported, g => ignoredBattleNetUids.Contains(g.Uid), g => g.ExePath, existingExePaths));

            // "Platform|ID" of every platform-tagged game already in the library, so a folder
            // candidate that resolves to one of them (e.g. a GOG game found under a manual
            // "C:\GOG Games" scan location) isn't offered as new just because its exe path
            // differs from the entry's - a Steam entry's path is a steam:// URL, never the exe.
            var existingPlatformKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _library.Games.Select(c => c.Game))
            {
                if (!string.IsNullOrEmpty(g.SteamAppId)) existingPlatformKeys.Add($"Steam|{g.SteamAppId}");
                if (!string.IsNullOrEmpty(g.GogGameId)) existingPlatformKeys.Add($"GOG|{g.GogGameId}");
                if (!string.IsNullOrEmpty(g.EaContentId)) existingPlatformKeys.Add($"EA|{g.EaContentId}");
                if (!string.IsNullOrEmpty(g.EpicAppName)) existingPlatformKeys.Add($"Epic|{g.EpicAppName}");
                if (!string.IsNullOrEmpty(g.UbisoftGameId)) existingPlatformKeys.Add($"Ubisoft|{g.UbisoftGameId}");
                if (!string.IsNullOrEmpty(g.XboxAumid)) existingPlatformKeys.Add($"Xbox|{g.XboxAumid}");
                if (!string.IsNullOrEmpty(g.BattleNetUid)) existingPlatformKeys.Add($"Battle.net|{g.BattleNetUid}");
            }

            var folderTask = Task.Run(() =>
            {
                var folderResults = new List<GameCandidate>();
                var ignoredExePaths = BuildIgnoredExePathSet();
                var ignoredPlatformKeys = BuildIgnoredPlatformKeySet();
                var index = _platformLookup.CreateIndex();
                foreach (var loc in folderLocations)
                {
                    if (!Directory.Exists(loc.Path))
                    {
                        LoggingService.Verbose("ScanForGames", $"Folders: skipped '{loc.Path}', it does not exist (a drive not attached?).");
                        continue;
                    }

                    // A scan location's identity is already known (the user configured it as a
                    // game library), so this skips ScanFolderOrLibrary's is-this-a-library
                    // confidence gating entirely and takes every immediate subfolder's own best
                    // candidate directly - see ScanKnownLibraryLocation.
                    var candidates = FilterIgnored(
                        ResolvePlatforms(_folderScannerService.ScanKnownLibraryLocation(loc.Path, GameNameExtractor.PreferExeForGameName), index),
                        ignoredExePaths, ignoredPlatformKeys);
                    // FilterIgnored has already dropped what is on the ignore list; the scanner
                    // itself logs every executable it weighed.
                    foreach (var c in candidates)
                    {
                        bool inLibrary = existingExePaths.Contains(c.ExePath) ||
                                         (c.Platform != null && existingPlatformKeys.Contains(PlatformKey(c.Platform)));
                        LoggingService.Verbose("ScanForGames", $"Folders: '{c.Name}' ({c.ExePath}): {(inLibrary ? "already in library" : "offered")}");
                        if (!inLibrary) folderResults.Add(c);
                    }
                }
                return folderResults;
            });

            await Task.WhenAll(steamTask, gogTask, eaTask, epicTask, ubisoftTask, xboxTask, battleNetTask, folderTask).ConfigureAwait(true);
            var (steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, battleNetGames, folderCandidates) =
                (steamTask.Result, gogTask.Result, eaTask.Result, epicTask.Result, ubisoftTask.Result, xboxTask.Result, battleNetTask.Result, folderTask.Result);

            // A game a platform scan already found in this run must not be listed a second time
            // as a folder candidate (a manual scan location that overlaps a launcher's install
            // root) - the picker would show it twice and the import would then dedupe silently.
            var runPlatformKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            runPlatformKeys.UnionWith(steamGames.Select(g => $"Steam|{g.AppId}"));
            runPlatformKeys.UnionWith(gogGames.Select(g => $"GOG|{g.GameId}"));
            runPlatformKeys.UnionWith(eaGames.Select(g => $"EA|{g.ContentId}"));
            runPlatformKeys.UnionWith(epicGames.Select(g => $"Epic|{g.AppName}"));
            runPlatformKeys.UnionWith(ubisoftGames.Select(g => $"Ubisoft|{g.GameId}"));
            runPlatformKeys.UnionWith(xboxGames.Select(g => $"Xbox|{g.Aumid}"));
            runPlatformKeys.UnionWith(battleNetGames.Select(g => $"Battle.net|{g.Uid}"));
            folderCandidates = folderCandidates
                .Where(c => c.Platform == null || !runPlatformKeys.Contains(PlatformKey(c.Platform)))
                .DistinctBy(c => c.Platform != null ? PlatformKey(c.Platform) : c.ExePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            LoggingService.Verbose("ImportCoordinator", $"Scan found new games - Steam {steamGames.Count}, GOG {gogGames.Count}, EA {eaGames.Count}, Epic {epicGames.Count}, Ubisoft {ubisoftGames.Count}, Xbox {xboxGames.Count}, Battle.net {battleNetGames.Count}, folders {folderCandidates.Count}.");

            if (steamGames.Count == 0 && gogGames.Count == 0 && eaGames.Count == 0 && epicGames.Count == 0 && ubisoftGames.Count == 0 && xboxGames.Count == 0 && battleNetGames.Count == 0 && folderCandidates.Count == 0)
            {
                _library.AnnounceImportResult("No new games found.");
                return;
            }

            int foundCount = steamGames.Count + gogGames.Count + eaGames.Count + epicGames.Count + ubisoftGames.Count + xboxGames.Count + battleNetGames.Count + folderCandidates.Count;
            _library.StatusMessage = $"Found {foundCount} new game(s).";
            // Only the scan TrayTrigger ran by itself: a scan the user started shows its results.
            if (silent)
            {
                ActivityService.Add(ActivityLevel.Activity,
                    $"The startup scan found {foundCount} new {(foundCount == 1 ? "game" : "games")}",
                    detail: "Add them with Scan for Games on the Library page.", groupKey: ScanFoundActivityKey);
            }
            RequestScanResultsPicker?.Invoke(steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, battleNetGames, folderCandidates);
        }
        finally
        {
            _isScanningForGames = false;
            // Scanning is the moment TrayTrigger goes and looks at what is on this PC, so it is
            // also when a game uninstalled since startup should stop looking playable. Every exit
            // above runs this, "No new games found" most of all - that is the scan most likely to
            // be the one where something went away rather than arrived. Fire and forget: the
            // cards re-mark themselves when it lands.
            _ = _library.RefreshAvailabilityInBackground();
        }
    }

    /// <summary>
    /// One launcher's leg of Scan for Games: what it found that is new, with a verbose line for
    /// the leg and for every game it found saying what became of it. The Steam leg said nothing
    /// at all once, and a game a scan did not offer left no trace of why - integration off,
    /// already in the library, on the ignore list - so every leg accounts for itself the same way.
    /// </summary>
    private static List<T> OfferedBy<T>(
        string platform, bool enabled, Func<List<T>> scan,
        Func<T, string> name, Func<T, string> id, Func<T, bool> alreadyImported, Func<T, bool> ignored, Func<T, string?> exePath,
        HashSet<string> existingExePaths)
    {
        if (!enabled)
        {
            LoggingService.Verbose("ScanForGames", $"{platform}: skipped, its integration is off.");
            return new List<T>();
        }

        var found = scan();
        LoggingService.Verbose("ScanForGames", $"{platform}: {found.Count} installed game(s) found.");

        var offered = new List<T>();
        foreach (var g in found)
        {
            string? exe = exePath(g);
            string verdict = alreadyImported(g) ? "already in library (by its ID)"
                : ignored(g) ? "on the ignore list"
                : exe != null && existingExePaths.Contains(exe) ? $"already in library (exe '{exe}')"
                : "offered";
            if (verdict == "offered") offered.Add(g);
            LoggingService.Verbose("ScanForGames", $"{platform}: '{name(g)}' [{id(g)}]: {verdict}");
        }
        return offered;
    }
}
