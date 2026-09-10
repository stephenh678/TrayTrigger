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

    public event Action<List<DiscoveredSteamGame>, List<DiscoveredGogGame>, List<DiscoveredEaGame>, List<DiscoveredEpicGame>, List<DiscoveredUbisoftGame>, List<GameCandidate>>? RequestScanResultsPicker;
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
            _library.StatusMessage = statusMessage;
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
                    (card.Game.Category == LibraryConstants.Uncategorized || card.Game.Category == LibraryConstants.SteamCategory || card.Game.Category == LibraryConstants.GogCategory || card.Game.Category == LibraryConstants.EaCategory || card.Game.Category == LibraryConstants.EpicCategory || card.Game.Category == LibraryConstants.UbisoftCategory || string.IsNullOrWhiteSpace(card.Game.CoverImagePath) || string.IsNullOrWhiteSpace(card.Game.SteamAppId)) &&
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

        _isImportInProgress = true;
        try
        {
            var addedEntries = new List<GameEntry>();
            foreach (var file in nonFolderFiles)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;

                if (!File.Exists(file)) continue;

                try
                {
                    var shortcut = _shortcutService.Resolve(file);
                    string entryName = shortcut.Name;
                    string? onlineAppId = shortcut.SteamAppId;

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

        foreach (var folderPath in folderPaths)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) continue;

            var scanResult = await Task.Run(() => _folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName));

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
        var scanResult = await Task.Run(() => _folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName));
        scanResult.DiscoveredGames = FilterIgnored(scanResult.DiscoveredGames).ToList();
        scanResult.SingleGameCandidates = FilterIgnored(scanResult.SingleGameCandidates).ToList();

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
                    LoggingService.Warn("ImportCoordinator", $"Failed to import candidate '{c.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

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

    private void AddGameBrowse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Game - Select Executable or Shortcut",
            Filter = "Games & Shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
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

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
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
            // Marked seen before the prompt is even shown - same reasoning as the app's other
            // one-time prompts (see MainWindow.MaybeShowWelcomePrompt): a crash mid-dialog, or
            // the user just declining, must not cause this to ask again on the next press.
            _settings.HasSeenLauncherDetectionPrompt = true;
            _storageService.SaveSettings(_settings);

            var detected = DetectInstalledLaunchers();
            if (detected.Count > 0)
            {
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
        try
        {
            AddIfFound(detected, Views.DetectedLauncher.Steam, "Steam", _steamScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
            AddIfFound(detected, Views.DetectedLauncher.Gog, "GOG Galaxy", _gogScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
            AddIfFound(detected, Views.DetectedLauncher.Ea, "EA App", _eaScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
            AddIfFound(detected, Views.DetectedLauncher.Epic, "Epic Games", _epicScannerService.ScanInstalledGames(Array.Empty<string>()).Count);
            AddIfFound(detected, Views.DetectedLauncher.Ubisoft, "Ubisoft Connect", _ubisoftScannerService.ScanInstalledGames(Array.Empty<string>()).Count);

            LoggingService.Info("ImportCoordinator", detected.Count == 0
                ? "First 'Scan for Games' press: no supported launcher had any installed games."
                : $"First 'Scan for Games' press: detected {string.Join(", ", detected.Select(d => $"{d.DisplayName} ({d.GameCount})"))}.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("ImportCoordinator", $"Error detecting installed launchers: {ex.Message}");
        }
        return detected;

        static void AddIfFound(List<Views.DetectedLauncherOption> list, Views.DetectedLauncher launcher, string displayName, int count)
        {
            if (count > 0) list.Add(new Views.DetectedLauncherOption(launcher, displayName, count));
        }
    }

    /// <summary>
    /// Called after the user confirms <see cref="Views.LauncherDetectionDialog"/> with at least
    /// one platform checked: enables only those platforms' toggles (unconditionally, so an
    /// unchecked one is explicitly turned off rather than left at whatever it was), re-syncs
    /// Steam's scan locations if Steam was just turned on, then runs the real scan.
    /// </summary>
    public void CompleteFirstTimeLauncherDetection(IReadOnlyList<Views.DetectedLauncher> enabledLaunchers)
    {
        _settings.SteamIntegrationEnabled = enabledLaunchers.Contains(Views.DetectedLauncher.Steam);
        _settings.GogIntegrationEnabled = enabledLaunchers.Contains(Views.DetectedLauncher.Gog);
        _settings.EaIntegrationEnabled = enabledLaunchers.Contains(Views.DetectedLauncher.Ea);
        _settings.EpicIntegrationEnabled = enabledLaunchers.Contains(Views.DetectedLauncher.Epic);
        _settings.UbisoftIntegrationEnabled = enabledLaunchers.Contains(Views.DetectedLauncher.Ubisoft);
        _storageService.SaveSettings(_settings);

        if (_settings.SteamIntegrationEnabled)
        {
            SyncSteamScanLocationsOnStartup();
        }

        _ = ScanForGamesCoreAsync(silent: false);
    }

    /// <summary>Called if the user closes/skips the first-time launcher-detection prompt without
    /// confirming - nothing to do, since HasSeenLauncherDetectionPrompt is already marked seen
    /// and no toggle was touched; the next "Scan for Games" press just runs normally.</summary>
    public void SkipFirstTimeLauncherDetection()
    {
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
        if (enabledLocations.Count == 0 && !_settings.GogIntegrationEnabled && !_settings.EaIntegrationEnabled && !_settings.EpicIntegrationEnabled && !_settings.UbisoftIntegrationEnabled)
        {
            if (!silent)
            {
                Window? owner = WindowHelper.ActiveOwner();
                ModernDialog.ShowInfo(
                    owner,
                    "No Scan Locations",
                    "You haven't added any scan locations yet.",
                    "Add one in Settings → Game Scanner, then try again.");
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

            var steamLocations = _settings.SteamIntegrationEnabled
                ? enabledLocations.Where(l => l.Source == ScanLocationSource.Steam).ToList()
                : new List<ScanLocation>();
            var folderLocations = enabledLocations.Where(l => l.Source != ScanLocationSource.Steam).ToList();
            bool gogEnabled = _settings.GogIntegrationEnabled;
            bool eaEnabled = _settings.EaIntegrationEnabled;
            bool epicEnabled = _settings.EpicIntegrationEnabled;
            bool ubisoftEnabled = _settings.UbisoftIntegrationEnabled;

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
                        steamResults = _steamScannerService
                            .ScanInstalledGames(steamLocations.Select(l => l.Path), steamPath, existingAppIds)
                            .Where(g => !g.IsAlreadyImported && !ignoredAppIds.Contains(g.AppId))
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

            var folderTask = Task.Run(() =>
            {
                var folderResults = new List<GameCandidate>();
                var ignoredExePaths = BuildIgnoredExePathSet();
                foreach (var loc in folderLocations)
                {
                    if (!Directory.Exists(loc.Path)) continue;

                    // A scan location's identity is already known (the user configured it as a
                    // game library), so this skips ScanFolderOrLibrary's is-this-a-library
                    // confidence gating entirely and takes every immediate subfolder's own best
                    // candidate directly - see ScanKnownLibraryLocation.
                    var candidates = FilterIgnored(_folderScannerService.ScanKnownLibraryLocation(loc.Path, _settings.PreferExeForGameName), ignoredExePaths);
                    folderResults.AddRange(candidates.Where(c => !existingExePaths.Contains(c.ExePath)));
                }
                return folderResults;
            });

            await Task.WhenAll(steamTask, gogTask, eaTask, epicTask, ubisoftTask, folderTask).ConfigureAwait(true);
            var (steamGames, gogGames, eaGames, epicGames, ubisoftGames, folderCandidates) =
                (steamTask.Result, gogTask.Result, eaTask.Result, epicTask.Result, ubisoftTask.Result, folderTask.Result);

            if (steamGames.Count == 0 && gogGames.Count == 0 && eaGames.Count == 0 && epicGames.Count == 0 && ubisoftGames.Count == 0 && folderCandidates.Count == 0)
            {
                _library.StatusMessage = "No new games found.";
                return;
            }

            _library.StatusMessage = $"Found {steamGames.Count + gogGames.Count + eaGames.Count + epicGames.Count + ubisoftGames.Count + folderCandidates.Count} new game(s).";
            RequestScanResultsPicker?.Invoke(steamGames, gogGames, eaGames, epicGames, ubisoftGames, folderCandidates);
        }
        finally
        {
            _isScanningForGames = false;
        }
    }

    /// <summary>Drops any candidate whose exe path the user has permanently ignored (see <see cref="IgnoreGamePath"/>).</summary>
    private IEnumerable<GameCandidate> FilterIgnored(IEnumerable<GameCandidate> candidates)
        => FilterIgnored(candidates, BuildIgnoredExePathSet());

    /// <summary>Same as the single-arg overload, but takes a pre-built set - for a caller that
    /// filters multiple lists (or scan locations) in one pass, so the ignore list isn't rebuilt
    /// into a fresh HashSet on every call.</summary>
    private static IEnumerable<GameCandidate> FilterIgnored(IEnumerable<GameCandidate> candidates, HashSet<string> ignoredExePaths)
        => candidates.Where(c => !ignoredExePaths.Contains(c.ExePath));

    private HashSet<string> BuildIgnoredExePathSet()
        => new(
            _settings.IgnoredGamePaths.Where(p => p.ExePath != null).Select(p => p.ExePath!),
            StringComparer.OrdinalIgnoreCase);

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
    public async Task ImportScanResultsAsync(List<DiscoveredSteamGame> steamGames, List<DiscoveredGogGame> gogGames, List<DiscoveredEaGame> eaGames, List<DiscoveredEpicGame> epicGames, List<DiscoveredUbisoftGame> ubisoftGames, List<GameCandidate> folderCandidates)
    {
        int total = steamGames.Count + gogGames.Count + eaGames.Count + epicGames.Count + ubisoftGames.Count + folderCandidates.Count;
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
        int folderAdded = folderCandidates.Count > 0 ? await ImportBatchGamesAsync(folderCandidates, announceProgress: false) : 0;

        // -1 means that leg's import threw (already logged) rather than everything just being a
        // duplicate - don't let a real failure hide behind the same reassuring "already in your
        // library" text a legitimate all-duplicates result gets.
        if (steamAdded < 0 || gogAdded < 0 || eaAdded < 0 || epicAdded < 0 || ubisoftAdded < 0 || folderAdded < 0)
        {
            int addedSoFar = Math.Max(steamAdded, 0) + Math.Max(gogAdded, 0) + Math.Max(eaAdded, 0) + Math.Max(epicAdded, 0) + Math.Max(ubisoftAdded, 0) + Math.Max(folderAdded, 0);
            _library.StatusMessage = addedSoFar > 0
                ? $"Imported {addedSoFar} game(s), but part of the import failed - check the log for details."
                : "Import failed - check the log for details.";
            return;
        }

        int addedTotal = steamAdded + gogAdded + eaAdded + epicAdded + ubisoftAdded + folderAdded;
        _library.StatusMessage = addedTotal > 0
            ? $"Imported {addedTotal} game(s)!"
            : "All selected games are already in your library.";
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
                    LoggingService.Warn("ImportCoordinator", $"Failed to import Steam game '{d.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

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
                    LoggingService.Warn("ImportCoordinator", $"Failed to import GOG game '{d.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

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
                    LoggingService.Warn("ImportCoordinator", $"Failed to import EA game '{d.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

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
                    LoggingService.Warn("ImportCoordinator", $"Failed to import Epic game '{d.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

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
                    LoggingService.Warn("ImportCoordinator", $"Failed to import Ubisoft game '{d.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

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
}
