using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>The three pictures a game has, as cards on Edit Game's Artwork tab.</summary>
public enum ArtSlot
{
    Banner,
    Poster,
    Icon
}

/// <summary>
/// Edit Game > Artwork: the Banner (behind the name on Game Details), the Poster (library cards) and the Icon (tray and
/// list). Each is Automatic, found for the game, or Custom, a file the player chose; Choose image... makes it Custom and
/// Use automatic puts the automatic one back.
/// </summary>
public partial class GameEditViewModel
{
    private static readonly string[] PictureExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    private static readonly string[] IconExtensions = [".ico", ".png", ".jpg", ".jpeg", ".bmp", ".exe"];

    /// <summary>Where a banner is looked for. A property so a test can give it a folder of its own.</summary>
    internal HeroArtService HeroArt { get; init; } = HeroArtService.Shared;

    /// <summary>Where a chosen banner is kept. A property so a test can give it a folder of its own.</summary>
    internal string CoversDirectory { get; init; } = SteamMetadataService.CoversDirectory;

    private bool _posterCustom;
    private bool _iconCustom;
    private bool _heroCustom;

    /// <summary>The banner file picked this session; "" once Use automatic was pressed; null while nothing was changed.</summary>
    private string? _customHeroSource;
    private BitmapImage? _heroPreview;

    private void InitArtworkTab(GameEntry game)
    {
        _posterCustom = game.HasCustomPoster;
        _iconCustom = game.HasCustomIcon;
        _heroCustom = !string.IsNullOrWhiteSpace(game.HeroImagePath);
        UpdateHeroPreview();
    }

    // --- What each picture is, for its card's chip and buttons ---

    public bool PosterIsCustom => _posterCustom;
    public bool PosterIsAutomatic => !_posterCustom;
    public bool IconIsCustom => _iconCustom;
    public bool HeroIsCustom => _heroCustom;
    public bool HeroIsAutomatic => !_heroCustom;

    public BitmapImage? HeroPreview => _heroPreview;
    public bool HasHeroPreview => _heroPreview != null;

    public string PosterChip => _posterCustom
        ? $"Custom · {Path.GetFileName(CustomCoverPath is { Length: > 0 } c ? c : SourceGame.CoverImagePath)}"
        : PosterSourceName() is { } source ? $"Automatic · {source}" : HasCoverPreview ? "Automatic" : "Automatic · none yet";

    public string IconChip => _iconCustom
        ? $"Custom · {Path.GetFileName(CustomIconPath is { Length: > 0 } c ? c : SourceGame.IconPath)}"
        : "Automatic · the game's exe";

    public string HeroChip => _heroCustom
        ? $"Custom · {Path.GetFileName(_customHeroSource is { Length: > 0 } c ? c : SourceGame.HeroImagePath)}"
        : HasHeroPreview ? "Automatic" : "Automatic · none found yet";

    private string? PosterSourceName() =>
        UrlProtocolHelper.IsValidSteamAppId(SteamAppId?.Trim()) && PosterSourceLedger.Get(SteamAppId!.Trim()) is { } source
            ? source switch
            {
                PosterSource.Steam => "Steam",
                PosterSource.SteamGridDb => "SteamGridDB",
                _ => "Steam banner",
            }
            : null;

    private void NotifyArtworkState()
    {
        foreach (string name in new[]
        {
            nameof(PosterIsCustom), nameof(PosterIsAutomatic), nameof(IconIsCustom), nameof(HeroIsCustom), nameof(HeroIsAutomatic),
            nameof(PosterChip), nameof(IconChip), nameof(HeroChip),
        })
        {
            OnPropertyChanged(name);
        }
    }

    // --- Banner ---

    /// <summary>The page behind this one has found (or lost) the banner: the card shows it too, unless one was chosen here.</summary>
    public void RefreshBannerPreview()
    {
        if (_customHeroSource == null) UpdateHeroPreview();
    }

    private void UpdateHeroPreview()
    {
        string? path = _customHeroSource switch
        {
            { Length: > 0 } staged => staged,
            "" => HeroArt.CachedPath(SteamAppId, SourceGame.Id),
            _ => HeroArtService.CustomPath(SourceGame) ?? HeroArt.CachedPath(SteamAppId, SourceGame.Id),
        };
        _heroPreview = path == null ? null : IconExtractorService.LoadBitmapSafely(path, decodePixelWidth: 900);
        OnPropertyChanged(nameof(HeroPreview));
        OnPropertyChanged(nameof(HasHeroPreview));
        NotifyArtworkState();
    }

    public ICommand ChooseHeroCommand => _chooseHero ??= new RelayCommand(ChooseHero);
    public ICommand UseAutomaticHeroCommand => _useAutoHero ??= new RelayCommand(UseAutomaticHero, () => _heroCustom);
    public ICommand FindHeroAgainCommand => _findHero ??= new RelayCommand(() => _ = FindHeroAgainAsync(), () => !IsRefreshingMetadata && !_heroCustom);
    public ICommand UseAutomaticPosterCommand => _useAutoPoster ??= new RelayCommand(UseAutomaticPoster, () => _posterCustom);
    public ICommand FindPosterAgainCommand => _findPoster ??= new RelayCommand(() => _ = FindPosterAgainAsync(), () => !IsRefreshingMetadata && !_posterCustom);
    public ICommand UseAutomaticIconCommand => _useAutoIcon ??= new RelayCommand(UseAutomaticIcon, () => _iconCustom);
    private ICommand? _chooseHero, _useAutoHero, _findHero, _useAutoPoster, _findPoster, _useAutoIcon;

