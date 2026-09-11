using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public class GameDetailsViewModel : ViewModelBase
{
    public event Action? RequestClose;

    private readonly SteamMetadataService _steamMetadataService;
    private readonly SteamSearchService _steamSearchService;
    private readonly Action<GameEntry>? _launchAction;
    private readonly Action<GameEntry>? _editAction;
    private readonly Action<GameEntry>? _deleteAction;

    private readonly RawgService _rawgService = new();
    private readonly string? _rawgApiKey;
    private readonly double _minConfidenceForRawg;
    private readonly Action<GameEntry>? _saveGame;

    private SteamAppDetails? _details;
    private RawgGameDetails? _rawgDetails;
    private MetadataSource _activeSource;
    private bool _rawgAttempted;
    private bool _isLoading = true;
    private string? _errorMessage;
    private bool _isShowingRecommendedReqs;

    /// <summary>True while the RAWG source is the active view (see <see cref="MetadataSource"/>).</summary>
    public bool IsRawgActive => _activeSource == MetadataSource.Rawg;

    /// <summary>The toggle only appears when a RAWG key is configured - without one there is no
    /// second source to switch to.</summary>
    public bool CanToggleSource => !string.IsNullOrWhiteSpace(_rawgApiKey);

    public string SourceToggleLabel => IsRawgActive ? "Showing RAWG data" : "Showing Steam data";

    /// <summary>"Data from RAWG" attribution link - shown (per RAWG's terms) whenever RAWG data is
    /// on screen.</summary>
    public bool ShowRawgAttribution => IsRawgActive && _rawgDetails != null;
    public string RawgUrl => _rawgDetails?.Website ?? "https://rawg.io";

    public GameEntry Game { get; }
    // RAWG never renames the entry - the game's own title stays. Steam may show its official
    // spelling when that's the active source.
    public string GameTitle => !IsRawgActive && !string.IsNullOrWhiteSpace(_details?.Name) ? _details!.Name : Game.Name;

    public string PlaytimeDisplay => string.IsNullOrWhiteSpace(Game.PlaytimeDisplay) ? "0 min played" : Game.PlaytimeDisplay;
    public string LastPlayedDisplay => Game.LastPlayedDisplay;
    public string Category => Game.Category;
    public bool HasHotkey => !string.IsNullOrWhiteSpace(Game.Hotkey);
    public string? Hotkey => Game.Hotkey;

    public bool IsFavorite
    {
        get => Game.IsFavorite;
        set
        {
            if (Game.IsFavorite != value)
            {
                Game.IsFavorite = value;
                OnPropertyChanged();
            }
        }
    }

    public ICommand ToggleFavoriteCommand { get; }

    public SteamAppDetails? Details
    {
        get => _details;
        internal set
        {
            if (_details != value)
            {
                _details = value;
                InvalidateCoverImageCache();
                OnPropertyChanged();
                OnPropertyChanged(nameof(GameTitle));
                OnPropertyChanged(nameof(HasDetails));
                OnPropertyChanged(nameof(Developers));
                OnPropertyChanged(nameof(Publishers));
                OnPropertyChanged(nameof(ReleaseDate));
                OnPropertyChanged(nameof(ShortDescription));
                OnPropertyChanged(nameof(DisplayCoverImage));
                OnPropertyChanged(nameof(MetacriticScore));
                OnPropertyChanged(nameof(HasMetacritic));
                OnPropertyChanged(nameof(ReviewSummary));
                OnPropertyChanged(nameof(HasReviewSummary));
                OnPropertyChanged(nameof(PlayModes));
                OnPropertyChanged(nameof(HasPlayModes));
                OnPropertyChanged(nameof(GenresDisplay));
                OnPropertyChanged(nameof(HasRequirements));
                OnPropertyChanged(nameof(CurrentRequirementsText));
                OnPropertyChanged(nameof(HasNews));
                OnPropertyChanged(nameof(NewsItems));
                OnPropertyChanged(nameof(StoreUrl));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        internal set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        internal set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);
    public bool HasDetails => IsRawgActive ? _rawgDetails != null : (_details != null && !string.IsNullOrWhiteSpace(_details.Name));

    // Developer/Publisher/Release/Genres/Metacritic read from whichever source is active. Empty
    // fields collapse in the view (no more "Unknown Developer" / "TBA" placeholders).
    public string Developers => IsRawgActive ? (_rawgDetails?.Developer ?? string.Empty) : (_details?.Developers ?? string.Empty);
    public bool HasDevelopers => !string.IsNullOrWhiteSpace(Developers);
    public string Publishers => IsRawgActive ? (_rawgDetails?.Publisher ?? string.Empty) : (_details?.Publishers ?? string.Empty);
    public bool HasPublishers => !string.IsNullOrWhiteSpace(Publishers);
    public string ReleaseDate => IsRawgActive ? (_rawgDetails?.ReleaseDate ?? string.Empty) : (_details?.ReleaseDate ?? string.Empty);
    public bool HasReleaseDate => !string.IsNullOrWhiteSpace(ReleaseDate);
    public string ShortDescription
    {
        get
        {
            string? text = IsRawgActive ? _rawgDetails?.Description : _details?.ShortDescription;
            return string.IsNullOrWhiteSpace(text) ? "No synopsis available for this title." : text;
        }
    }

    private bool _coverImageCached;
    private ImageSource? _cachedCoverImage;

    // DisplayCoverImage used to decode from disk (or start a fresh remote download) on every
    // read, and WPF reads bound properties often; cache the result and only recompute when the
    // underlying data actually changes (Details setter / the explicit sync-back in
    // LoadDetailsAsync both call this). See L-18.
    private void InvalidateCoverImageCache()
    {
        _coverImageCached = false;
        _cachedCoverImage = null;
    }

    public ImageSource? DisplayCoverImage
    {
        get
        {
            if (_coverImageCached)
            {
                return _cachedCoverImage;
            }

            string? localPath = null;
            if (!string.IsNullOrWhiteSpace(_details?.CoverImagePath) && File.Exists(_details.CoverImagePath))
                localPath = _details.CoverImagePath;
            else if (!string.IsNullOrWhiteSpace(Game.CoverImagePath) && File.Exists(Game.CoverImagePath))
                localPath = Game.CoverImagePath;
            else if (!string.IsNullOrWhiteSpace(Game.IconPath) && File.Exists(Game.IconPath))
                localPath = Game.IconPath;

            ImageSource? result = null;

            if (localPath != null)
            {
                result = IconExtractorService.LoadBitmapSafely(localPath, decodePixelWidth: 340);
            }
            else if (!string.IsNullOrWhiteSpace(_details?.HeaderImageUrl))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(_details.HeaderImageUrl, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    // A remote UriSource loads asynchronously regardless of CacheOption;
                    // Freeze() throws while it's still downloading (IsDownloading == true),
                    // which the old code swallowed silently here, so this fallback never
                    // actually returned an image. Skip Freeze() instead - this ImageSource is
                    // only ever bound on the UI thread, so cross-thread freezing isn't needed.
                    result = bmp;
                }
                catch { }
            }

            _cachedCoverImage = result;
            _coverImageCached = true;
            return result;
        }
    }

    public int? MetacriticScore => IsRawgActive ? _rawgDetails?.Metacritic : _details?.MetacriticScore;
    public bool HasMetacritic => MetacriticScore is > 0;

    // Steam's own review summary and (further down) news/patch notes have no RAWG equivalent, so
    // they hide while RAWG is the active source.
    public string? ReviewSummary => _details?.ReviewSummary;
    public bool HasReviewSummary => !IsRawgActive && !string.IsNullOrWhiteSpace(_details?.ReviewSummary);

    public System.Collections.Generic.List<string> PlayModes => _details?.PlayModes ?? new();
    public bool HasPlayModes => PlayModes.Count > 0;

    public string GenresDisplay
    {
        get
        {
            var genres = IsRawgActive ? _rawgDetails?.Genres : _details?.Genres;
            return genres != null && genres.Count > 0 ? string.Join(" • ", genres) : Game.Category;
        }
    }

    // PC requirements are a Steam-only field; hidden while RAWG is active.
    public bool HasRequirements => !IsRawgActive && (!string.IsNullOrWhiteSpace(_details?.PcRequirementsMin) || !string.IsNullOrWhiteSpace(_details?.PcRequirementsRec));

    public bool IsShowingRecommendedReqs
    {
        get => _isShowingRecommendedReqs;
        set
        {
            if (_isShowingRecommendedReqs != value)
            {
                _isShowingRecommendedReqs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentRequirementsText));
            }
        }
    }

    public string CurrentRequirementsText
    {
        get
        {
            if (IsShowingRecommendedReqs)
            {
                return !string.IsNullOrWhiteSpace(_details?.PcRequirementsRec) 
                    ? _details.PcRequirementsRec 
                    : "Recommended specifications not specified by developer.";
            }
            return !string.IsNullOrWhiteSpace(_details?.PcRequirementsMin) 
                ? _details.PcRequirementsMin 
                : "Minimum specifications not specified by developer.";
        }
    }

    public System.Collections.Generic.List<SteamNewsItem> NewsItems => _details?.NewsItems ?? new();
    public bool HasNews => !IsRawgActive && NewsItems.Count > 0;

    public string StoreUrl => _details?.StoreUrl ?? (!string.IsNullOrWhiteSpace(Game.SteamAppId) ? $"https://store.steampowered.com/app/{Game.SteamAppId}" : string.Empty);

    /// <summary>Hides the Steam Store button outright (not just disabled) for the many
    /// GOG/EA/Epic/Ubisoft/local games that have no Steam listing to open.</summary>
    public bool HasStoreUrl => !string.IsNullOrWhiteSpace(StoreUrl);

    /// <summary>Steam's news hub for this app: every announcement and patch note, not just the latest 3.</summary>
    public string NewsHubUrl => !string.IsNullOrWhiteSpace(Game.SteamAppId) ? $"https://store.steampowered.com/news/app/{Game.SteamAppId}" : string.Empty;
    public bool HasNewsHub => !string.IsNullOrWhiteSpace(NewsHubUrl);

    public ICommand LaunchGameCommand { get; }
    public ICommand EditGameCommand { get; }
    public ICommand DeleteGameCommand { get; }
    public ICommand OpenStorePageCommand { get; }
    public ICommand OpenNewsUrlCommand { get; }
    public ICommand OpenNewsHubCommand { get; }
    public ICommand ShowMinReqsCommand { get; }
    public ICommand ShowRecReqsCommand { get; }
    public ICommand OpenRawgPageCommand { get; private set; } = null!;

    private readonly string? _steamGridDbApiKey;
    private readonly double _minConfidence;

    public GameDetailsViewModel(
        GameEntry game, 
        SteamMetadataService steamMetadataService,
        SteamSearchService steamSearchService,
        Action<GameEntry>? launchAction = null,
        Action<GameEntry>? editAction = null,
        Action<GameEntry>? deleteAction = null,
        string? steamGridDbApiKey = null,
        double minConfidence = SteamSearchService.DefaultMinConfidence,
        string? rawgApiKey = null,
        Action<GameEntry>? saveGame = null)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        _steamMetadataService = steamMetadataService ?? throw new ArgumentNullException(nameof(steamMetadataService));
        _steamSearchService = steamSearchService ?? throw new ArgumentNullException(nameof(steamSearchService));
        _launchAction = launchAction;
        _editAction = editAction;
        _deleteAction = deleteAction;
        _steamGridDbApiKey = steamGridDbApiKey;
        _minConfidence = minConfidence;
        _minConfidenceForRawg = minConfidence;
        _rawgApiKey = rawgApiKey;
        _saveGame = saveGame;
        _activeSource = ResolveInitialSource();

        // If cached details are available, show them immediately so the dialog opens instantly
        if (!string.IsNullOrWhiteSpace(Game.SteamAppId) && SteamMetadataService.TryGetCached(Game.SteamAppId, out var cached))
        {
            _details = cached;
            _isLoading = false;
        }

        ToggleSourceCommand = new RelayCommand(ToggleSource, () => CanToggleSource);
        OpenRawgPageCommand = new RelayCommand(() => ExecuteOpenUrl(RawgUrl));
        ToggleFavoriteCommand = new RelayCommand(() => IsFavorite = !IsFavorite);
        LaunchGameCommand = new RelayCommand(ExecuteLaunch);
        EditGameCommand = new RelayCommand(ExecuteEdit);
        DeleteGameCommand = new RelayCommand(ExecuteDelete);
        OpenStorePageCommand = new RelayCommand(ExecuteOpenStorePage, () => !string.IsNullOrWhiteSpace(StoreUrl));
        OpenNewsUrlCommand = new RelayCommand(p => ExecuteOpenUrl(p as string));
        OpenNewsHubCommand = new RelayCommand(() => ExecuteOpenUrl(NewsHubUrl), () => HasNewsHub);
        ShowMinReqsCommand = new RelayCommand(() => IsShowingRecommendedReqs = false);
        ShowRecReqsCommand = new RelayCommand(() => IsShowingRecommendedReqs = true);

        // Asynchronously load and refresh latest details in the background every time
        _ = LoadDetailsAsync();

        // When RAWG is the active source on open (a non-Steam game preferring it), fetch it too.
        if (IsRawgActive)
        {
            _ = EnsureRawgLoadedAsync();
        }
    }

    /// <summary>The details source to show on open: the user's explicit per-game choice when set,
    /// otherwise automatic - RAWG for a game with no Steam App ID (when a RAWG key exists), Steam
    /// otherwise.</summary>
    private MetadataSource ResolveInitialSource()
    {
        bool rawgAvailable = !string.IsNullOrWhiteSpace(_rawgApiKey);

        return Game.PreferredMetadataSource switch
        {
            MetadataSource.Steam => MetadataSource.Steam,
            MetadataSource.Rawg => rawgAvailable ? MetadataSource.Rawg : MetadataSource.Steam,
            _ => rawgAvailable && string.IsNullOrWhiteSpace(Game.SteamAppId) ? MetadataSource.Rawg : MetadataSource.Steam,
        };
    }

    public ICommand ToggleSourceCommand { get; private set; } = null!;

    private void ToggleSource()
    {
        _activeSource = IsRawgActive ? MetadataSource.Steam : MetadataSource.Rawg;

        // Remember the explicit choice on the game so the window reopens to the same source.
        Game.PreferredMetadataSource = _activeSource;
        _saveGame?.Invoke(Game);

        if (IsRawgActive)
        {
            _ = EnsureRawgLoadedAsync();
        }

        RaiseSourceDependentChanged();
    }

    /// <summary>
    /// Loads RAWG metadata for this game once (by remembered id, else by name with the same
    /// similarity guard the Steam title match uses). A no-op without a key. The resolved RAWG id
    /// is persisted so reopening the window skips the search.
    /// </summary>
    private async Task EnsureRawgLoadedAsync()
    {
        if (_rawgDetails != null || _rawgAttempted || string.IsNullOrWhiteSpace(_rawgApiKey))
            return;

        _rawgAttempted = true;
        try
        {
            RawgGameDetails? result;
            if (Game.RawgId > 0)
            {
                result = await _rawgService.GetByIdAsync(Game.RawgId, _rawgApiKey);
            }
            else
            {
                result = await _rawgService.LookUpByNameAsync(Game.Name, _rawgApiKey);
                if (result != null && SteamSearchService.CalculateSimilarity(Game.Name, result.Name) < _minConfidenceForRawg)
                {
                    LoggingService.Verbose("GameDetailsViewModel", $"Rejected RAWG match '{result.Name}' for '{Game.Name}' (too dissimilar).");
                    result = null;
                }
                if (result != null)
                {
                    Game.RawgId = result.RawgId;
                    _saveGame?.Invoke(Game);
                }
            }

            _rawgDetails = result;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDetailsViewModel", $"RAWG load failed for '{Game.Name}': {ex.Message}");
        }
        finally
        {
            RaiseSourceDependentChanged();
        }
    }

    /// <summary>Re-raises every property whose value depends on the active source (after a toggle
    /// or once RAWG data arrives).</summary>
    private void RaiseSourceDependentChanged()
    {
        foreach (var name in new[]
        {
            nameof(IsRawgActive), nameof(SourceToggleLabel), nameof(ShowRawgAttribution), nameof(RawgUrl),
            nameof(HasDetails), nameof(GameTitle),
            nameof(Developers), nameof(HasDevelopers), nameof(Publishers), nameof(HasPublishers),
            nameof(ReleaseDate), nameof(HasReleaseDate), nameof(ShortDescription), nameof(GenresDisplay),
            nameof(MetacriticScore), nameof(HasMetacritic), nameof(ReviewSummary), nameof(HasReviewSummary),
            nameof(HasRequirements), nameof(HasNews), nameof(NewsItems),
            nameof(ErrorMessage), nameof(HasError),
        })
        {
            OnPropertyChanged(name);
        }
    }

    public async Task LoadDetailsAsync()
    {
        if (_details == null)
        {
            IsLoading = true;
        }
        ErrorMessage = null;

        try
        {
            string? targetAppId = Game.SteamAppId;

            // If game doesn't have an AppId yet, search Steam by name
            if (string.IsNullOrWhiteSpace(targetAppId))
            {
                var match = await _steamSearchService.FindBestMatchAsync(Game.Name, _minConfidence);
                if (match != null && !string.IsNullOrWhiteSpace(match.AppId))
                {
                    targetAppId = match.AppId;

                    // Only persist the match onto the game entry when it clears the same
                    // "decisive" bar used elsewhere (GameNameExtractor); below that, use it
                    // to show details this once without silently binding a possibly-wrong
                    // AppId. See M-22.
                    if (match.SimilarityScore >= Math.Max(0.85, _minConfidence))
                    {
                        Game.SteamAppId = targetAppId;
                        LoggingService.Info("GameDetailsViewModel", $"Auto-linked '{Game.Name}' to Steam App ID {targetAppId} (similarity {match.SimilarityScore:F2}).");
                    }
                    else
                    {
                        LoggingService.Verbose("GameDetailsViewModel", $"Steam match for '{Game.Name}' (App ID {targetAppId}, similarity {match.SimilarityScore:F2}) below decisive threshold - showing details without persisting the link.");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(targetAppId))
            {
                // Only surface the "not on Steam" notice when Steam is the source being viewed.
                // A non-Steam game showing RAWG shouldn't flash a Steam error.
                if (_details == null && _activeSource == MetadataSource.Steam)
                {
                    ErrorMessage = CanToggleSource
                        ? "This game has no Steam store entry. Switch to RAWG data above, or set a Steam App ID in Edit Properties."
                        : "This game has no Steam store entry. Add a RAWG key in Settings to show info for non-Steam games, or set a Steam App ID in Edit Properties.";
                }
                IsLoading = false;
                return;
            }

            // Always fetch with forceRefresh: true so that every time a card is clicked,
            // fresh news, reviews, and specs are updated in the background.
            bool noStoreData = false;
            var loaded = await _steamMetadataService.GetAppDetailsAsync(targetAppId, _steamGridDbApiKey, forceRefresh: true, onNoStoreData: _ => noStoreData = true);
            if (loaded != null)
            {
                Details = loaded;

                // Sync cover image back to game if missing or updated
                if (!string.IsNullOrWhiteSpace(loaded.CoverImagePath) && (string.IsNullOrWhiteSpace(Game.CoverImagePath) || !File.Exists(Game.CoverImagePath)))
                {
                    Game.CoverImagePath = loaded.CoverImagePath;
                    InvalidateCoverImageCache();
                    OnPropertyChanged(nameof(DisplayCoverImage));
                }

                // Auto-categorize if game is still uncategorized
                if ((string.IsNullOrWhiteSpace(Game.Category) || Game.Category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(loaded.PrimaryGenre))
                {
                    Game.Category = loaded.PrimaryGenre;
                }

                ErrorMessage = null;
            }
            else if (_details == null && _activeSource == MetadataSource.Steam)
            {
                // Distinguish a real "Steam has no store data for this AppId" (delisted /
                // region-locked) response from an actual transport/network failure. See L-25.
                ErrorMessage = noStoreData
                    ? $"Steam has no store page for App ID {targetAppId}. It may have been delisted or is region-locked."
                    : $"Could not retrieve metadata from Steam for App ID {targetAppId}. Check internet connection.";
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDetailsViewModel", $"Error loading Steam details for '{Game.Name}': {ex.Message}");
            if (_details == null && _activeSource == MetadataSource.Steam)
            {
                ErrorMessage = $"Error loading Steam information: {ex.Message}";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ExecuteLaunch()
    {
        RequestClose?.Invoke();
        _launchAction?.Invoke(Game);
    }

    private void ExecuteEdit()
    {
        RequestClose?.Invoke();
        _editAction?.Invoke(Game);
    }

    private void ExecuteDelete()
    {
        RequestClose?.Invoke();
        _deleteAction?.Invoke(Game);
    }

    private void ExecuteOpenStorePage()
    {
        if (!string.IsNullOrWhiteSpace(StoreUrl))
        {
            ExecuteOpenUrl(StoreUrl);
        }
    }

    private static void ExecuteOpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // This URL originates from Steam's public news/store APIs, not user input, but it's
        // still remote content. Restrict to http/https before shell-executing it so a
        // malformed or unexpected value can't be used to launch another URI handler or a
        // local file path.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            LoggingService.Warn("GameDetailsViewModel", $"Refused to open URL with unexpected scheme: '{url}'");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDetailsViewModel", $"Failed to open URL '{url}': {ex.Message}");
        }
    }
}
