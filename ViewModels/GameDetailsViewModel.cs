using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// <summary>Settings › "Automatically categorize games from store genres" - applies to a
    /// RAWG genre exactly as it does to Steam's, only while the game is still Uncategorized.</summary>
    private readonly bool _autoCategorize;
    /// <summary>The library's SteamGridDB-by-name poster fetch: (game, preferred title, replace
    /// existing). Null in tests / when the library isn't wired.</summary>
    private readonly Func<GameEntry, string?, bool, Task>? _fetchPosterByName;

    private SteamAppDetails? _details;
    private RawgGameDetails? _rawgDetails;
    private MetadataSource _activeSource;
    private bool _rawgAttempted;
    private bool _isRawgLoading;
    private RawgLookupStatus _rawgStatus = RawgLookupStatus.Found;
    private bool _isLoading = true;
    private string? _errorMessage;
    private bool _isShowingRecommendedReqs;

    /// <summary>True while the RAWG source is the active view (see <see cref="MetadataSource"/>).</summary>
    public bool IsRawgActive => _activeSource == MetadataSource.Rawg;

    /// <summary>The toggle only appears when a RAWG key is configured - without one there is no
    /// second source to switch to.</summary>
    public bool CanToggleSource => !string.IsNullOrWhiteSpace(_rawgApiKey);

    public bool IsSteamActive => _activeSource == MetadataSource.Steam;

    /// <summary>"Data from RAWG" attribution link - shown (per RAWG's terms) whenever RAWG data is
    /// on screen. Targets the game's rawg.io page.</summary>
    public bool ShowRawgAttribution => IsRawgActive && _rawgDetails != null;
    public string RawgUrl => !string.IsNullOrWhiteSpace(_rawgDetails?.RawgPageUrl) ? _rawgDetails!.RawgPageUrl : "https://rawg.io";

    /// <summary>"Change match" is offered for whichever source is showing (matched or not) once
    /// its fetch is done - a wrong pick is visible right there, so the fix lives right there. The
    /// Steam side needs nothing configured; the RAWG side needs the key.</summary>
    public bool CanChangeMatch => IsRawgActive ? (CanToggleSource && !IsRawgLoading) : !IsLoading;
    public string ChangeMatchToolTip => IsRawgActive
        ? "Search RAWG and pick the right entry for this game"
        : "Search the Steam store and pick the right listing for this game";

    /// <summary>"Matched to X" beside the switch: which Steam listing / RAWG entry the game resolved to.</summary>
    public string MatchCaption
    {
        get
        {
            if (IsRawgActive)
                return _rawgDetails != null ? $"Matched to “{_rawgDetails.Name}”" : string.Empty;
            return !string.IsNullOrWhiteSpace(_details?.Name) && !string.IsNullOrWhiteSpace(Game.SteamAppId)
                ? $"Matched to “{_details!.Name}” (App ID {Game.SteamAppId})"
                : string.Empty;
        }
    }
    public bool HasMatchCaption => !string.IsNullOrEmpty(MatchCaption);

    /// <summary>True while the RAWG lookup for this game is in flight (RAWG side only).</summary>
    public bool IsRawgLoading
    {
        get => _isRawgLoading;
        private set
        {
            if (_isRawgLoading != value)
            {
                _isRawgLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(CanChangeMatch));
            }
        }
    }

    /// <summary>Whichever source is active, is its fetch still running?</summary>
    public bool ShowLoading => IsRawgActive ? IsRawgLoading : IsLoading;
    public string LoadingMessage => IsRawgActive
        ? "Fetching game info from RAWG..."
        : "Fetching latest game details, specs & news from Steam...";

    /// <summary>A neutral, source-aware explanation for an empty RAWG side (no match, bad key,
    /// network), shown instead of the red Steam error banner. Null while loading or when data is
    /// showing.</summary>
    public string? RawgStatusMessage
    {
        get
        {
            if (!IsRawgActive || IsRawgLoading || _rawgDetails != null || !_rawgAttempted)
                return null;

            return _rawgStatus switch
            {
                RawgLookupStatus.Unauthorized => "RAWG rejected the API key. Check the key in Settings › Library.",
                RawgLookupStatus.Failed => "Could not reach RAWG. Check your internet connection and try again.",
                _ => $"RAWG has no entry matching \"{RawgSearchName}\". Use Change match to search RAWG yourself, or switch back to Steam.",
            };
        }
    }
    public bool HasRawgStatus => !string.IsNullOrWhiteSpace(RawgStatusMessage);

    public GameEntry Game { get; }
    /// <summary>The best-known title to search RAWG with: Steam's official name once Steam has
    /// resolved (it beats a folder-derived library name), else the library name. Keeps the two
    /// sources describing the same game after a Steam rematch.</summary>
    private string RawgSearchName => !string.IsNullOrWhiteSpace(_details?.Name) ? _details!.Name : Game.Name;

    // The header shows the active source's official title (Steam's or RAWG's spelling). The
    // library entry itself is never renamed from here.
    public string GameTitle => IsRawgActive
        ? (!string.IsNullOrWhiteSpace(_rawgDetails?.Name) ? _rawgDetails!.Name : Game.Name)
        : (!string.IsNullOrWhiteSpace(_details?.Name) ? _details!.Name : Game.Name);

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
                OnPropertyChanged(nameof(AmbientImage));
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
                OnPropertyChanged(nameof(HasStoreUrl));
                OnPropertyChanged(nameof(MatchCaption));
                OnPropertyChanged(nameof(HasMatchCaption));
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
                OnPropertyChanged(nameof(ShowLoading));
                OnPropertyChanged(nameof(CanChangeMatch));
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

    // The Steam error banner only applies while Steam is the source on screen.
    public bool HasError => !IsRawgActive && !string.IsNullOrWhiteSpace(_errorMessage);
    public bool HasDetails => IsRawgActive ? _rawgDetails != null : (_details != null && !string.IsNullOrWhiteSpace(_details.Name));

    // Developer/Publisher/Release/Genres/Metacritic read from whichever source is active. Empty
    // fields collapse in the view (no more "Unknown Developer" / "TBA" placeholders).
    public string Developers => IsRawgActive ? (_rawgDetails?.Developer ?? string.Empty) : (_details?.Developers ?? string.Empty);
    public bool HasDevelopers => !string.IsNullOrWhiteSpace(Developers);
    public string Publishers => IsRawgActive ? (_rawgDetails?.Publisher ?? string.Empty) : (_details?.Publishers ?? string.Empty);
    public bool HasPublishers => !string.IsNullOrWhiteSpace(Publishers);
    public string ReleaseDate => IsRawgActive ? RawgService.FormatReleaseDate(_rawgDetails?.ReleaseDate) : (_details?.ReleaseDate ?? string.Empty);
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

            ImageSource? result = null;

            if (localPath != null)
            {
                result = IconExtractorService.LoadBitmapSafely(localPath, decodePixelWidth: 340);
            }
            else if (!string.IsNullOrWhiteSpace(_rawgDetails?.BackgroundImageUrl))
            {
                // No poster at all: RAWG's screenshot beats the bare icon tile (Stretch=Uniform
                // letterboxes it in the portrait frame). Never persisted as the game's cover.
                result = LoadRemoteImage(_rawgDetails!.BackgroundImageUrl);
            }
            else if (!string.IsNullOrWhiteSpace(Game.IconPath) && File.Exists(Game.IconPath))
            {
                result = IconExtractorService.LoadBitmapSafely(Game.IconPath, decodePixelWidth: 340);
            }
            else if (!string.IsNullOrWhiteSpace(_details?.HeaderImageUrl))
            {
                result = LoadRemoteImage(_details.HeaderImageUrl);
            }

            _cachedCoverImage = result;
            _coverImageCached = true;
            return result;
        }
    }

    public int? MetacriticScore => IsRawgActive ? _rawgDetails?.Metacritic : _details?.MetacriticScore;
    public bool HasMetacritic => MetacriticScore is > 0;

    // RAWG-only badges: the community rating (0-5) covers the many titles with no Metacritic
    // score, and the ESRB rating has no Steam-side equivalent.
    public string RawgRatingDisplay => _rawgDetails?.Rating is double r && r > 0
        ? (_rawgDetails.RatingsCount > 0
            ? $"{r:0.0} / 5  ({_rawgDetails.RatingsCount:N0} ratings)"
            : $"{r:0.0} / 5")
        : string.Empty;
    public bool HasRawgRating => IsRawgActive && !string.IsNullOrWhiteSpace(RawgRatingDisplay);
    public string EsrbRating => _rawgDetails?.EsrbRating ?? string.Empty;
    public bool HasEsrbRating => IsRawgActive && !string.IsNullOrWhiteSpace(EsrbRating);

    // Steam's own review summary and (further down) news/patch notes have no RAWG equivalent, so
    // they hide while RAWG is the active source.
    public string? ReviewSummary => _details?.ReviewSummary;
    public bool HasReviewSummary => !IsRawgActive && !string.IsNullOrWhiteSpace(_details?.ReviewSummary);

    // Play-mode chips: Steam's categories, or RAWG's tags mapped to the same wording.
    public System.Collections.Generic.List<string> PlayModes => IsRawgActive ? (_rawgDetails?.PlayModes ?? new()) : (_details?.PlayModes ?? new());
    public bool HasPlayModes => PlayModes.Count > 0;

    // The store the game is sold on, per RAWG - the non-Steam counterpart of the Steam Store
    // button. Its own platform's store when known (Xbox / Epic / GOG import), else the first PC
    // storefront RAWG lists a link for.
    private RawgStoreLink? PickedStore => _rawgDetails != null
        ? RawgService.PickStore(_rawgDetails.Stores, Game, excludeSteam: HasStoreUrl)
        : null;
    public bool HasRawgStore => IsRawgActive && PickedStore != null;
    public string RawgStoreLabel => PickedStore is { } s ? $"Open on {s.Name}" : string.Empty;
    public string RawgStoreUrl => PickedStore?.Url ?? string.Empty;

    /// <summary>The blurred hero backdrop. RAWG's screenshot gives real game atmosphere for a
    /// title with only an icon tile; otherwise the poster itself is blurred as before.</summary>
    public ImageSource? AmbientImage
    {
        get
        {
            if (IsRawgActive && !string.IsNullOrWhiteSpace(_rawgDetails?.BackgroundImageUrl))
                return LoadRemoteImage(_rawgDetails!.BackgroundImageUrl) ?? DisplayCoverImage;
            return DisplayCoverImage;
        }
    }

    private static ImageSource? LoadRemoteImage(string url)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            // Remote sources load asynchronously; Freeze() would throw mid-download and this is
            // only ever bound on the UI thread.
            return bmp;
        }
        catch
        {
            return null;
        }
    }

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
    public ICommand OpenRawgStoreCommand { get; private set; } = null!;
    /// <summary>The two halves of the Steam | RAWG segmented switch above the synopsis.</summary>
    public ICommand SelectSteamSourceCommand { get; private set; } = null!;
    public ICommand SelectRawgSourceCommand { get; private set; } = null!;
    /// <summary>Opens the title-search picker for the active source so a wrong or missing match
    /// can be chosen by hand.</summary>
    public ICommand ChangeMatchCommand { get; private set; } = null!;

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
        Action<GameEntry>? saveGame = null,
        bool autoCategorize = false,
        Func<GameEntry, string?, bool, Task>? fetchPosterByName = null)
    {
        _autoCategorize = autoCategorize;
        _fetchPosterByName = fetchPosterByName;
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

        SelectSteamSourceCommand = new RelayCommand(() => SetSource(MetadataSource.Steam), () => CanToggleSource);
        SelectRawgSourceCommand = new RelayCommand(() => SetSource(MetadataSource.Rawg), () => CanToggleSource);
        ChangeMatchCommand = new RelayCommand(ChangeMatch, () => CanChangeMatch);
        OpenRawgPageCommand = new RelayCommand(() => ExecuteOpenUrl(RawgUrl));
        OpenRawgStoreCommand = new RelayCommand(() => ExecuteOpenUrl(RawgStoreUrl), () => HasRawgStore);
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
        => ResolveSource(Game.PreferredMetadataSource, hasSteamAppId: !string.IsNullOrWhiteSpace(Game.SteamAppId),
                         rawgAvailable: !string.IsNullOrWhiteSpace(_rawgApiKey));

    /// <summary>Pure source-resolution rule (unit-tested): an explicit preference wins, except
    /// RAWG without a key falls back to Steam; Auto picks RAWG only for a game with no Steam App
    /// ID when a key exists.</summary>
    internal static MetadataSource ResolveSource(MetadataSource preferred, bool hasSteamAppId, bool rawgAvailable)
    {
        return preferred switch
        {
            MetadataSource.Steam => MetadataSource.Steam,
            MetadataSource.Rawg => rawgAvailable ? MetadataSource.Rawg : MetadataSource.Steam,
            _ => rawgAvailable && !hasSteamAppId ? MetadataSource.Rawg : MetadataSource.Steam,
        };
    }

    /// <summary>Switches the text source and remembers the explicit choice on the game so the
    /// window reopens to the same side. A no-op when already on that side.</summary>
    private void SetSource(MetadataSource source)
    {
        if (source == MetadataSource.Auto || _activeSource == source)
            return;

        _activeSource = source;
        if (Game.PreferredMetadataSource != source)
        {
            Game.PreferredMetadataSource = source;
            _saveGame?.Invoke(Game);
        }

        if (IsRawgActive)
        {
            _ = EnsureRawgLoadedAsync();
        }

        RaiseSourceDependentChanged();
    }

    /// <summary>Routes "Change match" to the picker for whichever source is on screen.</summary>
    private void ChangeMatch()
    {
        if (IsRawgActive)
            ChangeRawgMatch();
        else
            ChangeSteamMatch();
    }

    /// <summary>
    /// Lets the user pick the RAWG entry by hand from a live search (pre-filled with the game's
    /// name). Applies the pick immediately: remembers the id, drops the automatic guard for it,
    /// and reloads the RAWG side.
    /// </summary>
    private void ChangeRawgMatch()
    {
        if (string.IsNullOrWhiteSpace(_rawgApiKey))
            return;

        string key = _rawgApiKey;
        var dialog = new Views.GameMatchPickerDialog(
            "Change RAWG Match",
            $"Pick the RAWG entry for “{Game.Name}”. The choice is remembered for this game.",
            Game.Name,
            Game.RawgId > 0 ? Game.RawgId.ToString() : null,
            async (query, ct) =>
            {
                var (status, hits) = await _rawgService.SearchAsync(query, key, ct);
                string? empty = status switch
                {
                    RawgLookupStatus.Unauthorized => "RAWG rejected the API key. Check it in Settings › Library.",
                    RawgLookupStatus.Failed => "Could not reach RAWG. Check your internet connection and try again.",
                    _ => null,
                };
                return new Views.MatchPickerResult(
                    hits.Select(h => new Views.MatchPickerHit(h.Id.ToString(), h.Name, h.Subtitle)).ToList(), empty);
            },
            attributionLabel: "Data from RAWG",
            attributionUrl: "https://rawg.io");

        if (dialog.ShowDialog() != true || dialog.SelectedHit == null || !int.TryParse(dialog.SelectedHit.Id, out int rawgId))
            return;

        Game.RawgId = rawgId;
        _saveGame?.Invoke(Game);
        LoggingService.Info("GameDetailsViewModel", $"'{Game.Name}' manually matched to RAWG id {rawgId} ('{dialog.SelectedHit.Name}').");

        _rawgDetails = null;
        _rawgAttempted = false;
        _rawgManualPick = true;
        RaiseSourceDependentChanged();
        _ = EnsureRawgLoadedAsync();
    }

    /// <summary>
    /// The Steam counterpart: a live Steam store search, pre-filled with the game's name. The
    /// pick becomes the game's Steam App ID - the same effect as "Fetch by ID" in Edit Game, so
    /// title/genre enrichment and poster art follow it (the wrong match's poster is replaced).
    /// Launch routing is untouched: a local exe stays a local exe.
    /// </summary>
    private void ChangeSteamMatch()
    {
        var dialog = new Views.GameMatchPickerDialog(
            "Change Steam Match",
            $"Pick the Steam store listing for “{Game.Name}”. Its details and poster art will be used for this game.",
            Game.Name,
            string.IsNullOrWhiteSpace(Game.SteamAppId) ? null : Game.SteamAppId,
            async (query, ct) =>
            {
                var matches = await _steamSearchService.SearchGamesAsync(query, ct);
                return new Views.MatchPickerResult(
                    matches.Select(m => new Views.MatchPickerHit(m.AppId, m.Name, $"App ID {m.AppId}")).ToList(),
                    null);
            });

        if (dialog.ShowDialog() != true || dialog.SelectedHit == null || string.IsNullOrWhiteSpace(dialog.SelectedHit.Id))
            return;

        string appId = dialog.SelectedHit.Id;
        if (appId == Game.SteamAppId)
            return;

        LoggingService.Info("GameDetailsViewModel", $"'{Game.Name}' manually matched to Steam App ID {appId} ('{dialog.SelectedHit.Name}').");

        // A previously cached (possibly wrong) result for this id must not be served back.
        SteamMetadataService.InvalidateCache(appId);
        Game.SteamAppId = appId;

        // The Steam listing is the game's identity, so the RAWG side must describe the same
        // game: drop the old RAWG match and let it re-resolve from the new Steam title.
        Game.RawgId = 0;
        _rawgDetails = null;
        _rawgAttempted = false;
        _saveGame?.Invoke(Game);

        Details = null;
        _replaceCoverOnNextLoad = true;
        _resyncRawgAfterSteamLoad = true;
        _ = LoadDetailsAsync();
    }

    /// <summary>Set by a Steam rematch: once the new Steam title is known, re-resolve RAWG from
    /// it (if RAWG is enabled) so a later switch to RAWG shows the same game.</summary>
    private bool _resyncRawgAfterSteamLoad;

    /// <summary>Set by a RAWG rematch and consumed when its details arrive: re-resolve Steam from
    /// RAWG's title and replace (not just fill) the poster.</summary>
    private bool _rawgManualPick;

    /// <summary>
    /// Searches Steam for RAWG's title and, on a decisive hit (the same bar the automatic
    /// auto-link uses) that differs from the current App ID, adopts it: new details, new poster.
    /// Returns true when the Steam side changed.
    /// </summary>
    private async Task<bool> TryResyncSteamFromRawgAsync(string rawgTitle)
    {
        if (string.IsNullOrWhiteSpace(rawgTitle))
            return false;

        try
        {
            var match = await _steamSearchService.FindBestMatchAsync(rawgTitle, _minConfidence);
            if (match == null || string.IsNullOrWhiteSpace(match.AppId) || match.SimilarityScore < Math.Max(0.85, _minConfidence))
            {
                LoggingService.Verbose("GameDetailsViewModel", $"No decisive Steam listing for RAWG title '{rawgTitle}' - Steam side left as is.");
                return false;
            }
            if (match.AppId == Game.SteamAppId)
                return false;

            LoggingService.Info("GameDetailsViewModel", $"'{Game.Name}' Steam side re-linked to App ID {match.AppId} ('{match.Name}') from RAWG title '{rawgTitle}' (similarity {match.SimilarityScore:F2}).");
            SteamMetadataService.InvalidateCache(match.AppId);
            Game.SteamAppId = match.AppId;
            _saveGame?.Invoke(Game);

            Details = null;
            _replaceCoverOnNextLoad = true;
            await LoadDetailsAsync();
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDetailsViewModel", $"Steam resync from RAWG title '{rawgTitle}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Set by a manual Steam rematch so the next load replaces the poster instead of
    /// keeping whatever the wrong match downloaded.</summary>
    private bool _replaceCoverOnNextLoad;

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
        IsRawgLoading = true;
        try
        {
            RawgLookupResult result;
            if (Game.RawgId > 0)
            {
                result = await _rawgService.GetByIdAsync(Game.RawgId, _rawgApiKey);
                if (result.Status == RawgLookupStatus.NoMatch)
                {
                    // The remembered id is gone from RAWG - forget it and fall through to a
                    // fresh name search.
                    Game.RawgId = 0;
                    result = await _rawgService.LookUpByNameAsync(RawgSearchName, _rawgApiKey, _minConfidenceForRawg);
                }
            }
            else
            {
                result = await _rawgService.LookUpByNameAsync(RawgSearchName, _rawgApiKey, _minConfidenceForRawg);
            }

            _rawgStatus = result.Status;
            _rawgDetails = result.Details;

            if (result.Details is { } found)
            {
                bool changed = false;
                if (Game.RawgId != found.RawgId)
                {
                    Game.RawgId = found.RawgId;
                    changed = true;
                }

                // RAWG's genre fills the category the same way Steam's does - only while the
                // game is still Uncategorized (or on a platform placeholder), never overriding
                // a category the user or Steam already set.
                if (_autoCategorize && LibraryConstants.IsEnrichableCategory(Game.Category) && !string.IsNullOrWhiteSpace(found.PrimaryGenre))
                {
                    Game.Category = found.PrimaryGenre;
                    changed = true;
                    OnPropertyChanged(nameof(Category));
                }

                if (changed)
                    _saveGame?.Invoke(Game);

                // The screenshot may now be the ambient backdrop / crisp fallback.
                InvalidateCoverImageCache();

                // Store links are a separate call; only pay for it once the RAWG side is up.
                await _rawgService.EnsureStoreLinksAsync(found, _rawgApiKey);

                bool manualPick = _rawgManualPick;
                _rawgManualPick = false;

                // A hand-picked RAWG entry is a statement of which game this is, so the Steam
                // side re-resolves from RAWG's title - the mirror of a Steam rematch dropping
                // the RAWG match. Only a decisive Steam hit is applied.
                bool steamChanged = manualPick && await TryResyncSteamFromRawgAsync(found.Name);

                // Poster: Steam's own art wins when Steam matched; otherwise SteamGridDB by
                // RAWG's canonical title - replacing the old poster after a manual pick, only
                // filling a missing one after an automatic match.
                if (!steamChanged && string.IsNullOrWhiteSpace(Game.SteamAppId) && _fetchPosterByName != null)
                {
                    string? before = Game.CoverImagePath;
                    await _fetchPosterByName(Game, found.Name, manualPick);
                    if (Game.CoverImagePath != before)
                    {
                        _saveGame?.Invoke(Game);
                        InvalidateCoverImageCache();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDetailsViewModel", $"RAWG load failed for '{Game.Name}': {ex.Message}");
            _rawgStatus = RawgLookupStatus.Failed;
        }
        finally
        {
            IsRawgLoading = false;
            RaiseSourceDependentChanged();
        }
    }

    /// <summary>Re-raises every property whose value depends on the active source (after a toggle
    /// or once RAWG data arrives).</summary>
    private void RaiseSourceDependentChanged()
    {
        foreach (var name in new[]
        {
            nameof(IsRawgActive), nameof(IsSteamActive),
            nameof(ShowRawgAttribution), nameof(RawgUrl),
            nameof(CanChangeMatch), nameof(ChangeMatchToolTip), nameof(MatchCaption), nameof(HasMatchCaption),
            nameof(ShowLoading), nameof(LoadingMessage), nameof(RawgStatusMessage), nameof(HasRawgStatus),
            nameof(HasDetails), nameof(GameTitle),
            nameof(Developers), nameof(HasDevelopers), nameof(Publishers), nameof(HasPublishers),
            nameof(ReleaseDate), nameof(HasReleaseDate), nameof(ShortDescription), nameof(GenresDisplay),
            nameof(MetacriticScore), nameof(HasMetacritic), nameof(RawgRatingDisplay), nameof(HasRawgRating),
            nameof(EsrbRating), nameof(HasEsrbRating), nameof(ReviewSummary), nameof(HasReviewSummary),
            nameof(PlayModes), nameof(HasPlayModes),
            nameof(HasRawgStore), nameof(RawgStoreLabel), nameof(RawgStoreUrl),
            nameof(DisplayCoverImage), nameof(AmbientImage), nameof(Category),
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
                        ? "This game has no Steam store entry. Select RAWG above to show its info, or set a Steam App ID in Edit Properties."
                        : "This game has no Steam store entry. Enable RAWG in Settings › Library to show info for non-Steam games, or set a Steam App ID in Edit Properties.";
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

                // Sync cover image back to game if missing or updated - or unconditionally after
                // a manual Steam rematch, where the old poster belongs to the wrong game.
                bool replaceCover = _replaceCoverOnNextLoad;
                _replaceCoverOnNextLoad = false;
                if (!string.IsNullOrWhiteSpace(loaded.CoverImagePath) &&
                    (replaceCover || string.IsNullOrWhiteSpace(Game.CoverImagePath) || !File.Exists(Game.CoverImagePath)))
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

                if (_resyncRawgAfterSteamLoad)
                {
                    _resyncRawgAfterSteamLoad = false;
                    if (CanToggleSource)
                        _ = EnsureRawgLoadedAsync();
                }
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
