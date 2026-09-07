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

    private SteamAppDetails? _details;
    private bool _isLoading = true;
    private string? _errorMessage;
    private bool _isShowingRecommendedReqs;

    public GameEntry Game { get; }
    public string GameTitle => !string.IsNullOrWhiteSpace(_details?.Name) ? _details.Name : Game.Name;

    public string PlaytimeDisplay => string.IsNullOrWhiteSpace(Game.PlaytimeDisplay) ? (Game.IsSteamGame ? "Tracked in Steam" : "0 min played") : Game.PlaytimeDisplay;
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
    public bool HasDetails => _details != null && !string.IsNullOrWhiteSpace(_details.Name);

    public string Developers => _details?.Developers ?? "Unknown Developer";
    public string Publishers => _details?.Publishers ?? "Unknown Publisher";
    public string ReleaseDate => !string.IsNullOrWhiteSpace(_details?.ReleaseDate) ? _details.ReleaseDate : "TBA";
    public string ShortDescription => !string.IsNullOrWhiteSpace(_details?.ShortDescription) 
        ? _details.ShortDescription 
        : "No synopsis available for this title.";

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

    public int? MetacriticScore => _details?.MetacriticScore;
    public bool HasMetacritic => _details?.MetacriticScore != null && _details.MetacriticScore > 0;

    public string? ReviewSummary => _details?.ReviewSummary;
    public bool HasReviewSummary => !string.IsNullOrWhiteSpace(_details?.ReviewSummary);

    public System.Collections.Generic.List<string> PlayModes => _details?.PlayModes ?? new();
    public bool HasPlayModes => PlayModes.Count > 0;

    public string GenresDisplay => _details?.Genres != null && _details.Genres.Count > 0 
        ? string.Join(" • ", _details.Genres) 
        : Game.Category;

    public bool HasRequirements => !string.IsNullOrWhiteSpace(_details?.PcRequirementsMin) || !string.IsNullOrWhiteSpace(_details?.PcRequirementsRec);

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
    public bool HasNews => NewsItems.Count > 0;

    public string StoreUrl => _details?.StoreUrl ?? (!string.IsNullOrWhiteSpace(Game.SteamAppId) ? $"https://store.steampowered.com/app/{Game.SteamAppId}" : string.Empty);

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
        double minConfidence = SteamSearchService.DefaultMinConfidence)
    {
        Game = game ?? throw new ArgumentNullException(nameof(game));
        _steamMetadataService = steamMetadataService ?? throw new ArgumentNullException(nameof(steamMetadataService));
        _steamSearchService = steamSearchService ?? throw new ArgumentNullException(nameof(steamSearchService));
        _launchAction = launchAction;
        _editAction = editAction;
        _deleteAction = deleteAction;
        _steamGridDbApiKey = steamGridDbApiKey;
        _minConfidence = minConfidence;

        // If cached details are available, show them immediately so the dialog opens instantly
        if (!string.IsNullOrWhiteSpace(Game.SteamAppId) && SteamMetadataService.TryGetCached(Game.SteamAppId, out var cached))
        {
            _details = cached;
            _isLoading = false;
        }

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
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(targetAppId))
            {
                if (_details == null)
                {
                    ErrorMessage = $"No matching Steam store entry was found for \"{Game.Name}\".";
                }
                IsLoading = false;
                return;
            }

            // Always fetch with forceRefresh: true so that every time a card is clicked,
            // fresh news, reviews, and specs are updated in the background.
            var loaded = await _steamMetadataService.GetAppDetailsAsync(targetAppId, _steamGridDbApiKey, forceRefresh: true);
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
            else if (_details == null)
            {
                ErrorMessage = $"Could not retrieve metadata from Steam for App ID {targetAppId}. Check internet connection.";
            }
        }
        catch (Exception ex)
        {
            if (_details == null)
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
