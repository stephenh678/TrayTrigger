using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

/// <summary>Changing a game's executable, icon, cover and Steam id, and fetching its metadata and art.</summary>
public partial class LibraryViewModel
{
    public void RelocateGame(GameCardViewModel card)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Locate Game Executable for {card.Name}",
            Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        // The old path may no longer exist, so this opens at the nearest folder above it that does.
        DialogStartLocation.ForFile(dialog, card.Game.ExecutablePath, card.Game.WorkingDirectory);

        if (FileDialogCloak.Show(dialog) == true)
        {
            card.Game.ExecutablePath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(card.Game.WorkingDirectory) || !Directory.Exists(card.Game.WorkingDirectory))
            {
                card.Game.WorkingDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            }

            string newIcon = _iconExtractorService.ExtractAndCacheIcon(card.Game.Id, dialog.FileName, card.Game.Name);
            if (!string.IsNullOrEmpty(newIcon))
            {
                card.Game.IconPath = newIcon;
            }

            card.RefreshProperties();
            SaveLibrary();
            UpdateHotkeys();
            LoggingService.Info("Library", $"'{card.Name}' relocated to '{dialog.FileName}'.");
            StatusMessage = $"Updated location for {card.Name}";
        }
    }

    public void ChangeGameIcon(GameCardViewModel card)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select Icon for \"{card.Name}\"",
            Filter = "Image & Icon Files (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            string cachedIcon = _iconExtractorService.ExtractAndCacheIcon(card.Game.Id, dialog.FileName, card.Game.Name);
            if (!string.IsNullOrEmpty(cachedIcon))
            {
                card.Game.IconPath = cachedIcon;
                card.RefreshProperties();
                SaveLibrary();
                LoggingService.Info("Library", $"'{card.Name}' icon updated from '{dialog.FileName}'.");
                StatusMessage = $"Updated icon for \"{card.Name}\"";
            }
        }
    }

    public void ChangeGameCover(GameCardViewModel card)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select Poster Artwork for \"{card.Name}\" (600×900 recommended)",
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (FileDialogCloak.Show(dialog) == true)
        {
            try
            {
                string coversDir = SteamMetadataService.CoversDirectory;
                if (!Directory.Exists(coversDir)) Directory.CreateDirectory(coversDir);

                string ext = Path.GetExtension(dialog.FileName);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                string destFile = Path.Combine(coversDir, $"{card.Game.Id}{ext}");

                if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(dialog.FileName, destFile, overwrite: true);
                }
                card.Game.CoverImagePath = destFile;
                card.ReloadCover();
                SaveLibrary();
                LoggingService.Info("Library", $"'{card.Name}' poster artwork updated from '{dialog.FileName}'.");
                StatusMessage = $"Updated poster artwork for \"{card.Name}\"";
            }
            catch (Exception ex)
            {
                LoggingService.Error("Library", $"Failed to set poster artwork for '{card.Name}' from '{dialog.FileName}'", ex);
                ModernDialog.ShowWarning(null, "Poster Artwork", "That image couldn't be used as the poster.", ex.Message);
            }
        }
    }

    public void EditSteamAppId(GameCardViewModel card)
    {
        RequestEditSteamAppId?.Invoke(card);
    }

    public async Task UpdateGameSteamAppIdAsync(GameCardViewModel card, string? newAppId)
    {
        string trimmed = newAppId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            card.Game.SteamAppId = null;
            card.RefreshProperties();
            SaveLibrary();
            LoggingService.Info("Library", $"'{card.Name}' Steam App ID cleared.");
            StatusMessage = $"Cleared Steam App ID for \"{card.Name}\"";
            return;
        }

        if (!UrlProtocolHelper.IsValidSteamAppId(trimmed))
        {
            ModernDialog.ShowWarning(Application.Current?.MainWindow, "Invalid App ID", "Steam App ID must be a numeric ID (e.g. 1245620).");
            return;
        }

        card.Game.SteamAppId = trimmed;
        // IsSteamGame is left alone: a Local game links an App ID purely for metadata and art,
        // and a Steam game set to launch its executable directly (a real path, not steam://)
        // must not be demoted to Local by re-linking its ID.

        StatusMessage = $"Fetching Steam metadata for App ID {trimmed}...";
        SteamMetadataService.InvalidateCache(trimmed);
        var details = await _steamMetadataService.GetAppDetailsAsync(trimmed, _getSteamGridDbApiKeyOrNull(), forceRefresh: true);
        if (details != null)
        {
            if (!string.IsNullOrWhiteSpace(details.Name) && (string.IsNullOrWhiteSpace(card.Game.Name) || card.Game.Name.StartsWith("Unnamed", StringComparison.OrdinalIgnoreCase)))
            {
                card.Game.Name = details.Name;
            }
            if (!string.IsNullOrWhiteSpace(details.CoverImagePath))
            {
                card.Game.CoverImagePath = details.CoverImagePath;
            }
            // The same setting and rule as the enrichment pass and Game Details.
            if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(card.Game.Category) &&
                !string.IsNullOrWhiteSpace(details.PrimaryGenre))
            {
                card.Game.Category = details.PrimaryGenre;
            }
            StatusMessage = $"Updated metadata and poster for \"{card.Name}\" (Steam App ID {trimmed})";
        }
        else
        {
            StatusMessage = $"Linked Steam App ID {trimmed} to \"{card.Name}\"";
        }

        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
        LoggingService.Info("Library", $"'{card.Name}' Steam App ID set to {trimmed}{(details != null ? " (metadata refreshed)" : "")}.");
    }

    public async Task RefreshGameMetadataAsync(GameCardViewModel card)
    {
        if (string.IsNullOrWhiteSpace(card.Game.SteamAppId))
        {
            StatusMessage = $"Searching Steam store for \"{card.Name}\"...";
            // A manual refresh re-fetches the RAWG entry too (bypassing the disk cache) before
            // the normal pass, which then serves it from the fresh cache.
            if (card.Game.RawgId > 0)
                await TryEnrichFromRawgAsync(card.Game, forceRefresh: true);
            await EnrichGameWithSteamMetadataAsync(card.Game);
        }
        else
        {
            StatusMessage = $"Refreshing metadata for \"{card.Name}\"...";
            SteamMetadataService.InvalidateCache(card.Game.SteamAppId);
            var details = await _steamMetadataService.GetAppDetailsAsync(card.Game.SteamAppId, _getSteamGridDbApiKeyOrNull(), forceRefresh: true);
            if (details != null && !string.IsNullOrWhiteSpace(details.CoverImagePath))
            {
                card.Game.CoverImagePath = details.CoverImagePath;
            }
            // Same RAWG fallback as the background pass for a still-uncategorized game.
            if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(card.Game.Category))
                await TryEnrichFromRawgAsync(card.Game, forceRefresh: card.Game.RawgId > 0);
        }

        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
        StatusMessage = $"Refreshed metadata for \"{card.Name}\"";
    }

    /// <summary>
    /// Fetches Steam metadata for an entry that already resolved (or was assigned) a Steam AppId,
    /// or - failing that - tries to resolve a title/AppId match via GameNameExtractor. Shared by
    /// LibraryViewModel (refresh actions) and ImportCoordinator (the import pipelines' enrichment
    /// tail), which is why this stays internal to LibraryViewModel rather than moving wholesale to
    /// ImportCoordinator: both need the same Steam metadata/search service instances.
    /// </summary>
    public async Task EnrichGameWithSteamMetadataAsync(GameEntry entry)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.SteamAppId))
            {
                if (_settings.AutoCategorizeFromSteam || _settings.UseVerticalPosterArt)
                {
                    var details = await _steamMetadataService.GetAppDetailsAsync(entry.SteamAppId, _getSteamGridDbApiKeyOrNull());
                    if (details != null)
                    {
                        if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                            entry.Category = details.PrimaryGenre;
                        if (string.IsNullOrWhiteSpace(entry.CoverImagePath) && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                            entry.CoverImagePath = details.CoverImagePath;
                    }

                    // A Steam App ID with no store page (delisted, region-locked) or a page that
                    // lists no genres leaves the category empty - let RAWG fill it.
                    if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category))
                        await TryEnrichFromRawgAsync(entry);
                }
                return;
            }

            if (!_settings.SearchOfficialTitleOnline && !_settings.AutoCategorizeFromSteam && !_settings.UseVerticalPosterArt
                && string.IsNullOrWhiteSpace(_getRawgApiKeyOrNull()))
                return;

            string folder = GameNameExtractor.FindMeaningfulFolderName(entry.ExecutablePath, entry.WorkingDirectory);

            // entry.Name is already an authoritative title for a platform import (GOG/EA/Epic all
            // set it from the platform's own metadata before this runs) - pass it as the known
            // name so the search tries it directly instead of relying solely on a guess derived
            // from the exe filename/folder (e.g. "OMD.exe"/"OrcsMustDie3" missing the real Steam
            // listing that searching "Orcs Must Die! 3" finds immediately). Harmless to pass for a
            // plain folder-scan/manual-drop entry too - it's just whatever guess was already there.
            var res = await GameNameExtractor.ResolveGameMatchAsync(
                entry.ExecutablePath,
                folder,
                preferExe: GameNameExtractor.PreferExeForGameName,
                searchOnline: true,
                steamSearch: _steamSearchService,
                minConfidence: _settings.OnlineMatchConfidenceThreshold,
                knownName: entry.Name);

            if (!string.IsNullOrWhiteSpace(res.ResolvedTitle) && _settings.SearchOfficialTitleOnline)
            {
                entry.Name = res.ResolvedTitle;
            }

            if (!string.IsNullOrWhiteSpace(res.SteamAppId))
            {
                entry.SteamAppId = res.SteamAppId;

                var details = await _steamMetadataService.GetAppDetailsAsync(res.SteamAppId, _getSteamGridDbApiKeyOrNull());
                if (details != null)
                {
                    if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                        entry.Category = details.PrimaryGenre;
                    if (string.IsNullOrWhiteSpace(entry.CoverImagePath) && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                        entry.CoverImagePath = details.CoverImagePath;
                }
                return;
            }

            // No Steam App ID resolved (Roblox, Fortnite, Game Pass exclusives, obscure indies):
            // RAWG is the peer source here - resolve (and remember) its match and let its genre
            // fill the category. Then SteamGridDB poster art by name, searched with RAWG's
            // canonical title first since the scanner's folder-derived name often misses.
            string? rawgTitle = await TryEnrichFromRawgAsync(entry);
            await TryFetchGridArtByNameAsync(entry, rawgTitle);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Library", $"Enrichment error for {entry.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// SteamGridDB-by-name poster fetch for an entry with no Steam App ID. A no-op unless vertical
    /// art is enabled, a SteamGridDB key is set, and the cover is currently empty. The community
    /// autocomplete can return a loosely-related game, so the art is applied only when the matched
    /// title actually resembles the game's name (same guard as the Steam title match) - otherwise
    /// a search for "Roblox" grabbing some unrelated poster would stick.
    /// </summary>
    internal async Task TryFetchGridArtByNameAsync(GameEntry entry, string? preferredName = null, bool replaceExisting = false)
    {
        if (!_settings.UseVerticalPosterArt
            || (!replaceExisting && !string.IsNullOrWhiteSpace(entry.CoverImagePath))
            || string.IsNullOrWhiteSpace(entry.Name))
            return;

        string? key = _getSteamGridDbApiKeyOrNull();
        if (string.IsNullOrWhiteSpace(key))
            return;

        // RAWG's canonical title first (when we have one and it differs), then the entry's own
        // name. Either way the result must resemble the entry's name before it sticks.
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredName) && !preferredName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
            queries.Add(preferredName);
        queries.Add(entry.Name);

        foreach (string query in queries)
        {
            var art = await _steamMetadataService.DownloadAndCacheGridArtByNameAsync(entry.Id, query, key);
            if (art == null)
                continue;

            double similarity = Math.Max(
                SteamSearchService.CalculateSimilarity(entry.Name, art.Value.MatchedName),
                SteamSearchService.CalculateSimilarity(query, art.Value.MatchedName));
            if (similarity >= _settings.OnlineMatchConfidenceThreshold)
            {
                entry.CoverImagePath = art.Value.Path;
                LoggingService.Info("Library", $"Applied SteamGridDB poster for '{entry.Name}' (searched '{query}', matched '{art.Value.MatchedName}', similarity {similarity:F2}).");
                return;
            }

            LoggingService.Verbose("Library", $"Rejected SteamGridDB poster for '{entry.Name}' (searched '{query}'): matched title '{art.Value.MatchedName}' too dissimilar (similarity {similarity:F2} < {_settings.OnlineMatchConfidenceThreshold:F2}).");
        }
    }

    /// <summary>
    /// RAWG enrichment for an entry with no Steam App ID: resolves (and remembers) the RAWG
    /// match, and fills the category from RAWG's primary genre under the same
    /// "auto-categorize" setting and Uncategorized-only rule as Steam. A no-op unless RAWG is
    /// enabled with a key. Returns RAWG's canonical title for the poster search, or null.
    /// </summary>
    private async Task<string?> TryEnrichFromRawgAsync(GameEntry entry, bool forceRefresh = false)
    {
        string? key = _getRawgApiKeyOrNull();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(entry.Name))
            return null;

        RawgLookupResult result;
        if (entry.RawgId > 0)
        {
            result = await _rawgService.GetByIdAsync(entry.RawgId, key, forceRefresh);
            if (result.Status == RawgLookupStatus.NoMatch)
            {
                entry.RawgId = 0;
                result = await _rawgService.LookUpByNameAsync(entry.Name, key, _settings.OnlineMatchConfidenceThreshold);
            }
        }
        else
        {
            result = await _rawgService.LookUpByNameAsync(entry.Name, key, _settings.OnlineMatchConfidenceThreshold);
        }

        if (result.Details is not { } details)
            return null;

        if (entry.RawgId != details.RawgId)
        {
            entry.RawgId = details.RawgId;
            LoggingService.Info("Library", $"'{entry.Name}' matched RAWG entry '{details.Name}' (id {details.RawgId}).");
        }

        if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
        {
            entry.Category = details.PrimaryGenre;
            LoggingService.Info("Library", $"'{entry.Name}' categorized as '{details.PrimaryGenre}' from RAWG.");
        }

        return details.Name;
    }
}
