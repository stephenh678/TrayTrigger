using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public class GameCardViewModel : ViewModelBase
{
    private readonly Action<GameCardViewModel> _onLaunch;
    private readonly Action<GameCardViewModel> _onEdit;
    private readonly Action<GameCardViewModel> _onDelete;
    private readonly Action<GameCardViewModel>? _onRelocate;
    private readonly Action<GameCardViewModel>? _onRename;
    private readonly Action<GameCardViewModel>? _onChangeCategory;
    private readonly Action<GameCardViewModel>? _onChangeIcon;
    private readonly Action<GameCardViewModel>? _onChangeCover;
    private readonly Action<GameCardViewModel>? _onFetchExeName;
    private readonly Action<GameCardViewModel>? _onViewDetails;
    private readonly Action<GameCardViewModel>? _onEditSteamAppId;
    private readonly Action<GameCardViewModel>? _onRefreshMetadata;
    private readonly Action<GameCardViewModel>? _onToggleFavorite;
    private readonly Action<GameCardViewModel>? _onToggleHidden;
    private readonly Action<GameCardViewModel>? _onEndSession;
    private readonly Action<GameCardViewModel>? _onForceClose;
    private readonly Action<GameCardViewModel>? _onPrimaryClick;
    private readonly Action<GameCardViewModel>? _onToggleSelect;
    private readonly Action<GameCardViewModel>? _onRangeSelect;
    private readonly Func<bool>? _getUseVerticalPosterArt;
    private bool _isSelected;
    private BitmapImage? _iconImage;
    private BitmapImage? _coverImage;
    private bool _isMissing;
    private bool _isPlaying;
    private string? _loadedIconPath;
    private DateTime? _loadedIconWriteTimeUtc;
    private string? _loadedCoverPath;
    private DateTime? _loadedCoverWriteTimeUtc;

    public GameEntry Game { get; }

    public GameCardViewModel(
        GameEntry game,
        Action<GameCardViewModel> onLaunch,
        Action<GameCardViewModel> onEdit,
        Action<GameCardViewModel> onDelete,
        Action<GameCardViewModel>? onRelocate = null,
        Action<GameCardViewModel>? onRename = null,
        Action<GameCardViewModel>? onChangeCategory = null,
        Action<GameCardViewModel>? onChangeIcon = null,
        Action<GameCardViewModel>? onChangeCover = null,
        Action<GameCardViewModel>? onFetchExeName = null,
        Action<GameCardViewModel>? onViewDetails = null,
        Action<GameCardViewModel>? onEditSteamAppId = null,
        Action<GameCardViewModel>? onRefreshMetadata = null,
        Action<GameCardViewModel>? onToggleFavorite = null,
        Action<GameCardViewModel>? onToggleHidden = null,
        Func<bool>? getUseVerticalPosterArt = null,
        bool deferHeavyInit = false,
        Action<GameCardViewModel>? onEndSession = null,
        Action<GameCardViewModel>? onForceClose = null,
        Action<GameCardViewModel>? onPrimaryClick = null,
        Action<GameCardViewModel>? onToggleSelect = null,
        Action<GameCardViewModel>? onRangeSelect = null)
    {
        _onEndSession = onEndSession;
        _onForceClose = onForceClose;
        _onPrimaryClick = onPrimaryClick;
        _onToggleSelect = onToggleSelect;
        _onRangeSelect = onRangeSelect;
        EndSessionCommand = new RelayCommand(() => _onEndSession?.Invoke(this));
        ForceCloseCommand = new RelayCommand(() => _onForceClose?.Invoke(this));
        // A plain left-click: Details normally, toggle-selection while the library is in Select
        // mode (LibraryViewModel decides). Ctrl+click / Shift+click always select, entering
        // Select mode if needed - the Explorer convention, alongside the toolbar's Select button.
        PrimaryClickCommand = new RelayCommand(() =>
        {
            if (_onPrimaryClick != null) _onPrimaryClick(this);
            else _onViewDetails?.Invoke(this);
        });
        ToggleSelectCommand = new RelayCommand(() => _onToggleSelect?.Invoke(this));
        RangeSelectCommand = new RelayCommand(() => _onRangeSelect?.Invoke(this));
        Game = game;
        _onLaunch = onLaunch;
        _onEdit = onEdit;
        _onDelete = onDelete;
        _onRelocate = onRelocate;
        _onRename = onRename;
        _onChangeCategory = onChangeCategory;
        _onChangeIcon = onChangeIcon;
        _onChangeCover = onChangeCover;
        _onFetchExeName = onFetchExeName;
        _onViewDetails = onViewDetails;
        _onEditSteamAppId = onEditSteamAppId;
        _onRefreshMetadata = onRefreshMetadata;
        _onToggleFavorite = onToggleFavorite;
        _onToggleHidden = onToggleHidden;
        _getUseVerticalPosterArt = getUseVerticalPosterArt;

        LaunchCommand = new RelayCommand(() => _onLaunch(this));
        EditCommand = new RelayCommand(() => _onEdit(this));
        DeleteCommand = new RelayCommand(() => _onDelete(this));
        ViewDetailsCommand = new RelayCommand(() => _onViewDetails?.Invoke(this));
        RelocateCommand = new RelayCommand(() => _onRelocate?.Invoke(this));
        RenameCommand = new RelayCommand(() => _onRename?.Invoke(this));
        ChangeCategoryCommand = new RelayCommand(() => _onChangeCategory?.Invoke(this));
        ChangeIconCommand = new RelayCommand(() => _onChangeIcon?.Invoke(this));
        ChangeCoverCommand = new RelayCommand(() => _onChangeCover?.Invoke(this));
        FetchExeNameCommand = new RelayCommand(() => _onFetchExeName?.Invoke(this));
        EditSteamAppIdCommand = new RelayCommand(() => _onEditSteamAppId?.Invoke(this));
        RefreshMetadataCommand = new RelayCommand(() => _onRefreshMetadata?.Invoke(this));
        ToggleFavoriteCommand = new RelayCommand(() =>
        {
            Game.IsFavorite = !Game.IsFavorite;
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteMenuLabel));
            _onToggleFavorite?.Invoke(this);
        });
        ToggleHiddenCommand = new RelayCommand(() =>
        {
            Game.IsHidden = !Game.IsHidden;
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(HideMenuLabel));
            _onToggleHidden?.Invoke(this);
        });
        OpenFolderCommand = new RelayCommand(OpenContainingFolder);
        OpenStoreCommand = new RelayCommand(OpenStorePage);
        OpenInSteamLibraryCommand = new RelayCommand(OpenInSteamLibrary);
        VerifyFilesCommand = new RelayCommand(VerifyFiles);

        // Skipped when loading the whole library at startup - the caller runs these off the UI
        // thread for every card at once instead (see ComputeHeavyState/ApplyHeavyState), so a
        // large library doesn't decode every icon/cover and stat every exe synchronously here.
        if (!deferHeavyInit)
        {
            CheckIsMissing();
            ReloadIcon();
            ReloadCover();
        }
    }

    /// <summary>
    /// Computes the "missing" flag and decodes both bitmaps without touching any UI-bound
    /// property - safe to call from a background thread (File.Exists is thread-safe and
    /// IconExtractorService.LoadBitmapSafely freezes the BitmapImages it returns). Pair with
    /// <see cref="ApplyHeavyState"/> on the UI thread to actually update the card.
    /// </summary>
    public (bool IsMissing, BitmapImage? Icon, DateTime? IconWriteTimeUtc, BitmapImage? Cover, DateTime? CoverWriteTimeUtc) ComputeHeavyState()
    {
        bool isMissing = ComputeIsMissing(Game);
        var icon = IconExtractorService.LoadBitmapSafely(Game.IconPath, decodePixelWidth: 64);
        var cover = !string.IsNullOrWhiteSpace(Game.CoverImagePath)
            ? IconExtractorService.LoadBitmapSafely(Game.CoverImagePath, decodePixelWidth: 368)
            : null;
        return (isMissing, icon, SafeGetLastWriteTimeUtc(Game.IconPath), cover, SafeGetLastWriteTimeUtc(Game.CoverImagePath));
    }

    /// <summary>
    /// Applies a result from <see cref="ComputeHeavyState"/>. Must run on the UI thread. Also
    /// records what was loaded so a later <see cref="ReloadIcon"/>/<see cref="ReloadCover"/> (e.g.
    /// from RefreshProperties during enrichment right after startup) can skip re-decoding.
    /// </summary>
    public void ApplyHeavyState(bool isMissing, BitmapImage? icon, DateTime? iconWriteTimeUtc, BitmapImage? cover, DateTime? coverWriteTimeUtc)
    {
        IsMissing = isMissing;
        IconImage = icon;
        _loadedIconPath = Game.IconPath;
        _loadedIconWriteTimeUtc = iconWriteTimeUtc;
        CoverImage = cover;
        _loadedCoverPath = Game.CoverImagePath;
        _loadedCoverWriteTimeUtc = coverWriteTimeUtc;
    }

    public string Id => Game.Id;
    public string Name => Game.Name;
    public string Category => Game.Category;
    public string? Hotkey => Game.Hotkey;
    public bool HasHotkey => !string.IsNullOrWhiteSpace(Game.Hotkey);
    public bool IsSteamGame => Game.IsSteamGame;
    public bool ForceSteamOverlayTag => Game.ForceSteamOverlayTag;
    public bool HasSteamOverlay => Game.HasSteamOverlay;
    public bool IsGogGame => Game.IsGogGame;
    public bool IsEaGame => Game.IsEaGame;
    public bool IsEpicGame => Game.IsEpicGame;
    public bool IsUbisoftGame => Game.IsUbisoftGame;
    public bool IsXboxGame => Game.IsXboxGame;
    /// <summary>True for a game added via a plain exe/shortcut/folder scan rather than any
    /// supported launcher - shown with the generic "Local Games" badge instead of a platform
    /// one. A forced Steam badge (<see cref="ForceSteamOverlayTag"/>) replaces the local badge
    /// rather than sitting beside it.</summary>
    public bool IsLocalGame => LibraryConstants.PlatformCategoryFor(Game) == null;
    /// <summary>The category pill is redundant while it still shows the platform's own placeholder
    /// name next to that platform's badge, or the meaningless "Uncategorized" default; it appears
    /// once the category is a real genre or a user's own choice.</summary>
    public bool ShowCategoryBadge =>
        !string.Equals(Category, LibraryConstants.Uncategorized, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Category, LibraryConstants.PlatformCategoryFor(Game), StringComparison.OrdinalIgnoreCase);
    /// <summary>Part of the library's Ctrl/Shift+click multi-selection (see LibraryViewModel).
    /// Purely UI state - never saved.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public ICommand PrimaryClickCommand { get; }
    public ICommand ToggleSelectCommand { get; }
    public ICommand RangeSelectCommand { get; }

    public bool IsFavorite => Game.IsFavorite;
    public string FavoriteMenuLabel => IsFavorite ? "Remove from Favorites" : "Add to Favorites";
    public bool IsHidden => Game.IsHidden;
    public string HideMenuLabel => IsHidden ? "Unhide" : "Hide";
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (_isMissing != value)
            {
                _isMissing = value;
                OnPropertyChanged();
            }
        }
    }

    public void CheckIsMissing()
    {
        IsMissing = ComputeIsMissing(Game);
    }

    /// <summary>True while ProcessLauncherService is tracking a session for this game (profile
    /// applied / launch in flight / game running). Drives the "PLAYING" badge and the End Session
    /// and Force Close menu items.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying != value)
            {
                _isPlaying = value;
                OnPropertyChanged();
            }
        }
    }

    public ICommand EndSessionCommand { get; }
    public ICommand ForceCloseCommand { get; }

    /// <summary>
    /// A non-Steam launcher protocol shortcut (Epic, GOG Galaxy, Ubisoft Connect, etc.) resolves
    /// to a "scheme://..." URL rather than a file, so File.Exists on it would always be false -
    /// exempt those the same way IsSteamGame already is.
    /// </summary>
    private static bool ComputeIsMissing(GameEntry game)
    {
        // An Xbox entry's exe path goes stale on every game update (the package folder is
        // versioned) and is re-resolved from the AUMID at launch, so it's never "missing" here.
        return !game.IsSteamGame && !game.IsXboxGame &&
            !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
            !ProcessLauncherService.IsNonFileProtocolUrl(game.ExecutablePath) &&
            !File.Exists(game.ExecutablePath);
    }
    public string PlaytimeDisplay => Game.PlaytimeDisplay;
    public string ListPlaytimeDisplay => string.IsNullOrWhiteSpace(Game.PlaytimeDisplay) ? "0 min played" : Game.PlaytimeDisplay;
    public string LastPlayedDisplay => Game.LastPlayedDisplay;

    public BitmapImage? IconImage
    {
        get => _iconImage;
        private set
        {
            _iconImage = value;
            OnPropertyChanged();
        }
    }

    public BitmapImage? CoverImage
    {
        get => _coverImage;
        private set
        {
            _coverImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCoverImage));
            OnPropertyChanged(nameof(ShowPosterArt));
        }
    }

    public bool HasCoverImage => CoverImage != null;

    public bool ShowPosterArt => HasCoverImage && (_getUseVerticalPosterArt?.Invoke() ?? true);

    public void NotifyPosterArtChanged()
    {
        OnPropertyChanged(nameof(ShowPosterArt));
    }

    public ICommand LaunchCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ViewDetailsCommand { get; }
    public ICommand EditSteamAppIdCommand { get; }
    public ICommand RefreshMetadataCommand { get; }
    public ICommand RelocateCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand FetchExeNameCommand { get; }
    public ICommand ChangeCategoryCommand { get; }
    public ICommand ChangeIconCommand { get; }
    public ICommand ChangeCoverCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand OpenInSteamLibraryCommand { get; }
    public ICommand VerifyFilesCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleHiddenCommand { get; }

    public string? SteamAppId => Game.SteamAppId;
    public bool HasSteamAppId => !string.IsNullOrWhiteSpace(Game.SteamAppId);
    public string SteamAppIdDisplay => HasSteamAppId ? $"ID: {SteamAppId}" : string.Empty;
    public string SteamAppIdTooltip => HasSteamAppId 
        ? $"Steam App ID: {SteamAppId}\nClick to update App ID or re-match metadata" 
        : "Click to link a Steam App ID for artwork and info";

    /// <summary>File.Exists + GetLastWriteTimeUtc, thread-safe, never throws.</summary>
    private static DateTime? SafeGetLastWriteTimeUtc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    /// <summary>
    /// Re-decodes the icon only if the path or the file's last-write time changed since the last
    /// load - a launch or playtime tick calls this every time nothing about the icon actually
    /// changed, so re-decoding unconditionally wasted a full bitmap decode for no reason.
    /// </summary>
    public void ReloadIcon()
    {
        var writeTimeUtc = SafeGetLastWriteTimeUtc(Game.IconPath);
        if (_loadedIconPath == Game.IconPath && _loadedIconWriteTimeUtc == writeTimeUtc)
        {
            return;
        }

        IconImage = IconExtractorService.LoadBitmapSafely(Game.IconPath, decodePixelWidth: 64);
        _loadedIconPath = Game.IconPath;
        _loadedIconWriteTimeUtc = writeTimeUtc;
    }

    /// <summary>Same skip-if-unchanged behavior as <see cref="ReloadIcon"/>, for the cover art.</summary>
    public void ReloadCover()
    {
        var writeTimeUtc = SafeGetLastWriteTimeUtc(Game.CoverImagePath);
        if (_loadedCoverPath == Game.CoverImagePath && _loadedCoverWriteTimeUtc == writeTimeUtc)
        {
            return;
        }

        CoverImage = !string.IsNullOrWhiteSpace(Game.CoverImagePath)
            ? IconExtractorService.LoadBitmapSafely(Game.CoverImagePath, decodePixelWidth: 368)
            : null;
        _loadedCoverPath = Game.CoverImagePath;
        _loadedCoverWriteTimeUtc = writeTimeUtc;
        OnPropertyChanged(nameof(ShowPosterArt));
    }

    public void RefreshProperties()
    {
        CheckIsMissing();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(Hotkey));
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(IsSteamGame));
        OnPropertyChanged(nameof(ForceSteamOverlayTag));
        OnPropertyChanged(nameof(HasSteamOverlay));
        OnPropertyChanged(nameof(IsGogGame));
        OnPropertyChanged(nameof(IsEaGame));
        OnPropertyChanged(nameof(IsEpicGame));
        OnPropertyChanged(nameof(IsUbisoftGame));
        OnPropertyChanged(nameof(IsXboxGame));
        OnPropertyChanged(nameof(IsLocalGame));
        OnPropertyChanged(nameof(ShowCategoryBadge));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteMenuLabel));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(HideMenuLabel));
        OnPropertyChanged(nameof(SteamAppId));
        OnPropertyChanged(nameof(HasSteamAppId));
        OnPropertyChanged(nameof(SteamAppIdDisplay));
        OnPropertyChanged(nameof(SteamAppIdTooltip));
        OnPropertyChanged(nameof(PlaytimeDisplay));
        OnPropertyChanged(nameof(LastPlayedDisplay));
        ReloadIcon();
        ReloadCover();
    }

    private void OpenContainingFolder()
    {
        try
        {
            string? targetPath = Game.ExecutablePath;
            if (File.Exists(targetPath))
            {
                using var proc = Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
            }
            else if (Directory.Exists(Game.WorkingDirectory))
            {
                using var proc = Process.Start("explorer.exe", $"\"{Game.WorkingDirectory}\"");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameCardViewModel", $"Error opening folder: {ex.Message}");
        }
    }

    private void OpenStorePage()
    {
        if (!string.IsNullOrEmpty(Game.SteamAppId))
        {
            SteamScannerService.OpenStorePage(Game.SteamAppId);
        }
    }

    private void OpenInSteamLibrary()
    {
        if (!string.IsNullOrEmpty(Game.SteamAppId))
        {
            SteamScannerService.OpenInSteamLibrary(Game.SteamAppId);
        }
    }

    private void VerifyFiles()
    {
        if (!string.IsNullOrEmpty(Game.SteamAppId))
        {
            SteamScannerService.VerifyGameFiles(Game.SteamAppId);
        }
    }
}
