using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

public enum NavSection
{
    Library,
    Settings,
    About,
    System
}

public enum AboutSubSection
{
    All,
    Overview,
    Features,
    Shortcuts,
    Support
}

public class CategoryTabItem : ViewModelBase
{
    public string Name { get; }
    public string DisplayName { get; }
    public bool IsFavoritesTab { get; }

    private bool _isSelected;
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

    public ICommand SelectCommand { get; }

    public CategoryTabItem(string name, string displayName, bool isSelected, Action<string> onSelect)
    {
        Name = name;
        DisplayName = displayName;
        IsFavoritesTab = string.Equals(name, "Favorites", StringComparison.OrdinalIgnoreCase);
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(() => onSelect(Name));
    }
}

public class MainViewModel : ViewModelBase
{
    private readonly StorageService _storageService;
    private readonly ShortcutService _shortcutService;
    private readonly IconExtractorService _iconExtractorService;
    private readonly SteamScannerService _steamScannerService;
    private readonly ProcessLauncherService _launcherService;
    private readonly HotkeyManager _hotkeyManager;
    private readonly StartupManager _startupManager;
    private readonly TrayPromotionService _trayPromotionService;
    private readonly FolderScannerService _folderScannerService;
    private readonly SystemInfoService _systemInfoService = new();
    private readonly SystemTweaksService _systemTweaksService = new();

    public SystemViewModel SystemVM { get; }
    public SettingsViewModel SettingsVM { get; }

    // Navigation state
    private NavSection _currentSection = NavSection.Library;
    private bool _isSidebarExpanded = false;

    // Filtering & Sorting
    private string _searchText = string.Empty;
    private string _selectedCategory = "All";
    private string _selectedSortOption = "Alphabetical (A - Z)";
    private string _statusMessage = string.Empty;
    private AppSettings _settings;
    private readonly SteamSearchService _steamSearchService = new();
    private readonly SteamMetadataService _steamMetadataService = new();

    // Undo toast state
    private bool _isUndoToastVisible = false;
    private string _undoToastMessage = string.Empty;
    private GameEntry? _lastRemovedGame;
    private int _lastRemovedIndex = -1;
    private DispatcherTimer? _undoToastTimer;

    // Reentrancy guards: these async operations all add to / enrich the shared Games
    // collection, so overlapping invocations (e.g. rapid double-clicks, a settings toggle
    // re-triggering enrichment mid-import) could interleave duplicate-check and add logic.
    private bool _isImportInProgress;
    private bool _isEnrichmentInProgress;

