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

/// <summary>
/// Owns the import pipelines: file/shortcut drop, folder scan, batch candidate import, and Steam
/// import, plus the shared enrichment tail (icon caching + Steam metadata) and library-wide
/// poster refresh/enrichment passes. Split out of MainViewModel per L-13. Depends on
/// <see cref="LibraryViewModel"/> (to add entries, check duplicates, and trigger the shared
/// post-mutation refresh) rather than the other way around.
/// </summary>
public partial class ImportCoordinator : ViewModelBase
{
    private readonly LibraryViewModel _library;
    private readonly ShortcutService _shortcutService;
    private readonly IconExtractorService _iconExtractorService;
    private readonly FolderScannerService _folderScannerService;
    private readonly SteamScannerService _steamScannerService;
    private readonly GogScannerService _gogScannerService;
    private readonly EaScannerService _eaScannerService;
    private readonly EpicScannerService _epicScannerService;
    private readonly UbisoftScannerService _ubisoftScannerService;
    private readonly XboxScannerService _xboxScannerService;
    private readonly BattleNetScannerService _battleNetScannerService;
    private readonly PlatformLookupService _platformLookup;
    private readonly SteamSearchService _steamSearchService;
    private readonly SteamMetadataService _steamMetadataService;
    private readonly StorageService _storageService;
    private readonly AppSettings _settings;
    private readonly Func<string?> _getSteamGridDbApiKeyOrNull;

    private const int MaxConcurrentEnrichments = 4;
    /// <summary>The Activity &amp; History group for "the startup scan found games" - what the
    /// "Notify me when a scan finds new games" toast keys on.</summary>
    public const string ScanFoundActivityKey = "scan.found";
    private static readonly TimeSpan EnrichmentRetryInterval = TimeSpan.FromDays(7);

    // Reentrancy guards: these async operations all add to / enrich the shared Games
    // collection, so overlapping invocations (e.g. rapid double-clicks, a settings toggle
    // re-triggering enrichment mid-import) could interleave duplicate-check and add logic.
    private bool _isImportInProgress;
    private bool _isEnrichmentInProgress;
    private bool _isScanningForGames;

