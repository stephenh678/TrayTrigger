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

    private bool _isRefreshingAllPosters;
    public bool IsRefreshingAllPosters
    {
        get => _isRefreshingAllPosters;
        private set { _isRefreshingAllPosters = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRefreshAllPosters)); }
    }

    public bool CanRefreshAllPosters => !IsRefreshingAllPosters;

    public event Action? RequestOpenSteamDialog;
    public event Action<string, List<GameCandidate>>? RequestCandidatePicker;
    public event Action<string, List<GameCandidate>>? RequestFolderBatchImport;

    public ICommand AddGameCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand OpenSteamImportCommand { get; }
    public ICommand RefreshAllPostersCommand { get; }

    public ImportCoordinator(
        LibraryViewModel library,
        ShortcutService shortcutService,
        IconExtractorService iconExtractorService,
        FolderScannerService folderScannerService,
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
        _steamSearchService = steamSearchService;
        _steamMetadataService = steamMetadataService;
        _storageService = storageService;
        _settings = settings;
        _getSteamGridDbApiKeyOrNull = getSteamGridDbApiKeyOrNull;

        AddGameCommand = new RelayCommand(AddGameBrowse);
        AddFolderCommand = new RelayCommand(AddGameFolderBrowse);
        OpenSteamImportCommand = new RelayCommand(OpenSteamImport);
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
    private void CommitImportedEntries(IEnumerable<GameEntry> entries, string statusMessage)
    {
        foreach (var entry in entries)
        {
            _library.Games.Add(_library.CreateCardViewModel(entry));
        }

        _library.RebuildCategories();
        _library.SaveLibrary();
        _library.UpdateHotkeys();
        _library.ApplySort();
        _library.StatusMessage = statusMessage;
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
                    (card.Game.Category == LibraryConstants.Uncategorized || card.Game.Category == LibraryConstants.SteamCategory || string.IsNullOrWhiteSpace(card.Game.CoverImagePath) || string.IsNullOrWhiteSpace(card.Game.SteamAppId)) &&
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
                aggregated.AddRange(scanResult.DiscoveredGames);
            }
            else if (scanResult.SingleGameCandidates.Count > 0)
            {
                // Represent this individual game folder with its single best-scoring candidate.
                aggregated.Add(scanResult.SingleGameCandidates[0]);
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

    public async Task ImportBatchGamesAsync(List<GameCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return;
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
                _library.StatusMessage = "All selected games are already in your library.";
                return;
            }

            _library.StatusMessage = $"Importing {toProcess.Count} game(s)...";

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
                string status = skippedDuplicates > 0
                    ? $"Added {preparedEntries.Count} games from folder! ({skippedDuplicates} already in library, skipped)"
                    : $"Added {preparedEntries.Count} games from folder!";
                CommitImportedEntries(preparedEntries, status);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportBatchGames", ex);
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

    public void OpenSteamImport()
    {
        if (!_settings.SteamIntegrationEnabled)
        {
            Window? owner = WindowHelper.ActiveOwner();
            var res = ModernDialog.Confirm(
                owner,
                "Steam Integration Disabled",
                "Steam library integration is currently disabled in Settings.",
                "Would you like to enable it now and scan your Steam library?",
                confirmText: "Enable & Scan",
                cancelText: "Cancel");

            if (res)
            {
                _settings.SteamIntegrationEnabled = true;
                _storageService.SaveSettings(_settings);
                RequestOpenSteamDialog?.Invoke();
            }
            return;
        }

        RequestOpenSteamDialog?.Invoke();
    }

    public void ImportSteamGames(List<DiscoveredSteamGame> discoveredGames) => _ = ImportSteamGamesAsync(discoveredGames);

    public async Task ImportSteamGamesAsync(List<DiscoveredSteamGame> discoveredGames)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return;

        if (_isImportInProgress)
        {
            _library.StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return;
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
                _library.StatusMessage = "All selected Steam games are already in your library.";
                return;
            }

            _library.StatusMessage = $"Importing {toProcess.Count} Steam game(s)...";

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
                string status = skippedDuplicates > 0
                    ? $"Imported {preparedEntries.Count} Steam game(s)! ({skippedDuplicates} already in library, skipped)"
                    : $"Imported {preparedEntries.Count} Steam game(s)!";
                CommitImportedEntries(preparedEntries, status);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("ImportCoordinator", "Error in ImportSteamGames", ex);
        }
        finally
        {
            _isImportInProgress = false;
        }
    }
}
