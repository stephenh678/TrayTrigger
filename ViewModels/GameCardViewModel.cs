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
    private readonly Action<GameCardViewModel>? _onViewDetails;
    private readonly Action<GameCardViewModel>? _onChangeMatch;
    private readonly Action<GameCardViewModel>? _onEditSteamAppId;
    private readonly Action<GameCardViewModel>? _onRefreshMetadata;
    private readonly Action<GameCardViewModel>? _onToggleFavorite;
    private readonly Action<GameCardViewModel>? _onToggleHidden;
    private readonly Action<GameCardViewModel>? _onCloseGame;
    private readonly Action<GameCardViewModel>? _onForceClose;
    private readonly Action<GameCardViewModel>? _onPrimaryClick;
    private readonly Action<GameCardViewModel>? _onToggleSelect;
    private readonly Action<GameCardViewModel>? _onRangeSelect;
    /// <summary>Persist-and-refresh for the context menu's cascading quick settings (profile, cores,
    /// elevation, close-launcher) - the same fields Edit Game Properties writes, reached without
    /// opening it.</summary>
    private readonly Action<GameCardViewModel>? _onQuickSettingChanged;
    private readonly Func<bool>? _getUseVerticalPosterArt;
    private bool _isSelected;
    private BitmapImage? _iconImage;
    private BitmapImage? _coverImage;
    private GameAvailability _availability;
    private bool _isPlaying;
    private string? _loadedIconPath;
    private DateTime? _loadedIconWriteTimeUtc;
    private string? _loadedCoverPath;
    private DateTime? _loadedCoverWriteTimeUtc;
    // Set once the card has loaded that piece itself, so a background snapshot computed before
    // then (ApplyHeavyState) can't replace it with older state.
    private bool _iconLoaded;
    private bool _coverLoaded;
    private bool _availabilityChecked;

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
        Action<GameCardViewModel>? onViewDetails = null,
        Action<GameCardViewModel>? onChangeMatch = null,
        Action<GameCardViewModel>? onEditSteamAppId = null,
        Action<GameCardViewModel>? onRefreshMetadata = null,
        Action<GameCardViewModel>? onToggleFavorite = null,
        Action<GameCardViewModel>? onToggleHidden = null,
        Func<bool>? getUseVerticalPosterArt = null,
        bool deferHeavyInit = false,
        Action<GameCardViewModel>? onCloseGame = null,
        Action<GameCardViewModel>? onForceClose = null,
        Action<GameCardViewModel>? onPrimaryClick = null,
        Action<GameCardViewModel>? onToggleSelect = null,
        Action<GameCardViewModel>? onRangeSelect = null,
        Action<GameCardViewModel>? onQuickSettingChanged = null)
    {
        _onQuickSettingChanged = onQuickSettingChanged;
        _onCloseGame = onCloseGame;
        _onForceClose = onForceClose;
        _onPrimaryClick = onPrimaryClick;
        _onToggleSelect = onToggleSelect;
        _onRangeSelect = onRangeSelect;
        CloseGameCommand = new RelayCommand(() => _onCloseGame?.Invoke(this));
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
        _onViewDetails = onViewDetails;
        _onChangeMatch = onChangeMatch;
        _onEditSteamAppId = onEditSteamAppId;
        _onRefreshMetadata = onRefreshMetadata;
        _onToggleFavorite = onToggleFavorite;
        _onToggleHidden = onToggleHidden;
        _getUseVerticalPosterArt = getUseVerticalPosterArt;

        LaunchCommand = new RelayCommand(() => _onLaunch(this));
        EditCommand = new RelayCommand(() => _onEdit(this));
        DeleteCommand = new RelayCommand(() => _onDelete(this));
        ViewDetailsCommand = new RelayCommand(() => _onViewDetails?.Invoke(this));
        ChangeMatchCommand = new RelayCommand(() => _onChangeMatch?.Invoke(this));
        RelocateCommand = new RelayCommand(() => _onRelocate?.Invoke(this));
        RenameCommand = new RelayCommand(() => _onRename?.Invoke(this));
        ChangeCategoryCommand = new RelayCommand(() => _onChangeCategory?.Invoke(this));
        ChangeIconCommand = new RelayCommand(() => _onChangeIcon?.Invoke(this));
        ChangeCoverCommand = new RelayCommand(() => _onChangeCover?.Invoke(this));
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
        // Cascading quick settings: the same fields Edit Game Properties writes, reached from the
        // context menu without a dialog. Each one writes the entry, notifies its own check marks,
        // and hands off to the library to save.
        SetProfileCommand = new RelayCommand(p =>
        {
            if (p is not PerformanceProfileMode mode || Game.PerformanceProfile == mode) return;
            Game.PerformanceProfile = mode;
            NotifyQuickSettingsChanged();
            _onQuickSettingChanged?.Invoke(this);
        });
        SetCpuAffinityCommand = new RelayCommand(p =>
        {
            if (p is not CpuAffinityMode mode || Game.CpuAffinity == mode) return;
            Game.CpuAffinity = mode;
            NotifyQuickSettingsChanged();
            _onQuickSettingChanged?.Invoke(this);
        });
        ToggleRunAsAdminCommand = new RelayCommand(() =>
        {
            Game.RunAsAdmin = !Game.RunAsAdmin;
            NotifyQuickSettingsChanged();
            _onQuickSettingChanged?.Invoke(this);
        });
        ToggleCloseLauncherCommand = new RelayCommand(() =>
        {
            Game.CloseLauncherOnExit = !Game.CloseLauncherOnExit;
            NotifyQuickSettingsChanged();
            _onQuickSettingChanged?.Invoke(this);
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
            CheckAvailability();
            ReloadIcon();
            ReloadCover();
        }
    }

    /// <summary>
    /// Works out whether the game is still there and decodes both bitmaps without touching any
    /// UI-bound property - safe to call from a background thread (File.Exists is thread-safe, the
    /// install index is read-only once captured, and IconExtractorService.LoadBitmapSafely freezes
    /// the BitmapImages it returns). Pair with <see cref="ApplyHeavyState"/> on the UI thread to
    /// actually update the card.
    /// </summary>
    public (GameAvailability Availability, BitmapImage? Icon, DateTime? IconWriteTimeUtc, BitmapImage? Cover, DateTime? CoverWriteTimeUtc) ComputeHeavyState()
    {
        var availability = ComputeAvailability();
        var icon = IconExtractorService.LoadBitmapSafely(Game.IconPath, decodePixelWidth: 64);
        var cover = !string.IsNullOrWhiteSpace(Game.CoverImagePath)
            ? IconExtractorService.LoadBitmapSafely(Game.CoverImagePath, decodePixelWidth: 368)
            : null;
        return (availability, icon, SafeGetLastWriteTimeUtc(Game.IconPath), cover, SafeGetLastWriteTimeUtc(Game.CoverImagePath));
    }

    /// <summary>
    /// This entry's availability against the current install snapshot, without touching the card.
    /// Safe on a background thread; see <see cref="Availability"/> for applying the result.
    /// </summary>
    public GameAvailability ComputeAvailability() => InstalledGameIndex.Current.AvailabilityOf(Game);

    /// <summary>
    /// Applies a result from <see cref="ComputeHeavyState"/>. Must run on the UI thread. Also
    /// records what was loaded so a later <see cref="ReloadIcon"/>/<see cref="ReloadCover"/> (e.g.
    /// from RefreshProperties during enrichment right after startup) can skip re-decoding.
    /// </summary>
    public void ApplyHeavyState(GameAvailability availability, BitmapImage? icon, DateTime? iconWriteTimeUtc, BitmapImage? cover, DateTime? coverWriteTimeUtc)
    {
        // Computed a while ago on another thread. Whatever the card loaded since - a
        // RefreshProperties after startup enrichment downloaded a poster - is newer and stays.
        if (!_availabilityChecked) Availability = availability;
        if (!_iconLoaded)
        {
            IconImage = icon;
            _loadedIconPath = Game.IconPath;
            _loadedIconWriteTimeUtc = iconWriteTimeUtc;
            _iconLoaded = true;
        }
        if (!_coverLoaded)
        {
            CoverImage = cover;
            _loadedCoverPath = Game.CoverImagePath;
            _loadedCoverWriteTimeUtc = coverWriteTimeUtc;
            _coverLoaded = true;
        }
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
    public bool IsBattleNetGame => Game.IsBattleNetGame;
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
    /// <summary>True while this card's right-click menu is open. The menu takes the mouse, so the
    /// card's hover state drops the moment it opens; the views use this to keep the card
    /// highlighted, so it stays clear which game the menu is changing. UI state only.</summary>
    public bool IsContextMenuOpen
    {
        get => _isContextMenuOpen;
        set
        {
            if (_isContextMenuOpen != value)
            {
                _isContextMenuOpen = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _isContextMenuOpen;

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
    /// <summary>
    /// Whether the game is still there to play. Drives the card's dimming and its one marker
    /// badge; nothing about launching changes, because a launcher that reports a game uninstalled
    /// is also the place to reinstall it from, and a wrongly-marked card must not become
    /// unplayable. See <see cref="Services.InstalledGameIndex"/>.
    /// </summary>
    public GameAvailability Availability
    {
        get => _availability;
        set
        {
            if (_availability == value) return;

            _availability = value;
            // This marker changes what the user is told when they press Play, so the log says when
            // it went up or came down, for which game, and on what evidence.
            LoggingService.Verbose("GameCard", value switch
            {
                GameAvailability.NotInstalled => $"'{Game.Name}': marked not installed - its launcher no longer lists it.",
                GameAvailability.ExecutableMissing => $"'{Game.Name}': marked missing, '{Game.ExecutablePath}' is not on disk.",
                _ => $"'{Game.Name}': available again."
            });

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMissing));
            OnPropertyChanged(nameof(IsNotInstalled));
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(UnavailableLabel));
            OnPropertyChanged(nameof(TrayMenuLabel));
        }
    }

    /// <summary>A local game whose executable is not on disk - the one case "Locate Executable..." fixes.</summary>
    public bool IsMissing => _availability == GameAvailability.ExecutableMissing;

    /// <summary>A launcher game that launcher no longer has installed.</summary>
    public bool IsNotInstalled => _availability == GameAvailability.NotInstalled;

    /// <summary>Either marker: what dims the card and shows the badge.</summary>
    public bool IsUnavailable => _availability != GameAvailability.Available;

    /// <summary>Neither marker - the card reads plainly and Play goes straight through.</summary>
    public bool IsAvailable => _availability == GameAvailability.Available;

    /// <summary>The badge's wording, blank while there is nothing to mark. "Missing" would be wrong
    /// for a game deliberately uninstalled, and "not installed" would be a guess for a local exe
    /// that may only have moved.</summary>
    public string UnavailableLabel => _availability switch
    {
        GameAvailability.NotInstalled => "NOT INSTALLED",
        GameAvailability.ExecutableMissing => "MISSING",
        _ => string.Empty
    };

    /// <summary>The tray menu's suffix, which has the room for lower case but not for a poster.</summary>
    public string TrayMenuLabel => IsUnavailable
        ? $"{Name}  ({(IsNotInstalled ? "not installed" : "missing")})"
        : Name;

    public void CheckAvailability()
    {
        Availability = ComputeAvailability();
        // Only a snapshot that actually read a launcher settles the question. The starting index
        // has read nothing and calls every launcher game Available, so latching on it would both
        // record a wrong answer and make the startup pass's real one arrive too late to be
        // applied - which is what happened when enrichment refreshed a card first.
        _availabilityChecked = !ReferenceEquals(InstalledGameIndex.Current, InstalledGameIndex.Unknown);
    }

    /// <summary>True while ProcessLauncherService is tracking a session for this game (profile
    /// applied / launch in flight / game running). Drives the "PLAYING" badge and the Close Game
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

    public ICommand CloseGameCommand { get; }
    public ICommand ForceCloseCommand { get; }

    /// <summary>
    /// Blank until there is playtime to show. A card already says "Never played" (or when it was
    /// last played) beside this, and "0 min played" next to "Never played" said the same thing twice.
    /// </summary>
    public string PlaytimeDisplay => Game.CumulativePlaytimeMinutes > 0 ? Game.PlaytimeDisplay : string.Empty;
    /// <summary>The list view's Playtime column: a dash rather than a blank cell.</summary>
    public string ListPlaytimeDisplay => Game.CumulativePlaytimeMinutes > 0 ? Game.PlaytimeDisplay : "—";
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
    public ICommand ChangeMatchCommand { get; }
    public ICommand EditSteamAppIdCommand { get; }
    public ICommand RefreshMetadataCommand { get; }
    public ICommand RelocateCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand ChangeCategoryCommand { get; }
    public ICommand ChangeIconCommand { get; }
    public ICommand ChangeCoverCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand OpenInSteamLibraryCommand { get; }
    public ICommand VerifyFilesCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    /// <summary>Parameter: a <see cref="PerformanceProfileMode"/>.</summary>
    public ICommand SetProfileCommand { get; }
    /// <summary>Parameter: a <see cref="CpuAffinityMode"/>.</summary>
    public ICommand SetCpuAffinityCommand { get; }
    public ICommand ToggleRunAsAdminCommand { get; }
    public ICommand ToggleCloseLauncherCommand { get; }

    // Check marks for the cascading quick-setting submenus.
    public bool ProfileIsOff => Game.PerformanceProfile == PerformanceProfileMode.Off;
    public bool ProfileIsOptimized => Game.PerformanceProfile == PerformanceProfileMode.Optimized;
    public bool ProfileIsAggressive => Game.PerformanceProfile == PerformanceProfileMode.Aggressive;
    public bool CpuAffinityIsDefault => Game.CpuAffinity == CpuAffinityMode.Default;
    public bool CpuAffinityIsPerformanceCores => Game.CpuAffinity == CpuAffinityMode.PerformanceCoresOnly;
    public bool RunAsAdmin => Game.RunAsAdmin;
    public bool CloseLauncherOnExit => Game.CloseLauncherOnExit;

    /// <summary>
    /// True when this entry launches through a platform client there is something to close - the
    /// "Close Launcher After Game Exits" item is meaningless for a plain local exe, which has no
    /// launcher behind it.
    /// </summary>
    public bool HasLauncherToClose => LibraryFilterViewModel.PlatformOf(Game) != null;

    /// <summary>
    /// Whether this machine has a hybrid (P-core/E-core) CPU. The core-pinning submenu is hidden
    /// elsewhere: on a uniform CPU "Performance Cores Only" is a documented no-op, and an option
    /// that cannot do anything is worse than no option. Edit Game Properties still offers it with
    /// a hint, for a library that will move to a hybrid machine. The topology is read once per
    /// process and cached, so this is free per card.
    /// </summary>
    public bool IsHybridCpu => CpuTopologyService.GetTopology().IsHybrid;

    private void NotifyQuickSettingsChanged()
    {
        OnPropertyChanged(nameof(ProfileIsOff));
        OnPropertyChanged(nameof(ProfileIsOptimized));
        OnPropertyChanged(nameof(ProfileIsAggressive));
        OnPropertyChanged(nameof(CpuAffinityIsDefault));
        OnPropertyChanged(nameof(CpuAffinityIsPerformanceCores));
        OnPropertyChanged(nameof(RunAsAdmin));
        OnPropertyChanged(nameof(CloseLauncherOnExit));
        OnPropertyChanged(nameof(HasLauncherToClose));
    }

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
        try { return File.GetLastWriteTimeUtc(path); } catch { /* a file that cannot be read has no timestamp to compare */ return null; }
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
        _iconLoaded = true;
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
        _coverLoaded = true;
        OnPropertyChanged(nameof(ShowPosterArt));
    }

    public void RefreshProperties()
    {
        CheckAvailability();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TrayMenuLabel));
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
        OnPropertyChanged(nameof(IsBattleNetGame));
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
        OnPropertyChanged(nameof(ListPlaytimeDisplay));
        OnPropertyChanged(nameof(LastPlayedDisplay));
        NotifyQuickSettingsChanged();
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
                using var proc = Process.Start(SystemExecutables.Explorer, $"/select,\"{targetPath}\"");
            }
            else if (Directory.Exists(Game.WorkingDirectory))
            {
                // Follow a junction to the real folder (an Xbox game's is a WindowsApps package
                // folder linked to D:\XboxGames\<Game>\Content). A junction whose target is gone
                // still passes Directory.Exists, and Explorer shows Documents for it instead.
                if (LinkedDirectory.Resolve(Game.WorkingDirectory, out string folder) == LinkedDirectoryState.BrokenLink)
                {
                    LoggingService.Warn("GameCard", $"Cannot open the folder for '{Game.Name}': '{Game.WorkingDirectory}' links to a folder or drive that no longer exists.");
                    Views.ModernDialog.ShowWarning(WindowHelper.ActiveOwner(), "Folder Not Found",
                        $"The folder for \"{Game.Name}\" no longer exists.",
                        $"{Game.WorkingDirectory} points to a folder or drive that has been removed or isn't connected.");
                }
                else
                {
                    using var proc = Process.Start(SystemExecutables.Explorer, $"\"{folder}\"");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameCard", $"Error opening folder: {ex.Message}");
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