    private bool _isRefreshingAllPosters;
    public bool IsRefreshingAllPosters
    {
        get => _isRefreshingAllPosters;
        private set { _isRefreshingAllPosters = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRefreshAllPosters)); }
    }

    public bool CanRefreshAllPosters => !IsRefreshingAllPosters;

    public event Action<List<DiscoveredSteamGame>, List<DiscoveredGogGame>, List<DiscoveredEaGame>, List<DiscoveredEpicGame>, List<DiscoveredUbisoftGame>, List<DiscoveredXboxGame>, List<DiscoveredBattleNetGame>, List<GameCandidate>>? RequestScanResultsPicker;
    public event Action<string, List<GameCandidate>>? RequestCandidatePicker;
    public event Action<string, List<GameCandidate>>? RequestFolderBatchImport;
    /// <summary>
    /// Raised the first time the user ever presses "Scan for Games" (see
    /// <see cref="ScanForGamesAsync"/>), if at least one platform's own scanner found an
    /// installed game. The view shows <see cref="Views.LauncherDetectionDialog"/> and reports the
    /// result back via <see cref="CompleteFirstTimeLauncherDetection"/> or
    /// <see cref="SkipFirstTimeLauncherDetection"/>.
    /// </summary>
    public event Action<List<Views.DetectedLauncherOption>>? RequestLauncherDetectionPrompt;

    public ICommand AddGameCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand OpenScanForGamesCommand { get; }
    public ICommand RefreshAllPostersCommand { get; }

    public ImportCoordinator(
        LibraryViewModel library,
        ShortcutService shortcutService,
        IconExtractorService iconExtractorService,
        FolderScannerService folderScannerService,
        SteamScannerService steamScannerService,
        GogScannerService gogScannerService,
        EaScannerService eaScannerService,
        EpicScannerService epicScannerService,
        UbisoftScannerService ubisoftScannerService,
        XboxScannerService xboxScannerService,
        BattleNetScannerService battleNetScannerService,
        SteamSearchService steamSearchService,
        SteamMetadataService steamMetadataService,
        StorageService storageService,
        AppSettings settings,
        Func<string?> getSteamGridDbApiKeyOrNull)
    {
        _library = library;
        _shortcutService = shortcutService;
        _iconExtractorService = iconExtractorService;
        _folderScannerService = folderScannerService;
        // Folders ignored in Settings are pruned by the scanner itself (never walked), for every
        // route that uses it: Scan for Games, Add Folder, and a dropped folder. Read live, so a
        // folder ignored a moment ago is skipped by the next scan without a restart.
        _folderScannerService.IgnoredFolderProvider = () =>
            settings.IgnoredGamePaths.Where(p => !string.IsNullOrWhiteSpace(p.FolderPath)).Select(p => p.FolderPath!);
        // Every scan location is a folder of games, one per subfolder - which is exactly what the
        // DLSS card needs to know to find a game's own folder. Disabled ones too: whether a
        // location is scanned does not change what it is.
        DlssProbeService.LibraryFolderProvider = () =>
            settings.ScanLocations
                .Where(l => !string.IsNullOrWhiteSpace(l.Path))
                .Select(l => l.Source == ScanLocationSource.Steam ? Path.Combine(l.Path, "steamapps", "common") : l.Path);
        _steamScannerService = steamScannerService;
        _gogScannerService = gogScannerService;
        _eaScannerService = eaScannerService;
        _epicScannerService = epicScannerService;
        _ubisoftScannerService = ubisoftScannerService;
        _xboxScannerService = xboxScannerService;
        _battleNetScannerService = battleNetScannerService;
        _platformLookup = new PlatformLookupService(steamScannerService, gogScannerService, eaScannerService, epicScannerService, ubisoftScannerService, xboxScannerService, battleNetScannerService);
        _steamSearchService = steamSearchService;
        _steamMetadataService = steamMetadataService;
        _storageService = storageService;
        _settings = settings;
        _getSteamGridDbApiKeyOrNull = getSteamGridDbApiKeyOrNull;

        AddGameCommand = new RelayCommand(AddGameBrowse);
        AddFolderCommand = new RelayCommand(AddGameFolderBrowse);
        OpenScanForGamesCommand = new AsyncRelayCommand(() => ScanForGamesAsync());
        RefreshAllPostersCommand = new AsyncRelayCommand(async () => await RefreshAllPostersAsync());
    }

    // Shared tail of every import pipeline (file drop, folder scan, batch import, Steam import):
    // cache the icon, then enrich from Steam metadata. See L-12.
    //
    // PerformanceProfile is set explicitly here - to the System page's "New games start on" choice -
    // rather than relying on GameEntry's own default: this method only ever runs for a game newly
    // added through the app. An existing game loaded from an older games.json (from before
    // Performance Profiles existed) has no serialized value for that property and must fall back to
    // GameEntry's class default instead - which stays Off, so upgrading never silently opts
    // already-installed games into system tweaks.
    private async Task FinalizeNewEntryAsync(GameEntry entry, string iconSourcePath)
    {
        entry.IconPath = _iconExtractorService.ExtractAndCacheIcon(entry.Id, iconSourcePath, entry.Name);
        entry.PerformanceProfile = _settings.NewGameProfile;
        await _library.EnrichGameWithSteamMetadataAsync(entry);
        // Stamp it like EnrichLibraryAsync does, or the next startup re-searches every game this
        // import just looked up and couldn't match (Xbox, Battle.net titles not on Steam). Only when a
        // lookup could run, though: with every source off nothing was tried, and a stamp would keep
        // the game out of the pass that runs once one is switched on.
        if (AnyEnrichmentSourceEnabled)
            entry.LastEnrichmentAttemptUtc = DateTime.UtcNow;
    }

    // What EnrichLibraryAsync can run with - and so what makes an import's lookup count as an attempt.
    private bool AnyEnrichmentSourceEnabled =>
        _settings.AutoCategorizeFromSteam || _settings.SearchOfficialTitleOnline || RawgEnabled;

    private bool RawgEnabled => _settings.UseRawgMetadata && !string.IsNullOrWhiteSpace(_settings.RawgApiKey);

    /// <summary>The category a hand-added game gets: the tab the user is on, unless that tab is a
    /// view (All, Favorites, Hidden) rather than a category a game can belong to.</summary>
    private string NewEntryCategory => LibraryConstants.NormalizeCategory(_library.SelectedCategory);

    // Shared commit step of every import pipeline: add the prepared entries to the visible
    // library and refresh everything that depends on it. See L-12.
    internal void CommitImportedEntries(IEnumerable<GameEntry> entries, string? statusMessage)
    {
        // Cards start light and decode their icon and poster off the UI thread, as at startup: a
        // scan adding hundreds of games otherwise decoded every bitmap here, on the dispatcher.
        var cards = new List<GameCardViewModel>();
        foreach (var entry in entries)
        {
            var card = _library.CreateCardViewModel(entry, deferHeavyInit: true);
            _library.Games.Add(card);
            cards.Add(card);
            _addedSinceRecorded.Add(entry);
        }
        _library.LoadCardHeavyStateInBackground(cards);

        _library.RebuildCategories();
        PersistCommittedLibrary();
        _library.ApplySort();
        if (statusMessage != null)
        {
            _library.AnnounceImportResult(statusMessage);
        }
        _library.NotifyGameCountChanged();
    }

    /// <summary>
    /// The persistent half of a commit - rewriting games.json/settings.json and re-registering
    /// every hotkey - deferred to the end of the enclosing <see cref="BeginCommitBatch"/>, if any.
    ///
    /// One "Scan for Games" confirmation runs up to seven imports back to back, each of which
    /// used to do this itself: a nine-game scan wrote the whole library six times over and tore
    /// down and re-registered the global hotkey six times, leaving six windows in which it was
    /// unbound. The in-memory half (cards, categories, sort) still runs per import, so the
    /// library still fills in platform by platform on screen.
    /// </summary>
    private void PersistCommittedLibrary()
    {
        if (_commitBatchDepth > 0)
        {
            _commitBatchPending = true;
            return;
        }

        _library.SaveLibrary();
        _library.UpdateHotkeys();
        RecordAddedGames();
    }

    private int _commitBatchDepth;
    private bool _commitBatchPending;
    /// <summary>Games committed since the last "Added" entry: one entry per import, so a scan
    /// that imports from several launchers back to back is one line, not one per launcher.</summary>
    private readonly List<GameEntry> _addedSinceRecorded = new();

    /// <summary>A quiet Activity line for the games just added - no notification, no dot.</summary>
    private void RecordAddedGames()
    {
        if (_addedSinceRecorded.Count == 0) return;
        var added = _addedSinceRecorded.ToList();
        _addedSinceRecorded.Clear();
        var (text, detail) = DescribeAddedGames(added);
        // Its own group each time: two imports are two things that happened, not one repeated.
        ActivityService.Add(ActivityLevel.Activity, text, detail: detail,
            groupKey: $"library.added|{DateTime.UtcNow.Ticks}");
    }

    /// <summary>"Added Elden Ring and Hades (Steam)"; past three names, a count, with every name in the detail.</summary>
    internal static (string Text, string Detail) DescribeAddedGames(IReadOnlyList<GameEntry> added)
    {
        var names = added.Select(g => g.Name).ToList();
        string who = names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            3 => $"{names[0]}, {names[1]} and {names[2]}",
            _ => $"{names.Count} games",
        };
        var platforms = added.Select(DiagnosticReportService.PlatformOf).Distinct().ToList();
        string from = platforms.Count == 1 ? $" ({platforms[0]})" : "";
        string detail = string.Join(Environment.NewLine,
            added.Select(g => $"{g.Name} - {DiagnosticReportService.PlatformOf(g)}"));
        return ($"Added {who}{from}", detail);
    }

    /// <summary>
    /// Coalesces the saves and hotkey rebuilds of every commit made inside the returned scope
    /// into one, run on dispose. Nests safely; only the outermost scope flushes.
    /// </summary>
    internal CommitBatch BeginCommitBatch() => new(this);

    internal sealed class CommitBatch : IDisposable
    {
        private readonly ImportCoordinator _owner;
        private bool _disposed;

        internal CommitBatch(ImportCoordinator owner)
        {
            _owner = owner;
            _owner._commitBatchDepth++;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Decrement even on the failure path: a leg that threw must not leave the coordinator
            // permanently batching and therefore never saving again.
            if (--_owner._commitBatchDepth > 0 || !_owner._commitBatchPending) return;

            _owner._commitBatchPending = false;
            _owner._library.SaveLibrary();
            _owner._library.UpdateHotkeys();
            _owner.RecordAddedGames();
        }
    }

    /// <summary>
    /// Force-refreshes poster art for every game with a known Steam AppId, bypassing the
    /// "already has a poster" cache check so a game stuck on the composited-banner fallback
    /// tier can pick up better art (e.g. after the user adds/enables a SteamGridDB API key).
    /// Unlike <see cref="EnrichLibraryAsync"/>, this ignores existing category/cover state and
    /// always re-fetches, since the whole point is to override a previously-cached poster.
    /// Games with no Steam AppId and no poster are searched on SteamGridDB by name as well. Also
    /// run by Settings when a new SteamGridDB key is confirmed to work.
    /// </summary>
    /// <param name="progress">
    /// Optional progress/result text sink. StatusMessage alone isn't enough here: this action
    /// is triggered from the Settings page, whose status bar (bound to StatusMessage) lives in
    /// a different XAML section that's collapsed while Settings is the active view - callers
    /// needing on-screen feedback in that context should pass a sink that surfaces it there.
    /// </param>
    public async Task RefreshAllPostersAsync(IProgress<string>? progress = null)
    {
        void Report(string message)
        {
            _library.StatusMessage = message;
            progress?.Report(message);
        }

        if (IsRefreshingAllPosters)
        {
            Report("A poster refresh is already running. Try again when it finishes.");
            return;
        }

        // Games with no Steam App ID can only get a poster from SteamGridDB, by name, and only the
        // ones with none yet: the name search is looser than an App ID, so art already chosen for
        // such a game (by this search or by hand) is left alone.
        bool canSearchByName = _settings.UseVerticalPosterArt && !string.IsNullOrWhiteSpace(_getSteamGridDbApiKeyOrNull());
        var candidates = _library.Games
            .Where(card => !string.IsNullOrWhiteSpace(card.Game.SteamAppId)
                           || (canSearchByName && string.IsNullOrWhiteSpace(card.Game.CoverImagePath)))
            .ToList();
        if (candidates.Count == 0)
        {
            Report(canSearchByName
                ? "Every game already has a poster, and none is linked to Steam to re-check."
                : "No games with a linked Steam AppId to refresh.");
            return;
        }

        IsRefreshingAllPosters = true;
        try
        {
            int completed = 0;
            int updated = 0;
            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);

            var tasks = candidates.Select(async card =>
            {
                await throttle.WaitAsync();
                try
                {
                    if (string.IsNullOrWhiteSpace(card.Game.SteamAppId))
                    {
                        // RAWG's title for the game searches first when it's matched, as in the
                        // background pass (LibraryViewModel.EnrichGameWithSteamMetadataAsync).
                        string? rawgTitle = card.Game.RawgId > 0 && RawgService.TryGetCached(card.Game.RawgId, out var rawg) ? rawg.Name : null;
                        await _library.TryFetchGridArtByNameAsync(card.Game, rawgTitle);
                        if (!string.IsNullOrWhiteSpace(card.Game.CoverImagePath))
                        {
                            card.RefreshProperties();
                            Interlocked.Increment(ref updated);
                        }
                        return;
                    }

                    string appId = card.Game.SteamAppId!;
                    var details = await _steamMetadataService.GetAppDetailsAsync(appId, _getSteamGridDbApiKeyOrNull(), forceRefresh: true);
                    // DownloadAndCachePosterAsync always writes to the same Covers/{appId}.jpg
                    // path regardless of which source tier supplied it, so CoverImagePath is
                    // virtually always unchanged even when the file's actual contents just got
                    // replaced with better art. Don't gate on path equality - always reassign
                    // and refresh so the in-memory bitmap reloads from disk (LoadBitmapSafely
                    // already bypasses WPF's image cache), or a same-path content change would
                    // never show up until the app restarts.
                    if (details != null && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                    {
                        card.Game.CoverImagePath = details.CoverImagePath;
                        card.RefreshProperties();
                        Interlocked.Increment(ref updated);
                    }
                }
                finally
                {
                    throttle.Release();
                    Interlocked.Increment(ref completed);
                    Report($"Refreshing posters... {completed}/{candidates.Count}");
                }
            });

            await Task.WhenAll(tasks);

            if (updated > 0)
            {
                _library.SaveLibrary();
            }

            Report($"Poster refresh complete: {updated} of {candidates.Count} game(s) refreshed.");
        }
        finally
        {
            IsRefreshingAllPosters = false;
        }
    }

    /// <param name="retryForRawg">
    /// True when RAWG has just become available (enabled, or a key entered): games with no Steam
    /// App ID are retried now instead of waiting out <see cref="EnrichmentRetryInterval"/>, since
    /// their last attempt ran without the source that can categorise them.
    /// </param>
    public async Task EnrichLibraryAsync(bool retryForRawg = false)
    {
        // RAWG enrichment (match id, category, canonical title for poster search) rides the same
        // pass, so an enabled RAWG source is reason enough to run it.
        bool rawgEnabled = RawgEnabled;
        if (!AnyEnrichmentSourceEnabled)
            return;

        // Settings toggles, library load, and post-import enrichment can all request this
        // around the same time; let the in-flight run finish rather than starting a
        // duplicate pass over the same candidates.
        if (_isEnrichmentInProgress)
            return;

        _isEnrichmentInProgress = true;
        try
        {
            bool retryNow = retryForRawg && rawgEnabled;
            var candidates = _library.Games
                .Where(card =>
                    (LibraryConstants.IsEnrichableCategory(card.Game.Category) || string.IsNullOrWhiteSpace(card.Game.CoverImagePath) || string.IsNullOrWhiteSpace(card.Game.SteamAppId)) &&
                    (card.Game.LastEnrichmentAttemptUtc == null || DateTime.UtcNow - card.Game.LastEnrichmentAttemptUtc.Value >= EnrichmentRetryInterval
                     || (retryNow && card.Game.RawgId == 0 && LibraryConstants.IsEnrichableCategory(card.Game.Category))))
                .ToList();

            if (candidates.Count == 0)
                return;

            bool changed = false;
            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);

            var tasks = candidates.Select(async card =>
            {
                await throttle.WaitAsync();
                try
                {
                    string oldCat = card.Game.Category;
                    string? oldCover = card.Game.CoverImagePath;
                    string? oldAppId = card.Game.SteamAppId;

                    await _library.EnrichGameWithSteamMetadataAsync(card.Game);
                    // Record the attempt regardless of outcome so a game that legitimately never
                    // matches (indie, emulator, tool) isn't re-searched online every single launch -
                    // and so this needs saving even when nothing else about the game changed.
                    card.Game.LastEnrichmentAttemptUtc = DateTime.UtcNow;
                    changed = true;

                    if (card.Game.Category != oldCat || card.Game.CoverImagePath != oldCover || card.Game.SteamAppId != oldAppId)
                    {
                        card.RefreshProperties();
                    }
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            if (changed)
            {
                _library.RebuildCategories();
                _library.SaveLibrary();
            }
        }
        finally
        {
            _isEnrichmentInProgress = false;
        }
    }

    /// <summary>
    /// One status line for an import that mixed Local entries with platform-routed ones, e.g.
    /// "Added 4 game(s) and linked 1 existing (2 local, 2 via Steam, 1 linked to GOG)".
    /// </summary>
    private static string ComposeImportSummary(int localAdded, PlatformImportResult platform, string nothingNewMessage)
    {
        int added = localAdded + platform.Added;
        if (added == 0 && platform.Upgraded == 0) return nothingNewMessage;

        var parts = new List<string>(6);
        if (localAdded > 0) parts.Add($"{localAdded} local");
        if (platform.Description.Length > 0) parts.Add(platform.Description);

        string head = added > 0 ? $"Added {added} game(s)" : "Nothing new to add";
        if (platform.Upgraded > 0) head += $"{(added > 0 ? " and" : ", but")} linked {platform.Upgraded} existing";
        return $"{head}! ({string.Join(", ", parts)})";
    }

    /// <summary>
    /// The Local-game leg of <see cref="ImportBatchGamesAsync"/>: imports candidates as plain exe
    /// entries with heuristic/online naming. Callers must already have routed launcher-owned
    /// candidates elsewhere.
    /// </summary>
    private async Task<int> ImportLocalCandidatesAsync(List<GameCandidate> candidates, bool announceProgress)
    {
        if (candidates.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already running. Try again when it finishes.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            // Avoid duplicates
            var toProcess = candidates
                .Where(c => !_library.Games.Any(g => g.Game.ExecutablePath.Equals(c.ExePath, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(c => c.ExePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int skippedDuplicates = candidates.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = "All selected games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async c =>
            {
                await throttle.WaitAsync();
                try
                {
                    string gameName = c.Name;
                    string? matchedAppId = null;

                    if (_settings.SearchOfficialTitleOnline)
                    {
                        var res = await GameNameExtractor.ResolveGameMatchAsync(
                            c.ExePath,
                            c.WorkingDirectory,
                            preferExe: GameNameExtractor.PreferExeForGameName,
                            searchOnline: true,
                            steamSearch: _steamSearchService,
                            minConfidence: _settings.OnlineMatchConfidenceThreshold);

                        if (!string.IsNullOrWhiteSpace(res.ResolvedTitle))
                        {
                            gameName = res.ResolvedTitle;
                        }
                        if (!string.IsNullOrWhiteSpace(res.SteamAppId))
                        {
                            matchedAppId = res.SteamAppId;
                        }
                    }

                    var entry = new GameEntry
                    {
                        Name = gameName,
                        ExecutablePath = c.ExePath,
                        WorkingDirectory = c.WorkingDirectory,
                        Category = NewEntryCategory,
                        SteamAppId = matchedAppId
                    };

                    await FinalizeNewEntryAsync(entry, entry.ExecutablePath);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import candidate '{c.Name}': {ex.Message}");
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
                        ? $"Added {preparedEntries.Count} games from folder! ({skippedDuplicates} already in library, skipped)"
                        : $"Added {preparedEntries.Count} games from folder!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportBatchGames", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    public void AddCandidate(GameCandidate candidate) => _ = AddCandidateAsync(candidate);

    public async Task AddCandidateAsync(GameCandidate candidate)
    {
        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already running. Try again when it finishes.";
            return;
        }

        // A candidate that sits inside a launcher-owned install is imported through that
        // platform's own route (real ID/name/art, launcher-aware launch) rather than as a Local
        // exe. Resolved before taking the guard because the platform import takes it itself.
        var platformMatch = candidate.Platform ?? await Task.Run(() => _platformLookup.FindByPath(candidate.ExePath));
        if (platformMatch != null)
        {
            LoggingService.Info("ImportCoordinator", $"Candidate '{candidate.ExePath}' resolved to {platformMatch.Platform} game '{platformMatch.Name}' - importing via {platformMatch.Platform}.");
            var buckets = new PlatformImportBuckets();
            buckets.Add(platformMatch, candidate.ExePath);
            var result = await ImportPlatformBucketsAsync(buckets);
            _library.AnnounceImportResult(result switch
            {
                { Added: > 0 } => $"Added \"{platformMatch.Name}\" to library as a {platformMatch.Platform} game!",
                { Upgraded: > 0 } => $"\"{platformMatch.Name}\" was already in your library - linked it to {platformMatch.Platform}.",
                { Added: 0 } => $"\"{platformMatch.Name}\" is already in your library.",
                _ => "Import failed - check the log for details."
            });
            return;
        }

        // Re-checked after the await above: two fire-and-forget AddCandidate calls issued in the
        // same UI turn both pass the first check, and without this the second would run its own
        // duplicate check before the first has committed - adding the same exe twice, unprompted.
        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already running. Try again when it finishes.";
            return;
        }

        _isImportInProgress = true;
        try
        {
            var existingDuplicate = _library.FindDuplicateGame(candidate.ExePath, null);
            if (existingDuplicate != null)
            {
                Window? dupOwner = WindowHelper.ActiveOwner();
                bool addAnyway = ModernDialog.Confirm(
                    dupOwner,
                    "Game Already in Library",
                    $"\"{existingDuplicate.Name}\" is already in your library.",
                    "Add it again anyway?",
                    confirmText: "Add Anyway",
                    cancelText: "Skip");
                if (!addAnyway)
                {
                    return;
                }
            }

            string finalName = candidate.Name;
            string? matchedAppId = null;

            if (_settings.SearchOfficialTitleOnline)
            {
                var res = await GameNameExtractor.ResolveGameMatchAsync(
                    candidate.ExePath,
                    candidate.WorkingDirectory,
                    preferExe: GameNameExtractor.PreferExeForGameName,
                    searchOnline: true,
                    steamSearch: _steamSearchService,
                    minConfidence: _settings.OnlineMatchConfidenceThreshold);

                if (!string.IsNullOrWhiteSpace(res.ResolvedTitle))
                {
                    finalName = res.ResolvedTitle;
                }
                if (!string.IsNullOrWhiteSpace(res.SteamAppId))
                {
                    matchedAppId = res.SteamAppId;
                }
            }

            var entry = new GameEntry
            {
                Name = finalName,
                ExecutablePath = candidate.ExePath,
                WorkingDirectory = candidate.WorkingDirectory,
                Category = NewEntryCategory,
                SteamAppId = matchedAppId
            };

            await FinalizeNewEntryAsync(entry, entry.ExecutablePath);
            CommitImportedEntries([entry], $"Added \"{entry.Name}\" to library!");
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", $"Error adding candidate '{candidate?.Name}'", ex);
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    /// <summary>
    /// Per-platform lists of discovery records resolved from manually-imported paths (see
    /// <see cref="PlatformLookupService"/>), ready to hand to the matching Import*GamesAsync
    /// method. Same-game hits are collapsed by platform ID, so dragging two exes out of one GOG
    /// install adds it once.
    /// </summary>
    private sealed class PlatformImportBuckets
    {
        public List<DiscoveredSteamGame> Steam { get; } = [];
        public List<DiscoveredGogGame> Gog { get; } = [];
        public List<DiscoveredEaGame> Ea { get; } = [];
        public List<DiscoveredEpicGame> Epic { get; } = [];
        public List<DiscoveredUbisoftGame> Ubisoft { get; } = [];
        public List<DiscoveredXboxGame> Xbox { get; } = [];
        public List<DiscoveredBattleNetGame> BattleNet { get; } = [];

        public bool IsEmpty => Steam.Count == 0 && Gog.Count == 0 && Ea.Count == 0 && Epic.Count == 0 && Ubisoft.Count == 0 && Xbox.Count == 0 && BattleNet.Count == 0;

        // Every exe path that led to a given platform record ("Steam|440" -> {the dropped exe,
        // the record's own exe}) - so an existing Local entry can be matched by whichever exe
        // it was originally added from, not only the platform's registered one.
        private readonly Dictionary<string, HashSet<string>> _sourcePaths = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> SourcePathsFor(string platform, string id)
            => _sourcePaths.TryGetValue($"{platform}|{id}", out var set) ? set : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void Remember(string platform, string id, string? recordExe, string sourceExe)
        {
            string key = $"{platform}|{id}";
            if (!_sourcePaths.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _sourcePaths[key] = set;
            }
            if (!string.IsNullOrWhiteSpace(recordExe)) set.Add(recordExe);
            if (!string.IsNullOrWhiteSpace(sourceExe)) set.Add(sourceExe);
        }

        /// <param name="sourceExePath">The exe the user actually dropped/the folder scan chose -
        /// substituted only when the platform record has no exe of its own, so the entry always
        /// has something to track/launch.</param>
        public void Add(PlatformMatch match, string sourceExePath)
        {
            if (match.Steam is { } s)
            {
                Remember("Steam", s.AppId, s.ExePath, sourceExePath);
                if (!Steam.Any(x => x.AppId.Equals(s.AppId, StringComparison.OrdinalIgnoreCase)))
                    Steam.Add(s.ExePath == null ? s with { ExePath = sourceExePath, IconPath = s.IconPath ?? sourceExePath } : s);
            }
            else if (match.Gog is { } g)
            {
                Remember("GOG", g.GameId, g.ExePath, sourceExePath);
                if (!Gog.Any(x => x.GameId.Equals(g.GameId, StringComparison.OrdinalIgnoreCase)))
                    Gog.Add(g.ExePath == null ? g with { ExePath = sourceExePath, IconPath = g.IconPath ?? sourceExePath } : g);
            }
            else if (match.Ea is { } e)
            {
                Remember("EA", e.ContentId, e.ExePath, sourceExePath);
                if (!Ea.Any(x => x.ContentId.Equals(e.ContentId, StringComparison.OrdinalIgnoreCase)))
                    Ea.Add(e.ExePath == null ? e with { ExePath = sourceExePath, IconPath = e.IconPath ?? sourceExePath } : e);
            }
            else if (match.Epic is { } p)
            {
                Remember("Epic", p.AppName, p.ExePath, sourceExePath);
                if (!Epic.Any(x => x.AppName.Equals(p.AppName, StringComparison.OrdinalIgnoreCase)))
                    Epic.Add(p.ExePath == null ? p with { ExePath = sourceExePath, IconPath = p.IconPath ?? sourceExePath } : p);
            }
            else if (match.Ubisoft is { } u)
            {
                Remember("Ubisoft", u.GameId, u.ExePath, sourceExePath);
                if (!Ubisoft.Any(x => x.GameId.Equals(u.GameId, StringComparison.OrdinalIgnoreCase)))
                    Ubisoft.Add(u.ExePath == null ? u with { ExePath = sourceExePath, IconPath = u.IconPath ?? sourceExePath } : u);
            }
            else if (match.Xbox is { } x)
            {
                Remember("Xbox", x.Aumid, x.ExePath, sourceExePath);
                if (!Xbox.Any(g => g.Aumid.Equals(x.Aumid, StringComparison.OrdinalIgnoreCase)))
                    Xbox.Add(x.ExePath == null ? x with { ExePath = sourceExePath, IconPath = x.IconPath ?? sourceExePath } : x);
            }
            else if (match.BattleNet is { } b)
            {
                Remember("Battle.net", b.Uid, b.ExePath, sourceExePath);
                if (!BattleNet.Any(g => g.Uid.Equals(b.Uid, StringComparison.OrdinalIgnoreCase)))
                    BattleNet.Add(b.ExePath == null ? b with { ExePath = sourceExePath, IconPath = b.IconPath ?? sourceExePath } : b);
            }
        }
    }

    /// <summary>Outcome of <see cref="ImportPlatformBucketsAsync"/>.</summary>
    /// <param name="Added">Games actually added across all platforms, or -1 if any leg threw.</param>
    /// <param name="Upgraded">Existing Local entries for the same exe that were linked to their
    /// platform in place (see <see cref="UpgradeExistingEntries"/>) instead of being duplicated.</param>
    /// <param name="Description">e.g. "2 via Steam, 1 linked to GOG" (counting only games actually
    /// added or linked, so a duplicate isn't reported as imported). Empty when nothing changed.</param>
    private readonly record struct PlatformImportResult(int Added, int Upgraded, string Description);

    /// <summary>File types <see cref="HandleFileDropAsync"/> accepts. Matches the Add Game dialog's
    /// primary filter; its "All Files" escape hatch also lands here and is refused the same way.</summary>
    private static readonly string[] DroppableExtensions = [".exe", ".lnk", ".url"];

    /// <summary>
    /// Reasons a resolved shortcut/exe must not become a library entry, or null if it's fine:
    /// a URL with a scheme outside <see cref="UrlProtocolHelper.IsAllowedLaunchUrl"/>'s allow-list,
    /// a UNC/network target, or a local target that doesn't exist.
    /// </summary>
    private static string? ValidateDroppedTarget(ShortcutResolution shortcut)
    {
        string target = shortcut.TargetPath;
        if (string.IsNullOrWhiteSpace(target)) return "the shortcut has no target";

        if (ProcessLauncherService.IsNonFileProtocolUrl(target))
        {
            return UrlProtocolHelper.IsAllowedLaunchUrl(target)
                ? null
                : "its link type isn't a supported game launcher (only Steam, Epic, EA, Ubisoft, GOG Galaxy and web links are allowed)";
        }

        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal))
            return "it points at a network location - copy the game locally first";

        if (!File.Exists(target)) return "its target file doesn't exist";

        return null;
    }

    /// <summary>
    /// The manual routes' counterpart of Scan for Games' existingExePaths check: a game that is
    /// already in the library as a plain Local entry (added before the platform lookup existed, or
    /// by a route it couldn't resolve) must not become a second card. Instead the existing entry
    /// is tagged with the platform's ID and flag in place - keeping the user's name, category,
    /// hotkey, scripts, playtime and artwork - and dropped from the bucket. A Steam record also
    /// matches a Local entry that already carries the same SteamAppId from online title matching.
    /// </summary>
    /// <returns>How many existing entries were linked.</returns>
    private int UpgradeExistingEntries(PlatformImportBuckets buckets)
    {
        int upgraded = 0;

        upgraded += UpgradeBucket(buckets.Steam, "Steam", d => d.AppId,
            (card, d) => !card.Game.IsSteamGame && string.Equals(card.Game.SteamAppId, d.AppId, StringComparison.OrdinalIgnoreCase),
            (game, d) =>
            {
                game.IsSteamGame = true;
                game.ImportedFrom = LauncherPlatform.Steam;
                game.SteamAppId = d.AppId;
                if (string.IsNullOrWhiteSpace(game.WorkingDirectory)) game.WorkingDirectory = d.InstallDir;
                if (game.Category == LibraryConstants.Uncategorized) game.Category = LibraryConstants.SteamCategory;
            });

        upgraded += UpgradeBucket(buckets.Gog, "GOG", d => d.GameId, null,
            (game, d) =>
            {
                game.IsGogGame = true;
                game.ImportedFrom = LauncherPlatform.Gog;
                game.GogGameId = d.GameId;
                if (string.IsNullOrWhiteSpace(game.Arguments) && d.LaunchParam != null) game.Arguments = d.LaunchParam;
                if (string.IsNullOrWhiteSpace(game.WorkingDirectory)) game.WorkingDirectory = d.InstallDir;
                if (game.Category == LibraryConstants.Uncategorized) game.Category = LibraryConstants.GogCategory;
            });

        upgraded += UpgradeBucket(buckets.Ea, "EA", d => d.ContentId, null,
            (game, d) =>
            {
                game.IsEaGame = true;
                game.ImportedFrom = LauncherPlatform.Ea;
                game.EaContentId = d.ContentId;
                if (string.IsNullOrWhiteSpace(game.WorkingDirectory)) game.WorkingDirectory = d.InstallDir;
                if (game.Category == LibraryConstants.Uncategorized) game.Category = LibraryConstants.EaCategory;
            });

        upgraded += UpgradeBucket(buckets.Epic, "Epic", d => d.AppName, null,
            (game, d) =>
            {
                game.IsEpicGame = true;
                game.ImportedFrom = LauncherPlatform.Epic;
                game.EpicAppName = d.AppName;
                if (string.IsNullOrWhiteSpace(game.WorkingDirectory)) game.WorkingDirectory = d.InstallDir;
                if (game.Category == LibraryConstants.Uncategorized) game.Category = LibraryConstants.EpicCategory;
            });

        upgraded += UpgradeBucket(buckets.Ubisoft, "Ubisoft", d => d.GameId, null,
            (game, d) =>
            {
                game.IsUbisoftGame = true;
                game.ImportedFrom = LauncherPlatform.Ubisoft;
                game.UbisoftGameId = d.GameId;
                if (string.IsNullOrWhiteSpace(game.WorkingDirectory)) game.WorkingDirectory = d.InstallDir;
                if (game.Category == LibraryConstants.Uncategorized) game.Category = LibraryConstants.UbisoftCategory;
            });

        upgraded += UpgradeBucket(buckets.Xbox, "Xbox", d => d.Aumid, null,
            (game, d) =>
            {
                game.IsXboxGame = true;
                game.ImportedFrom = LauncherPlatform.Xbox;
                game.XboxAumid = d.Aumid;
                if (string.IsNullOrWhiteSpace(game.WorkingDirectory)) game.WorkingDirectory = d.InstallDir;
                // No "Xbox" placeholder category (see ImportXboxGamesAsync) - leave it Uncategorized.
            });

        upgraded += UpgradeBucket(buckets.BattleNet, "Battle.net", d => d.Uid, null,
            (game, d) =>
            {
                game.IsBattleNetGame = true;
                game.ImportedFrom = LauncherPlatform.BattleNet;
                game.BattleNetUid = d.Uid;
                if (!string.IsNullOrWhiteSpace(d.ProgramId)) game.BattleNetProgramId = d.ProgramId;
                // A Battle.net game can't launch directly (see GameEntry.IsBattleNetGame), so a
                // Local entry's "launch directly" choice no longer applies.
                game.LaunchDirectly = false;
                if (string.IsNullOrWhiteSpace(game.WorkingDirectory)) game.WorkingDirectory = d.InstallDir;
                if (game.Category == LibraryConstants.Uncategorized) game.Category = LibraryConstants.BattleNetCategory;
            });

        if (upgraded > 0)
        {
            _library.RebuildCategories();
            // Joins the caller's commit batch, so the legs that follow share its one save.
            PersistCommittedLibrary();
            _library.ApplySort();
        }
        return upgraded;

        int UpgradeBucket<T>(List<T> bucket, string platform, Func<T, string> id, Func<GameCardViewModel, T, bool>? extraMatch, Action<GameEntry, T> apply)
        {
            int count = 0;
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                var record = bucket[i];
                var paths = buckets.SourcePathsFor(platform, id(record));
                var card = _library.Games.FirstOrDefault(g =>
                    !g.IsLocalGame ? false
                    : paths.Contains(g.Game.ExecutablePath) || (extraMatch?.Invoke(g, record) ?? false));
                if (card == null) continue;

                apply(card.Game, record);
                card.RefreshProperties();
                bucket.RemoveAt(i);
                count++;
                LoggingService.Info("ImportCoordinator", $"Linked existing Local entry '{card.Name}' ({card.Game.ExecutablePath}) to {platform} game ID {id(record)} instead of adding a duplicate.");
            }
            return count;
        }
    }

    /// <summary>
    /// Splits folder-scan candidates into launcher-owned ones (bucketed by platform) and genuinely
    /// local ones. Runs the platform lookups, so call it off the UI thread.
    /// </summary>
    private (PlatformImportBuckets Platform, List<GameCandidate> Local) PartitionByPlatform(List<GameCandidate> candidates)
    {
        var buckets = new PlatformImportBuckets();
        var local = new List<GameCandidate>(candidates.Count);
        var index = _platformLookup.CreateIndex();

        foreach (var candidate in candidates)
        {
            var match = candidate.Platform ?? index.Match(candidate.ExePath);
            if (match != null)
            {
                LoggingService.Info("ImportCoordinator", $"Candidate '{candidate.ExePath}' resolved to {match.Platform} game '{match.Name}' - importing via {match.Platform}.");
                buckets.Add(match, candidate.ExePath);
            }
            else
            {
                local.Add(candidate);
            }
        }

        return (buckets, local);
    }

    /// <summary>
    /// Runs each non-empty bucket through its platform's own import (quietly - no per-leg status
    /// messages; the caller composes one from the returned description).
    /// </summary>
    private async Task<PlatformImportResult> ImportPlatformBucketsAsync(PlatformImportBuckets buckets)
    {
        // One save and one hotkey rebuild for the upgrades and every leg, not one each.
        using var commitBatch = BeginCommitBatch();

        // Existing Local entries for the same exe are linked in place and leave the buckets
        // before any leg runs, so they can't be duplicated by the ID-only dedup in the legs.
        var linked = new Dictionary<string, int>();
        int upgraded = 0;
        foreach (var (platform, before) in new[] { ("Steam", buckets.Steam.Count), ("GOG", buckets.Gog.Count), ("EA", buckets.Ea.Count), ("Epic", buckets.Epic.Count), ("Ubisoft", buckets.Ubisoft.Count), ("Xbox", buckets.Xbox.Count), ("Battle.net", buckets.BattleNet.Count) })
        {
            linked[platform] = before;
        }
        upgraded = UpgradeExistingEntries(buckets);
        linked["Steam"] -= buckets.Steam.Count;
        linked["GOG"] -= buckets.Gog.Count;
        linked["EA"] -= buckets.Ea.Count;
        linked["Epic"] -= buckets.Epic.Count;
        linked["Ubisoft"] -= buckets.Ubisoft.Count;
        linked["Xbox"] -= buckets.Xbox.Count;
        linked["Battle.net"] -= buckets.BattleNet.Count;

        int steamAdded = buckets.Steam.Count > 0 ? await ImportSteamGamesAsync(buckets.Steam, announceProgress: false) : 0;
        int gogAdded = buckets.Gog.Count > 0 ? await ImportGogGamesAsync(buckets.Gog, announceProgress: false) : 0;
        int eaAdded = buckets.Ea.Count > 0 ? await ImportEaGamesAsync(buckets.Ea, announceProgress: false) : 0;
        int epicAdded = buckets.Epic.Count > 0 ? await ImportEpicGamesAsync(buckets.Epic, announceProgress: false) : 0;
        int ubisoftAdded = buckets.Ubisoft.Count > 0 ? await ImportUbisoftGamesAsync(buckets.Ubisoft, announceProgress: false) : 0;
        int xboxAdded = buckets.Xbox.Count > 0 ? await ImportXboxGamesAsync(buckets.Xbox, announceProgress: false) : 0;
        int battleNetAdded = buckets.BattleNet.Count > 0 ? await ImportBattleNetGamesAsync(buckets.BattleNet, announceProgress: false) : 0;

        if (steamAdded < 0 || gogAdded < 0 || eaAdded < 0 || epicAdded < 0 || ubisoftAdded < 0 || xboxAdded < 0 || battleNetAdded < 0)
        {
            return new PlatformImportResult(-1, upgraded, string.Empty);
        }

        var parts = new List<string>(10);
        void Describe(string platform, int added)
        {
            if (added > 0) parts.Add($"{added} via {platform}");
            if (linked[platform] > 0) parts.Add($"{linked[platform]} linked to {platform}");
        }
        Describe("Steam", steamAdded);
        Describe("GOG", gogAdded);
        Describe("EA", eaAdded);
        Describe("Epic", epicAdded);
        Describe("Ubisoft", ubisoftAdded);
        Describe("Xbox", xboxAdded);
        Describe("Battle.net", battleNetAdded);

        return new PlatformImportResult(steamAdded + gogAdded + eaAdded + epicAdded + ubisoftAdded + xboxAdded + battleNetAdded, upgraded, string.Join(", ", parts));
    }

    private void AddGameBrowse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Game - Select Executable or Shortcut",
            Filter = "Games & Shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            HandleFileDrop(dialog.FileNames);
        }
    }

    private void AddGameFolderBrowse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add Game - Select Game Folder",
            Multiselect = false
        };

        if (FileDialogCloak.Show(dialog) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ProcessFolderAdd(dialog.FolderName);
        }
    }

    /// <summary>
    /// Single-click "Scan for Games" entry point. The very first time this is ever pressed
    /// (tracked by AppSettings.HasSeenLauncherDetectionPrompt, and only for a real button press -
    /// see the <paramref name="silent"/> guard below), it detects which launchers actually have
    /// games installed and, if any do, hands off to <see cref="RequestLauncherDetectionPrompt"/>
    /// instead of scanning immediately - the view shows a picker letting the user choose which of
    /// them to enable, then calls back into <see cref="CompleteFirstTimeLauncherDetection"/> to
    /// continue. Every later press (and this one too, if nothing was detected) goes straight to
    /// <see cref="ScanForGamesCoreAsync"/>.
    /// </summary>
    /// <param name="silent">
    /// True for the automatic startup run (see AppSettings.AutoScanForGamesOnStartup) - skips both
    /// the first-time launcher-detection prompt (that's reserved for an explicit button press) and
    /// the "No Scan Locations" prompt, instead of greeting the user with a dialog the moment the
    /// app opens. The install prompt still opens normally if the scan actually finds something.
    /// </param>
    public async Task ScanForGamesAsync(bool silent = false)
    {
        if (!silent && !_settings.HasSeenLauncherDetectionPrompt)
        {
            var detected = DetectInstalledLaunchers();
            if (detected.Count > 0)
            {
                // Marked seen before the prompt is shown - same reasoning as the app's other
                // one-time prompts (see MainWindow.MaybeShowWelcomePrompt): a crash mid-dialog,
                // or the user just declining, must not cause this to ask again on the next
                // press. But only once there's something to show: a user who pressed Scan
                // before installing any launcher still gets the picker when they later do.
                _settings.HasSeenLauncherDetectionPrompt = true;
                _storageService.SaveSettings(_settings);

                RequestLauncherDetectionPrompt?.Invoke(detected);
                return;
            }
        }

        await ScanForGamesCoreAsync(silent);
    }

}
