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
        "Alphabetical (A - Z)",
        "Most Recently Played",
        "Cumulative Playtime",
        "Alphabetical (Z - A)"
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
    public ICommand OpenScanForGamesCommand { get; }
    public ICommand RefreshAllPostersCommand { get; }
    public ICommand AddScanLocationCommand { get; }
    public ICommand RemoveScanLocationCommand { get; }
    public ICommand RefreshSteamScanLocationsCommand { get; }
    public ICommand RemoveIgnoredGamePathCommand { get; }

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
        Func<string>? getUpdateStatusText = null)
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
    // The editable folder list "Scan for Games" (Library page) looks in. Steam library folders
    // are auto-managed by ScanLocationService and shown here read-only-ish (removable, but they
    // reappear on the next Steam sync); manually-added folders are fully user-owned.

    /// <summary>Re-reads AppSettings.ScanLocations - call after something outside SettingsViewModel
    /// (e.g. ImportCoordinator adding a "remembered" folder) has changed it.</summary>
    public void RefreshScanLocations() => RebuildScanLocationRows();

    private void RebuildScanLocationRows()
    {
        ScanLocations.Clear();
        foreach (var loc in _settings.ScanLocations
                     .OrderByDescending(l => l.Source == ScanLocationSource.Steam)
                     .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
        {
            ScanLocations.Add(new ScanLocationRowViewModel(loc, () => AutoSaveSettings()));
        }
        OnPropertyChanged(nameof(HasNoScanLocations));
    }

    private void AddScanLocation()
    {
        var dialog = new OpenFolderDialog { Title = "Add a Scan Location", Multiselect = false };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        string path = dialog.FolderName.TrimEnd('\\', '/');
        if (_settings.ScanLocations.Any(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "That folder is already a scan location.";
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
            StatusMessage = "Steam integration is disabled - enable it above first.";
            return;
        }

        if (ScanLocationService.SyncSteamLocations(_settings, _steamScannerService))
        {
            AutoSaveSettings();
            RebuildScanLocationRows();
            StatusMessage = "Steam scan locations refreshed.";
        }
        else
        {
            StatusMessage = "Steam scan locations are already up to date.";
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

    public bool SteamIntegrationEnabled
    {
        get => _settings.SteamIntegrationEnabled;
        set
        {
            if (_settings.SteamIntegrationEnabled != value)
            {
                _settings.SteamIntegrationEnabled = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
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
        set
        {
            if (_settings.GogIntegrationEnabled != value)
            {
                _settings.GogIntegrationEnabled = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool EaIntegrationEnabled
    {
        get => _settings.EaIntegrationEnabled;
        set
        {
            if (_settings.EaIntegrationEnabled != value)
            {
                _settings.EaIntegrationEnabled = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool EpicIntegrationEnabled
    {
        get => _settings.EpicIntegrationEnabled;
        set
        {
            if (_settings.EpicIntegrationEnabled != value)
            {
                _settings.EpicIntegrationEnabled = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
        }
    }

    /// <summary>Same no-scan-location-needed reasoning as GogIntegrationEnabled.</summary>
    public bool UbisoftIntegrationEnabled
    {
        get => _settings.UbisoftIntegrationEnabled;
        set
        {
            if (_settings.UbisoftIntegrationEnabled != value)
            {
                _settings.UbisoftIntegrationEnabled = value;
                OnPropertyChanged();
                AutoSaveSettings();
            }
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
            // UI layout state / one-time-prompt state, same as LastCategoryFilter above.
            nameof(AppSettings.IsSidebarExpanded),
            nameof(AppSettings.HasSeenPerformanceProfileMigrationPrompt),
            nameof(AppSettings.HasSeenLauncherDetectionPrompt),
            nameof(AppSettings.HasSeenWelcomePrompt),
        };
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || excludedFromReset.Contains(prop.Name)) continue;
            prop.SetValue(_settings, prop.GetValue(defaults));
        }
        _settings.SteamGridDbApiKey = existingApiKey;

        // Execute side effects
        _startupManager.SetStartupEnabled(false, true);
        _trayPromotionService.TrySetAlwaysShow(true, out _);
        _onHotkeySettingChanged?.Invoke();
        LoggingService.Initialize(false);
        _onPosterArtSettingChanged?.Invoke();

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
        OnPropertyChanged(nameof(SteamIntegrationEnabled));
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
