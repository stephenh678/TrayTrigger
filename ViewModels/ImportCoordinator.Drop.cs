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

/// <summary>Adding games from what the player drops or picks: files and shortcuts, one folder, several folders, and a batch of candidates.</summary>
public partial class ImportCoordinator
{
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
            _library.StatusMessage = "An import is already running. Try again when it finishes.";
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
                            preferExe: GameNameExtractor.PreferExeForGameName,
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
                        Category = NewEntryCategory,
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

            var scanResult = await Task.Run(() => ResolveAndFilterIgnored(_folderScannerService.ScanFolderOrLibrary(folderPath, GameNameExtractor.PreferExeForGameName), lookupIndex));

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

        // Folders dropped together can overlap (C:\Games and C:\Games\Hades): an exe found under
        // both is offered once, not once per folder.
        aggregated = aggregated
            .DistinctBy(c => c.ExePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (aggregated.Count == 0)
        {
            Window? owner = WindowHelper.ActiveOwner();
            ModernDialog.ShowInfo(
                owner,
                "No Games Found",
                "No game was found in the folders you dropped.",
                "Drop the folder a game is installed in, or one that holds several game folders.");
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
        var scanResult = await Task.Run(() => ResolveAndFilterIgnored(_folderScannerService.ScanFolderOrLibrary(folderPath, GameNameExtractor.PreferExeForGameName), _platformLookup.CreateIndex()));

        // A. Multi-game parent folder detected (e.g. C:\Games, D:\SteamLibrary\steamapps\common, C:\GOG Games)
        if (scanResult.IsMultiGameLibrary)
        {
            if (scanResult.DiscoveredGames.Count == 0)
            {
                Window? owner = WindowHelper.ActiveOwner();
                ModernDialog.ShowInfo(
                    owner,
                    "No Games Found",
                    $"No game was found in \"{Path.GetFileName(folderPath.TrimEnd('\\', '/'))}\".",
                    "Pick the folder a game is installed in, or one that holds several game folders.");
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
                "No Games Found",
                $"No game was found in \"{Path.GetFileName(folderPath.TrimEnd('\\', '/'))}\".",
                "Pick the folder a game is installed in, or one that holds several game folders.");
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
            _library.StatusMessage = "An import is already running. Try again when it finishes.";
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

        int localAdded;
        PlatformImportResult platformResult;
        using (BeginCommitBatch())
        {
            localAdded = localCandidates.Count > 0 ? await ImportLocalCandidatesAsync(localCandidates, announceProgress: false) : 0;
            platformResult = await ImportPlatformBucketsAsync(platformBuckets);
        }

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
}
