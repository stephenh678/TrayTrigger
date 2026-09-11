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
public class ImportCoordinator : ViewModelBase
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
    private readonly PlatformLookupService _platformLookup;
    private readonly SteamSearchService _steamSearchService;
    private readonly SteamMetadataService _steamMetadataService;
    private readonly StorageService _storageService;
    private readonly AppSettings _settings;
    private readonly Func<string?> _getSteamGridDbApiKeyOrNull;

    private const int MaxConcurrentEnrichments = 4;
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

    public event Action<List<DiscoveredSteamGame>, List<DiscoveredGogGame>, List<DiscoveredEaGame>, List<DiscoveredEpicGame>, List<DiscoveredUbisoftGame>, List<DiscoveredXboxGame>, List<GameCandidate>>? RequestScanResultsPicker;
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
        _steamScannerService = steamScannerService;
        _gogScannerService = gogScannerService;
        _eaScannerService = eaScannerService;
        _epicScannerService = epicScannerService;
        _ubisoftScannerService = ubisoftScannerService;
        _xboxScannerService = xboxScannerService;
        _platformLookup = new PlatformLookupService(steamScannerService, gogScannerService, eaScannerService, epicScannerService, ubisoftScannerService, xboxScannerService);
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
    // PerformanceProfile is set explicitly here rather than relying on GameEntry's own default:
    // this method only ever runs for a game newly added through the app. An existing game loaded
    // from an older games.json (from before Performance Profiles existed) has no serialized value
    // for that property and must fall back to GameEntry's class default instead - which stays Off,
    // so upgrading never silently opts already-installed games into system tweaks.
    private async Task FinalizeNewEntryAsync(GameEntry entry, string iconSourcePath)
    {
        entry.IconPath = _iconExtractorService.ExtractAndCacheIcon(entry.Id, iconSourcePath, entry.Name);
        entry.PerformanceProfile = PerformanceProfileMode.Optimized;
        await _library.EnrichGameWithSteamMetadataAsync(entry);
    }

    // Shared commit step of every import pipeline: add the prepared entries to the visible
    // library and refresh everything that depends on it. See L-12.
    private void CommitImportedEntries(IEnumerable<GameEntry> entries, string? statusMessage)
    {
        foreach (var entry in entries)
        {
            _library.Games.Add(_library.CreateCardViewModel(entry));
        }

        _library.RebuildCategories();
        _library.SaveLibrary();
        _library.UpdateHotkeys();
        _library.ApplySort();
        if (statusMessage != null)
        {
            _library.AnnounceImportResult(statusMessage);
        }
        _library.NotifyGameCountChanged();
    }

    /// <summary>
    /// Force-refreshes poster art for every game with a known Steam AppId, bypassing the
    /// "already has a cover" cache check so a game stuck on the composited-banner fallback
    /// tier can pick up better art (e.g. after the user adds/enables a SteamGridDB API key).
    /// Unlike <see cref="EnrichLibraryAsync"/>, this ignores existing category/cover state and
    /// always re-fetches, since the whole point is to override a previously-cached poster.
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
            Report("A poster refresh is already in progress. Please wait for it to finish.");
            return;
        }

        var candidates = _library.Games.Where(card => !string.IsNullOrWhiteSpace(card.Game.SteamAppId)).ToList();
        if (candidates.Count == 0)
        {
            Report("No games with a linked Steam AppId to refresh.");
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

    public async Task EnrichLibraryAsync()
    {
        if (!_settings.AutoCategorizeFromSteam && !_settings.SearchOfficialTitleOnline)
            return;

        // Settings toggles, library load, and post-import enrichment can all request this
        // around the same time; let the in-flight run finish rather than starting a
        // duplicate pass over the same candidates.
        if (_isEnrichmentInProgress)
            return;

        _isEnrichmentInProgress = true;
        try
        {
            var candidates = _library.Games
                .Where(card =>
                    (LibraryConstants.IsEnrichableCategory(card.Game.Category) || string.IsNullOrWhiteSpace(card.Game.CoverImagePath) || string.IsNullOrWhiteSpace(card.Game.SteamAppId)) &&
                    (card.Game.LastEnrichmentAttemptUtc == null || DateTime.UtcNow - card.Game.LastEnrichmentAttemptUtc.Value >= EnrichmentRetryInterval))
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

    public void HandleFileDrop(string[] files) => _ = HandleFileDropAsync(files);

    public async Task HandleFileDropAsync(string[] files)
    {
        if (files == null || files.Length == 0) return;

        // Folder drops go through ProcessFolderAdd, which can show a modal picker/batch-import
        // dialog and then calls AddCandidateAsync/ImportBatchGamesAsync - both of which check
        // _isImportInProgress themselves. Handle folders in their own pass, outside the guard
        // below, so that nested call doesn't see "already in progress" (held by this very method)
        // and silently no-op instead of actually adding the game.
        var folders = files.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)).ToList();
        var nonFolderFiles = files.Where(f => !string.IsNullOrWhiteSpace(f) && !Directory.Exists(f)).ToArray();

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return;
        }

        // Dropped exes/shortcuts that turn out to live inside a Steam/GOG/EA/Epic/Ubisoft install
        // are collected here and imported through that platform's own route once the guard is
        // released (the platform import methods take the guard themselves).
        var platformDrops = new PlatformImportBuckets();
        int localAdded = 0;
        // Files refused by the validation below, with the reason - reported once, after the loop.
        var skipped = new List<string>();

        _isImportInProgress = true;
        try
        {
            var addedEntries = new List<GameEntry>();
            var lookupIndex = _platformLookup.CreateIndex();
            foreach (var file in nonFolderFiles)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;

                if (!File.Exists(file)) continue;

                // Only executables and shortcuts become library entries. Anything else (.bat,
                // .ps1, .hta, .scr, ...) would still be launched through the shell's file
                // association, so a dropped file is not allowed to smuggle a script in as a
                // "game" with an extracted icon and a fetched title.
                string ext = Path.GetExtension(file);
                if (!DroppableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    skipped.Add($"{Path.GetFileName(file)} - not an .exe, .lnk or .url");
                    LoggingService.Warn("ImportCoordinator", $"Skipped dropped file '{file}': extension '{ext}' is not importable.");
                    continue;
                }

                try
                {
                    var shortcut = _shortcutService.Resolve(file);
                    string entryName = shortcut.Name;
                    string? onlineAppId = shortcut.SteamAppId;

                    // A shortcut's target is untrusted input: a .url can name any URL scheme and
                    // a .lnk any target, and both are launched through ShellExecute later.
                    string? targetProblem = ValidateDroppedTarget(shortcut);
                    if (targetProblem != null)
                    {
                        skipped.Add($"{Path.GetFileName(file)} - {targetProblem}");
                        LoggingService.Warn("ImportCoordinator", $"Skipped dropped file '{file}': {targetProblem} (target '{shortcut.TargetPath}').");
                        continue;
                    }

                    if (!shortcut.IsSteamUrl)
                    {
                        var platformMatch = await Task.Run(() => lookupIndex.Match(shortcut.TargetPath));
                        if (platformMatch != null)
                        {
                            LoggingService.Info("ImportCoordinator", $"Dropped '{file}' resolved to {platformMatch.Platform} game '{platformMatch.Name}' - importing via {platformMatch.Platform}.");
                            UnignoreForExplicitDrop(shortcut.TargetPath, platformMatch);
                            platformDrops.Add(platformMatch, shortcut.TargetPath);
                            continue;
                        }
                    }

                    UnignoreForExplicitDrop(shortcut.TargetPath, null);

                    var existingDuplicate = _library.FindDuplicateGame(shortcut.TargetPath, shortcut.SteamAppId);
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
                            continue;
                        }
                    }

                    if (_settings.SearchOfficialTitleOnline && !shortcut.IsSteamUrl)
                    {
                        var res = await GameNameExtractor.ResolveGameMatchAsync(
                            shortcut.TargetPath,
                            shortcut.WorkingDirectory,
                            preferExe: _settings.PreferExeForGameName,
                            searchOnline: true,
                            steamSearch: _steamSearchService,
                            minConfidence: _settings.OnlineMatchConfidenceThreshold);

                        if (!string.IsNullOrWhiteSpace(res.ResolvedTitle))
                        {
                            entryName = res.ResolvedTitle;
                        }
                        if (!string.IsNullOrWhiteSpace(res.SteamAppId))
                        {
                            onlineAppId = res.SteamAppId;
                        }
                    }

                    var entry = new GameEntry
                    {
                        Name = entryName,
                        ExecutablePath = shortcut.TargetPath,
                        Arguments = shortcut.Arguments,
                        WorkingDirectory = shortcut.WorkingDirectory,
                        Category = _library.SelectedCategory != LibraryConstants.AllCategory ? _library.SelectedCategory : LibraryConstants.Uncategorized,
                        IsSteamGame = shortcut.IsSteamUrl,
                        SteamAppId = onlineAppId
                    };

                    // Extract & cache icon
                    string iconSource = !string.IsNullOrEmpty(shortcut.IconLocation) ? shortcut.IconLocation : shortcut.TargetPath;
                    await FinalizeNewEntryAsync(entry, iconSource);
                    addedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("ImportCoordinator", $"Failed to ingest dropped file '{file}': {ex.Message}");
                }
            }

            if (addedEntries.Count > 0)
            {
                localAdded = addedEntries.Count;
                CommitImportedEntries(addedEntries, $"Added {addedEntries.Count} new game(s) instantly!");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in HandleFileDrop", ex);
        }
        finally
        {
            _isImportInProgress = false;
        }

        // Processed after the guard above is released - see comment at the top of this method.
        if (!platformDrops.IsEmpty)
        {
            var platformResult = await ImportPlatformBucketsAsync(platformDrops);
            _library.AnnounceImportResult(platformResult.Added < 0
                ? "Import failed - check the log for details."
                : ComposeImportSummary(localAdded, platformResult, "All dropped games are already in your library."));
        }

        if (skipped.Count > 0)
        {
            Window? owner = WindowHelper.ActiveOwner();
            ModernDialog.ShowWarning(
                owner,
                skipped.Count == 1 ? "File Skipped" : "Some Files Were Skipped",
                skipped.Count == 1
                    ? "One dropped file wasn't added to the library:"
                    : $"{skipped.Count} dropped files weren't added to the library:",
                string.Join("\n", skipped.Select(s => "• " + s)) +
                "\n\nTrayTrigger only imports game executables (.exe) and shortcuts (.lnk, .url) whose target is a local program or a game-launcher link.");
        }

        if (folders.Count == 1)
        {
            await ProcessFolderAddAsync(folders[0]);
        }
        else if (folders.Count > 1)
        {
            await ProcessFolderAddBatchAsync(folders);
        }
    }

    // Scans every dropped folder up front and merges the results into a single batch-import
    // prompt, instead of prompting once per folder (see ProcessFolderAdd, which still owns the
    // single-folder path so its "no games" / "one game" / "overwhelming match" shortcuts are
    // unaffected).
    public async Task ProcessFolderAddBatchAsync(List<string> folderPaths)
    {
        _library.StatusMessage = "Scanning folders...";
        var aggregated = new List<GameCandidate>();
        var lookupIndex = _platformLookup.CreateIndex();

        foreach (var folderPath in folderPaths)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) continue;

            var scanResult = await Task.Run(() => ResolveAndFilterIgnored(_folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName), lookupIndex));

            if (scanResult.IsMultiGameLibrary)
            {
                aggregated.AddRange(FilterIgnored(scanResult.DiscoveredGames));
            }
            else
            {
                // Filter ignored candidates before picking the single best-scoring one, not
                // after - otherwise an ignored candidate that happens to score highest silently
                // drops this whole folder instead of falling back to the next-best real one.
                var fresh = FilterIgnored(scanResult.SingleGameCandidates).ToList();
                if (fresh.Count > 0)
                {
                    aggregated.Add(fresh[0]);
                }
            }
        }

        if (aggregated.Count == 0)
        {
            Window? owner = WindowHelper.ActiveOwner();
            ModernDialog.ShowInfo(
                owner,
                "No Games Found",
                "No game executables found across the dropped folders.",
                "Please ensure the selected folders contain installed games or executable files.");
            return;
        }

        if (aggregated.Count == 1)
        {
            AddCandidate(aggregated[0]);
            return;
        }

        string combinedLabel = $"{folderPaths.Count} folders";
        RequestFolderBatchImport?.Invoke(combinedLabel, aggregated);
    }

    public void ProcessFolderAdd(string folderPath) => _ = ProcessFolderAddAsync(folderPath);

    public async Task ProcessFolderAddAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        _library.StatusMessage = "Scanning folder...";
        // Platform resolution and ignore filtering happen here, before any picking, so an
        // ignored exe inside a Steam/GOG install can't hide the game the platform record names.
        var scanResult = await Task.Run(() => ResolveAndFilterIgnored(_folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName), _platformLookup.CreateIndex()));

        // A. Multi-game parent folder detected (e.g. C:\Games, D:\SteamLibrary\steamapps\common, C:\GOG Games)
        if (scanResult.IsMultiGameLibrary)
        {
            if (scanResult.DiscoveredGames.Count == 0)
            {
                Window? owner = WindowHelper.ActiveOwner();
                ModernDialog.ShowInfo(
                    owner,
                    "No Games Found",
                    $"No game executables found across subfolders in \"{Path.GetFileName(folderPath.TrimEnd('\\', '/'))}\".",
                    "Please ensure the selected directory contains installed games or executable files.");
                return;
            }

            if (scanResult.DiscoveredGames.Count == 1)
            {
                AddCandidate(scanResult.DiscoveredGames[0]);
                return;
            }

            // Launch Batch Import dialog for the multi-game folder
            RequestFolderBatchImport?.Invoke(folderPath, scanResult.DiscoveredGames);
            return;
        }

        // B. Single-game folder branch
        var candidates = scanResult.SingleGameCandidates;
        if (candidates.Count == 0)
        {
            Window? owner = WindowHelper.ActiveOwner();
            ModernDialog.ShowInfo(
                owner,
                "No Game Executable Found",
                $"No game executables found across the folder branch of \"{Path.GetFileName(folderPath.TrimEnd('\\', '/'))}\".",
                "Please ensure the selected folder contains the installed game files.");
            return;
        }

        if (candidates.Count == 1)
        {
            AddCandidate(candidates[0]);
        }
        else
        {
            // If the top candidate is an overwhelming clear match compared to runner-up, auto-add
            if (candidates[0].ConfidenceScore >= 90 && (candidates[0].ConfidenceScore - candidates[1].ConfidenceScore >= 40))
            {
                AddCandidate(candidates[0]);
            }
            else
            {
                RequestCandidatePicker?.Invoke(folderPath, candidates);
            }
        }
    }

    public void ImportBatchGames(List<GameCandidate> candidates) => _ = ImportBatchGamesAsync(candidates);

    /// <param name="announceProgress">
    /// False when this is one leg of a combined import (see ImportScanResultsAsync) - suppresses
    /// this method's own progress/completion status messages so a batch spanning multiple sources
    /// doesn't overwrite the caller's single running total mid-way (e.g. "Importing 1..." then
    /// "Importing 2..." instead of one steady "Importing 3...").
    /// </param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportBatchGamesAsync(List<GameCandidate> candidates, bool announceProgress = true)
    {
        if (candidates == null || candidates.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        // Candidates came from the generic folder heuristics; any that actually sit inside a
        // launcher-owned install go through that platform's own import instead (see
        // PlatformLookupService). Partitioned per candidate, so a mixed folder resolves correctly.
        if (announceProgress)
        {
            _library.StatusMessage = "Checking for launcher-owned games...";
        }
        var (platformBuckets, localCandidates) = await Task.Run(() => PartitionByPlatform(candidates));

        if (platformBuckets.IsEmpty)
        {
            return await ImportLocalCandidatesAsync(localCandidates, announceProgress);
        }

        if (announceProgress)
        {
            _library.StatusMessage = $"Importing {candidates.Count} game(s)...";
        }

        int localAdded = localCandidates.Count > 0 ? await ImportLocalCandidatesAsync(localCandidates, announceProgress: false) : 0;
        var platformResult = await ImportPlatformBucketsAsync(platformBuckets);

        if (localAdded < 0 || platformResult.Added < 0)
        {
            if (announceProgress)
            {
                int addedSoFar = Math.Max(localAdded, 0) + Math.Max(platformResult.Added, 0);
                _library.StatusMessage = addedSoFar > 0
                    ? $"Imported {addedSoFar} game(s), but part of the import failed - check the log for details."
                    : "Import failed - check the log for details.";
            }
            return -1;
        }

        int total = localAdded + platformResult.Added + platformResult.Upgraded;
        if (announceProgress)
        {
            _library.AnnounceImportResult(ComposeImportSummary(localAdded, platformResult, "All selected games are already in your library."));
        }
        return total;
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
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            // Avoid duplicates
            var toProcess = candidates
                .Where(c => !_library.Games.Any(g => g.Game.ExecutablePath.Equals(c.ExePath, StringComparison.OrdinalIgnoreCase)))
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
                            preferExe: _settings.PreferExeForGameName,
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
                        Category = _library.SelectedCategory != LibraryConstants.AllCategory ? _library.SelectedCategory : LibraryConstants.Uncategorized,
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
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
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
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
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
                    preferExe: _settings.PreferExeForGameName,
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
                Category = _library.SelectedCategory != LibraryConstants.AllCategory ? _library.SelectedCategory : LibraryConstants.Uncategorized,
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

        public bool IsEmpty => Steam.Count == 0 && Gog.Count == 0 && Ea.Count == 0 && Epic.Count == 0 && Ubisoft.Count == 0 && Xbox.Count == 0;

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

        if (upgraded > 0)
        {
            _library.RebuildCategories();
            _library.SaveLibrary();
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
        // Existing Local entries for the same exe are linked in place and leave the buckets
        // before any leg runs, so they can't be duplicated by the ID-only dedup in the legs.
        var linked = new Dictionary<string, int>();
        int upgraded = 0;
        foreach (var (platform, before) in new[] { ("Steam", buckets.Steam.Count), ("GOG", buckets.Gog.Count), ("EA", buckets.Ea.Count), ("Epic", buckets.Epic.Count), ("Ubisoft", buckets.Ubisoft.Count), ("Xbox", buckets.Xbox.Count) })
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

        int steamAdded = buckets.Steam.Count > 0 ? await ImportSteamGamesAsync(buckets.Steam, announceProgress: false) : 0;
        int gogAdded = buckets.Gog.Count > 0 ? await ImportGogGamesAsync(buckets.Gog, announceProgress: false) : 0;
        int eaAdded = buckets.Ea.Count > 0 ? await ImportEaGamesAsync(buckets.Ea, announceProgress: false) : 0;
        int epicAdded = buckets.Epic.Count > 0 ? await ImportEpicGamesAsync(buckets.Epic, announceProgress: false) : 0;
        int ubisoftAdded = buckets.Ubisoft.Count > 0 ? await ImportUbisoftGamesAsync(buckets.Ubisoft, announceProgress: false) : 0;
        int xboxAdded = buckets.Xbox.Count > 0 ? await ImportXboxGamesAsync(buckets.Xbox, announceProgress: false) : 0;

        if (steamAdded < 0 || gogAdded < 0 || eaAdded < 0 || epicAdded < 0 || ubisoftAdded < 0 || xboxAdded < 0)
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

        return new PlatformImportResult(steamAdded + gogAdded + eaAdded + epicAdded + ubisoftAdded + xboxAdded, upgraded, string.Join(", ", parts));
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
            if (!silent) _library.StatusMessage = "A scan is already in progress. Please wait for it to finish.";
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
        if (!anySteamLibrary && !anyManualFolder && !_settings.GogIntegrationEnabled && !_settings.EaIntegrationEnabled && !_settings.EpicIntegrationEnabled && !_settings.UbisoftIntegrationEnabled && !_settings.XboxIntegrationEnabled)
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

            var existingAppIds = _library.Games
                .Where(g => g.IsSteamGame && !string.IsNullOrEmpty(g.Game.SteamAppId))
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
            var existingExePaths = new HashSet<string>(_library.Games.Select(g => g.Game.ExecutablePath), StringComparer.OrdinalIgnoreCase);
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

            var steamLocations = _settings.SteamIntegrationEnabled
                ? enabledLocations.Where(l => l.Source == ScanLocationSource.Steam).ToList()
                : new List<ScanLocation>();
            var folderLocations = enabledLocations.Where(l => l.Source != ScanLocationSource.Steam).ToList();
            bool gogEnabled = _settings.GogIntegrationEnabled;
            bool eaEnabled = _settings.EaIntegrationEnabled;
            bool epicEnabled = _settings.EpicIntegrationEnabled;
            bool ubisoftEnabled = _settings.UbisoftIntegrationEnabled;
            bool xboxEnabled = _settings.XboxIntegrationEnabled;

            // Each platform's scan hits its own independent registry/filesystem locations, so
            // they run concurrently rather than one after another.
            var steamTask = Task.Run(() =>
            {
                var steamResults = new List<DiscoveredSteamGame>();
                if (steamLocations.Count > 0)
                {
                    string? steamPath = _steamScannerService.GetSteamInstallPath();
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        // The exe-path fallback mirrors the GOG/EA/Epic/Ubisoft tasks below: a
                        // Steam game that reached the library without a SteamAppId (added by
                        // some route the platform lookup couldn't resolve) must not be offered
                        // again as "new" - selecting it would create a second row for the same exe.
                        steamResults = _steamScannerService
                            .ScanInstalledGames(steamLocations.Select(l => l.Path), steamPath, existingAppIds)
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
            var gogTask = Task.Run(() => gogEnabled
                ? _gogScannerService.ScanInstalledGames(existingGogGameIds)
                    .Where(g => !g.IsAlreadyImported && !ignoredGogGameIds.Contains(g.GameId) &&
                                (g.ExePath == null || !existingExePaths.Contains(g.ExePath)))
                    .ToList()
                : new List<DiscoveredGogGame>());

            // Same reasoning as GOG above - EA has no scan-location concept either.
            var eaTask = Task.Run(() => eaEnabled
                ? _eaScannerService.ScanInstalledGames(existingEaContentIds)
                    .Where(g => !g.IsAlreadyImported && !ignoredEaContentIds.Contains(g.ContentId) &&
                                (g.ExePath == null || !existingExePaths.Contains(g.ExePath)))
                    .ToList()
                : new List<DiscoveredEaGame>());

            // Same reasoning as GOG/EA above - Epic has no scan-location concept either.
            var epicTask = Task.Run(() => epicEnabled
                ? _epicScannerService.ScanInstalledGames(existingEpicAppNames)
                    .Where(g => !g.IsAlreadyImported && !ignoredEpicAppNames.Contains(g.AppName) &&
                                (g.ExePath == null || !existingExePaths.Contains(g.ExePath)))
                    .ToList()
                : new List<DiscoveredEpicGame>());

            // Same reasoning as GOG/EA/Epic above - Ubisoft has no scan-location concept either.
            var ubisoftTask = Task.Run(() => ubisoftEnabled
                ? _ubisoftScannerService.ScanInstalledGames(existingUbisoftGameIds)
                    .Where(g => !g.IsAlreadyImported && !ignoredUbisoftGameIds.Contains(g.GameId) &&
                                (g.ExePath == null || !existingExePaths.Contains(g.ExePath)))
                    .ToList()
                : new List<DiscoveredUbisoftGame>());

            // Same reasoning again - Xbox games are wherever the Xbox app put them, and Gaming
            // Services' registry knows each one.
            var xboxTask = Task.Run(() => xboxEnabled
                ? _xboxScannerService.ScanInstalledGames(existingXboxAumids)
                    .Where(g => !g.IsAlreadyImported && !ignoredXboxAumids.Contains(g.Aumid) &&
                                (g.ExePath == null || !existingExePaths.Contains(g.ExePath)))
                    .ToList()
                : new List<DiscoveredXboxGame>());

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
            }

            var folderTask = Task.Run(() =>
            {
                var folderResults = new List<GameCandidate>();
                var ignoredExePaths = BuildIgnoredExePathSet();
                var ignoredPlatformKeys = BuildIgnoredPlatformKeySet();
                var index = _platformLookup.CreateIndex();
                foreach (var loc in folderLocations)
                {
                    if (!Directory.Exists(loc.Path)) continue;

                    // A scan location's identity is already known (the user configured it as a
                    // game library), so this skips ScanFolderOrLibrary's is-this-a-library
                    // confidence gating entirely and takes every immediate subfolder's own best
                    // candidate directly - see ScanKnownLibraryLocation.
                    var candidates = FilterIgnored(
                        ResolvePlatforms(_folderScannerService.ScanKnownLibraryLocation(loc.Path, _settings.PreferExeForGameName), index),
                        ignoredExePaths, ignoredPlatformKeys);
                    folderResults.AddRange(candidates.Where(c =>
                        !existingExePaths.Contains(c.ExePath) &&
                        (c.Platform == null || !existingPlatformKeys.Contains(PlatformKey(c.Platform)))));
                }
                return folderResults;
            });

            await Task.WhenAll(steamTask, gogTask, eaTask, epicTask, ubisoftTask, xboxTask, folderTask).ConfigureAwait(true);
            var (steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, folderCandidates) =
                (steamTask.Result, gogTask.Result, eaTask.Result, epicTask.Result, ubisoftTask.Result, xboxTask.Result, folderTask.Result);

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
            folderCandidates = folderCandidates
                .Where(c => c.Platform == null || !runPlatformKeys.Contains(PlatformKey(c.Platform)))
                .DistinctBy(c => c.Platform != null ? PlatformKey(c.Platform) : c.ExePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (steamGames.Count == 0 && gogGames.Count == 0 && eaGames.Count == 0 && epicGames.Count == 0 && ubisoftGames.Count == 0 && xboxGames.Count == 0 && folderCandidates.Count == 0)
            {
                _library.AnnounceImportResult("No new games found.");
                return;
            }

            _library.StatusMessage = $"Found {steamGames.Count + gogGames.Count + eaGames.Count + epicGames.Count + ubisoftGames.Count + xboxGames.Count + folderCandidates.Count} new game(s).";
            RequestScanResultsPicker?.Invoke(steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, folderCandidates);
        }
        finally
        {
            _isScanningForGames = false;
        }
    }

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
    private static IEnumerable<GameCandidate> FilterIgnored(IEnumerable<GameCandidate> candidates, HashSet<string> ignoredExePaths, HashSet<string> ignoredPlatformKeys)
        => candidates.Where(c => c.Platform != null
            ? !ignoredPlatformKeys.Contains(PlatformKey(c.Platform))
            : !ignoredExePaths.Contains(c.ExePath));

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
                (p.XboxAumid != null && key.Equals($"Xbox|{p.XboxAumid}", StringComparison.OrdinalIgnoreCase)))));
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
    public void IgnoreSteamGame(string appId, string name)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.SteamAppId, appId, StringComparison.OrdinalIgnoreCase))) return;

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { SteamAppId = appId, Name = name });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored Steam scan candidate '{name}' (AppId {appId}) - will be excluded from future scans.");
    }

    /// <summary>Permanently excludes a GOG game ID from future "Scan for Games" results.</summary>
    public void IgnoreGogGame(string gameId, string name)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.GogGameId, gameId, StringComparison.OrdinalIgnoreCase))) return;

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { GogGameId = gameId, Name = name });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored GOG scan candidate '{name}' (GameId {gameId}) - will be excluded from future scans.");
    }

    /// <summary>Permanently excludes an EA content ID from future "Scan for Games" results.</summary>
    public void IgnoreEaGame(string contentId, string name)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.EaContentId, contentId, StringComparison.OrdinalIgnoreCase))) return;

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { EaContentId = contentId, Name = name });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored EA scan candidate '{name}' (ContentId {contentId}) - will be excluded from future scans.");
    }

    /// <summary>Permanently excludes an Epic AppName from future "Scan for Games" results.</summary>
    public void IgnoreEpicGame(string appName, string name)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.EpicAppName, appName, StringComparison.OrdinalIgnoreCase))) return;

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { EpicAppName = appName, Name = name });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored Epic scan candidate '{name}' (AppName {appName}) - will be excluded from future scans.");
    }

    /// <summary>Permanently excludes a Ubisoft GameId from future "Scan for Games" results.</summary>
    public void IgnoreUbisoftGame(string gameId, string name)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.UbisoftGameId, gameId, StringComparison.OrdinalIgnoreCase))) return;

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { UbisoftGameId = gameId, Name = name });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored Ubisoft scan candidate '{name}' (GameId {gameId}) - will be excluded from future scans.");
    }

    /// <summary>Permanently excludes an Xbox AUMID from future "Scan for Games" results.</summary>
    public void IgnoreXboxGame(string aumid, string name)
    {
        if (_settings.IgnoredGamePaths.Any(p => string.Equals(p.XboxAumid, aumid, StringComparison.OrdinalIgnoreCase))) return;

        _settings.IgnoredGamePaths.Add(new IgnoredGamePath { XboxAumid = aumid, Name = name });
        _storageService.SaveSettings(_settings);
        LoggingService.Info("ImportCoordinator", $"Ignored Xbox scan candidate '{name}' ({aumid}) - will be excluded from future scans.");
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
    public async Task ImportScanResultsAsync(List<DiscoveredSteamGame> steamGames, List<DiscoveredGogGame> gogGames, List<DiscoveredEaGame> eaGames, List<DiscoveredEpicGame> epicGames, List<DiscoveredUbisoftGame> ubisoftGames, List<DiscoveredXboxGame> xboxGames, List<GameCandidate> folderCandidates)
    {
        int total = steamGames.Count + gogGames.Count + eaGames.Count + epicGames.Count + ubisoftGames.Count + xboxGames.Count + folderCandidates.Count;
        if (total == 0) return;

        // announceProgress: false on every leg - each one's own "Importing N..."/"Imported N!"
        // status would otherwise stomp over the others' as they run one after another (e.g.
        // "Importing 1..." followed by "Importing 2...") instead of one steady running total.
        _library.StatusMessage = $"Importing {total} game(s)...";

        int steamAdded = steamGames.Count > 0 ? await ImportSteamGamesAsync(steamGames, announceProgress: false) : 0;
        int gogAdded = gogGames.Count > 0 ? await ImportGogGamesAsync(gogGames, announceProgress: false) : 0;
        int eaAdded = eaGames.Count > 0 ? await ImportEaGamesAsync(eaGames, announceProgress: false) : 0;
        int epicAdded = epicGames.Count > 0 ? await ImportEpicGamesAsync(epicGames, announceProgress: false) : 0;
        int ubisoftAdded = ubisoftGames.Count > 0 ? await ImportUbisoftGamesAsync(ubisoftGames, announceProgress: false) : 0;
        int xboxAdded = xboxGames.Count > 0 ? await ImportXboxGamesAsync(xboxGames, announceProgress: false) : 0;
        int folderAdded = folderCandidates.Count > 0 ? await ImportBatchGamesAsync(folderCandidates, announceProgress: false) : 0;

        // -1 means that leg's import threw (already logged) rather than everything just being a
        // duplicate - don't let a real failure hide behind the same reassuring "already in your
        // library" text a legitimate all-duplicates result gets.
        if (steamAdded < 0 || gogAdded < 0 || eaAdded < 0 || epicAdded < 0 || ubisoftAdded < 0 || xboxAdded < 0 || folderAdded < 0)
        {
            int addedSoFar = Math.Max(steamAdded, 0) + Math.Max(gogAdded, 0) + Math.Max(eaAdded, 0) + Math.Max(epicAdded, 0) + Math.Max(ubisoftAdded, 0) + Math.Max(xboxAdded, 0) + Math.Max(folderAdded, 0);
            _library.AnnounceImportResult(addedSoFar > 0
                ? $"Imported {addedSoFar} game(s), but part of the import failed - check the log for details."
                : "Import failed - check the log for details.");
            return;
        }

        int addedTotal = steamAdded + gogAdded + eaAdded + epicAdded + ubisoftAdded + xboxAdded + folderAdded;
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

    public void ImportSteamGames(List<DiscoveredSteamGame> discoveredGames) => _ = ImportSteamGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportSteamGamesAsync(List<DiscoveredSteamGame> discoveredGames, bool announceProgress = true)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !_library.Games.Any(g => string.Equals(g.Game.SteamAppId, d.AppId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = "All selected Steam games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} Steam game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var entry = new GameEntry
                    {
                        Name = d.Name,
                        ExecutablePath = $"steam://rungameid/{d.AppId}",
                        IsSteamGame = true,
                        ImportedFrom = LauncherPlatform.Steam,
                        SteamAppId = d.AppId,
                        Category = LibraryConstants.SteamCategory,
                        WorkingDirectory = d.InstallDir
                    };

                    string iconSource = !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty);
                    await FinalizeNewEntryAsync(entry, iconSource);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import Steam game '{d.Name}': {ex.Message}");
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
                        ? $"Imported {preparedEntries.Count} Steam game(s)! ({skippedDuplicates} already in library, skipped)"
                        : $"Imported {preparedEntries.Count} Steam game(s)!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportSteamGames", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    public void ImportGogGames(List<DiscoveredGogGame> discoveredGames) => _ = ImportGogGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportGogGamesAsync(List<DiscoveredGogGame> discoveredGames, bool announceProgress = true)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !_library.Games.Any(g => string.Equals(g.Game.GogGameId, d.GameId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = "All selected GOG games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} GOG game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var entry = new GameEntry
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
                    };

                    string iconSource = !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty);
                    await FinalizeNewEntryAsync(entry, iconSource);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import GOG game '{d.Name}': {ex.Message}");
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
                        ? $"Imported {preparedEntries.Count} GOG game(s)! ({skippedDuplicates} already in library, skipped)"
                        : $"Imported {preparedEntries.Count} GOG game(s)!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportGogGames", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    public void ImportEaGames(List<DiscoveredEaGame> discoveredGames) => _ = ImportEaGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportEaGamesAsync(List<DiscoveredEaGame> discoveredGames, bool announceProgress = true)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !_library.Games.Any(g => string.Equals(g.Game.EaContentId, d.ContentId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = "All selected EA games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} EA game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var entry = new GameEntry
                    {
                        Name = d.Name,
                        ExecutablePath = d.ExePath ?? string.Empty,
                        IsEaGame = true,
                        ImportedFrom = LauncherPlatform.Ea,
                        EaContentId = d.ContentId,
                        Category = LibraryConstants.EaCategory,
                        WorkingDirectory = d.InstallDir
                    };

                    string iconSource = !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty);
                    await FinalizeNewEntryAsync(entry, iconSource);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import EA game '{d.Name}': {ex.Message}");
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
                        ? $"Imported {preparedEntries.Count} EA game(s)! ({skippedDuplicates} already in library, skipped)"
                        : $"Imported {preparedEntries.Count} EA game(s)!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportEaGames", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    public void ImportEpicGames(List<DiscoveredEpicGame> discoveredGames) => _ = ImportEpicGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportEpicGamesAsync(List<DiscoveredEpicGame> discoveredGames, bool announceProgress = true)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !_library.Games.Any(g => string.Equals(g.Game.EpicAppName, d.AppName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = "All selected Epic games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} Epic game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var entry = new GameEntry
                    {
                        Name = d.Name,
                        ExecutablePath = d.ExePath ?? string.Empty,
                        IsEpicGame = true,
                        ImportedFrom = LauncherPlatform.Epic,
                        EpicAppName = d.AppName,
                        Category = LibraryConstants.EpicCategory,
                        WorkingDirectory = d.InstallDir
                    };

                    string iconSource = !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty);
                    await FinalizeNewEntryAsync(entry, iconSource);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import Epic game '{d.Name}': {ex.Message}");
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
                        ? $"Imported {preparedEntries.Count} Epic game(s)! ({skippedDuplicates} already in library, skipped)"
                        : $"Imported {preparedEntries.Count} Epic game(s)!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportEpicGames", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    public void ImportUbisoftGames(List<DiscoveredUbisoftGame> discoveredGames) => _ = ImportUbisoftGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportUbisoftGamesAsync(List<DiscoveredUbisoftGame> discoveredGames, bool announceProgress = true)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !_library.Games.Any(g => string.Equals(g.Game.UbisoftGameId, d.GameId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = "All selected Ubisoft games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} Ubisoft game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var entry = new GameEntry
                    {
                        Name = d.Name,
                        ExecutablePath = d.ExePath ?? string.Empty,
                        IsUbisoftGame = true,
                        ImportedFrom = LauncherPlatform.Ubisoft,
                        UbisoftGameId = d.GameId,
                        Category = LibraryConstants.UbisoftCategory,
                        WorkingDirectory = d.InstallDir
                    };

                    string iconSource = !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty);
                    await FinalizeNewEntryAsync(entry, iconSource);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import Ubisoft game '{d.Name}': {ex.Message}");
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
                        ? $"Imported {preparedEntries.Count} Ubisoft game(s)! ({skippedDuplicates} already in library, skipped)"
                        : $"Imported {preparedEntries.Count} Ubisoft game(s)!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportUbisoftGames", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    public void ImportXboxGames(List<DiscoveredXboxGame> discoveredGames) => _ = ImportXboxGamesAsync(discoveredGames);

    /// <param name="announceProgress">See the matching parameter on <see cref="ImportBatchGamesAsync"/>.</param>
    /// <returns>The number of games actually added; 0 if none (e.g. all were duplicates); -1 if the import itself threw (logged separately - callers should not treat this the same as "0, all duplicates").</returns>
    public async Task<int> ImportXboxGamesAsync(List<DiscoveredXboxGame> discoveredGames, bool announceProgress = true)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return 0;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return 0;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !_library.Games.Any(g => string.Equals(g.Game.XboxAumid, d.Aumid, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                if (announceProgress)
                {
                    _library.StatusMessage = "All selected Xbox games are already in your library.";
                }
                return 0;
            }

            if (announceProgress)
            {
                _library.StatusMessage = $"Importing {toProcess.Count} Xbox game(s)...";
            }

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();
            int failed = 0;

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var entry = new GameEntry
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
                    };

                    string iconSource = !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty);
                    await FinalizeNewEntryAsync(entry, iconSource);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    LoggingService.Warn("ImportCoordinator", $"Failed to import Xbox game '{d.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

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
                        ? $"Imported {preparedEntries.Count} Xbox game(s)! ({skippedDuplicates} already in library, skipped)"
                        : $"Imported {preparedEntries.Count} Xbox game(s)!";
                }
                CommitImportedEntries(preparedEntries, status);
            }

            return preparedEntries.Count;
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportXboxGames", ex);
            return -1;
        }
        finally
        {
            _isImportInProgress = false;
        }
    }
}