    public ObservableCollection<GameCardViewModel> Games { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ObservableCollection<CategoryTabItem> CategoryTabs { get; } = new();
    public ObservableCollection<string> SortOptions { get; } = new()
    {
        "Alphabetical (A - Z)",
        "Alphabetical (Z - A)",
        "Favorites First (A - Z)",
        "Most Recently Played",
        "Cumulative Playtime"
    };
    public ICollectionView FilteredGames { get; }

    public AppSettings Settings => _settings;
    public IconExtractorService IconExtractorService => _iconExtractorService;
    public event Action? RequestOpenSteamDialog;
    public event Action<GameCardViewModel>? RequestEditGameDialog;
    public event Action<string, List<GameCandidate>>? RequestCandidatePicker;
    public event Action<string, List<GameCandidate>>? RequestFolderBatchImport;
    public event Action<GameCardViewModel>? RequestQuickRename;
    public event Action<GameCardViewModel>? RequestQuickCategory;
    public event Action<GameCardViewModel>? RequestEditSteamAppId;
    public event Action? LibraryUpdated;
    public event Action? RequestMinimizeToTray;
    public event Action? RequestExitApplication;

    public MainViewModel(
        StorageService storageService,
        ShortcutService shortcutService,
        IconExtractorService iconExtractorService,
        SteamScannerService steamScannerService,
        ProcessLauncherService launcherService,
        HotkeyManager hotkeyManager,
        StartupManager startupManager,
        TrayPromotionService trayPromotionService,
        FolderScannerService? folderScannerService = null)
    {
        _storageService = storageService;
        _shortcutService = shortcutService;
        _iconExtractorService = iconExtractorService;
        _steamScannerService = steamScannerService;
        _launcherService = launcherService;
        _hotkeyManager = hotkeyManager;
        _startupManager = startupManager;
        _trayPromotionService = trayPromotionService;
        _folderScannerService = folderScannerService ?? new FolderScannerService();

        _settings = _storageService.LoadSettings();
        _settings.LibraryViewMode = SettingsViewModel.NormalizeViewMode(_settings.LibraryViewMode);
        _isSidebarExpanded = false; // Left panel is collapsed at startup per user preference
        if (!string.IsNullOrWhiteSpace(_settings.LastSortOption) && SortOptions.Contains(_settings.LastSortOption))
        {
            _selectedSortOption = _settings.LastSortOption;
        }
        if (!string.IsNullOrWhiteSpace(_settings.LastCategoryFilter))
        {
            _selectedCategory = _settings.LastCategoryFilter;
        }

        LoggingService.Initialize(_settings.VerboseLoggingEnabled);

        SettingsVM = new SettingsViewModel(
            _settings,
            _storageService,
            _startupManager,
            _trayPromotionService,
            onTrayMenuSettingChanged: () => LibraryUpdated?.Invoke(),
            onPosterArtSettingChanged: () =>
            {
                foreach (var card in Games)
                {
                    card.NotifyPosterArtChanged();
                }
            },
            onHotkeySettingChanged: UpdateHotkeys,
            onRequestEnrichLibrary: EnrichLibraryAsync,
            onRequestRefreshAllPosters: progress => RefreshAllPostersAsync(progress),
            onRequestOpenSteamImport: OpenSteamImport
        );

        SettingsVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.LibraryViewMode)
                or nameof(SettingsViewModel.IsGridView)
                or nameof(SettingsViewModel.IsIconsView)
                or nameof(SettingsViewModel.IsListView))
            {
                OnPropertyChanged(e.PropertyName);
            }
        };

        FilteredGames = CollectionViewSource.GetDefaultView(Games);
        FilteredGames.Filter = FilterGameItem;
        ApplySort();

        SystemVM = new SystemViewModel(_systemInfoService, _systemTweaksService);

        // Navigation Commands
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarExpanded = !IsSidebarExpanded);
        SelectLibraryCommand = new RelayCommand(() => CurrentSection = NavSection.Library);
        SelectSystemCommand = new RelayCommand(() => CurrentSection = NavSection.System);
        SelectSettingsCommand = new RelayCommand(() => CurrentSection = NavSection.Settings);
        OpenDiagnosticsSettingsCommand = new RelayCommand(() =>
        {
            SettingsVM.SelectedTab = SettingsCategoryTab.Diagnostics;
            CurrentSection = NavSection.Settings;
        });
        SelectAboutCommand = new RelayCommand(() => CurrentSection = NavSection.About);
        SelectAboutAllTabCommand = new RelayCommand(() => CurrentAboutSection = AboutSubSection.All);
        SelectAboutOverviewTabCommand = new RelayCommand(() => CurrentAboutSection = AboutSubSection.Overview);
        SelectAboutFeaturesTabCommand = new RelayCommand(() => CurrentAboutSection = AboutSubSection.Features);
        SelectAboutShortcutsTabCommand = new RelayCommand(() => CurrentAboutSection = AboutSubSection.Shortcuts);
        SelectAboutSupportTabCommand = new RelayCommand(() => CurrentAboutSection = AboutSubSection.Support);
        NavigateToGeneralSettingsCommand = new RelayCommand(() =>
        {
            SettingsVM.SelectedTab = SettingsCategoryTab.General;
            CurrentSection = NavSection.Settings;
        });
        OpenGitHubCommand = new RelayCommand(() =>
        {
            try
            {
                string repo = string.IsNullOrWhiteSpace(_settings.GitHubRepository) ? "stephenh678/TrayTrigger" : _settings.GitHubRepository.Trim();
                Process.Start(new ProcessStartInfo($"https://github.com/{repo}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LoggingService.Error("MainViewModel", "Failed to open GitHub repo link", ex);
            }
        });
        OpenGitHubIssuesCommand = new RelayCommand(() =>
        {
            try
            {
                string repo = string.IsNullOrWhiteSpace(_settings.GitHubRepository) ? "stephenh678/TrayTrigger" : _settings.GitHubRepository.Trim();
                Process.Start(new ProcessStartInfo($"https://github.com/{repo}/issues") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LoggingService.Error("MainViewModel", "Failed to open GitHub issues link", ex);
            }
        });
        CopySystemInfoCommand = new RelayCommand(() =>
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("### TrayTrigger Environment Report");
                sb.AppendLine($"- **App Version**: {UpdateService.CurrentVersionDisplay}");
                sb.AppendLine($"- **OS**: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
                sb.AppendLine($"- **Runtime**: .NET {Environment.Version}");
                sb.AppendLine($"- **Architecture**: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                sb.AppendLine($"- **Total Games**: {Games.Count}");
                Clipboard.SetText(sb.ToString());
            }
            catch (Exception ex)
            {
                LoggingService.Error("MainViewModel", "Failed to copy system info to clipboard", ex);
            }
        });
        CheckForUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync(true));
        ExitApplicationCommand = new RelayCommand(PromptExitApplication);

        // Game Commands
        AddGameCommand = new RelayCommand(AddGameBrowse);
        AddFolderCommand = new RelayCommand(AddGameFolderBrowse);
        OpenSteamImportCommand = new RelayCommand(OpenSteamImport);
        RefreshAllPostersCommand = new RelayCommand(async () => await RefreshAllPostersAsync());
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenTaskbarSettingsCommand = new RelayCommand(TrayPromotionService.OpenWindowsTaskbarSettings);
        OpenSteamGridDbSiteCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://www.steamgriddb.com/profile/preferences") { UseShellExecute = true }));
        RefreshCategoriesCommand = new RelayCommand(RebuildCategories);
        UndoDeleteCommand = new RelayCommand(UndoDelete);
        DismissUndoToastCommand = new RelayCommand(() => IsUndoToastVisible = false);

        _launcherService.GameUpdated += OnGameUpdatedFromLauncher;
        _hotkeyManager.GameHotkeyTriggered += OnGameHotkeyTriggered;

        LoadLibrary();

        if (_storageService.SettingsLoadWarning != null || _storageService.GamesLoadWarning != null)
        {
            string warning = string.Join(" ", new[] { _storageService.SettingsLoadWarning, _storageService.GamesLoadWarning }
                .Where(w => w != null));
            ModernDialog.ShowWarning(null, "Data File Recovered", warning);
        }

        // Schedule quiet background check for updates if enabled: once shortly after
        // launch, then again every 24 hours for as long as the app keeps running.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3500);
                while (true)
                {
                    if (_settings.AutoCheckForUpdates)
                    {
                        Application.Current?.Dispatcher.InvokeAsync(async () =>
                        {
                            await CheckForUpdatesAsync(false);
                        });
                    }
                    await Task.Delay(TimeSpan.FromHours(24));
                }
            }
            catch
            {
                // Ignore silent update check exceptions
            }
        });
    }

    // --- NAVIGATION PROPERTIES ---
    public ICommand SelectSystemCommand { get; }

    public NavSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (_currentSection != value)
            {
                var prev = _currentSection;
                _currentSection = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLibraryView));
                OnPropertyChanged(nameof(IsSystemView));
                OnPropertyChanged(nameof(IsSettingsView));
                OnPropertyChanged(nameof(IsAboutView));

                if (_currentSection == NavSection.System)
                {
                    SystemVM.StartTelemetry();
                    if (SystemVM.Report.Drives.Count == 0)
                    {
                        _ = SystemVM.LoadHardwareSpecsAsync();
                    }
                }
                else if (prev == NavSection.System)
                {
                    SystemVM.StopTelemetry();
                }
            }
        }
    }

    public bool IsLibraryView => CurrentSection == NavSection.Library;
    public bool IsSystemView => CurrentSection == NavSection.System;
    public bool IsSettingsView => CurrentSection == NavSection.Settings;
    public bool IsAboutView => CurrentSection == NavSection.About;

    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set
        {
            if (_isSidebarExpanded != value)
            {
                _isSidebarExpanded = value;
                _settings.IsSidebarExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SidebarWidth));
                OnPropertyChanged(nameof(SidebarToggleTooltip));
                OnPropertyChanged(nameof(SidebarToggleLabel));
                OnPropertyChanged(nameof(SidebarToggleChevron));
                AutoSaveSettings();
            }
        }
    }

    public double SidebarWidth => IsSidebarExpanded ? 190 : 60;
    public string SidebarToggleTooltip => IsSidebarExpanded ? "Collapse Sidebar" : "Expand Sidebar";
    public string SidebarToggleLabel => IsSidebarExpanded ? "◀ Collapse" : "▶";
    public string SidebarToggleChevron => IsSidebarExpanded ? "\uE76B" : "\uE76C";

    public ICommand ToggleSidebarCommand { get; }
    public ICommand SelectLibraryCommand { get; }
    public ICommand SelectSettingsCommand { get; }
    public ICommand OpenDiagnosticsSettingsCommand { get; }
    public ICommand SelectAboutCommand { get; }
    public ICommand SelectAboutAllTabCommand { get; }
    public ICommand SelectAboutOverviewTabCommand { get; }
    public ICommand SelectAboutFeaturesTabCommand { get; }
    public ICommand SelectAboutShortcutsTabCommand { get; }
    public ICommand SelectAboutSupportTabCommand { get; }
    public ICommand NavigateToGeneralSettingsCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand OpenGitHubIssuesCommand { get; }
    public ICommand CopySystemInfoCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ExitApplicationCommand { get; }

    // --- ABOUT SUB-SECTIONS ---
    private AboutSubSection _currentAboutSection = AboutSubSection.All;
    public AboutSubSection CurrentAboutSection
    {
        get => _currentAboutSection;
        set
        {
            if (_currentAboutSection != value)
            {
                _currentAboutSection = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAboutAllTab));
                OnPropertyChanged(nameof(IsAboutOverviewTab));
                OnPropertyChanged(nameof(IsAboutFeaturesTab));
                OnPropertyChanged(nameof(IsAboutShortcutsTab));
                OnPropertyChanged(nameof(IsAboutSupportTab));
                OnPropertyChanged(nameof(ShowAboutOverviewSection));
                OnPropertyChanged(nameof(ShowAboutFeaturesSection));
                OnPropertyChanged(nameof(ShowAboutShortcutsSection));
                OnPropertyChanged(nameof(ShowAboutSupportSection));
            }
        }
    }

    public bool IsAboutAllTab => CurrentAboutSection == AboutSubSection.All;
    public bool IsAboutOverviewTab => CurrentAboutSection == AboutSubSection.Overview;
    public bool IsAboutFeaturesTab => CurrentAboutSection == AboutSubSection.Features;
    public bool IsAboutShortcutsTab => CurrentAboutSection == AboutSubSection.Shortcuts;
    public bool IsAboutSupportTab => CurrentAboutSection == AboutSubSection.Support;

    public bool ShowAboutOverviewSection => CurrentAboutSection == AboutSubSection.All || CurrentAboutSection == AboutSubSection.Overview;
    public bool ShowAboutFeaturesSection => CurrentAboutSection == AboutSubSection.All || CurrentAboutSection == AboutSubSection.Features;
    public bool ShowAboutShortcutsSection => CurrentAboutSection == AboutSubSection.All || CurrentAboutSection == AboutSubSection.Shortcuts;
    public bool ShowAboutSupportSection => CurrentAboutSection == AboutSubSection.All || CurrentAboutSection == AboutSubSection.Support;

    // --- APPLICATION VERSION & UPDATES ---

    public string AppVersionDisplay => UpdateService.CurrentVersionDisplay;

    private string _updateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Up to date";
    public string UpdateStatusBadgeText
    {
        get => _updateStatusBadgeText;
        set => SetProperty(ref _updateStatusBadgeText, value);
    }

    private string _updateStatusIcon = "\uE73E"; // Segoe checkmark
    public string UpdateStatusIcon
    {
        get => _updateStatusIcon;
        set => SetProperty(ref _updateStatusIcon, value);
    }

    private Brush _updateStatusBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176)); // #4EC9B0
    public Brush UpdateStatusBrush
    {
        get => _updateStatusBrush;
        set => SetProperty(ref _updateStatusBrush, value);
    }

    public async Task CheckForUpdatesAsync(bool interactive)
    {
        try
        {
            UpdateStatusBadgeText = "Checking for updates...";
            UpdateStatusIcon = "\uE896";
            if (Application.Current?.TryFindResource("BrushAccentHover") is Brush accentBrush)
            {
                UpdateStatusBrush = accentBrush;
            }

            string repo = string.IsNullOrWhiteSpace(_settings.GitHubRepository) ? "stephenh678/TrayTrigger" : _settings.GitHubRepository.Trim();
            var result = await UpdateService.Instance.CheckForUpdatesAsync(repo);

            if (result.IsUpdateAvailable && result.LatestRelease != null)
            {
                UpdateStatusBadgeText = $"{result.LatestRelease.TagName} available!";
                UpdateStatusIcon = "\uE896";
                if (Application.Current?.TryFindResource("BrushAccentHover") is Brush acBrush)
                {
                    UpdateStatusBrush = acBrush;
                }

                // Always surface the dialog when a real update is found - even for the quiet
                // background checks (startup / 24h timer) - since a silently-updated badge is
                // easy to miss. Only the "up to date" / "no releases" / "error" outcomes below
                // stay gated behind `interactive`, since nagging the user with those on every
                // automatic check would be annoying.
                Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
                UpdateDialog.ShowUpdateDialog(owner, result.LatestRelease, result.CurrentVersion);
            }
            else if (result.IsUpToDate)
            {
                UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Up to date";
                UpdateStatusIcon = "\uE73E";
                UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176));

                if (interactive)
                {
                    Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
                    ModernDialog.ShowInfo(
                        owner,
                        "Check for Updates",
                        "TrayTrigger is Up to Date",
                        $"You are currently running the latest version ({UpdateService.CurrentVersionDisplay}). No updates are available.");
                }
            }
            else if (result.Status == UpdateStatus.NoReleasesFound)
            {
                UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Up to date";
                UpdateStatusIcon = "\uE73E";
                UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(78, 201, 176));

                if (interactive)
                {
                    Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
                    ModernDialog.ShowInfo(
                        owner,
                        "Check for Updates",
                        "No Releases Found on GitHub",
                        $"No published releases were found for repository '{repo}'. Once you create a release on GitHub, updates will appear here.");
                }
            }
            else
            {
                UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Check failed";
                UpdateStatusIcon = "\uE783";
                UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(224, 108, 117));

                if (interactive)
                {
                    Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
                    ModernDialog.ShowWarning(
                        owner,
                        "Check for Updates",
                        "Unable to Check for Updates",
                        result.ErrorMessage ?? "Please verify your internet connection and GitHub repository setting.");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainViewModel", "CheckForUpdatesAsync error", ex);
            UpdateStatusBadgeText = $"{UpdateService.CurrentVersionDisplay} • Check failed";
            UpdateStatusIcon = "\uE783";
            UpdateStatusBrush = new SolidColorBrush(Color.FromRgb(224, 108, 117));

            if (interactive)
            {
                Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
                ModernDialog.ShowWarning(
                    owner,
                    "Check for Updates",
                    "Error Checking for Updates",
                    ex.Message);
            }
        }
    }

    // Delegated settings commands
    public ICommand SaveInWindowSettingsCommand => SettingsVM.SaveInWindowSettingsCommand;
    public ICommand ResetSettingsCommand => SettingsVM.ResetSettingsCommand;
    public ICommand OpenStorageFolderCommand => SettingsVM.OpenStorageFolderCommand;
    public ICommand OpenCacheFolderCommand => SettingsVM.OpenCacheFolderCommand;
    public ICommand SetViewModeCommand => SettingsVM.SetViewModeCommand;
    public ICommand OpenLogFileCommand => SettingsVM.OpenLogFileCommand;
    public ICommand ClearLogCommand => SettingsVM.ClearLogCommand;
    public ICommand OpenLogFolderCommand => SettingsVM.OpenLogFolderCommand;

    // Settings ViewModel & view-mode properties
    public const string ViewModePosterGrid = SettingsViewModel.ViewModePosterGrid;
    public const string ViewModeCompactIcons = SettingsViewModel.ViewModeCompactIcons;
    public const string ViewModeDetailsList = SettingsViewModel.ViewModeDetailsList;
    public static string NormalizeViewMode(string? mode) => SettingsViewModel.NormalizeViewMode(mode);

    public string? SteamGridDbApiKeyOrNull => SettingsVM.SteamGridDbApiKeyOrNull;

    public string LibraryViewMode
    {
        get => SettingsVM.LibraryViewMode;
        set => SettingsVM.LibraryViewMode = value;
    }

    public bool IsGridView => SettingsVM.IsGridView;
    public bool IsIconsView => SettingsVM.IsIconsView;
    public bool IsListView => SettingsVM.IsListView;

    public void AutoSaveSettings() => SettingsVM.AutoSaveSettings();
    public void ResetSettingsToDefaults(bool promptConfirm = true) => SettingsVM.ResetSettingsToDefaults(promptConfirm);

    // Forwarded settings properties for backward compatibility with external consumers and diagnostics
    public bool StartWithWindows { get => SettingsVM.StartWithWindows; set => SettingsVM.StartWithWindows = value; }
    public bool StartMinimizedToTray { get => SettingsVM.StartMinimizedToTray; set => SettingsVM.StartMinimizedToTray = value; }
    public bool CanConfigureMinimized => SettingsVM.CanConfigureMinimized;
    public bool AlwaysShowTrayIcon { get => SettingsVM.AlwaysShowTrayIcon; set => SettingsVM.AlwaysShowTrayIcon = value; }
    public bool MinimizeOnGameLaunch { get => SettingsVM.MinimizeOnGameLaunch; set => SettingsVM.MinimizeOnGameLaunch = value; }
    public bool ShowRecentInTray { get => SettingsVM.ShowRecentInTray; set => SettingsVM.ShowRecentInTray = value; }
    public int MaxRecentInTray { get => SettingsVM.MaxRecentInTray; set => SettingsVM.MaxRecentInTray = value; }
    public string TrayMenuSortOption { get => SettingsVM.TrayMenuSortOption; set => SettingsVM.TrayMenuSortOption = value; }
    public bool GroupTrayMenuByCategory { get => SettingsVM.GroupTrayMenuByCategory; set => SettingsVM.GroupTrayMenuByCategory = value; }
    public bool PreferExeForGameName { get => SettingsVM.PreferExeForGameName; set => SettingsVM.PreferExeForGameName = value; }
    public bool SearchOfficialTitleOnline { get => SettingsVM.SearchOfficialTitleOnline; set => SettingsVM.SearchOfficialTitleOnline = value; }
    public string OnlineMatchSensitivity { get => SettingsVM.OnlineMatchSensitivity; set => SettingsVM.OnlineMatchSensitivity = value; }
    public ObservableCollection<string> ConfidenceThresholdOptions => SettingsVM.ConfidenceThresholdOptions;
    public bool AutoCategorizeFromSteam { get => SettingsVM.AutoCategorizeFromSteam; set => SettingsVM.AutoCategorizeFromSteam = value; }
    public bool UseVerticalPosterArt { get => SettingsVM.UseVerticalPosterArt; set => SettingsVM.UseVerticalPosterArt = value; }
    public bool UseSteamGridDbArt { get => SettingsVM.UseSteamGridDbArt; set => SettingsVM.UseSteamGridDbArt = value; }
    public string SteamGridDbApiKey { get => SettingsVM.SteamGridDbApiKey; set => SettingsVM.SteamGridDbApiKey = value; }
    public bool SteamIntegrationEnabled { get => SettingsVM.SteamIntegrationEnabled; set => SettingsVM.SteamIntegrationEnabled = value; }
    public string GlobalManageHotkey { get => SettingsVM.GlobalManageHotkey; set => SettingsVM.GlobalManageHotkey = value; }
    public bool VerboseLoggingEnabled { get => SettingsVM.VerboseLoggingEnabled; set => SettingsVM.VerboseLoggingEnabled = value; }
    public string StorageDirectoryDisplay => SettingsVM.StorageDirectoryDisplay;
    public string CacheDirectoryDisplay => SettingsVM.CacheDirectoryDisplay;


    // --- LIBRARY & SEARCH PROPERTIES ---
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            FilteredGames.Refresh();
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (_selectedCategory != value)
            {
                _selectedCategory = value;
                _settings.LastCategoryFilter = value;
                OnPropertyChanged();
                foreach (var tab in CategoryTabs)
                {
                    tab.IsSelected = string.Equals(tab.Name, value, StringComparison.OrdinalIgnoreCase);
                }
                FilteredGames.Refresh();
                AutoSaveSettings();
            }
        }
    }

    public string SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (_selectedSortOption != value)
            {
                _selectedSortOption = value;
                _settings.LastSortOption = value;
                _storageService.SaveSettings(_settings);
                OnPropertyChanged();
                ApplySort();
            }
        }
    }

    public bool IsUndoToastVisible
    {
        get => _isUndoToastVisible;
        set { _isUndoToastVisible = value; OnPropertyChanged(); }
    }

    public string UndoToastMessage
    {
        get => _undoToastMessage;
        set { _undoToastMessage = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public int TotalGameCount => Games.Count;
    public string TotalGameCountDisplay => $"{Games.Count} game(s)";

    public ICommand AddGameCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand OpenSteamImportCommand { get; }
    public ICommand RefreshAllPostersCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenTaskbarSettingsCommand { get; }
    public ICommand OpenSteamGridDbSiteCommand { get; }
    public ICommand RefreshCategoriesCommand { get; }
    public ICommand UndoDeleteCommand { get; }
    public ICommand DismissUndoToastCommand { get; }

    public void LoadLibrary()
    {
        Games.Clear();
        var rawGames = _storageService.LoadGames();

        foreach (var g in rawGames)
        {
            Games.Add(CreateCardViewModel(g));
        }

        RebuildCategories();
        UpdateHotkeys();
        ApplySort();
        StatusMessage = $"{Games.Count} game(s) in library";
        OnPropertyChanged(nameof(TotalGameCount));
        OnPropertyChanged(nameof(TotalGameCountDisplay));
        LibraryUpdated?.Invoke();

        _ = EnrichLibraryAsync();
    }

    public GameCardViewModel CreateCardViewModel(GameEntry game)
    {
        return new GameCardViewModel(
            game,
            onLaunch: LaunchGame,
            onEdit: card => RequestEditGameDialog?.Invoke(card),
            onDelete: DeleteGame,
            onRelocate: RelocateGame,
            onRename: card => RequestQuickRename?.Invoke(card),
            onChangeCategory: card => RequestQuickCategory?.Invoke(card),
            onChangeIcon: ChangeGameIcon,
            onChangeCover: ChangeGameCover,
            onFetchExeName: FetchExeNameForGame,
            onViewDetails: OpenGameDetails,
            onEditSteamAppId: EditSteamAppId,
            onRefreshMetadata: card => _ = RefreshGameMetadataAsync(card),
            onToggleFavorite: ToggleFavorite,
            getUseVerticalPosterArt: () => UseVerticalPosterArt
        );
    }

    private void ToggleFavorite(GameCardViewModel card)
    {
        SaveLibrary();
        FilteredGames.Refresh();
    }

    public void OpenGameDetails(GameCardViewModel card)
    {
        bool requestedLaunch = false;
        bool requestedEdit = false;
        bool requestedDelete = false;

        var vm = new GameDetailsViewModel(
            card.Game, 
            _steamMetadataService, 
            _steamSearchService, 
            launchAction: _ => requestedLaunch = true,
            editAction: _ => requestedEdit = true,
            deleteAction: _ => requestedDelete = true,
            steamGridDbApiKey: SteamGridDbApiKeyOrNull,
            minConfidence: _settings.OnlineMatchConfidenceThreshold);

        var dlg = new Views.GameDetailsDialog(vm);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dlg.Owner = owner;
        }
        dlg.ShowDialog();

        if (requestedLaunch)
        {
            LaunchGame(card);
        }
        else if (requestedEdit)
        {
            RequestEditGameDialog?.Invoke(card);
        }
        else if (requestedDelete)
        {
            DeleteGame(card);
        }

        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
        LibraryUpdated?.Invoke();
    }

    public void LaunchGameEntry(GameEntry game)
    {
        var card = Games.FirstOrDefault(c => c.Game.Id == game.Id);
        if (card != null)
        {
            LaunchGame(card);
        }
        else
        {
            if (_launcherService.LaunchGame(game, out _, out _))
            {
                if (_settings.MinimizeOnGameLaunch)
                {
                    RequestMinimizeToTray?.Invoke();
                }
            }
        }
    }

    public void LaunchGame(GameCardViewModel card)
    {
        Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        if (card.IsMissing)
        {
            var res = ModernDialog.Confirm(
                owner,
                "Game Executable Missing",
                $"The executable for \"{card.Name}\" was not found.",
                $"Expected location:\n{card.Game.ExecutablePath}\n\nWould you like to locate the game executable now?",
                confirmText: "Locate...",
                cancelText: "Cancel");

            if (res)
            {
                RelocateGame(card);
            }
            return;
        }

        if (_launcherService.LaunchGame(card.Game, out string? err, out bool isMissing))
        {
            StatusMessage = $"Launched {card.Name}";
            card.RefreshProperties();
            SaveLibrary();

            if (_settings.MinimizeOnGameLaunch)
            {
                RequestMinimizeToTray?.Invoke();
            }
        }
        else if (isMissing)
        {
            card.RefreshProperties();
            var res = ModernDialog.Confirm(
                owner,
                "Game Executable Missing",
                $"The executable for \"{card.Name}\" was not found.",
                $"Expected location:\n{card.Game.ExecutablePath}\n\nWould you like to locate the game executable now?",
                confirmText: "Locate...",
                cancelText: "Cancel");

            if (res)
            {
                RelocateGame(card);
            }
        }
        else
        {
            StatusMessage = $"Error: {err}";
            ModernDialog.ShowWarning(owner, "Launch Error", err ?? "Failed to launch game.");
        }
    }

    public void RelocateGame(GameCardViewModel card)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Locate Game Executable for {card.Name}",
            Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            card.Game.ExecutablePath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(card.Game.WorkingDirectory) || !Directory.Exists(card.Game.WorkingDirectory))
            {
                card.Game.WorkingDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            }

            string newIcon = _iconExtractorService.ExtractAndCacheIcon(card.Game.Id, dialog.FileName, card.Game.Name);
            if (!string.IsNullOrEmpty(newIcon))
            {
                card.Game.IconPath = newIcon;
            }

            card.RefreshProperties();
            SaveLibrary();
            UpdateHotkeys();
            StatusMessage = $"Updated location for {card.Name}";
        }
    }

    public void ChangeGameIcon(GameCardViewModel card)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select Icon for \"{card.Name}\"",
            Filter = "Image & Icon Files (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            string cachedIcon = _iconExtractorService.ExtractAndCacheIcon(card.Game.Id, dialog.FileName, card.Game.Name);
            if (!string.IsNullOrEmpty(cachedIcon))
            {
                card.Game.IconPath = cachedIcon;
                card.RefreshProperties();
                SaveLibrary();
                StatusMessage = $"Updated icon for \"{card.Name}\"";
            }
        }
    }

    public void ChangeGameCover(GameCardViewModel card)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select Poster Artwork for \"{card.Name}\" (600×900 recommended)",
            Filter = "Image Files (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|All Files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                string coversDir = SteamMetadataService.CoversDirectory;
                if (!Directory.Exists(coversDir)) Directory.CreateDirectory(coversDir);

                string ext = Path.GetExtension(dialog.FileName);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                string destFile = Path.Combine(coversDir, $"{card.Game.Id}{ext}");

                if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(dialog.FileName, destFile, overwrite: true);
                }
                card.Game.CoverImagePath = destFile;
                card.ReloadCover();
                SaveLibrary();
                StatusMessage = $"Updated poster artwork for \"{card.Name}\"";
            }
            catch (Exception ex)
            {
                ModernDialog.ShowWarning(null, "Poster Artwork Error", $"Failed to set poster artwork: {ex.Message}");
            }
        }
    }

    public void EditSteamAppId(GameCardViewModel card)
    {
        RequestEditSteamAppId?.Invoke(card);
    }

    public async Task UpdateGameSteamAppIdAsync(GameCardViewModel card, string? newAppId)
    {
        string trimmed = newAppId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            card.Game.SteamAppId = null;
            card.RefreshProperties();
            SaveLibrary();
            StatusMessage = $"Cleared Steam App ID for \"{card.Name}\"";
            return;
        }

        if (!trimmed.All(char.IsDigit))
        {
            ModernDialog.ShowWarning(Application.Current?.MainWindow, "Invalid App ID", "Steam App ID must be a numeric ID (e.g. 1245620).");
            return;
        }

        card.Game.SteamAppId = trimmed;
        // IMPORTANT: Never change IsSteamGame to true! Non-Steam games link Steam App ID purely for metadata/art.
        if (!string.IsNullOrWhiteSpace(card.Game.ExecutablePath) && !card.Game.ExecutablePath.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
        {
            card.Game.IsSteamGame = false;
        }

        StatusMessage = $"Fetching Steam metadata for App ID {trimmed}...";
        SteamMetadataService.InvalidateCache(trimmed);
        var details = await _steamMetadataService.GetAppDetailsAsync(trimmed, SteamGridDbApiKeyOrNull, forceRefresh: true);
        if (details != null)
        {
            if (!string.IsNullOrWhiteSpace(details.Name) && (string.IsNullOrWhiteSpace(card.Game.Name) || card.Game.Name.StartsWith("Unnamed", StringComparison.OrdinalIgnoreCase)))
            {
                card.Game.Name = details.Name;
            }
            if (!string.IsNullOrWhiteSpace(details.CoverImagePath))
            {
                card.Game.CoverImagePath = details.CoverImagePath;
            }
            if ((string.IsNullOrWhiteSpace(card.Game.Category) || card.Game.Category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(details.PrimaryGenre))
            {
                card.Game.Category = details.PrimaryGenre;
            }
            StatusMessage = $"Updated metadata and poster for \"{card.Name}\" (Steam App ID {trimmed})";
        }
        else
        {
            StatusMessage = $"Linked Steam App ID {trimmed} to \"{card.Name}\"";
        }

        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
    }

    public async Task RefreshGameMetadataAsync(GameCardViewModel card)
    {
        if (string.IsNullOrWhiteSpace(card.Game.SteamAppId))
        {
            StatusMessage = $"Searching Steam store for \"{card.Name}\"...";
            await EnrichGameWithSteamMetadataAsync(card.Game);
        }
        else
        {
            StatusMessage = $"Refreshing metadata for \"{card.Name}\"...";
            SteamMetadataService.InvalidateCache(card.Game.SteamAppId);
            var details = await _steamMetadataService.GetAppDetailsAsync(card.Game.SteamAppId, SteamGridDbApiKeyOrNull, forceRefresh: true);
            if (details != null && !string.IsNullOrWhiteSpace(details.CoverImagePath))
            {
                card.Game.CoverImagePath = details.CoverImagePath;
            }
        }

        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
        StatusMessage = $"Refreshed metadata for \"{card.Name}\"";
    }

    public void FetchExeNameForGame(GameCardViewModel card) => _ = FetchExeNameForGameAsync(card);

    public async Task FetchExeNameForGameAsync(GameCardViewModel card)
    {
        if (card.IsSteamGame || string.IsNullOrWhiteSpace(card.Game.ExecutablePath))
            return;

        try
        {
            string folder = GameNameExtractor.FindMeaningfulFolderName(card.Game.ExecutablePath, card.Game.WorkingDirectory);

            var res = await GameNameExtractor.ResolveGameMatchAsync(
                card.Game.ExecutablePath,
                folder,
                preferExe: _settings.PreferExeForGameName,
                searchOnline: _settings.SearchOfficialTitleOnline,
                steamSearch: _steamSearchService,
                minConfidence: _settings.OnlineMatchConfidenceThreshold);

            bool updated = false;

            if (!string.IsNullOrWhiteSpace(res.ResolvedTitle) && !res.ResolvedTitle.Equals(card.Name, StringComparison.Ordinal))
            {
                string oldName = card.Name;
                card.Game.Name = res.ResolvedTitle;
                updated = true;
                LoggingService.Info("Library", $"Updated title from \"{oldName}\" to \"{res.ResolvedTitle}\" via online/exe metadata.");
            }

            if (!string.IsNullOrWhiteSpace(res.SteamAppId))
            {
                card.Game.SteamAppId = res.SteamAppId;
                updated = true;

                if (_settings.AutoCategorizeFromSteam)
                {
                    // This is an explicit, user-triggered refresh (unlike the passive background
                    // enrichment pass), so force a fresh lookup/poster download - bypassing the
                    // in-memory details cache and the "poster file already exists" check - and
                    // apply whatever cover it finds even if one is already set. Without
                    // forceRefresh, a game that fell back to a composited-banner poster earlier
                    // would never get a chance to pick up better art later (e.g. after the user
                    // adds a SteamGridDB API key).
                    var details = await _steamMetadataService.GetAppDetailsAsync(res.SteamAppId, SteamGridDbApiKeyOrNull, forceRefresh: true);
                    if (details != null)
                    {
                        if (card.Game.Category == "Uncategorized" && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                        {
                            card.Game.Category = details.PrimaryGenre;
                        }
                        if (!string.IsNullOrWhiteSpace(details.CoverImagePath))
                        {
                            card.Game.CoverImagePath = details.CoverImagePath;
                        }
                    }
                }
            }

            if (updated)
            {
                card.RefreshProperties();
                RebuildCategories();
                SaveLibrary();
                ApplySort();
                StatusMessage = $"Updated \"{card.Name}\" with Steam metadata.";
                LibraryUpdated?.Invoke();
            }
            else
            {
                StatusMessage = $"Game info is already up to date for \"{card.Name}\".";
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainViewModel", $"Error fetching exe/online name for '{card.Name}'", ex);
            StatusMessage = $"Failed to update info for \"{card.Name}\".";
        }
    }

    public void NotifySettingsChanged()
    {
        OnPropertyChanged(nameof(UseVerticalPosterArt));
        OnPropertyChanged(nameof(AutoCategorizeFromSteam));
        OnPropertyChanged(nameof(SearchOfficialTitleOnline));
        OnPropertyChanged(nameof(PreferExeForGameName));
        foreach (var card in Games)
        {
            card.NotifyPosterArtChanged();
        }
        if (_settings.AutoCategorizeFromSteam)
        {
            _ = EnrichLibraryAsync();
        }
    }

    private async Task EnrichGameWithSteamMetadataAsync(GameEntry entry)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.SteamAppId))
            {
                if (_settings.AutoCategorizeFromSteam || _settings.UseVerticalPosterArt)
                {
                    var details = await _steamMetadataService.GetAppDetailsAsync(entry.SteamAppId, SteamGridDbApiKeyOrNull);
                    if (details != null)
                    {
                        if (_settings.AutoCategorizeFromSteam && (entry.Category == "Uncategorized" || entry.Category == "Steam") && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                            entry.Category = details.PrimaryGenre;
                        if (string.IsNullOrWhiteSpace(entry.CoverImagePath) && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                            entry.CoverImagePath = details.CoverImagePath;
                    }
                }
                return;
            }

            if (!_settings.SearchOfficialTitleOnline && !_settings.AutoCategorizeFromSteam && !_settings.UseVerticalPosterArt)
                return;

            string folder = GameNameExtractor.FindMeaningfulFolderName(entry.ExecutablePath, entry.WorkingDirectory);

            var res = await GameNameExtractor.ResolveGameMatchAsync(
                entry.ExecutablePath,
                folder,
                preferExe: _settings.PreferExeForGameName,
                searchOnline: true,
                steamSearch: _steamSearchService,
                minConfidence: _settings.OnlineMatchConfidenceThreshold);

            if (!string.IsNullOrWhiteSpace(res.ResolvedTitle) && _settings.SearchOfficialTitleOnline)
            {
                entry.Name = res.ResolvedTitle;
            }

            if (!string.IsNullOrWhiteSpace(res.SteamAppId))
            {
                entry.SteamAppId = res.SteamAppId;

                var details = await _steamMetadataService.GetAppDetailsAsync(res.SteamAppId, SteamGridDbApiKeyOrNull);
                if (details != null)
                {
                    if (_settings.AutoCategorizeFromSteam && (entry.Category == "Uncategorized" || entry.Category == "Steam") && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                        entry.Category = details.PrimaryGenre;
                    if (string.IsNullOrWhiteSpace(entry.CoverImagePath) && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                        entry.CoverImagePath = details.CoverImagePath;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel", $"Enrichment error for {entry.Name}: {ex.Message}");
        }
    }

    private const int MaxConcurrentEnrichments = 4;

    private bool _isRefreshingAllPosters;
    public bool IsRefreshingAllPosters
    {
        get => _isRefreshingAllPosters;
        private set { _isRefreshingAllPosters = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRefreshAllPosters)); }
    }

    public bool CanRefreshAllPosters => !IsRefreshingAllPosters;

    /// <summary>
    /// Force-refreshes poster art for every game with a known Steam AppId, bypassing the
    /// "already has a cover" cache check so a game stuck on the composited-banner fallback
    /// tier can pick up better art (e.g. after the user adds/enables a SteamGridDB API key).
    /// Unlike <see cref="EnrichLibraryAsync"/>, this ignores existing category/cover state and
    /// always re-fetches, since the whole point is to override a previously-cached poster.
    /// </summary>
    /// <param name="progress">
    /// Optional progress/result text sink. StatusMessage alone isn't enough here: this action
    /// is triggered from the Settings page, whose status bar (bound to StatusMessage) lives in
    /// a different XAML section that's collapsed while Settings is the active view - callers
    /// needing on-screen feedback in that context should pass a sink that surfaces it there.
    /// </param>
    public async Task RefreshAllPostersAsync(IProgress<string>? progress = null)
    {
        void Report(string message)
        {
            StatusMessage = message;
            progress?.Report(message);
        }

        if (IsRefreshingAllPosters)
        {
            Report("A poster refresh is already in progress. Please wait for it to finish.");
            return;
        }

        var candidates = Games.Where(card => !string.IsNullOrWhiteSpace(card.Game.SteamAppId)).ToList();
        if (candidates.Count == 0)
        {
            Report("No games with a linked Steam AppId to refresh.");
            return;
        }

        IsRefreshingAllPosters = true;
        try
        {
            int completed = 0;
            int updated = 0;
            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);

            var tasks = candidates.Select(async card =>
            {
                await throttle.WaitAsync();
                try
                {
                    string appId = card.Game.SteamAppId!;
                    var details = await _steamMetadataService.GetAppDetailsAsync(appId, SteamGridDbApiKeyOrNull, forceRefresh: true);
                    // DownloadAndCachePosterAsync always writes to the same Covers/{appId}.jpg
                    // path regardless of which source tier supplied it, so CoverImagePath is
                    // virtually always unchanged even when the file's actual contents just got
                    // replaced with better art. Don't gate on path equality - always reassign
                    // and refresh so the in-memory bitmap reloads from disk (LoadBitmapSafely
                    // already bypasses WPF's image cache), or a same-path content change would
                    // never show up until the app restarts.
                    if (details != null && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                    {
                        card.Game.CoverImagePath = details.CoverImagePath;
                        card.RefreshProperties();
                        Interlocked.Increment(ref updated);
                    }
                }
                finally
                {
                    throttle.Release();
                    Interlocked.Increment(ref completed);
                    Report($"Refreshing posters... {completed}/{candidates.Count}");
                }
            });

            await Task.WhenAll(tasks);

            if (updated > 0)
            {
                SaveLibrary();
                LibraryUpdated?.Invoke();
            }

            Report($"Poster refresh complete: {updated} of {candidates.Count} game(s) refreshed.");
        }
        finally
        {
            IsRefreshingAllPosters = false;
        }
    }

    public async Task EnrichLibraryAsync()
    {
        if (!_settings.AutoCategorizeFromSteam && !_settings.SearchOfficialTitleOnline)
            return;

        // Settings toggles, library load, and post-import enrichment can all request this
        // around the same time; let the in-flight run finish rather than starting a
        // duplicate pass over the same candidates.
        if (_isEnrichmentInProgress)
            return;

        _isEnrichmentInProgress = true;
        try
        {
            var candidates = Games
                .Where(card => card.Game.Category == "Uncategorized" || card.Game.Category == "Steam" || string.IsNullOrWhiteSpace(card.Game.CoverImagePath) || string.IsNullOrWhiteSpace(card.Game.SteamAppId))
                .ToList();

            if (candidates.Count == 0)
                return;

            bool changed = false;
            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);

            var tasks = candidates.Select(async card =>
            {
                await throttle.WaitAsync();
                try
                {
                    string oldCat = card.Game.Category;
                    string? oldCover = card.Game.CoverImagePath;
                    string? oldAppId = card.Game.SteamAppId;

                    await EnrichGameWithSteamMetadataAsync(card.Game);

                    if (card.Game.Category != oldCat || card.Game.CoverImagePath != oldCover || card.Game.SteamAppId != oldAppId)
                    {
                        card.RefreshProperties();
                        changed = true;
                    }
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            if (changed)
            {
                RebuildCategories();
                SaveLibrary();
                LibraryUpdated?.Invoke();
            }
        }
        finally
        {
            _isEnrichmentInProgress = false;
        }
    }

    public void ApplyRename(GameCardViewModel card, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;

        card.Game.Name = newName.Trim();
        card.RefreshProperties();
        SaveLibrary();
        StatusMessage = $"Renamed to \"{card.Name}\"";
    }

    public void ApplyCategory(GameCardViewModel card, string newCategory)
    {
        if (string.IsNullOrWhiteSpace(newCategory))
            newCategory = "Uncategorized";

        card.Game.Category = newCategory.Trim();
        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
        FilteredGames.Refresh();
        StatusMessage = $"Updated category for \"{card.Name}\" to {card.Category}";
    }

    public void DeleteGame(GameCardViewModel card)
    {
        Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        bool confirmed = ModernDialog.ConfirmDelete(
            owner,
            "Remove from Library",
            $"Are you sure you want to remove \"{card.Name}\" from your library?",
            "This will only remove the shortcut from TrayTrigger. Your installed game files will not be deleted.",
            confirmText: "Remove",
            cancelText: "Cancel");

        if (!confirmed)
        {
            return;
        }

        _undoToastTimer?.Stop();

        // Only one pending deletion can be undone at a time. If another one is still sitting in
        // its undo window when this new delete arrives, it's about to be overwritten and can no
        // longer be undone anyway - finalize its cached artwork cleanup now instead of leaking it.
        if (_lastRemovedGame != null)
        {
            DeleteCachedArtwork(_lastRemovedGame);
        }

        _lastRemovedGame = card.Game;
        _lastRemovedIndex = Games.IndexOf(card);

        Games.Remove(card);
        RebuildCategories();
        SaveLibrary();
        UpdateHotkeys();

        UndoToastMessage = $"Removed \"{card.Name}\"";
        IsUndoToastVisible = true;

        _undoToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _undoToastTimer.Tick += (s, e) =>
        {
            _undoToastTimer.Stop();
            IsUndoToastVisible = false;

            // The undo window has expired - the removal is now final, so it's safe to delete
            // the game's cached icon/cover files instead of leaving them orphaned forever.
            if (_lastRemovedGame != null)
            {
                DeleteCachedArtwork(_lastRemovedGame);
                _lastRemovedGame = null;
            }
        };
        _undoToastTimer.Start();

        StatusMessage = $"Removed {card.Name}";
        OnPropertyChanged(nameof(TotalGameCount));
        OnPropertyChanged(nameof(TotalGameCountDisplay));
        LibraryUpdated?.Invoke();
    }

    public void UndoDelete()
    {
        _undoToastTimer?.Stop();
        IsUndoToastVisible = false;

        if (_lastRemovedGame != null)
        {
            var card = CreateCardViewModel(_lastRemovedGame);
            if (_lastRemovedIndex >= 0 && _lastRemovedIndex <= Games.Count)
            {
                Games.Insert(_lastRemovedIndex, card);
            }
            else
            {
                Games.Add(card);
            }

            RebuildCategories();
            SaveLibrary();
            UpdateHotkeys();
            ApplySort();

            StatusMessage = $"Restored \"{card.Name}\" to library.";
            OnPropertyChanged(nameof(TotalGameCount));
            OnPropertyChanged(nameof(TotalGameCountDisplay));
            LibraryUpdated?.Invoke();
            _lastRemovedGame = null;
        }
    }

    /// <summary>
    /// Deletes a removed game's cached icon/cover files. Both are always TrayTrigger-owned
    /// copies under IconsDirectory/CoversDirectory - even "Change Icon"/"Change Cover" copy the
    /// user's chosen file in rather than referencing it in place - so this never touches a game's
    /// actual installed files or a user's original external image.
    /// </summary>
    private void DeleteCachedArtwork(GameEntry game)
    {
        // Two library entries can share one cached file (same Steam AppId added twice, e.g. via
        // "Add Anyway", or a Steam entry plus a local exe entry) - don't blank the other entry's
        // art out from under it just because this one is being removed.
        if (!IsArtworkPathStillReferenced(game.IconPath, g => g.Game.IconPath, game.Id))
        {
            TryDeleteManagedFile(game.IconPath, _storageService.IconsDirectory);
        }
        if (!IsArtworkPathStillReferenced(game.CoverImagePath, g => g.Game.CoverImagePath, game.Id))
        {
            TryDeleteManagedFile(game.CoverImagePath, SteamMetadataService.CoversDirectory);
        }

        // Without this, re-adding the same game later hits SteamMetadataService's in-memory
        // details cache and gets back a CoverImagePath pointing at the file just deleted above,
        // so the re-added card shows no cover art at all (icon-only) until the app restarts.
        if (!string.IsNullOrWhiteSpace(game.SteamAppId))
        {
            SteamMetadataService.InvalidateCache(game.SteamAppId);
        }
    }

    private bool IsArtworkPathStillReferenced(string? path, Func<GameCardViewModel, string?> selector, string excludeGameId)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Games.Any(g => g.Id != excludeGameId && string.Equals(selector(g), path, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDeleteManagedFile(string? path, string expectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullExpectedDir = Path.GetFullPath(expectedDirectory);
            if (!fullPath.StartsWith(fullExpectedDir, StringComparison.OrdinalIgnoreCase)) return;

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("MainViewModel", $"Failed to delete cached artwork '{path}': {ex.Message}");
        }
    }

    public void HandleFileDrop(string[] files) => _ = HandleFileDropAsync(files);

    public async Task HandleFileDropAsync(string[] files)
    {
        if (files == null || files.Length == 0) return;

        // Folder drops go through ProcessFolderAdd, which can show a modal picker/batch-import
        // dialog and then calls AddCandidateAsync/ImportBatchGamesAsync - both of which check
        // _isImportInProgress themselves. Handle folders in their own pass, outside the guard
        // below, so that nested call doesn't see "already in progress" (held by this very method)
        // and silently no-op instead of actually adding the game.
        var folders = files.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)).ToList();
        var nonFolderFiles = files.Where(f => !string.IsNullOrWhiteSpace(f) && !Directory.Exists(f)).ToArray();

        if (_isImportInProgress)
        {
            StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return;
        }

        _isImportInProgress = true;
        try
        {
            int addedCount = 0;
            foreach (var file in nonFolderFiles)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;

                if (!File.Exists(file)) continue;

                try
                {
                    var shortcut = _shortcutService.Resolve(file);
                    string entryName = shortcut.Name;
                    string? onlineAppId = shortcut.SteamAppId;

                    var existingDuplicate = FindDuplicateGame(shortcut.TargetPath, shortcut.SteamAppId);
                    if (existingDuplicate != null)
                    {
                        Window? dupOwner = Application.Current?.MainWindow is { IsVisible: true } dw ? dw : null;
                        bool addAnyway = ModernDialog.Confirm(
                            dupOwner,
                            "Game Already in Library",
                            $"\"{existingDuplicate.Name}\" is already in your library.",
                            "Add it again anyway?",
                            confirmText: "Add Anyway",
                            cancelText: "Skip");
                        if (!addAnyway)
                        {
                            continue;
                        }
                    }

                    if (_settings.SearchOfficialTitleOnline && !shortcut.IsSteamUrl)
                    {
                        var res = await GameNameExtractor.ResolveGameMatchAsync(
                            shortcut.TargetPath,
                            shortcut.WorkingDirectory,
                            preferExe: _settings.PreferExeForGameName,
                            searchOnline: true,
                            steamSearch: _steamSearchService,
                            minConfidence: _settings.OnlineMatchConfidenceThreshold);

                        if (!string.IsNullOrWhiteSpace(res.ResolvedTitle))
                        {
                            entryName = res.ResolvedTitle;
                        }
                        if (!string.IsNullOrWhiteSpace(res.SteamAppId))
                        {
                            onlineAppId = res.SteamAppId;
                        }
                    }

                    var entry = new GameEntry
                    {
                        Name = entryName,
                        ExecutablePath = shortcut.TargetPath,
                        Arguments = shortcut.Arguments,
                        WorkingDirectory = shortcut.WorkingDirectory,
                        Category = SelectedCategory != "All" ? SelectedCategory : "Uncategorized",
                        IsSteamGame = shortcut.IsSteamUrl,
                        SteamAppId = onlineAppId
                    };

                    // Extract & cache icon
                    string iconSource = !string.IsNullOrEmpty(shortcut.IconLocation) ? shortcut.IconLocation : shortcut.TargetPath;
                    entry.IconPath = _iconExtractorService.ExtractAndCacheIcon(entry.Id, iconSource, entry.Name);

                    await EnrichGameWithSteamMetadataAsync(entry);
                    Games.Add(CreateCardViewModel(entry));
                    addedCount++;
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("MainViewModel", $"Failed to ingest dropped file '{file}': {ex.Message}");
                }
            }

            if (addedCount > 0)
            {
                RebuildCategories();
                SaveLibrary();
                UpdateHotkeys();
                ApplySort();
                StatusMessage = $"Added {addedCount} new game(s) instantly!";
                OnPropertyChanged(nameof(TotalGameCount));
                OnPropertyChanged(nameof(TotalGameCountDisplay));
                LibraryUpdated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainViewModel", "Error in HandleFileDrop", ex);
        }
        finally
        {
            _isImportInProgress = false;
        }

        // Processed after the guard above is released - see comment at the top of this method.
        if (folders.Count == 1)
        {
            ProcessFolderAdd(folders[0]);
        }
        else if (folders.Count > 1)
        {
            ProcessFolderAddBatch(folders);
        }
    }

    // Scans every dropped folder up front and merges the results into a single batch-import
    // prompt, instead of prompting once per folder (see ProcessFolderAdd, which still owns the
    // single-folder path so its "no games" / "one game" / "overwhelming match" shortcuts are
    // unaffected).
    public void ProcessFolderAddBatch(List<string> folderPaths)
    {
        var aggregated = new List<GameCandidate>();

        foreach (var folderPath in folderPaths)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) continue;

            var scanResult = _folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName);

            if (scanResult.IsMultiGameLibrary)
            {
                aggregated.AddRange(scanResult.DiscoveredGames);
            }
            else if (scanResult.SingleGameCandidates.Count > 0)
            {
                // Represent this individual game folder with its single best-scoring candidate.
                aggregated.Add(scanResult.SingleGameCandidates[0]);
            }
        }

        if (aggregated.Count == 0)
        {
            Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
            ModernDialog.ShowInfo(
                owner,
                "No Games Found",
                "No game executables found across the dropped folders.",
                "Please ensure the selected folders contain installed games or executable files.");
            return;
        }

        if (aggregated.Count == 1)
        {
            AddCandidate(aggregated[0]);
            return;
        }

        string combinedLabel = $"{folderPaths.Count} folders";
        RequestFolderBatchImport?.Invoke(combinedLabel, aggregated);
    }

    public void ProcessFolderAdd(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        var scanResult = _folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName);

        // A. Multi-game parent folder detected (e.g. C:\Games, D:\SteamLibrary\steamapps\common, C:\GOG Games)
        if (scanResult.IsMultiGameLibrary)
        {
            if (scanResult.DiscoveredGames.Count == 0)
            {
                Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
                ModernDialog.ShowInfo(
                    owner,
                    "No Games Found",
                    $"No game executables found across subfolders in \"{Path.GetFileName(folderPath.TrimEnd('\\', '/'))}\".",
                    "Please ensure the selected directory contains installed games or executable files.");
                return;
            }

            if (scanResult.DiscoveredGames.Count == 1)
            {
                AddCandidate(scanResult.DiscoveredGames[0]);
                return;
            }

            // Launch Batch Import dialog for the multi-game folder
            RequestFolderBatchImport?.Invoke(folderPath, scanResult.DiscoveredGames);
            return;
        }

        // B. Single-game folder branch
        var candidates = scanResult.SingleGameCandidates;
        if (candidates.Count == 0)
        {
            Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
            ModernDialog.ShowInfo(
                owner,
                "No Game Executable Found",
                $"No game executables found across the folder branch of \"{Path.GetFileName(folderPath.TrimEnd('\\', '/'))}\".",
                "Please ensure the selected folder contains the installed game files.");
            return;
        }

        if (candidates.Count == 1)
        {
            AddCandidate(candidates[0]);
        }
        else
        {
            // If the top candidate is an overwhelming clear match compared to runner-up, auto-add
            if (candidates[0].ConfidenceScore >= 90 && (candidates[0].ConfidenceScore - candidates[1].ConfidenceScore >= 40))
            {
                AddCandidate(candidates[0]);
            }
            else
            {
                RequestCandidatePicker?.Invoke(folderPath, candidates);
            }
        }
    }

    public void ImportBatchGames(List<GameCandidate> candidates) => _ = ImportBatchGamesAsync(candidates);

    public async Task ImportBatchGamesAsync(List<GameCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0) return;

        if (_isImportInProgress)
        {
            StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return;
        }

        _isImportInProgress = true;
        try
        {
            // Avoid duplicates
            var toProcess = candidates
                .Where(c => !Games.Any(g => g.Game.ExecutablePath.Equals(c.ExePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = candidates.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                StatusMessage = "All selected games are already in your library.";
                return;
            }

            StatusMessage = $"Importing {toProcess.Count} game(s)...";

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();

            var tasks = toProcess.Select(async c =>
            {
                await throttle.WaitAsync();
                try
                {
                    string gameName = c.Name;
                    string? matchedAppId = null;

                    if (_settings.SearchOfficialTitleOnline)
                    {
                        var res = await GameNameExtractor.ResolveGameMatchAsync(
                            c.ExePath,
                            c.WorkingDirectory,
                            preferExe: _settings.PreferExeForGameName,
                            searchOnline: true,
                            steamSearch: _steamSearchService,
                            minConfidence: _settings.OnlineMatchConfidenceThreshold);

                        if (!string.IsNullOrWhiteSpace(res.ResolvedTitle))
                        {
                            gameName = res.ResolvedTitle;
                        }
                        if (!string.IsNullOrWhiteSpace(res.SteamAppId))
                        {
                            matchedAppId = res.SteamAppId;
                        }
                    }

                    var entry = new GameEntry
                    {
                        Name = gameName,
                        ExecutablePath = c.ExePath,
                        WorkingDirectory = c.WorkingDirectory,
                        Category = SelectedCategory != "All" ? SelectedCategory : "Uncategorized",
                        SteamAppId = matchedAppId
                    };

                    entry.IconPath = _iconExtractorService.ExtractAndCacheIcon(entry.Id, entry.ExecutablePath, entry.Name);
                    await EnrichGameWithSteamMetadataAsync(entry);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("MainViewModel", $"Failed to import candidate '{c.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            if (!preparedEntries.IsEmpty)
            {
                foreach (var entry in preparedEntries)
                {
                    Games.Add(CreateCardViewModel(entry));
                }

                RebuildCategories();
                SaveLibrary();
                UpdateHotkeys();
                ApplySort();
                StatusMessage = skippedDuplicates > 0
                    ? $"Added {preparedEntries.Count} games from folder! ({skippedDuplicates} already in library, skipped)"
                    : $"Added {preparedEntries.Count} games from folder!";
                OnPropertyChanged(nameof(TotalGameCount));
                OnPropertyChanged(nameof(TotalGameCountDisplay));
                LibraryUpdated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainViewModel", "Error in ImportBatchGames", ex);
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    public void AddCandidate(GameCandidate candidate) => _ = AddCandidateAsync(candidate);

    public async Task AddCandidateAsync(GameCandidate candidate)
    {
        if (_isImportInProgress)
        {
            StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return;
        }

        _isImportInProgress = true;
        try
        {
            var existingDuplicate = FindDuplicateGame(candidate.ExePath, null);
            if (existingDuplicate != null)
            {
                Window? dupOwner = Application.Current?.MainWindow is { IsVisible: true } dw ? dw : null;
                bool addAnyway = ModernDialog.Confirm(
                    dupOwner,
                    "Game Already in Library",
                    $"\"{existingDuplicate.Name}\" is already in your library.",
                    "Add it again anyway?",
                    confirmText: "Add Anyway",
                    cancelText: "Skip");
                if (!addAnyway)
                {
                    return;
                }
            }

            string finalName = candidate.Name;
            string? matchedAppId = null;

            if (_settings.SearchOfficialTitleOnline)
            {
                var res = await GameNameExtractor.ResolveGameMatchAsync(
                    candidate.ExePath,
                    candidate.WorkingDirectory,
                    preferExe: _settings.PreferExeForGameName,
                    searchOnline: true,
                    steamSearch: _steamSearchService,
                    minConfidence: _settings.OnlineMatchConfidenceThreshold);

                if (!string.IsNullOrWhiteSpace(res.ResolvedTitle))
                {
                    finalName = res.ResolvedTitle;
                }
                if (!string.IsNullOrWhiteSpace(res.SteamAppId))
                {
                    matchedAppId = res.SteamAppId;
                }
            }

            var entry = new GameEntry
            {
                Name = finalName,
                ExecutablePath = candidate.ExePath,
                WorkingDirectory = candidate.WorkingDirectory,
                Category = SelectedCategory != "All" ? SelectedCategory : "Uncategorized",
                SteamAppId = matchedAppId
            };

            entry.IconPath = _iconExtractorService.ExtractAndCacheIcon(entry.Id, entry.ExecutablePath, entry.Name);
            await EnrichGameWithSteamMetadataAsync(entry);
            Games.Add(CreateCardViewModel(entry));
            RebuildCategories();
            SaveLibrary();
            UpdateHotkeys();
            ApplySort();
            StatusMessage = $"Added \"{entry.Name}\" to library!";
            OnPropertyChanged(nameof(TotalGameCount));
            OnPropertyChanged(nameof(TotalGameCountDisplay));
            LibraryUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainViewModel", $"Error adding candidate '{candidate?.Name}'", ex);
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    private GameCardViewModel? FindDuplicateGame(string? executablePath, string? steamAppId)
    {
        return Games.FirstOrDefault(g =>
            (!string.IsNullOrWhiteSpace(steamAppId) && string.Equals(g.Game.SteamAppId, steamAppId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(executablePath) && string.Equals(g.Game.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)));
    }

    private void AddGameBrowse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add Game - Select Executable or Shortcut",
            Filter = "Games & Shortcuts (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            HandleFileDrop(dialog.FileNames);
        }
    }

    private void AddGameFolderBrowse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add Game - Select Game Folder",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ProcessFolderAdd(dialog.FolderName);
        }
    }

    private void OpenSteamImport()
    {
        if (!_settings.SteamIntegrationEnabled)
        {
            Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
            var res = ModernDialog.Confirm(
                owner,
                "Steam Integration Disabled",
                "Steam library integration is currently disabled in Settings.",
                "Would you like to enable it now and scan your Steam library?",
                confirmText: "Enable & Scan",
                cancelText: "Cancel");

            if (res)
            {
                _settings.SteamIntegrationEnabled = true;
                _storageService.SaveSettings(_settings);
                RequestOpenSteamDialog?.Invoke();
            }
            return;
        }

        RequestOpenSteamDialog?.Invoke();
    }

    public void ImportSteamGames(List<DiscoveredSteamGame> discoveredGames) => _ = ImportSteamGamesAsync(discoveredGames);

    public async Task ImportSteamGamesAsync(List<DiscoveredSteamGame> discoveredGames)
    {
        if (discoveredGames == null || discoveredGames.Count == 0) return;

        if (_isImportInProgress)
        {
            StatusMessage = "An import is already in progress. Please wait for it to finish.";
            return;
        }

        _isImportInProgress = true;
        try
        {
            var toProcess = discoveredGames
                .Where(d => !Games.Any(g => string.Equals(g.Game.SteamAppId, d.AppId, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int skippedDuplicates = discoveredGames.Count - toProcess.Count;

            if (toProcess.Count == 0)
            {
                StatusMessage = "All selected Steam games are already in your library.";
                return;
            }

            StatusMessage = $"Importing {toProcess.Count} Steam game(s)...";

            using var throttle = new SemaphoreSlim(MaxConcurrentEnrichments);
            var preparedEntries = new System.Collections.Concurrent.ConcurrentBag<GameEntry>();

            var tasks = toProcess.Select(async d =>
            {
                await throttle.WaitAsync();
                try
                {
                    var entry = new GameEntry
                    {
                        Name = d.Name,
                        ExecutablePath = $"steam://rungameid/{d.AppId}",
                        IsSteamGame = true,
                        SteamAppId = d.AppId,
                        Category = "Steam",
                        WorkingDirectory = d.InstallDir
                    };

                    entry.IconPath = _iconExtractorService.ExtractAndCacheIcon(
                        entry.Id,
                        !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty),
                        entry.Name
                    );

                    await EnrichGameWithSteamMetadataAsync(entry);
                    preparedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("MainViewModel", $"Failed to import Steam game '{d.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            });

            await Task.WhenAll(tasks);

            if (!preparedEntries.IsEmpty)
            {
                foreach (var entry in preparedEntries)
                {
                    Games.Add(CreateCardViewModel(entry));
                }

                RebuildCategories();
                SaveLibrary();
                UpdateHotkeys();
                ApplySort();
                StatusMessage = skippedDuplicates > 0
                    ? $"Imported {preparedEntries.Count} Steam game(s)! ({skippedDuplicates} already in library, skipped)"
                    : $"Imported {preparedEntries.Count} Steam game(s)!";
                OnPropertyChanged(nameof(TotalGameCount));
                OnPropertyChanged(nameof(TotalGameCountDisplay));
                LibraryUpdated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("MainViewModel", "Error in ImportSteamGames", ex);
        }
        finally
        {
            _isImportInProgress = false;
        }
    }

    private void PromptExitApplication()
    {
        Window? owner = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        var choice = ModernDialog.PromptExitAction(owner);

        if (choice == DialogResultOption.Primary)
        {
            RequestExitApplication?.Invoke();
        }
        else if (choice == DialogResultOption.Secondary)
        {
            RequestMinimizeToTray?.Invoke();
        }
    }

    private void OpenSettings()
    {
        CurrentSection = NavSection.Settings;
    }

    public void SaveLibrary()
    {
        _storageService.SaveGames(Games.Select(g => g.Game));
        _storageService.SaveSettings(_settings);
        LibraryUpdated?.Invoke();
    }

    public void UpdateHotkeys()
    {
        _hotkeyManager.RegisterHotkeys(_settings.GlobalManageHotkey, Games.Select(g => g.Game));
    }

    private void OnGameUpdatedFromLauncher(GameEntry game)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var card = Games.FirstOrDefault(g => g.Id == game.Id);
            card?.RefreshProperties();
            SaveLibrary();
            ApplySort();
        });
    }

    private void OnGameHotkeyTriggered(string gameId)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var card = Games.FirstOrDefault(g => g.Id == gameId);
            if (card != null)
            {
                LaunchGame(card);
            }
        });
    }

    public void RebuildCategories()
    {
        string previous = SelectedCategory;
        Categories.Clear();
        Categories.Add("All");
        Categories.Add("Favorites");

        var distinctCategories = Games
            .Select(g => g.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c) && c != "All" && c != "Favorites")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c);

        foreach (var cat in distinctCategories)
        {
            Categories.Add(cat);
        }

        if (Categories.Contains(previous))
        {
            _selectedCategory = previous;
        }
        else
        {
            _selectedCategory = "All";
        }
        OnPropertyChanged(nameof(SelectedCategory));

        RebuildCategoryTabs();
    }

    public void RebuildCategoryTabs()
    {
        CategoryTabs.Clear();
        foreach (var cat in Categories)
        {
            string display = cat == "All" ? "All Games" : cat == "Favorites" ? "Favorites" : cat;
            bool isSelected = string.Equals(cat, SelectedCategory, StringComparison.OrdinalIgnoreCase);
            CategoryTabs.Add(new CategoryTabItem(cat, display, isSelected, SelectCategoryTab));
        }
    }

    public void SelectCategoryTab(string category)
    {
        SelectedCategory = category;
    }

    private void ApplySort()
    {
        FilteredGames.SortDescriptions.Clear();
        switch (SelectedSortOption)
        {
            case "Alphabetical (Z - A)":
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
                break;
            case "Favorites First (A - Z)":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.IsFavorite", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
            case "Most Recently Played":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.LastPlayed", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
            case "Cumulative Playtime":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.CumulativePlaytimeMinutes", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
            case "Alphabetical (A - Z)":
            default:
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                break;
        }
        FilteredGames.Refresh();
    }

    private bool FilterGameItem(object obj)
    {
        if (obj is not GameCardViewModel card) return false;

        // Category filter
        if (SelectedCategory == "Favorites")
        {
            if (!card.Game.IsFavorite) return false;
        }
        else if (SelectedCategory != "All" &&
            !card.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Search text filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            return card.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   card.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   card.Game.ExecutablePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}