    private void ChooseHero()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a Hero Banner (wide, about 1920×620)",
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All Files (*.*)|*.*",
            CheckFileExists = true
        };
        DialogStartLocation.ForFile(dialog, _customHeroSource, SourceGame.HeroImagePath);

        if (FileDialogCloak.Show(dialog) == true) TryUseImage(ArtSlot.Banner, dialog.FileName);
    }

    private void UseAutomaticHero()
    {
        _customHeroSource = string.Empty;
        _heroCustom = false;
        UpdateHeroPreview();
        // Nothing found yet for the game: look, so the card shows what Automatic gives.
        if (!HasHeroPreview) _ = FindHeroAgainAsync();
    }

    /// <summary>Looks again for the automatic banner (Steam's, then SteamGridDB's), whatever was found or not found before.</summary>
    /// <param name="quiet">After a new match: look without replacing the message that says what the match did.</param>
    public async Task FindHeroAgainAsync(bool quiet = false)
    {
        IsRefreshingMetadata = true;
        if (!quiet) StatusMessage = "Looking for a hero banner...";
        try
        {
            string? path = await HeroArt.RefetchAsync(SourceGame, SteamAppId?.Trim(), _steamGridDbApiKey, _minConfidence);
            UpdateHeroPreview();
            if (!quiet) StatusMessage = path != null ? "Found a hero banner." : "No hero banner found for this game. Game Details shows the blurred poster instead.";
        }
        catch (Exception ex)
        {
            ShowProblem($"Could not look for a hero banner: {ex.Message}");
            LoggingService.Warn("GameEdit", $"Banner look-up failed: {ex.Message}");
        }
        finally
        {
            IsRefreshingMetadata = false;
        }
    }

    // --- Poster and icon: the older commands, with their state ---

    private void UseAutomaticPoster()
    {
        ResetCover();
        if (UrlProtocolHelper.IsValidSteamAppId(SteamAppId?.Trim()) || !string.IsNullOrWhiteSpace(Name))
            _ = FindPosterAgainAsync();
    }

    /// <summary>Fetches the poster again: from Steam for a game with an App ID, otherwise from SteamGridDB by name (with a key).</summary>
    public async Task FindPosterAgainAsync()
    {
        if (UrlProtocolHelper.IsValidSteamAppId(SteamAppId?.Trim()))
        {
            await RefreshPosterAsync();
        }
        else if (!string.IsNullOrWhiteSpace(Name))
        {
            IsRefreshingMetadata = true;
            try { await LookUpGridArtByNameAsync(Name.Trim()); }
            finally { IsRefreshingMetadata = false; }
        }
        NotifyArtworkState();
    }

    private void UseAutomaticIcon()
    {
        ResetIcon();
        _iconCustom = false;
        NotifyArtworkState();
    }

    // --- Choosing or dropping a file ---

    /// <summary>
    /// Makes a picture the player's own: what Choose image... and a dropped file both do. Returns why the file
    /// was refused, which is also shown in the status line, or null once it is in use.
    /// </summary>
    public string? TryUseImage(ArtSlot slot, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string[] allowed = slot == ArtSlot.Icon ? IconExtensions : PictureExtensions;
        string? problem = null;
        if (!File.Exists(path)) problem = "That file isn't there.";
        else if (!allowed.Contains(ext))
            problem = slot == ArtSlot.Icon
                ? "The icon can be a .ico, .png, .jpg or .bmp picture, or an .exe to take its icon."
                : "That isn't a picture TrayTrigger can use. Use a .jpg, .png, .webp or .bmp.";
        else if (ext != ".exe" && IconExtractorService.LoadBitmapSafely(path, decodePixelWidth: 64) == null)
            problem = "That file couldn't be read as a picture.";

        if (problem != null)
        {
            ShowProblem(problem);
            return problem;
        }

        switch (slot)
        {
            case ArtSlot.Poster:
                CustomCoverPath = path;
                break;
            case ArtSlot.Icon:
                CustomIconPath = path;
                break;
            default:
                _customHeroSource = path;
                _heroCustom = true;
                UpdateHeroPreview();
                if (_heroPreview is { } p && p.PixelHeight > 0 && (double)p.PixelWidth / p.PixelHeight < 1.8)
                    StatusMessage = "That picture isn't wide, so Game Details will crop it. Steam's hero banners are 1920 by 620.";
                break;
        }
        NotifyArtworkState();
        return null;
    }

    /// <summary>Called from Save: the banner chosen here is copied into Covers (so a backup holds it), and a banner put back to Automatic is let go.</summary>
    private void CommitHero()
    {
        SourceGame.HasCustomPoster = _posterCustom;
        SourceGame.HasCustomIcon = _iconCustom;
        if (_customHeroSource == null) return;

        string? old = SourceGame.HeroImagePath;
        if (_customHeroSource.Length == 0)
        {
            SourceGame.HeroImagePath = null;
        }
        else
        {
            try
            {
                string dir = CoversDirectory;
                Directory.CreateDirectory(dir);
                // {Id}_{ticks}: the name Covers' clean-up and backup already know as this game's.
                string dest = Path.Combine(dir, $"{SourceGame.Id}_{DateTime.UtcNow.Ticks}{Path.GetExtension(_customHeroSource)}");
                File.Copy(_customHeroSource, dest, overwrite: true);
                SourceGame.HeroImagePath = dest;
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed("GameEdit", ex, "copying the chosen banner into the covers folder; the original path is kept");
                SourceGame.HeroImagePath = _customHeroSource;
            }
        }

        if (!string.IsNullOrWhiteSpace(old) && !string.Equals(old, SourceGame.HeroImagePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(old), Path.TrimEndingDirectorySeparator(CoversDirectory), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(old); }
            catch (IOException ex) { LoggingService.Swallowed("GameEdit", ex, "removing the banner this one replaced"); }
        }
        _customHeroSource = null;
    }
}
