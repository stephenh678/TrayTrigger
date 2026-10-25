using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

public enum SettingsCategoryTab
{
    All,
    General,
    Library,
    TrayMenu,
    PerformanceTweaks,
    Diagnostics
}

/// <summary>
/// Dedicated ViewModel encapsulating all application settings, diagnostic logging,
/// and external integration preferences.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly StorageService _storageService;
    private readonly StartupManager _startupManager;
    private readonly TrayPromotionService _trayPromotionService;
    private readonly SteamScannerService _steamScannerService;

    // Callbacks to notify parent/services of external side-effects
    private readonly Action? _onTrayMenuSettingChanged;
    private readonly Action? _onPosterArtSettingChanged;
    private readonly Action? _onHotkeySettingChanged;
    private readonly Func<bool, Task>? _onRequestEnrichLibrary;
    private readonly Func<IProgress<string>, Task>? _onRequestRefreshAllPosters;
    private readonly Action? _onRequestOpenScanForGames;
    private readonly Func<Task>? _onCheckForUpdates;
    private readonly Func<string>? _getUpdateStatusText;
    private readonly Func<DetectedLauncher, (int Total, int Hidden)>? _getPlatformGameCount;
    private readonly Func<DetectedLauncher, int>? _removePlatformGames;
    private readonly Action? _onProfileTweaksReset;

    public const string ViewModePosterGrid = "Poster Grid";
    public const string ViewModeExtraLarge = "Extra Large";
    public const string ViewModeCompactIcons = "Compact Icons";
    public const string ViewModeDetailsList = "Details List";

    public AppSettings Settings => _settings;

    private SettingsCategoryTab _selectedTab = SettingsCategoryTab.All;
    public SettingsCategoryTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsAllTab));
                OnPropertyChanged(nameof(IsGeneralTab));
                OnPropertyChanged(nameof(IsLibraryTab));
                OnPropertyChanged(nameof(IsTrayMenuTab));
                OnPropertyChanged(nameof(IsPerformanceTweaksTab));
                OnPropertyChanged(nameof(IsDiagnosticsTab));
                OnPropertyChanged(nameof(ShowGeneralSection));
                OnPropertyChanged(nameof(ShowLibrarySection));
                OnPropertyChanged(nameof(ShowTrayMenuSection));
                OnPropertyChanged(nameof(ShowPerformanceTweaksSection));
                OnPropertyChanged(nameof(ShowDiagnosticsSection));
            }
        }
    }

    private string _settingsSearchText = string.Empty;
    /// <summary>
    /// The card search beside the tabs (see Views/CardSearch). It searches the tab you're on (All
    /// searches everything) and stays as you switch tabs, filtering each one in turn.
    /// </summary>
    public string SettingsSearchText
    {
        get => _settingsSearchText;
        set => SetProperty(ref _settingsSearchText, value ?? string.Empty);
    }

    public bool IsAllTab => SelectedTab == SettingsCategoryTab.All;
    public bool IsGeneralTab => SelectedTab == SettingsCategoryTab.General;
    public bool IsLibraryTab => SelectedTab == SettingsCategoryTab.Library;
    public bool IsTrayMenuTab => SelectedTab == SettingsCategoryTab.TrayMenu;
    public bool IsPerformanceTweaksTab => SelectedTab == SettingsCategoryTab.PerformanceTweaks;
    public bool IsDiagnosticsTab => SelectedTab == SettingsCategoryTab.Diagnostics;

    public bool ShowGeneralSection => SelectedTab == SettingsCategoryTab.All || SelectedTab == SettingsCategoryTab.General;
    public bool ShowLibrarySection => SelectedTab == SettingsCategoryTab.All || SelectedTab == SettingsCategoryTab.Library;
    public bool ShowTrayMenuSection => SelectedTab == SettingsCategoryTab.All || SelectedTab == SettingsCategoryTab.TrayMenu;
    public bool ShowPerformanceTweaksSection => SelectedTab == SettingsCategoryTab.All || SelectedTab == SettingsCategoryTab.PerformanceTweaks;
    public bool ShowDiagnosticsSection => SelectedTab == SettingsCategoryTab.All || SelectedTab == SettingsCategoryTab.Diagnostics;

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set { if (SetProperty(ref _statusMessage, value)) LoggingService.Shown("Settings status", value); }
    }

    public ObservableCollection<string> TraySortOptions { get; } = new()
    {
        // Same order as the Library's sort list.
        "Alphabetical (A - Z)",
        "Alphabetical (Z - A)",
        "Most Recently Played",
        "Cumulative Playtime"
    };

    public ObservableCollection<string> ViewModeOptions { get; } = new()
    {
        ViewModeExtraLarge,
        ViewModePosterGrid,
        ViewModeCompactIcons,
        ViewModeDetailsList
    };

    public const string SensitivityRelaxed = "Relaxed (50%)";
    public const string SensitivityBalanced = "Balanced (60% - Default)";
    public const string SensitivityStrict = "Strict (75%)";
    public const string SensitivityVeryStrict = "Very Strict (85%)";

    public ObservableCollection<string> ConfidenceThresholdOptions { get; } = new()
    {
        SensitivityRelaxed,
        SensitivityBalanced,
        SensitivityStrict,
        SensitivityVeryStrict
    };

    // "Refresh game info": how long the cached Steam / RAWG details behind Game Details are
    // trusted before the window re-fetches them in the background. See MetadataFreshness.
    public const string RefreshEveryOpen = "Every time details open";
    public const string RefreshDaily = "Daily";
    public const string RefreshEvery3Days = "Every 3 days (Default)";
    public const string RefreshWeekly = "Weekly";
    public const string RefreshMonthly = "Monthly";
    public const string RefreshNever = "Never (manual refresh only)";

    public ObservableCollection<string> MetadataRefreshOptions { get; } = new()
    {
        RefreshEveryOpen,
        RefreshDaily,
        RefreshEvery3Days,
        RefreshWeekly,
        RefreshMonthly,
        RefreshNever
    };

    public string MetadataRefreshOption
    {
        get => _settings.MetadataRefreshInterval switch
        {
            MetadataRefreshInterval.EveryOpen => RefreshEveryOpen,
            MetadataRefreshInterval.Daily => RefreshDaily,
            MetadataRefreshInterval.Weekly => RefreshWeekly,
            MetadataRefreshInterval.Monthly => RefreshMonthly,
            MetadataRefreshInterval.Never => RefreshNever,
            _ => RefreshEvery3Days
        };
        set
        {
            var target = value switch
            {
                RefreshEveryOpen => MetadataRefreshInterval.EveryOpen,
                RefreshDaily => MetadataRefreshInterval.Daily,
                RefreshWeekly => MetadataRefreshInterval.Weekly,
                RefreshMonthly => MetadataRefreshInterval.Monthly,
                RefreshNever => MetadataRefreshInterval.Never,
                _ => MetadataRefreshInterval.Every3Days
            };
            if (_settings.MetadataRefreshInterval != target)
            {
                _settings.MetadataRefreshInterval = target;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Drops both detail caches (not posters). Every game re-fetches on its next open.</summary>
    public ICommand ClearMetadataCacheCommand { get; }

    // Category Tab Commands
    public ICommand SelectAllTabCommand { get; }
    public ICommand SelectGeneralTabCommand { get; }
    public ICommand SelectLibraryTabCommand { get; }
    public ICommand SelectTrayMenuTabCommand { get; }
    public ICommand SelectPerformanceTweaksTabCommand { get; }
    public ICommand SelectDiagnosticsTabCommand { get; }

    // Commands
    public ICommand SaveInWindowSettingsCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand OpenStorageFolderCommand { get; }
    public ICommand OpenCacheFolderCommand { get; }
    public ICommand OpenLogFileCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand TogglePosterDetailsOnHoverCommand { get; }
    public ICommand OpenTaskbarSettingsCommand { get; }

    /// <summary>Settings > General > Appearance: the theme picker.</summary>
    public ThemeSettingsViewModel Appearance { get; }
    public ICommand OpenSteamGridDbSiteCommand { get; }
    public ICommand OpenRawgSiteCommand { get; }
    public ICommand OpenScanForGamesCommand { get; }
    public ICommand RefreshAllPostersCommand { get; }
    public ICommand AddScanLocationCommand { get; }
    public ICommand RemoveScanLocationCommand { get; }
    public ICommand RefreshSteamScanLocationsCommand { get; }
    public ICommand RemoveIgnoredGamePathCommand { get; }
    public ICommand AddIgnoredFolderCommand { get; }
    public ICommand BrowseDefaultPreLaunchScriptCommand { get; }
    public ICommand BrowseDefaultPostExitScriptCommand { get; }
    public ICommand TestDefaultPreLaunchScriptCommand { get; }
    public ICommand TestDefaultPostExitScriptCommand { get; }
    public ICommand NewDefaultPreLaunchScriptCommand { get; }
    public ICommand NewDefaultPostExitScriptCommand { get; }
    public ICommand EditDefaultPreLaunchScriptCommand { get; }
    public ICommand EditDefaultPostExitScriptCommand { get; }
    public ICommand OpenScriptsFolderCommand { get; }

    /// <summary>Steam's own library folders, auto-detected and kept in sync by
    /// <see cref="ScanLocationService"/>. Shown under the Steam integration toggle (not in the
    /// manual Scan Locations list) since they belong to the integration, not the user.</summary>
    public ObservableCollection<ScanLocationRowViewModel> SteamLibraryLocations { get; } = new();
    public bool HasNoSteamLibraryLocations => SteamLibraryLocations.Count == 0;

    /// <summary>Folders the user added by hand for "Scan for Games" to look in.</summary>
    public ObservableCollection<ScanLocationRowViewModel> ScanLocations { get; } = new();
    public bool HasNoScanLocations => ScanLocations.Count == 0;

    public ObservableCollection<IgnoredGamePathRowViewModel> IgnoredGamePaths { get; } = new();
    public bool HasNoIgnoredGamePaths => IgnoredGamePaths.Count == 0;

    /// <summary>The NVIDIA DLSS card. Set by the main view model once the services it needs exist.</summary>
    public DlssSettingsViewModel? Dlss { get; set; }

    public SettingsViewModel(
        AppSettings settings,
        StorageService storageService,
        StartupManager startupManager,
        TrayPromotionService trayPromotionService,
        SteamScannerService steamScannerService,
        Action? onTrayMenuSettingChanged = null,
        Action? onPosterArtSettingChanged = null,
        Action? onHotkeySettingChanged = null,
        Func<bool, Task>? onRequestEnrichLibrary = null,
        Func<IProgress<string>, Task>? onRequestRefreshAllPosters = null,
        Action? onRequestOpenScanForGames = null,
        Func<Task>? onCheckForUpdates = null,
        Func<string>? getUpdateStatusText = null,
        Func<DetectedLauncher, (int Total, int Hidden)>? getPlatformGameCount = null,
        Func<DetectedLauncher, int>? removePlatformGames = null,
        Action? onProfileTweaksReset = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _startupManager = startupManager ?? throw new ArgumentNullException(nameof(startupManager));
        _trayPromotionService = trayPromotionService ?? throw new ArgumentNullException(nameof(trayPromotionService));
        _steamScannerService = steamScannerService ?? throw new ArgumentNullException(nameof(steamScannerService));

        _onTrayMenuSettingChanged = onTrayMenuSettingChanged;
        _onPosterArtSettingChanged = onPosterArtSettingChanged;
        _onHotkeySettingChanged = onHotkeySettingChanged;
        _onRequestEnrichLibrary = onRequestEnrichLibrary;
        _onRequestRefreshAllPosters = onRequestRefreshAllPosters;
        _onRequestOpenScanForGames = onRequestOpenScanForGames;
        _onCheckForUpdates = onCheckForUpdates;
        _getUpdateStatusText = getUpdateStatusText;
        _getPlatformGameCount = getPlatformGameCount;
        _removePlatformGames = removePlatformGames;
        _onProfileTweaksReset = onProfileTweaksReset;

        // Initialize Tab Commands
        SelectAllTabCommand = new RelayCommand(() => SelectedTab = SettingsCategoryTab.All);
        SelectGeneralTabCommand = new RelayCommand(() => SelectedTab = SettingsCategoryTab.General);
        SelectLibraryTabCommand = new RelayCommand(() => SelectedTab = SettingsCategoryTab.Library);
        SelectTrayMenuTabCommand = new RelayCommand(() => SelectedTab = SettingsCategoryTab.TrayMenu);
        SelectPerformanceTweaksTabCommand = new RelayCommand(() => SelectedTab = SettingsCategoryTab.PerformanceTweaks);
        SelectDiagnosticsTabCommand = new RelayCommand(() => SelectedTab = SettingsCategoryTab.Diagnostics);

        SaveInWindowSettingsCommand = new RelayCommand(SaveInWindowSettings);
        ResetSettingsCommand = new RelayCommand(() => ResetSettingsToDefaults());
        OpenStorageFolderCommand = new RelayCommand(OpenStorageFolder);
        OpenCacheFolderCommand = new RelayCommand(OpenCacheFolder);
        OpenLogFileCommand = new RelayCommand(OpenLogFile);
        ClearLogCommand = new RelayCommand(ClearLogFile);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        SetViewModeCommand = new RelayCommand(mode => LibraryViewMode = mode?.ToString() ?? ViewModePosterGrid);
        TogglePosterDetailsOnHoverCommand = new RelayCommand(() => PosterDetailsOnHover = !PosterDetailsOnHover);
        PosterDetails = new PosterDetailsViewModel(() => _settings.PosterDetailsAtRest, () => AutoSaveSettings(nameof(PosterDetails)));
        OpenTaskbarSettingsCommand = new RelayCommand(TrayPromotionService.OpenWindowsTaskbarSettings);
        Appearance = new ThemeSettingsViewModel(_settings, () => AutoSaveSettings());
        OpenSteamGridDbSiteCommand = new RelayCommand(() => HelpCommands.OpenUrl.Execute(SteamGridDbService.KeyPageUrl));
        OpenRawgSiteCommand = new RelayCommand(() => HelpCommands.OpenUrl.Execute(RawgService.KeyPageUrl));
        ClearMetadataCacheCommand = new RelayCommand(() =>
        {
            SteamMetadataService.ClearCache();
            RawgService.ClearCache();
            LoggingService.Info("Settings", "Cleared the cached Steam and RAWG game info.");
            StatusMessage = "Cached game info cleared. Each game fetches fresh details the next time you open it.";
        });
        OpenScanForGamesCommand = new RelayCommand(() => _onRequestOpenScanForGames?.Invoke());
        BrowseDefaultPreLaunchScriptCommand = new RelayCommand(() => BrowseDefaultScript(isPreLaunch: true));
        BrowseDefaultPostExitScriptCommand = new RelayCommand(() => BrowseDefaultScript(isPreLaunch: false));
        TestDefaultPreLaunchScriptCommand = new AsyncRelayCommand(() => TestDefaultScriptAsync(isPreLaunch: true), () => !IsTestingDefaultScript && HasDefaultPreLaunchScript);
        TestDefaultPostExitScriptCommand = new AsyncRelayCommand(() => TestDefaultScriptAsync(isPreLaunch: false), () => !IsTestingDefaultScript && HasDefaultPostExitScript);
        NewDefaultPreLaunchScriptCommand = new RelayCommand(() => NewDefaultScript(isPreLaunch: true));
        NewDefaultPostExitScriptCommand = new RelayCommand(() => NewDefaultScript(isPreLaunch: false));
        EditDefaultPreLaunchScriptCommand = new RelayCommand(() => ScriptLibraryService.OpenInEditor(DefaultPreLaunchScriptPath), () => HasDefaultPreLaunchScript);
        EditDefaultPostExitScriptCommand = new RelayCommand(() => ScriptLibraryService.OpenInEditor(DefaultPostExitScriptPath), () => HasDefaultPostExitScript);
        OpenScriptsFolderCommand = new RelayCommand(() => ScriptLibrary.OpenFolder());
        AddScanLocationCommand = new RelayCommand(AddScanLocation);
        RemoveScanLocationCommand = new RelayCommand(param =>
        {
            if (param is ScanLocationRowViewModel row) RemoveScanLocation(row);
        });
        RefreshSteamScanLocationsCommand = new RelayCommand(RefreshSteamScanLocations);
        RebuildScanLocationRows();

        AddIgnoredFolderCommand = new RelayCommand(AddIgnoredFolder);
        RemoveIgnoredGamePathCommand = new RelayCommand(param =>
        {
            if (param is IgnoredGamePathRowViewModel row) RemoveIgnoredGamePath(row);
        });
        RebuildIgnoredGamePathRows();
        RefreshAllPostersCommand = new AsyncRelayCommand(RefreshAllPostersAsync);

        SteamGridDbKeyCheck = new ApiKeyCheckViewModel("SteamGridDB", () => _settings.SteamGridDbApiKey, SteamGridDbService.CheckApiKeyAsync);
        SteamGridDbKeyCheck.KeyAccepted += OnSteamGridDbKeyAccepted;
        RawgKeyCheck = new ApiKeyCheckViewModel("RAWG", () => _settings.RawgApiKey, RawgService.CheckApiKeyAsync);
        RawgKeyCheck.KeyAccepted += OnRawgKeyAccepted;
        // A key already saved and switched on has done its catching up; only a new one needs to.
        _steamGridDbKeyCaughtUp = SteamGridDbApiKeyOrNull;
        _rawgKeyCaughtUp = RawgApiKeyOrNull;
        CheckUpdatesInSettingsCommand = new AsyncRelayCommand(async () =>
        {
            if (_onCheckForUpdates != null)
            {
                await _onCheckForUpdates();
            }
        });
    }

    // --- Windows Startup & System Tray Integration ---

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows != value)
            {
                _settings.StartWithWindows = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanConfigureMinimized));
                _startupManager.SetStartupEnabled(value, StartMinimizedToTray);
                AutoSaveSettings();
            }
        }
    }

    public bool StartMinimizedToTray
    {
        get => _settings.StartMinimizedToTray;
        set
        {
            if (_settings.StartMinimizedToTray != value)
            {
                _settings.StartMinimizedToTray = value;
                OnPropertyChanged();
                if (StartWithWindows)
                {
                    _startupManager.SetStartupEnabled(StartWithWindows, value);
                }
                AutoSaveSettings();
            }
        }
    }

    public bool CanConfigureMinimized => StartWithWindows;

    /// <summary>
    /// Re-reads the real registry state (Run key + Task Manager's StartupApproved flag) and
    /// updates the toggle if it disagrees with what's stored in settings.json - e.g. the user
    /// disabled TrayTrigger from Task Manager's Startup tab, or a portable copy moved folders.
    /// Called when the Settings page is opened, not on every property access, since it's a
    /// registry read.
    /// </summary>
    public void ReconcileStartWithWindows()
    {
        _startupManager.ReconcilePath();

        bool actuallyEnabled = _startupManager.IsStartupEnabled();
        if (_settings.StartWithWindows != actuallyEnabled)
        {
            _settings.StartWithWindows = actuallyEnabled;
            OnPropertyChanged(nameof(StartWithWindows));
            OnPropertyChanged(nameof(CanConfigureMinimized));
            AutoSaveSettings();
        }
    }

    public bool AlwaysShowTrayIcon
    {
        get => _settings.AlwaysShowTrayIcon;
        set
        {
            if (_settings.AlwaysShowTrayIcon != value)
            {
                _settings.AlwaysShowTrayIcon = value;
                OnPropertyChanged();
                AutoSaveSettings();
                var outcome = _trayPromotionService.TrySetAlwaysShow(value, out string msg);
                LoggingService.Info("Settings", $"AlwaysShowTrayIcon set to {value}: {outcome} - {msg}");
                TrayPromotionStatus = msg;
            }
        }
    }

    /// <summary>
    /// What happened the last time the "always show" flag was written - shown under the checkbox,
    /// because a request Windows silently dropped used to look identical to one it honoured.
    /// Set from the setter above and from the startup retry in App.
    /// </summary>
    public string TrayPromotionStatus
    {
        get => _trayPromotionStatus;
        set
        {
            if (_trayPromotionStatus == value) return;
            _trayPromotionStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTrayPromotionStatus));
        }
    }
    private string _trayPromotionStatus = string.Empty;
    public bool HasTrayPromotionStatus => !string.IsNullOrEmpty(_trayPromotionStatus);

    public bool MinimizeOnGameLaunch
    {
        get => _settings.MinimizeOnGameLaunch;
        set
        {
            if (_settings.MinimizeOnGameLaunch != value)
            {
                _settings.MinimizeOnGameLaunch = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public bool KeepLaunchersMinimized
    {
        get => _settings.KeepLaunchersMinimized;
        set
        {
            if (_settings.KeepLaunchersMinimized != value)
            {
                _settings.KeepLaunchersMinimized = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public bool ShowLaunchPopup
    {
        get => _settings.ShowLaunchPopup;
        set
        {
            if (_settings.ShowLaunchPopup != value)
            {
                _settings.ShowLaunchPopup = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public bool ShowLaunchPopupOnEveryLaunch
    {
        get => _settings.ShowLaunchPopupOnEveryLaunch;
        set
        {
            if (_settings.ShowLaunchPopupOnEveryLaunch != value)
            {
                _settings.ShowLaunchPopupOnEveryLaunch = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    // --- Updates & Version Control Preferences ---

    public bool AutoCheckForUpdates
    {
        get => _settings.AutoCheckForUpdates;
        set
        {
            if (_settings.AutoCheckForUpdates != value)
            {
                _settings.AutoCheckForUpdates = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public bool IncludePrereleaseUpdates
    {
        get => _settings.IncludePrereleaseUpdates;
        set
        {
            if (_settings.IncludePrereleaseUpdates != value)
            {
                _settings.IncludePrereleaseUpdates = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    // Update checking itself lives in MainViewModel (the single implementation, per L-10);
    // this just displays that status and triggers it via the callbacks passed at construction.
    public string UpdateStatusText => _getUpdateStatusText?.Invoke() ?? string.Empty;

    public void NotifyUpdateStatusChanged() => OnPropertyChanged(nameof(UpdateStatusText));

    public ICommand CheckUpdatesInSettingsCommand { get; }

    // --- System Tray Context Menu Preferences ---

    public bool ShowTraySearch
    {
        get => _settings.ShowTraySearch;
        set
        {
            if (_settings.ShowTraySearch != value)
            {
                _settings.ShowTraySearch = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public bool ShowTrayMenuIcons
    {
        get => _settings.ShowTrayMenuIcons;
        set
        {
            if (_settings.ShowTrayMenuIcons != value)
            {
                _settings.ShowTrayMenuIcons = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public bool CompactTrayMenu
    {
        get => _settings.CompactTrayMenu;
        set
        {
            if (_settings.CompactTrayMenu != value)
            {
                _settings.CompactTrayMenu = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public bool TrayLeftClickOpensMenu
    {
        get => _settings.TrayLeftClickOpensMenu;
        set
        {
            if (_settings.TrayLeftClickOpensMenu != value)
            {
                _settings.TrayLeftClickOpensMenu = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public bool ShowRecentInTray
    {
        get => _settings.ShowRecentInTray;
        set
        {
            if (_settings.ShowRecentInTray != value)
            {
                _settings.ShowRecentInTray = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public int MaxRecentInTray
    {
        get => _settings.MaxRecentInTray;
        set
        {
            int clamped = Math.Max(0, value);
            if (_settings.MaxRecentInTray != clamped)
            {
                _settings.MaxRecentInTray = clamped;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public string RecentTraySortOption
    {
        get => _settings.RecentTraySortOption;
        set
        {
            if (_settings.RecentTraySortOption != value)
            {
                _settings.RecentTraySortOption = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public bool ShowFavoritesInTray
    {
        get => _settings.ShowFavoritesInTray;
        set
        {
            if (_settings.ShowFavoritesInTray != value)
            {
                _settings.ShowFavoritesInTray = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public int MaxFavoritesInTray
    {
        get => _settings.MaxFavoritesInTray;
        set
        {
            int clamped = Math.Max(0, value);
            if (_settings.MaxFavoritesInTray != clamped)
            {
                _settings.MaxFavoritesInTray = clamped;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public string FavoritesTraySortOption
    {
        get => _settings.FavoritesTraySortOption;
        set
        {
            if (_settings.FavoritesTraySortOption != value)
            {
                _settings.FavoritesTraySortOption = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public string TrayMenuSortOption
    {
        get => string.IsNullOrWhiteSpace(_settings.TrayMenuSortOption) ? "Alphabetical (A - Z)" : _settings.TrayMenuSortOption;
        set
        {
            if (_settings.TrayMenuSortOption != value)
            {
                _settings.TrayMenuSortOption = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public bool GroupTrayMenuByCategory
    {
        get => _settings.GroupTrayMenuByCategory;
        set
        {
            if (_settings.GroupTrayMenuByCategory != value)
            {
                _settings.GroupTrayMenuByCategory = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    // --- Game Titles & Categories, Art & Info Sources ---

    public bool SearchOfficialTitleOnline
    {
        get => _settings.SearchOfficialTitleOnline;
        set
        {
            if (_settings.SearchOfficialTitleOnline != value)
            {
                _settings.SearchOfficialTitleOnline = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public string OnlineMatchSensitivity
    {
        get
        {
            if (_settings.OnlineMatchConfidenceThreshold <= 0.52)
                return SensitivityRelaxed;
            if (_settings.OnlineMatchConfidenceThreshold <= 0.67)
                return SensitivityBalanced;
            if (_settings.OnlineMatchConfidenceThreshold <= 0.80)
                return SensitivityStrict;
            return SensitivityVeryStrict;
        }
        set
        {
            double target = value switch
            {
                SensitivityRelaxed => 0.50,
                SensitivityStrict => 0.75,
                SensitivityVeryStrict => 0.85,
                _ => 0.60
            };

            if (Math.Abs(_settings.OnlineMatchConfidenceThreshold - target) > 0.01)
            {
                _settings.OnlineMatchConfidenceThreshold = target;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public bool AutoCategorizeFromSteam
    {
        get => _settings.AutoCategorizeFromSteam;
        set
        {
            if (_settings.AutoCategorizeFromSteam != value)
            {
                _settings.AutoCategorizeFromSteam = value;
                OnPropertyChanged();
                AutoSaveSettings();
                if (value)
                {
                    _ = _onRequestEnrichLibrary?.Invoke(false);
                }
            }
        }
    }

    // --- Library Box Art & Visuals ---

    public static string NormalizeViewMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return ViewModePosterGrid;
        if (mode.Contains("Icon", StringComparison.OrdinalIgnoreCase)) return ViewModeCompactIcons;
        if (mode.Contains("List", StringComparison.OrdinalIgnoreCase)) return ViewModeDetailsList;
        if (mode.Contains("Large", StringComparison.OrdinalIgnoreCase)) return ViewModeExtraLarge;
        return ViewModePosterGrid;
    }

    public string LibraryViewMode
    {
        get => NormalizeViewMode(_settings.LibraryViewMode);
        set
        {
            string normalized = NormalizeViewMode(value);
            if (_settings.LibraryViewMode != normalized)
            {
                _settings.LibraryViewMode = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGridView));
                OnPropertyChanged(nameof(IsExtraLargeView));
                OnPropertyChanged(nameof(IsIconsView));
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(IsPosterView));
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Either poster view - the two that <see cref="PosterDetailsOnHover"/> applies to.</summary>
    public bool IsPosterView => IsGridView || IsExtraLargeView;

    public bool PosterDetailsOnHover
    {
        get => _settings.PosterDetailsOnHover;
        set
        {
            if (_settings.PosterDetailsOnHover != value)
            {
                _settings.PosterDetailsOnHover = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PosterDetailsOnHoverAccessibleName));
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Which details stay on a poster card at rest while <see cref="PosterDetailsOnHover"/> is on.</summary>
    public PosterDetailsViewModel PosterDetails { get; }

    /// <summary>The library toolbar's toggle is a plain Button, which has no on/off state for
    /// Narrator to read, so the state goes in its name.</summary>
    public string PosterDetailsOnHoverAccessibleName =>
        PosterDetailsOnHover ? "Show poster details on hover, on" : "Show poster details on hover, off";

    public bool IsGridView
    {
        get => NormalizeViewMode(LibraryViewMode) == ViewModePosterGrid;
        set { if (value) LibraryViewMode = ViewModePosterGrid; }
    }

    public bool IsExtraLargeView
    {
        get => NormalizeViewMode(LibraryViewMode) == ViewModeExtraLarge;
        set { if (value) LibraryViewMode = ViewModeExtraLarge; }
    }

    public bool IsIconsView
    {
        get => NormalizeViewMode(LibraryViewMode) == ViewModeCompactIcons;
        set { if (value) LibraryViewMode = ViewModeCompactIcons; }
    }

    public bool IsListView
    {
        get => NormalizeViewMode(LibraryViewMode) == ViewModeDetailsList;
        set { if (value) LibraryViewMode = ViewModeDetailsList; }
    }

    public bool UseVerticalPosterArt
    {
        get => _settings.UseVerticalPosterArt;
        set
        {
            if (_settings.UseVerticalPosterArt != value)
            {
                _settings.UseVerticalPosterArt = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onPosterArtSettingChanged?.Invoke();
            }
        }
    }

    public bool UseSteamGridDbArt
    {
        get => _settings.UseSteamGridDbArt;
        set
        {
            if (_settings.UseSteamGridDbArt != value)
            {
                _settings.UseSteamGridDbArt = value;
                OnPropertyChanged();
                AutoSaveSettings();
                // Switched on with a key already there: check it, and catch up once it works.
                if (value)
                    SteamGridDbKeyCheck.ScheduleCheck();
            }
        }
    }

    public string SteamGridDbApiKey
    {
        get => _settings.SteamGridDbApiKey;
        set
        {
            // Trimmed: a key copied off a web page often brings a space or line break with it,
            // which the service then rejects as a wrong key.
            value = value?.Trim() ?? string.Empty;
            if (_settings.SteamGridDbApiKey != value)
            {
                bool wasBlank = string.IsNullOrEmpty(_settings.SteamGridDbApiKey);
                _settings.SteamGridDbApiKey = value;
                // See the note in RawgApiKey: an explicit edit wins over a preserved ciphertext.
                _storageService.NoteApiKeyEdited(StorageService.ApiKeyField.SteamGridDb);
                OnPropertyChanged();
                // Pasting a key is the clearest way to say "use it": tick the switch with it, so a
                // key isn't left sitting unused behind an unticked box.
                if (wasBlank && value.Length > 0 && !_settings.UseSteamGridDbArt)
                {
                    _settings.UseSteamGridDbArt = true;
                    OnPropertyChanged(nameof(UseSteamGridDbArt));
                }
                AutoSaveSettings();
                SteamGridDbKeyCheck.ScheduleCheck();
            }
        }
    }

    public bool BackupIncludesActivity
    {
        get => _settings.BackupIncludesActivity;
        set
        {
            if (_settings.BackupIncludesActivity != value)
            {
                _settings.BackupIncludesActivity = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    // --- Steam Library Scanner ---

    // --- Global Manage Hotkey ---

    // --- Tools ---

    /// <summary>Settings > General "Enable Tools". The tray options are hidden while it is off.</summary>
    public bool EnableTools
    {
        get => _settings.EnableTools;
        set
        {
            if (_settings.EnableTools != value)
            {
                _settings.EnableTools = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LaunchPopupDescription));
                OnPropertyChanged(nameof(LaunchPopupEveryLaunchLabel));
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    /// <summary>Settings > Tray Menu "Show Tools in tray menu". Off by default.</summary>
    public bool ShowToolsInTray
    {
        get => _settings.ShowToolsInTray;
        set
        {
            if (_settings.ShowToolsInTray != value)
            {
                _settings.ShowToolsInTray = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    public IReadOnlyList<string> ToolsSortOptions => ToolCatalog.SortOptions;

    public string ToolsTraySortOption
    {
        get => ToolCatalog.NormalizeSortOption(_settings.ToolsTraySortOption);
        set
        {
            string next = ToolCatalog.NormalizeSortOption(value);
            if (_settings.ToolsTraySortOption != next)
            {
                _settings.ToolsTraySortOption = next;
                OnPropertyChanged();
                AutoSaveSettings();
                _onTrayMenuSettingChanged?.Invoke();
            }
        }
    }

    /// <summary>The launch popup's description names tools once Tools is on.</summary>
    public string LaunchPopupDescription => EnableTools
        ? "While TrayTrigger is hidden, a game or tool started from its hotkey or the tray menu shows a small card by the tray icon: what's starting, what it's waiting for, and any launch error. It never takes focus and closes once it starts."
        : "While TrayTrigger is hidden, a game started from its hotkey or the tray menu shows a small card by the tray icon: the game, what it's waiting for, and any launch error. It never takes focus and closes once the game starts.";

    public string LaunchPopupEveryLaunchLabel => EnableTools ? "Show it on every game and tool launch" : "Show it on every game launch";

    public bool EnableGameScripts
    {
        get => _settings.EnableGameScripts;
        set
        {
            if (_settings.EnableGameScripts != value)
            {
                _settings.EnableGameScripts = value;
                OnPropertyChanged();
                AutoSaveSettings();
                // First time on: put the examples and README where Browse will land.
                if (value) _ = Task.Run(() => ScriptLibrary.EnsureInstalled());
            }
        }
    }

    private ScriptLibraryService? _scriptLibrary;
    /// <summary>The scripts folder helper, rooted at the same %AppData% folder as games.json.</summary>
    public ScriptLibraryService ScriptLibrary => _scriptLibrary ??= new ScriptLibraryService(_storageService.BaseDirectory);

    // --- Performance Tweaks ---

    public bool CreateRestorePointBeforeTweaks
    {
        get => _settings.CreateRestorePointBeforeTweaks;
        set
        {
            if (_settings.CreateRestorePointBeforeTweaks != value)
            {
                _settings.CreateRestorePointBeforeTweaks = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    // --- Troubleshooting & Diagnostics ---

    public bool VerboseLoggingEnabled
    {
        get => _settings.VerboseLoggingEnabled;
        set
        {
            if (_settings.VerboseLoggingEnabled != value)
            {
                _settings.VerboseLoggingEnabled = value;
                LoggingService.IsVerboseEnabled = value;
                OnPropertyChanged();
                AutoSaveSettings();
                if (value)
                {
                    LoggingService.Info("Settings", $"[VERBOSE ENABLED] Verbose diagnostic logging is now active. Detailed traces will be written to: {LoggingService.LogFilePath}");
                }
                else
                {
                    // Preston reported verbose turning itself off. Nothing in the app disables it
                    // except this setter, so record what reached it: if a binding is writing the
                    // checkbox back rather than a person clicking it, the stack says so. Written
                    // only on the way off, and only once per toggle.
                    LoggingService.Info("Settings", "[VERBOSE DISABLED] Verbose diagnostic logging has been turned off.");
                    LoggingService.Info("Settings", $"[VERBOSE DISABLED] Turned off from:\n{Environment.StackTrace}");
                }
                OnPropertyChanged(nameof(LogFileSizeDisplay));
            }
        }
    }

    public string LogFilePathDisplay => LoggingService.LogFilePath;
    public string LogFileSizeDisplay => LoggingService.GetLogFileSizeDisplay();

    // --- Storage & Directories ---

    public string StorageDirectoryDisplay => _storageService.BaseDirectory;
    public string CacheDirectoryDisplay => _storageService.LocalCacheDirectory;

}
