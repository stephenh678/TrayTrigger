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
    private readonly Func<bool>? _getUseVerticalPosterArt;
    private BitmapImage? _iconImage;
    private BitmapImage? _coverImage;
    private bool _isMissing;

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
        Func<bool>? getUseVerticalPosterArt = null)
    {
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
            _onToggleFavorite?.Invoke(this);
        });
        OpenFolderCommand = new RelayCommand(OpenContainingFolder);
        OpenStoreCommand = new RelayCommand(OpenStorePage);
        OpenInSteamLibraryCommand = new RelayCommand(OpenInSteamLibrary);
        VerifyFilesCommand = new RelayCommand(VerifyFiles);

        CheckIsMissing();
        ReloadIcon();
        ReloadCover();
    }

    public string Id => Game.Id;
    public string Name => Game.Name;
    public string Category => Game.Category;
    public string? Hotkey => Game.Hotkey;
    public bool HasHotkey => !string.IsNullOrWhiteSpace(Game.Hotkey);
    public bool IsSteamGame => Game.IsSteamGame;
    public bool ForceSteamOverlayTag => Game.ForceSteamOverlayTag;
    public bool HasSteamOverlay => Game.HasSteamOverlay;
    public bool ShowCategoryBadge => !HasSteamOverlay || !string.Equals(Category, "Steam", StringComparison.OrdinalIgnoreCase);
    public bool IsFavorite => Game.IsFavorite;
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
        IsMissing = !IsSteamGame && !string.IsNullOrWhiteSpace(Game.ExecutablePath) && !File.Exists(Game.ExecutablePath);
    }
    public string PlaytimeDisplay => Game.PlaytimeDisplay;
    public string ListPlaytimeDisplay => string.IsNullOrWhiteSpace(Game.PlaytimeDisplay) ? "—" : Game.PlaytimeDisplay;
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

    public string? SteamAppId => Game.SteamAppId;
    public bool HasSteamAppId => !string.IsNullOrWhiteSpace(Game.SteamAppId);
    public string SteamAppIdDisplay => HasSteamAppId ? $"ID: {SteamAppId}" : string.Empty;
    public string SteamAppIdTooltip => HasSteamAppId 
        ? $"Steam App ID: {SteamAppId}\nClick to update App ID or re-match metadata" 
        : "Click to link a Steam App ID for artwork and info";

    public void ReloadIcon()
    {
        IconImage = IconExtractorService.LoadBitmapSafely(Game.IconPath, decodePixelWidth: 64);
    }

    public void ReloadCover()
    {
        CoverImage = !string.IsNullOrWhiteSpace(Game.CoverImagePath)
            ? IconExtractorService.LoadBitmapSafely(Game.CoverImagePath, decodePixelWidth: 368)
            : null;
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
        OnPropertyChanged(nameof(ShowCategoryBadge));
        OnPropertyChanged(nameof(IsFavorite));
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
