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
public class SettingsViewModel : ViewModelBase
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
    private readonly Func<Task>? _onRequestEnrichLibrary;
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
        set => SetProperty(ref _statusMessage, value);
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
    public ICommand OpenTaskbarSettingsCommand { get; }
    public ICommand OpenSteamGridDbSiteCommand { get; }
    public ICommand OpenRawgSiteCommand { get; }
    public ICommand OpenScanForGamesCommand { get; }
    public ICommand RefreshAllPostersCommand { get; }
    public ICommand AddScanLocationCommand { get; }
    public ICommand RemoveScanLocationCommand { get; }
    public ICommand RefreshSteamScanLocationsCommand { get; }
    public ICommand RemoveIgnoredGamePathCommand { get; }

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

    public SettingsViewModel(
        AppSettings settings,
        StorageService storageService,
        StartupManager startupManager,
        TrayPromotionService trayPromotionService,
        SteamScannerService steamScannerService,
        Action? onTrayMenuSettingChanged = null,
        Action? onPosterArtSettingChanged = null,
        Action? onHotkeySettingChanged = null,
        Func<Task>? onRequestEnrichLibrary = null,
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
        OpenTaskbarSettingsCommand = new RelayCommand(TrayPromotionService.OpenWindowsTaskbarSettings);
        OpenSteamGridDbSiteCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://www.steamgriddb.com/profile/preferences") { UseShellExecute = true }));
        OpenRawgSiteCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://rawg.io/apidocs") { UseShellExecute = true }));
        OpenScanForGamesCommand = new RelayCommand(() => _onRequestOpenScanForGames?.Invoke());
        AddScanLocationCommand = new RelayCommand(AddScanLocation);
        RemoveScanLocationCommand = new RelayCommand(param =>
        {
            if (param is ScanLocationRowViewModel row) RemoveScanLocation(row);
        });
        RefreshSteamScanLocationsCommand = new RelayCommand(RefreshSteamScanLocations);
        RebuildScanLocationRows();

        RemoveIgnoredGamePathCommand = new RelayCommand(param =>
        {
            if (param is IgnoredGamePathRowViewModel row) RemoveIgnoredGamePath(row);
        });
        RebuildIgnoredGamePathRows();
        RefreshAllPostersCommand = new AsyncRelayCommand(async () =>
        {
            if (_onRequestRefreshAllPosters == null || IsRefreshingAllPosters)
                return;

            IsRefreshingAllPosters = true;
            try
            {
                await _onRequestRefreshAllPosters(new Progress<string>(msg => PosterRefreshStatus = msg));
            }
            finally
            {
                IsRefreshingAllPosters = false;
            }
        });
        CheckUpdatesInSettingsCommand = new AsyncRelayCommand(async () =>
        {
            if (_onCheckForUpdates != null)
            {
                await _onCheckForUpdates();
            }
        });
    }

    // --- Scan for Games: Scan Locations ---
    // The folder list "Scan for Games" (Library page) looks in, split into two views of the same
    // AppSettings.ScanLocations list: Steam library folders (auto-managed by ScanLocationService,
    // shown under the Steam integration toggle, checkbox only) and manually-added folders (fully
    // user-owned, shown in the Scan Locations section with a Remove button).

    /// <summary>Re-reads AppSettings.ScanLocations - call after something outside SettingsViewModel
    /// (e.g. ImportCoordinator adding a "remembered" folder) has changed it.</summary>
    public void RefreshScanLocations() => RebuildScanLocationRows();

    private void RebuildScanLocationRows()
    {
        SteamLibraryLocations.Clear();
        ScanLocations.Clear();
        foreach (var loc in _settings.ScanLocations.OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ScanLocationRowViewModel(loc, () => AutoSaveSettings());
            if (loc.Source == ScanLocationSource.Steam)
                SteamLibraryLocations.Add(row);
            else
                ScanLocations.Add(row);
        }
        OnPropertyChanged(nameof(HasNoSteamLibraryLocations));
        OnPropertyChanged(nameof(HasNoScanLocations));
    }

    private void AddScanLocation()
    {
        var dialog = new OpenFolderDialog { Title = "Add a Scan Location", Multiselect = false };
        if (FileDialogCloak.Show(dialog) != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        string path = dialog.FolderName.TrimEnd('\\', '/');
        var existing = _settings.ScanLocations.FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (existing.Source == ScanLocationSource.Steam && !_settings.SteamIntegrationEnabled)
            {
                // A Steam library row lingers in settings while Steam integration is off, hidden
                // from the UI and skipped by the scan - it must not block the user from folder-
                // scanning that same directory for non-Steam installs. Convert it in place.
                existing.Source = ScanLocationSource.Manual;
                existing.IsAutoManaged = false;
                existing.IsEnabled = true;
                AutoSaveSettings();
                RebuildScanLocationRows();
                LoggingService.Info("Settings", $"Converted hidden Steam library row '{path}' to a manual scan location.");
                StatusMessage = "Added scan location (it was a Steam library folder; it's now scanned as a plain folder while Steam integration is off).";
                return;
            }

            StatusMessage = existing.Source == ScanLocationSource.Steam
                ? "That folder is already one of your Steam libraries - see the Steam integration above."
                : "That folder is already a scan location.";
            return;
        }

        _settings.ScanLocations.Add(new ScanLocation { Path = path, Source = ScanLocationSource.Manual, IsEnabled = true });
        AutoSaveSettings();
        RebuildScanLocationRows();
        LoggingService.Info("Settings", $"Added scan location: '{path}'.");
        StatusMessage = "Added scan location.";
    }

    private void RemoveScanLocation(ScanLocationRowViewModel row)
    {
        _settings.ScanLocations.RemoveAll(l => l.Id == row.Model.Id);
        AutoSaveSettings();
        RebuildScanLocationRows();
        LoggingService.Info("Settings", $"Removed scan location: '{row.Path}'.");
    }

    private void RefreshSteamScanLocations()
    {
        if (!_settings.SteamIntegrationEnabled)
        {
            StatusMessage = "Steam integration is disabled - enable it first.";
            return;
        }

        if (ScanLocationService.SyncSteamLocations(_settings, _steamScannerService))
        {
            AutoSaveSettings();
            RebuildScanLocationRows();
            StatusMessage = "Steam libraries refreshed.";
        }
        else
        {
            StatusMessage = SteamLibraryLocations.Count == 0
                ? "No Steam libraries found - is Steam installed?"
                : "Steam libraries are already up to date.";
        }
    }

    // --- Scan for Games: Ignored Paths ---
    // Executables permanently excluded from "Add Folder" and "Scan for Games" results after the
    // user clicks "Ignore" on a false-positive candidate (e.g. a bundled non-game tool).

    /// <summary>Re-reads AppSettings.IgnoredGamePaths - call after something outside SettingsViewModel
    /// (e.g. ImportCoordinator recording a new ignore) has changed it.</summary>
    public void RefreshIgnoredGamePaths() => RebuildIgnoredGamePathRows();

    private void RebuildIgnoredGamePathRows()
    {
        IgnoredGamePaths.Clear();
        foreach (var p in _settings.IgnoredGamePaths.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            IgnoredGamePaths.Add(new IgnoredGamePathRowViewModel(p));
        }
        OnPropertyChanged(nameof(HasNoIgnoredGamePaths));
    }

    private void RemoveIgnoredGamePath(IgnoredGamePathRowViewModel row)
    {
        _settings.IgnoredGamePaths.RemoveAll(p => p.Id == row.Model.Id);
        AutoSaveSettings();
        RebuildIgnoredGamePathRows();
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
                _trayPromotionService.TrySetAlwaysShow(value, out string msg);
                LoggingService.Info("Settings", $"AlwaysShowTrayIcon set to {value}: {msg}");
                StatusMessage = msg;
            }
        }
    }

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

    public string GitHubRepository
    {
        get => string.IsNullOrWhiteSpace(_settings.GitHubRepository) ? "stephenh678/TrayTrigger" : _settings.GitHubRepository;
        set
        {
            string clean = string.IsNullOrWhiteSpace(value) ? "stephenh678/TrayTrigger" : value.Trim();
            if (_settings.GitHubRepository != clean)
            {
                _settings.GitHubRepository = clean;
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

    // --- Game Titles & Metadata Detection ---

    public bool PreferExeForGameName
    {
        get => _settings.PreferExeForGameName;
        set
        {
            if (_settings.PreferExeForGameName != value)
            {
                _settings.PreferExeForGameName = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

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
                    _ = _onRequestEnrichLibrary?.Invoke();
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
                AutoSaveSettings();
            }
        }
    }

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
            }
        }
    }

    public string SteamGridDbApiKey
    {
        get => _settings.SteamGridDbApiKey;
        set
        {
            if (_settings.SteamGridDbApiKey != value)
            {
                _settings.SteamGridDbApiKey = value ?? string.Empty;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    public string RawgApiKey
    {
        get => _settings.RawgApiKey;
        set
        {
            if (_settings.RawgApiKey != value)
            {
                _settings.RawgApiKey = value ?? string.Empty;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>The RAWG key when set, else null - the details window uses this to decide whether
    /// the RAWG source is available for the toggle.</summary>
    public string? RawgApiKeyOrNull =>
        !string.IsNullOrWhiteSpace(_settings.RawgApiKey) ? _settings.RawgApiKey : null;

    private bool _isRefreshingAllPosters;
    public bool IsRefreshingAllPosters
    {
        get => _isRefreshingAllPosters;
        private set { _isRefreshingAllPosters = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRefreshAllPosters)); }
    }

    public bool CanRefreshAllPosters => !IsRefreshingAllPosters;

    private string _posterRefreshStatus = string.Empty;
    public string PosterRefreshStatus
    {
        get => _posterRefreshStatus;
        private set { _posterRefreshStatus = value; OnPropertyChanged(); }
    }

    public string? SteamGridDbApiKeyOrNull =>
        _settings.UseSteamGridDbArt && !string.IsNullOrWhiteSpace(_settings.SteamGridDbApiKey)
            ? _settings.SteamGridDbApiKey
            : null;

    // --- Steam Library Scanner ---

    // --- Launcher integration toggles ---
    // Each toggle gates only *discovery* ("Scan for Games" and the startup auto-scan) for its
    // platform. Games already in the library keep launching through their launcher and keep their
    // badge regardless - so turning one off asks whether to also remove that platform's games
    // (see SetIntegrationEnabled), rather than silently leaving them or silently deleting them.

    public bool SteamIntegrationEnabled
    {
        get => _settings.SteamIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Steam, "Steam", _settings.SteamIntegrationEnabled, value, v => _settings.SteamIntegrationEnabled = v);
    }

    /// <summary>
    /// Unlike SteamIntegrationEnabled, this has no matching ScanLocations sync to trigger - GOG
    /// has no library-folder concept (every installed game's path is already pinned in its own
    /// registry entry), so this toggle alone gates whether "Scan for Games" looks for GOG titles.
    /// See ImportCoordinator.ScanForGamesAsync.
    /// </summary>
    public bool GogIntegrationEnabled
    {
        get => _settings.GogIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Gog, "GOG", _settings.GogIntegrationEnabled, value, v => _settings.GogIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool EaIntegrationEnabled
    {
        get => _settings.EaIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Ea, "EA", _settings.EaIntegrationEnabled, value, v => _settings.EaIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool EpicIntegrationEnabled
    {
        get => _settings.EpicIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Epic, "Epic", _settings.EpicIntegrationEnabled, value, v => _settings.EpicIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool UbisoftIntegrationEnabled
    {
        get => _settings.UbisoftIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Ubisoft, "Ubisoft", _settings.UbisoftIntegrationEnabled, value, v => _settings.UbisoftIntegrationEnabled = v);
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool XboxIntegrationEnabled
    {
        get => _settings.XboxIntegrationEnabled;
        set => SetIntegrationEnabled(DetectedLauncher.Xbox, "Xbox", _settings.XboxIntegrationEnabled, value, v => _settings.XboxIntegrationEnabled = v);
    }

    // True while ApplyDetectedLauncherChoices runs: the first-launch picker is choosing initial
    // defaults on an (effectively) empty library, so the "also remove its games?" prompt and the
    // "run Scan for Games" hint would both be noise there.
    private bool _applyingDetectedLaunchers;

    /// <summary>
    /// Shared body of the five *IntegrationEnabled setters. Persists the flag and raises the
    /// change notification for <paramref name="propertyName"/> (the calling property), then:
    /// on enable, nudges the user to scan (and for Steam, re-syncs its library folders so they
    /// appear immediately); on disable, offers to remove that platform's games from the library,
    /// defaulting to keeping them.
    /// </summary>
    private void SetIntegrationEnabled(DetectedLauncher launcher, string platformName, bool current, bool value, Action<bool> store, [CallerMemberName] string propertyName = "")
    {
        if (current == value) return;

        // Disabling with games present is asked about *before* anything is persisted, so the
        // prompt's Cancel genuinely cancels: nothing changes and the checkbox snaps back.
        bool removeGames = false;
        if (!value && !_applyingDetectedLaunchers)
        {
            var (count, hidden) = _getPlatformGameCount?.Invoke(launcher) ?? (0, 0);
            if (count > 0)
            {
                string plural = count == 1 ? "" : "s";
                // Hidden entries aren't visible on any tab, so say so - otherwise "12 games"
                // can exceed everything the user can see and the sweep looks wrong.
                string hiddenNote = hidden > 0 ? $" ({hidden} hidden)" : string.Empty;
                Window? owner = WindowHelper.ActiveOwner();
                var choice = ModernDialog.PromptChoice(
                    owner,
                    $"Turn Off {platformName} Integration?",
                    $"You have {count} {platformName} game{plural}{hiddenNote} in your library. Remove {(count == 1 ? "it" : "them")} too?",
                    $"Turning off {platformName} integration only stops new {platformName} games from being detected. " +
                    $"Games already in your library stay and keep launching through {platformName} unless you remove them here.\n\n" +
                    "Removing can't be undone: playtime, hotkeys, categories and custom artwork for those games are lost. " +
                    "Installed game files are never deleted either way.",
                    primaryText: "Remove Games",
                    secondaryText: "Keep Games",
                    cancelText: "Cancel");

                if (choice == DialogResultOption.Cancel)
                {
                    // Re-sync the bound checkbox to the unchanged setting.
                    OnPropertyChanged(propertyName);
                    return;
                }
                removeGames = choice == DialogResultOption.Primary;
            }
        }

        store(value);
        OnPropertyChanged(propertyName);
        AutoSaveSettings();
        LoggingService.Info("Settings", $"{platformName} integration {(value ? "enabled" : "disabled")}.");

        if (launcher == DetectedLauncher.Steam)
        {
            if (value)
            {
                // Populate the library-folder rows under the toggle right away rather than
                // waiting for the next startup sync.
                if (ScanLocationService.SyncSteamLocations(_settings, _steamScannerService))
                {
                    AutoSaveSettings();
                }
            }
            RebuildScanLocationRows();
        }

        if (_applyingDetectedLaunchers) return;

        if (value)
        {
            StatusMessage = $"{platformName} integration enabled - run Scan for Games to add your {platformName} titles.";
            return;
        }

        if (!removeGames)
        {
            StatusMessage = $"{platformName} integration disabled - existing {platformName} games kept.";
            return;
        }

        int removed = _removePlatformGames?.Invoke(launcher) ?? 0;
        StatusMessage = $"{platformName} integration disabled - removed {removed} {platformName} game{(removed == 1 ? "" : "s")} from your library.";
    }

    /// <summary>
    /// Applies the user's choice from the first-launch <see cref="LauncherDetectionDialog"/>:
    /// enables only the confirmed launchers, through each toggle's own setter above so Settings
    /// UI bindings and auto-save behave exactly as if the user had flipped them by hand - setting
    /// the underlying AppSettings fields directly (as ImportCoordinator briefly did) leaves an
    /// already-bound checkbox showing its stale old value, since nothing raises the property's
    /// change notification in that case. The setters' interactive prompts are suppressed here.
    /// </summary>
    /// <param name="enabledLaunchers">The launchers the user ticked in the picker.</param>
    /// <param name="probedLaunchers">
    /// The launchers whose detection probe actually completed (see
    /// ImportCoordinator.DetectInstalledLaunchers). A launcher outside this set had its probe throw,
    /// so "absent from the picker" doesn't mean "not installed" - its toggle is left exactly as it
    /// was rather than silently turned off. Null means every launcher was probed.
    /// </param>
    public void ApplyDetectedLauncherChoices(IReadOnlyList<DetectedLauncher> enabledLaunchers, IReadOnlySet<DetectedLauncher>? probedLaunchers = null)
    {
        _applyingDetectedLaunchers = true;
        try
        {
            bool Probed(DetectedLauncher l) => probedLaunchers == null || probedLaunchers.Contains(l);

            if (Probed(DetectedLauncher.Steam)) SteamIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Steam);
            if (Probed(DetectedLauncher.Gog)) GogIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Gog);
            if (Probed(DetectedLauncher.Ea)) EaIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Ea);
            if (Probed(DetectedLauncher.Epic)) EpicIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Epic);
            if (Probed(DetectedLauncher.Ubisoft)) UbisoftIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Ubisoft);
            if (Probed(DetectedLauncher.Xbox)) XboxIntegrationEnabled = enabledLaunchers.Contains(DetectedLauncher.Xbox);
        }
        finally
        {
            _applyingDetectedLaunchers = false;
        }
    }

    public bool AutoScanForGamesOnStartup
    {
        get => _settings.AutoScanForGamesOnStartup;
        set
        {
            if (_settings.AutoScanForGamesOnStartup != value)
            {
                _settings.AutoScanForGamesOnStartup = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    // --- Global Manage Hotkey ---

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
            }
        }
    }

    public string GlobalManageHotkey
    {
        get => _settings.GlobalManageHotkey;
        set
        {
            if (_settings.GlobalManageHotkey != value)
            {
                _settings.GlobalManageHotkey = value;
                OnPropertyChanged();
                AutoSaveSettings();
                _onHotkeySettingChanged?.Invoke();
            }
        }
    }

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
                    LoggingService.Info("Settings", "[VERBOSE DISABLED] Verbose diagnostic logging has been turned off.");
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

    // --- Operations ---

    public void AutoSaveSettings([CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
    {
        _storageService.SaveSettings(_settings, callerMember, callerFile);
        LoggingService.Verbose("Settings", "Settings auto-saved.");
    }

    public void SaveInWindowSettings()
    {
        _storageService.SaveSettings(_settings);
        _startupManager.SetStartupEnabled(StartWithWindows, StartMinimizedToTray);

        if (AlwaysShowTrayIcon)
        {
            _trayPromotionService.TrySetAlwaysShow(true, out _);
        }

        _onHotkeySettingChanged?.Invoke();
        LoggingService.Info("Settings", "User clicked Save Settings. Settings saved & applied.");
        StatusMessage = "Settings saved successfully!";
        OnPropertyChanged(nameof(LogFileSizeDisplay));
    }

    public void ResetSettingsToDefaults(bool promptConfirm = true)
    {
        if (promptConfirm)
        {
            Window? owner = WindowHelper.ActiveOwner();
            var confirmed = ModernDialog.Confirm(
                owner,
                "Reset Settings",
                "Are you sure you want to reset all settings to their recommended default values?",
                "Your game library, categories, and custom artwork will remain untouched.",
                confirmText: "Reset to Defaults",
                cancelText: "Cancel");

            if (!confirmed) return;
        }

        string existingApiKey = _settings.SteamGridDbApiKey;
        string existingRawgKey = _settings.RawgApiKey;

        // Copy every default from a fresh AppSettings instead of a hand-maintained literal
        // list, so a newly added setting is reset automatically instead of silently staying
        // un-reset if someone forgets to add it here. Excludes properties that aren't a
        // user "preference" in the sense this dialog means - library view state
        // (LastCategoryFilter/LastSortOption), one-time-prompt/update-snooze state
        // (SkippedUpdateVersion/RemindAfterUtc), and the API key (preserved explicitly below,
        // same as before). See L-23.
        var defaults = new AppSettings();
        var excludedFromReset = new HashSet<string>
        {
            nameof(AppSettings.LastCategoryFilter),
            nameof(AppSettings.LastSortOption),
            nameof(AppSettings.SkippedUpdateVersion),
            nameof(AppSettings.RemindAfterUtc),
            nameof(AppSettings.SteamGridDbApiKey),
            nameof(AppSettings.RawgApiKey),
            // Manually-curated, not a "preference" in the dialog's sense - same bucket as the
            // library/categories/artwork the confirmation text already promises to leave alone.
            nameof(AppSettings.ScanLocations),
            nameof(AppSettings.IgnoredGamePaths),
            // Set once from actual detected launchers on the first "Scan for Games" press (see
            // ImportCoordinator.DetectInstalledLaunchers) rather than a real "preference" default -
            // resetting to the AppSettings class default would blindly re-enable every
            // platform regardless of what's actually installed. Same bucket as ScanLocations.
            nameof(AppSettings.SteamIntegrationEnabled),
            nameof(AppSettings.GogIntegrationEnabled),
            nameof(AppSettings.EaIntegrationEnabled),
            nameof(AppSettings.EpicIntegrationEnabled),
            nameof(AppSettings.UbisoftIntegrationEnabled),
            nameof(AppSettings.XboxIntegrationEnabled),
            // UI layout state / one-time-prompt state, same as LastCategoryFilter above.
            nameof(AppSettings.IsSidebarExpanded),
            nameof(AppSettings.MainWindowLeft),
            nameof(AppSettings.MainWindowTop),
            nameof(AppSettings.MainWindowWidth),
            nameof(AppSettings.MainWindowHeight),
            nameof(AppSettings.MainWindowMaximized),
            nameof(AppSettings.HasSeenPerformanceProfileMigrationPrompt),
            nameof(AppSettings.HasSeenLauncherDetectionPrompt),
            nameof(AppSettings.HasSeenWelcomePrompt),
            // Describes the machine (what a tweak found before it was applied), not a preference.
            nameof(AppSettings.TweakPriorState),
        };
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || excludedFromReset.Contains(prop.Name)) continue;

            // Nested config objects (OptimizedProfileTweaks/AggressiveProfileTweaks) are reset
            // field-by-field into the *existing* instance rather than swapped for a new one:
            // SystemViewModel's tweak toggles captured the original instances at construction,
            // so replacing the reference would leave them editing a detached object whose
            // changes never get saved.
            object? current = prop.GetValue(_settings);
            object? fresh = prop.GetValue(defaults);
            if (current != null && fresh != null && prop.PropertyType.IsClass && prop.PropertyType != typeof(string)
                && !typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                foreach (var inner in prop.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (inner.CanWrite) inner.SetValue(current, inner.GetValue(fresh));
                }
                continue;
            }

            prop.SetValue(_settings, fresh);
        }
        _settings.SteamGridDbApiKey = existingApiKey;
        _settings.RawgApiKey = existingRawgKey;

        // Execute side effects
        _startupManager.SetStartupEnabled(false, true);
        _trayPromotionService.TrySetAlwaysShow(true, out _);
        _onHotkeySettingChanged?.Invoke();
        LoggingService.Initialize(false);
        _onPosterArtSettingChanged?.Invoke();
        _onProfileTweaksReset?.Invoke();

        // Fire property changed for all settings
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(StartMinimizedToTray));
        OnPropertyChanged(nameof(CanConfigureMinimized));
        OnPropertyChanged(nameof(AlwaysShowTrayIcon));
        OnPropertyChanged(nameof(GroupTrayMenuByCategory));
        OnPropertyChanged(nameof(ShowRecentInTray));
        OnPropertyChanged(nameof(MaxRecentInTray));
        OnPropertyChanged(nameof(RecentTraySortOption));
        OnPropertyChanged(nameof(ShowFavoritesInTray));
        OnPropertyChanged(nameof(MaxFavoritesInTray));
        OnPropertyChanged(nameof(FavoritesTraySortOption));
        OnPropertyChanged(nameof(TrayMenuSortOption));
        OnPropertyChanged(nameof(PreferExeForGameName));
        OnPropertyChanged(nameof(SearchOfficialTitleOnline));
        OnPropertyChanged(nameof(OnlineMatchSensitivity));
        OnPropertyChanged(nameof(AutoCategorizeFromSteam));
        OnPropertyChanged(nameof(UseVerticalPosterArt));
        OnPropertyChanged(nameof(UseSteamGridDbArt));
        OnPropertyChanged(nameof(SteamGridDbApiKey));
        OnPropertyChanged(nameof(RawgApiKey));
        OnPropertyChanged(nameof(SteamIntegrationEnabled));
        OnPropertyChanged(nameof(AutoScanForGamesOnStartup));
        OnPropertyChanged(nameof(MinimizeOnGameLaunch));
        OnPropertyChanged(nameof(AutoCheckForUpdates));
        OnPropertyChanged(nameof(IncludePrereleaseUpdates));
        OnPropertyChanged(nameof(GitHubRepository));
        OnPropertyChanged(nameof(GlobalManageHotkey));
        OnPropertyChanged(nameof(EnableGameScripts));
        OnPropertyChanged(nameof(CreateRestorePointBeforeTweaks));
        OnPropertyChanged(nameof(VerboseLoggingEnabled));
        OnPropertyChanged(nameof(LibraryViewMode));
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsExtraLargeView));
        OnPropertyChanged(nameof(IsIconsView));
        OnPropertyChanged(nameof(IsListView));

        AutoSaveSettings();
        _onTrayMenuSettingChanged?.Invoke();

        StatusMessage = "Settings restored to recommended defaults.";
    }

    private void OpenStorageFolder()
    {
        try
        {
            _storageService.EnsureDirectories();
            Process.Start("explorer.exe", _storageService.BaseDirectory);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Storage", $"Failed to open folder: {ex.Message}");
        }
    }

    private void OpenCacheFolder()
    {
        try
        {
            _storageService.EnsureDirectories();
            Process.Start("explorer.exe", _storageService.LocalCacheDirectory);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Storage", $"Failed to open cache folder: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        LoggingService.OpenLogInEditor();
    }

    private void ClearLogFile()
    {
        LoggingService.ClearLog();
        OnPropertyChanged(nameof(LogFileSizeDisplay));
        StatusMessage = "Debug log cleared.";
    }

    private void OpenLogFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(LoggingService.LogFilePath) ?? _storageService.BaseDirectory;
            Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            LoggingService.Error("App", $"Failed to open log folder: {ex.Message}");
        }
    }
}
