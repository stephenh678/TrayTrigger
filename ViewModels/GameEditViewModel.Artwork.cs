using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>Edit Game: icon, cover, name and Steam look-ups, and the undo for a look-up.</summary>
public partial class GameEditViewModel
{
    private void BrowseIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Custom Game Icon",
            Filter = "Image & Icon Files (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        DialogStartLocation.ForFile(dialog, CustomIconPath, ExecutablePath);

        if (FileDialogCloak.Show(dialog) == true)
        {
            CustomIconPath = dialog.FileName;
        }
    }

    private void ResetIcon()
    {
        _customIconPath = string.Empty;
        OnPropertyChanged(nameof(CustomIconPath));

        if (!string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath) && ExtractIconPreview(ExecutablePath) is { } preview)
        {
            IconPreview = preview;
            return;
        }

        UpdateIconPreview();
    }

    private void UpdateIconPreview()
    {
        if (!string.IsNullOrEmpty(_customIconPath) && File.Exists(_customIconPath))
        {
            string ext = Path.GetExtension(_customIconPath).ToLowerInvariant();
            if ((ext == ".exe" || ext == ".dll" || ext == ".lnk") && ExtractIconPreview(_customIconPath) is { } fromFile)
            {
                IconPreview = fromFile;
                return;
            }

            IconPreview = IconExtractorService.LoadBitmapSafely(_customIconPath);
            return;
        }

        if (!string.IsNullOrEmpty(SourceGame.IconPath) && File.Exists(SourceGame.IconPath))
        {
            IconPreview = IconExtractorService.LoadBitmapSafely(SourceGame.IconPath);
            return;
        }

        IconPreview = !string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath)
            ? ExtractIconPreview(ExecutablePath)
            : null;
    }

    /// <summary>The file's associated icon as a frozen bitmap for the preview, or null when it has none.</summary>
    private static BitmapImage? ExtractIconPreview(string path)
    {
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico == null) return null;
            using var bmp = ico.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameEdit", $"Failed to extract icon preview from '{path}': {ex.Message}");
            return null;
        }
    }

    private void BrowseCover()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Custom Poster Artwork (600×900 recommended)",
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        DialogStartLocation.ForFile(dialog, CustomCoverPath);

        if (FileDialogCloak.Show(dialog) == true)
        {
            CustomCoverPath = dialog.FileName;
        }
    }

    private void ResetCover()
    {
        CustomCoverPath = string.Empty;
    }

    private void UpdateCoverPreview()
    {
        if (!string.IsNullOrEmpty(_fetchedCoverPath) && File.Exists(_fetchedCoverPath))
        {
            CoverPreview = IconExtractorService.LoadBitmapSafely(_fetchedCoverPath, decodePixelWidth: 340);
        }
        else if (!string.IsNullOrEmpty(_customCoverPath) && File.Exists(_customCoverPath))
        {
            CoverPreview = IconExtractorService.LoadBitmapSafely(_customCoverPath, decodePixelWidth: 340);
        }
        else if (!string.IsNullOrEmpty(SourceGame.CoverImagePath) && File.Exists(SourceGame.CoverImagePath))
        {
            CoverPreview = IconExtractorService.LoadBitmapSafely(SourceGame.CoverImagePath, decodePixelWidth: 340);
        }
        else
        {
            CoverPreview = null;
        }
    }

    public void FetchNameFromExe()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return;

        string folderFallback = string.Empty;
        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && Directory.Exists(WorkingDirectory))
        {
            folderFallback = Path.GetFileName(WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        if (string.IsNullOrWhiteSpace(folderFallback) && !string.IsNullOrWhiteSpace(ExecutablePath))
        {
            folderFallback = Path.GetFileName(Path.GetDirectoryName(ExecutablePath) ?? "");
        }

        string resolved = GameNameExtractor.ExtractGameName(ExecutablePath, folderFallback, preferExe: true);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            Name = resolved;
        }
    }

    /// <summary>The SteamGridDB-by-name look for a poster, for a game with no App ID (Find again, Use automatic). Applies a poster only when the matched
    /// community title resembles what was searched, so a stray autocomplete hit isn't pinned.</summary>
    private async Task LookUpGridArtByNameAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(_steamGridDbApiKey))
        {
            StatusMessage = $"No Steam match for \"{term}\". Add a SteamGridDB key in Settings to find poster art for non-Steam games.";
            return;
        }

        StatusMessage = $"No Steam match; searching SteamGridDB art for \"{term}\"...";
        var metadataService = SharedMetadataService.Value;
        var art = await metadataService.DownloadAndCacheGridArtByNameAsync(SourceGame.Id, term, _steamGridDbApiKey);
        if (art == null)
        {
            StatusMessage = $"No match found for \"{term}\" on Steam or SteamGridDB.";
            return;
        }

        double similarity = SteamSearchService.CalculateSimilarity(term, art.Value.MatchedName);
        if (similarity >= _minConfidence)
        {
            ApplyFetchedCover(art.Value.Path);
            StatusMessage = $"No Steam listing; found poster art for \"{art.Value.MatchedName}\" via SteamGridDB.";
        }
        else
        {
            StatusMessage = $"Closest SteamGridDB art (\"{art.Value.MatchedName}\") didn't look like \"{term}\" - left unchanged. Use Change... to set art manually.";
        }
    }

    /// <summary>Applies a full Steam details fetch to the edit fields - shared by Change match and
    /// "Fetch by ID" so both routes to an App ID populate title, poster and genre identically.</summary>
    private void ApplySteamDetails(SteamAppDetails details, string appId)
    {
        if (UpdateTitleFromMatch) Name = details.Name;
        SteamAppId = appId;
        // IsSteamGame is left as it is: a Local game links an App ID purely for metadata and
        // art, and a Steam game set to launch its executable directly (a real path, not
        // steam://) must not be demoted to Local by a lookup.
        if (UpdatePosterFromMatch) ApplyFetchedCover(details.CoverImagePath);
        // The same placeholder rule as the library's enrichment: a platform's default category
        // ("Steam", "GOG") is replaced like Uncategorized; a category the user chose is kept.
        if (UpdateGenreFromMatch && LibraryConstants.IsEnrichableCategory(Category) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
        {
            Category = details.PrimaryGenre;
        }
    }

    /// <summary>
    /// Corrects a misidentified game by fetching name + poster art directly from Steam for an
    /// exact AppID, bypassing whatever the fuzzy name search or folder-scan heuristics guessed.
    /// Invalidates any cached details/poster for this AppID first, so a previously wrong result
    /// (e.g. from a bad automatic match) doesn't get served back out of the in-memory cache.
    /// Note: Does NOT modify IsSteamGame; local executables remain local executable games.
    /// </summary>
    public Task FetchBySteamIdAsync() => FetchBySteamIdCoreAsync(CurrentLookUpState());

    /// <param name="before">The state Undo puts back: taken before the App ID was set, when the match was picked rather than typed.</param>
    private async Task FetchBySteamIdCoreAsync(LookUpState before)
    {
        string id = SteamAppId?.Trim() ?? string.Empty;
        if (!UrlProtocolHelper.IsValidSteamAppId(id))
        {
            StatusMessage = "Enter a numeric Steam App ID.";
            LoggingService.Warn("GameEdit", $"Invalid Steam AppID '{id}' supplied for manual match.");
            return;
        }

        IsRefreshingMetadata = true;
        StatusMessage = $"Fetching Steam details for App ID {id}...";
        bool matched = false;
        try
        {
            SteamMetadataService.InvalidateCache(id);
            var metadataService = SharedMetadataService.Value;
            var details = await metadataService.GetAppDetailsAsync(id, _steamGridDbApiKey, forceRefresh: true);
            if (details == null || string.IsNullOrWhiteSpace(details.Name))
            {
                StatusMessage = $"No Steam store details found for App ID {id}.";
                LoggingService.Warn("GameEdit", $"No Steam app details found for AppID '{id}'.");
                return;
            }

            ApplySteamDetails(details, id);
            matched = true;
            StatusMessage = $"Successfully matched with \"{details.Name}\"!";
        }
        catch (Exception ex)
        {
            ShowProblem($"Error fetching from Steam: {ex.Message}");
            LoggingService.Warn("GameEdit", $"Failed to fetch Steam details for AppID '{id}': {ex.Message}");
        }
        finally
        {
            IsRefreshingMetadata = false;
            OfferUndo(before);
        }

        // The banner follows the match while it is Automatic: look under the new App ID.
        if (matched && !_heroCustom) await FindHeroAgainAsync(quiet: true);
    }

    public void FetchBySteamId() => _ = FetchBySteamIdAsync();

    /// <summary>What Look Up and Fetch by ID can replace, kept from before the last one that did, for Undo.</summary>
    private sealed record LookUpState(string Name, string? SteamAppId, string Category, string? FetchedCoverPath, string? CustomCoverPath, bool PosterCustom);

    private LookUpState? _lookUpUndo;

    private LookUpState CurrentLookUpState() => new(Name, SteamAppId, Category, _fetchedCoverPath, _customCoverPath, _posterCustom);

    /// <summary>The last Look Up or Fetch by ID replaced something, and Undo can put it back.</summary>
    public bool CanUndoLookUp => _lookUpUndo != null;

    /// <summary>After a look-up: offers Undo when it changed anything. One that changed nothing leaves an earlier Undo as it was.</summary>
    private void OfferUndo(LookUpState before)
    {
        if (CurrentLookUpState() == before) return;
        _lookUpUndo = before;
        OnPropertyChanged(nameof(CanUndoLookUp));
    }

    /// <summary>Puts back the title, App ID, category and poster from before the last look-up. One step, then it's gone.</summary>
    private void UndoLookUp()
    {
        if (_lookUpUndo is not { } before) return;
        _lookUpUndo = null;
        OnPropertyChanged(nameof(CanUndoLookUp));
        Name = before.Name;
        SteamAppId = before.SteamAppId;
        Category = before.Category;
        _fetchedCoverPath = before.FetchedCoverPath;
        _customCoverPath = before.CustomCoverPath;
        _posterCustom = before.PosterCustom;
        OnPropertyChanged(nameof(CustomCoverPath));
        UpdateCoverPreview();
        NotifyArtworkState();
        RefreshBannerPreview();
        StatusMessage = "Put back the title, Steam App ID, category and poster from before the look-up.";
    }

    /// <summary>
    /// Force re-downloads poster art for the currently set Steam AppID, bypassing the on-disk
    /// "poster already exists" check - useful when the name/AppID are already correct but the
    /// cached art is a low-quality fallback (or was cached before SteamGridDB was configured).
    /// </summary>
    public async Task RefreshPosterAsync()
    {
        string id = SteamAppId?.Trim() ?? string.Empty;
        if (!UrlProtocolHelper.IsValidSteamAppId(id))
        {
            StatusMessage = "Cannot refresh poster: no valid numeric Steam App ID set.";
            LoggingService.Warn("GameEdit", "Cannot refresh poster: no valid numeric Steam AppID set for this game.");
            return;
        }

        IsRefreshingMetadata = true;
        StatusMessage = $"Downloading latest poster for App ID {id}...";
        try
        {
            SteamMetadataService.InvalidateCache(id);
            var metadataService = SharedMetadataService.Value;
            string? cover = await metadataService.DownloadAndCachePosterAsync(id, null, _steamGridDbApiKey, forceRefresh: true);
            if (!string.IsNullOrWhiteSpace(cover))
            {
                ApplyFetchedCover(cover);
                StatusMessage = "Poster refreshed.";
            }
            else
            {
                StatusMessage = "No updated poster art found on Steam.";
            }
        }
        catch (Exception ex)
        {
            ShowProblem($"Failed to refresh poster: {ex.Message}");
            LoggingService.Warn("GameEdit", $"Failed to refresh poster for AppID '{id}': {ex.Message}");
        }
        finally
        {
            IsRefreshingMetadata = false;
        }
    }

    public void RefreshPoster() => _ = RefreshPosterAsync();

    internal void ApplyFetchedCover(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return;

        // Staged like CustomCoverPath: only committed to SourceGame in Save(), so Cancel leaves
        // the game's on-disk CoverImagePath reference untouched. The downloaded file itself is
        // already written to the covers cache dir by the metadata fetch, which is fine to keep.
        _fetchedCoverPath = coverPath;
        // Clear any earlier manual "Change..." pick so the freshly fetched official art wins,
        // both in the preview and in Save()'s custom-cover-copy check.
        _customCoverPath = null;
        _posterCustom = false;
        NotifyArtworkState();
        OnPropertyChanged(nameof(CustomCoverPath));
        UpdateCoverPreview();
    }
}
