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

public class MainViewModel : ViewModelBase
{
    private readonly StorageService _storageService;
    private readonly ShortcutService _shortcutService;
    private readonly IconExtractorService _iconExtractorService;
    private readonly SteamScannerService _steamScannerService;
    private readonly GogScannerService _gogScannerService;
    private readonly EaScannerService _eaScannerService;
    private readonly EpicScannerService _epicScannerService;
    private readonly UbisoftScannerService _ubisoftScannerService;
    private readonly XboxScannerService _xboxScannerService;
    private readonly ProcessLauncherService _launcherService;
    private readonly HotkeyManager _hotkeyManager;
    private readonly StartupManager _startupManager;
    private readonly TrayPromotionService _trayPromotionService;
    private readonly FolderScannerService _folderScannerService;
    private readonly SystemInfoService _systemInfoService = new();
    private readonly SystemTweaksService _systemTweaksService;

    public SystemViewModel SystemVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public UpdateCoordinator Update { get; }
    public LibraryViewModel Library { get; }
    public ImportCoordinator Import { get; }

    // Navigation state
    private NavSection _currentSection = NavSection.Library;
    private bool _isSidebarExpanded = false;

    private AppSettings _settings;
    private readonly SteamSearchService _steamSearchService = new();
    private readonly SteamMetadataService _steamMetadataService = new();

    // --- Forwarded to LibraryViewModel; see L-13 ---
    public ObservableCollection<GameCardViewModel> Games => Library.Games;
    public ObservableCollection<string> Categories => Library.Categories;
    public ObservableCollection<CategoryTabItem> CategoryTabs => Library.CategoryTabs;
    public ObservableCollection<string> SortOptions => Library.SortOptions;
    public ICollectionView FilteredGames => Library.FilteredGames;

    public AppSettings Settings => _settings;
    public IconExtractorService IconExtractorService => _iconExtractorService;
    public StorageService StorageService => _storageService;
    public event Action<List<DiscoveredSteamGame>, List<DiscoveredGogGame>, List<DiscoveredEaGame>, List<DiscoveredEpicGame>, List<DiscoveredUbisoftGame>, List<DiscoveredXboxGame>, List<GameCandidate>>? RequestScanResultsPicker;
    /// <summary>
    /// Forwarded from <see cref="ImportCoordinator.RequestLauncherDetectionPrompt"/>: raised the
    /// first time the user ever presses "Scan for Games", if at least one platform's own scanner
    /// found an installed game. See <see cref="Views.LauncherDetectionDialog"/>.
    /// </summary>
    public event Action<List<DetectedLauncherOption>>? RequestLauncherDetectionPrompt;
    public event Action<GameCardViewModel>? RequestEditGameDialog;
    public event Action<string, List<GameCandidate>>? RequestCandidatePicker;
    public event Action<string, List<GameCandidate>>? RequestFolderBatchImport;
    public event Action<GameCardViewModel>? RequestQuickRename;
    public event Action<GameCardViewModel>? RequestQuickCategory;
    public event Action<List<GameCardViewModel>>? RequestBatchCategory;
    public event Action<GameCardViewModel>? RequestEditSteamAppId;
    public event Action? LibraryUpdated;
    public event Action? RequestMinimizeToTray;
    public event Action? RequestExitApplication;
    public event Action<string, string>? RequestTrayNotification;

    /// <summary>Shows a tray balloon (title + message) via the App's tray icon.</summary>
    public void NotifyTray(string title, string message) => RequestTrayNotification?.Invoke(title, message);

    public MainViewModel(
        StorageService storageService,
        AppSettings settings,
        ShortcutService shortcutService,
        IconExtractorService iconExtractorService,
        SteamScannerService steamScannerService,
        GogScannerService gogScannerService,
        EaScannerService eaScannerService,
        EpicScannerService epicScannerService,
        UbisoftScannerService ubisoftScannerService,
        XboxScannerService xboxScannerService,
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
        _gogScannerService = gogScannerService;
        _eaScannerService = eaScannerService;
        _epicScannerService = epicScannerService;
        _ubisoftScannerService = ubisoftScannerService;
        _xboxScannerService = xboxScannerService;
        _launcherService = launcherService;
        _hotkeyManager = hotkeyManager;
        _startupManager = startupManager;
        _trayPromotionService = trayPromotionService;
        _folderScannerService = folderScannerService ?? new FolderScannerService();

        _settings = settings;
        _settings.LibraryViewMode = SettingsViewModel.NormalizeViewMode(_settings.LibraryViewMode);
        _isSidebarExpanded = false; // Left panel is collapsed at startup per user preference

        Update = new UpdateCoordinator(
            _storageService,
            _settings,
            onStatusChanged: () => SettingsVM?.NotifyUpdateStatusChanged(),
            onTrayNotification: (title, message) => RequestTrayNotification?.Invoke(title, message)
        );

        Library = new LibraryViewModel(
            _storageService,
            _iconExtractorService,
            _launcherService,
            _hotkeyManager,
            _steamMetadataService,
            _steamSearchService,
            _settings,
            getUseVerticalPosterArt: () => SettingsVM?.UseVerticalPosterArt ?? false,
            getSteamGridDbApiKeyOrNull: () => SettingsVM?.SteamGridDbApiKeyOrNull,
            getRawgApiKeyOrNull: () => SettingsVM?.RawgApiKeyOrNull
        );

        Import = new ImportCoordinator(
            Library,
            _shortcutService,
            _iconExtractorService,
            _folderScannerService,
            _steamScannerService,
            _gogScannerService,
            _eaScannerService,
            _epicScannerService,
            _ubisoftScannerService,
            _xboxScannerService,
            _steamSearchService,
            _steamMetadataService,
            _storageService,
            _settings,
            getSteamGridDbApiKeyOrNull: () => SettingsVM?.SteamGridDbApiKeyOrNull
        );

        // Runs before SettingsVM is constructed so its initial Scan Locations list already
        // reflects any newly-detected Steam libraries, rather than needing a manual refresh.
        Import.SyncSteamScanLocationsOnStartup();

        SettingsVM = new SettingsViewModel(
            _settings,
            _storageService,
            _startupManager,
            _trayPromotionService,
            _steamScannerService,
            onTrayMenuSettingChanged: () => Library.NotifyLibraryUpdated(),
            onPosterArtSettingChanged: () => Library.NotifyAllCardsPosterArtChanged(),
            onHotkeySettingChanged: UpdateHotkeys,
            onRequestEnrichLibrary: retryForRawg => Import.EnrichLibraryAsync(retryForRawg),
            onRequestRefreshAllPosters: progress => Import.RefreshAllPostersAsync(progress),
            onRequestOpenScanForGames: () => _ = Import.ScanForGamesAsync(),
            onCheckForUpdates: () => Update.CheckForUpdatesAsync(true),
            getUpdateStatusText: () => Update.UpdateStatusBadgeText,
            getPlatformGameCount: Library.CountPlatformGames,
            removePlatformGames: Library.RemovePlatformGames,
            // SystemVM is constructed a few lines below; the closure resolves it at call time.
            onProfileTweaksReset: () => SystemVM?.RefreshProfileTweakToggles()
        );

        Library.RequestEditGameDialog += card => RequestEditGameDialog?.Invoke(card);
        Library.RequestQuickRename += card => RequestQuickRename?.Invoke(card);
        Library.RequestQuickCategory += card => RequestQuickCategory?.Invoke(card);
        Library.RequestBatchCategory += cards => RequestBatchCategory?.Invoke(cards);
        Library.RequestEditSteamAppId += card => RequestEditSteamAppId?.Invoke(card);
        Library.RequestMinimizeToTray += () => RequestMinimizeToTray?.Invoke();
        Library.LibraryUpdated += () => LibraryUpdated?.Invoke();

        Import.RequestScanResultsPicker += (steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, folderCandidates) => RequestScanResultsPicker?.Invoke(steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, folderCandidates);
        Import.RequestLauncherDetectionPrompt += detected => RequestLauncherDetectionPrompt?.Invoke(detected);
        Import.RequestCandidatePicker += (path, candidates) => RequestCandidatePicker?.Invoke(path, candidates);
        Import.RequestFolderBatchImport += (path, candidates) => RequestFolderBatchImport?.Invoke(path, candidates);

        // XAML binds these forwarded property names (SearchText, StatusMessage, TotalGameCount,
        // CanRefreshAllPosters, etc.) against MainViewModel directly - relay Library's/Import's
        // own notifications so those bindings still refresh, the same way SettingsVM's view-mode
        // properties are relayed above.
        Library.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);
        Import.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);

        // Lets import/scan completion messages fall back to the floating toast whenever the
        // Library status bar isn't the section on screen (see LibraryViewModel.AnnounceImportResult).
        Library.IsLibraryVisible = () => CurrentSection == NavSection.Library;

        // The tweaks service records what it found on the machine before applying a tweak (prior
        // power plan, prior visual-effects state) into settings so "Revert to Default" is exact.
        _systemTweaksService = new SystemTweaksService(() => _settings, () => _storageService.SaveSettings(_settings));
        SystemVM = new SystemViewModel(_systemInfoService, _systemTweaksService, _settings, _storageService);

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
        OpenPerformanceSettingsCommand = new RelayCommand(() =>
        {
            SettingsVM.SelectedTab = SettingsCategoryTab.PerformanceTweaks;
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
        ExitApplicationCommand = new RelayCommand(PromptExitApplication);

        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenTaskbarSettingsCommand = new RelayCommand(TrayPromotionService.OpenWindowsTaskbarSettings);
        OpenSteamGridDbSiteCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://www.steamgriddb.com/profile/preferences") { UseShellExecute = true }));

        _launcherService.GameUpdated += Library.OnGameUpdatedFromLauncher;
        _launcherService.GameWindowReady += Library.OnGameWindowReady;
        _launcherService.SessionStarted += Library.OnSessionStarted;
        _launcherService.SessionEnded += Library.OnSessionEnded;
        _hotkeyManager.GameHotkeyTriggered += Library.OnGameHotkeyTriggered;

        Library.LoadLibrary();
        _ = Import.EnrichLibraryAsync();

        if (_settings.AutoScanForGamesOnStartup)
        {
            _ = Import.ScanForGamesAsync(silent: true);
        }

        if (_storageService.SettingsLoadWarning != null || _storageService.GamesLoadWarning != null)
        {
            string warning = string.Join(" ", new[] { _storageService.SettingsLoadWarning, _storageService.GamesLoadWarning }
                .Where(w => w != null));
            ModernDialog.ShowWarning(null, "Data File Recovered", warning);
        }

        Update.StartBackgroundChecks();
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
                    if (SystemVM.TotalTweakCount == 0)
                    {
                        _ = SystemVM.LoadTweaksAsync();
                    }
                }
                else if (prev == NavSection.System)
                {
                    SystemVM.StopTelemetry();
                }

                if (_currentSection == NavSection.Settings)
                {
                    SettingsVM.ReconcileStartWithWindows();
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
                OnPropertyChanged(nameof(SidebarToggleName));
                OnPropertyChanged(nameof(SidebarToggleChevron));
                SettingsVM.AutoSaveSettings();
            }
        }
    }

    public double SidebarWidth => IsSidebarExpanded ? 190 : 60;
    public string SidebarToggleName => IsSidebarExpanded ? "Collapse Sidebar" : "Expand Sidebar";
    public string SidebarToggleChevron => IsSidebarExpanded ? "\uE76B" : "\uE76C";

    public ICommand ToggleSidebarCommand { get; }
    public ICommand SelectLibraryCommand { get; }
    public ICommand SelectSettingsCommand { get; }
    public ICommand OpenDiagnosticsSettingsCommand { get; }
    /// <summary>System page's RESTORE POINT badge -> Settings > Launch &amp; Performance.</summary>
    public ICommand OpenPerformanceSettingsCommand { get; }
    public ICommand SelectAboutCommand { get; }

    /// <summary>About > Overview environment strip: the real runtime this build is on.</summary>
    public string RuntimeDisplay => $".NET {Environment.Version.Major}.{Environment.Version.Minor} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";

    /// <summary>About > Overview environment strip: "Windows 11 Pro 24H2 (build 26200)". Read
    /// synchronously from the registry so it is right on first paint, unlike the async telemetry.
    /// Windows 11 still reports ProductName "Windows 10 ..." there, hence the build-number fix-up.</summary>
    public string OsDisplay
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string product = key?.GetValue("ProductName") as string ?? "Windows";
                string display = key?.GetValue("DisplayVersion") as string ?? "";
                string build = key?.GetValue("CurrentBuild") as string ?? "";
                if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
                return $"{product} {display} (build {build})".Replace("  ", " ").Trim();
            }
            catch
            {
                return Environment.OSVersion.VersionString;
            }
        }
    }

    public ICommand SelectAboutAllTabCommand { get; }
    public ICommand SelectAboutOverviewTabCommand { get; }
    public ICommand SelectAboutFeaturesTabCommand { get; }
    public ICommand SelectAboutShortcutsTabCommand { get; }
    public ICommand SelectAboutSupportTabCommand { get; }
    public ICommand NavigateToGeneralSettingsCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand OpenGitHubIssuesCommand { get; }
    public ICommand CopySystemInfoCommand { get; }
    public ICommand CheckForUpdatesCommand => Update.CheckForUpdatesCommand;
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

    // --- APPLICATION VERSION & UPDATES (forwarded to UpdateCoordinator; see L-13) ---

    public string AppVersionDisplay => Update.AppVersionDisplay;

    /// <summary>About tab's "Help Topics" card: every embedded help topic, grouped by section.</summary>
    public IReadOnlyList<HelpTopicGroup> HelpTopicIndex { get; } = HelpContentService.GetIndex();

    public string UpdateStatusBadgeText => Update.UpdateStatusBadgeText;

    public string UpdateStatusIcon => Update.UpdateStatusIcon;
    public Brush UpdateStatusBrush => Update.UpdateStatusBrush;
    public Task CheckForUpdatesAsync(bool interactive) => Update.CheckForUpdatesAsync(interactive);

    // --- LIBRARY & SEARCH PROPERTIES (forwarded to LibraryViewModel; see L-13) ---
    public string SearchText
    {
        get => Library.SearchText;
        set => Library.SearchText = value;
    }

    public string SelectedCategory
    {
        get => Library.SelectedCategory;
        set => Library.SelectedCategory = value;
    }

    public string SelectedSortOption
    {
        get => Library.SelectedSortOption;
        set => Library.SelectedSortOption = value;
    }

    public bool IsUndoToastVisible
    {
        get => Library.IsUndoToastVisible;
        set => Library.IsUndoToastVisible = value;
    }

    public string UndoToastMessage
    {
        get => Library.UndoToastMessage;
        set => Library.UndoToastMessage = value;
    }

    public bool IsLaunchToastVisible
    {
        get => Library.IsLaunchToastVisible;
        set => Library.IsLaunchToastVisible = value;
    }

    public string LaunchToastMessage
    {
        get => Library.LaunchToastMessage;
        set => Library.LaunchToastMessage = value;
    }

    public string LaunchToastIcon => Library.LaunchToastIcon;

    public string StatusMessage
    {
        get => Library.StatusMessage;
        set => Library.StatusMessage = value;
    }

    public int TotalGameCount => Library.TotalGameCount;
    public string TotalGameCountDisplay => Library.TotalGameCountDisplay;

    public ICommand AddGameCommand => Import.AddGameCommand;
    public ICommand AddFolderCommand => Import.AddFolderCommand;
    public ICommand OpenScanForGamesCommand => Import.OpenScanForGamesCommand;
    public ICommand RefreshAllPostersCommand => Import.RefreshAllPostersCommand;
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenTaskbarSettingsCommand { get; }
    public ICommand OpenSteamGridDbSiteCommand { get; }
    public ICommand RefreshCategoriesCommand => Library.RefreshCategoriesCommand;
    public ICommand UndoDeleteCommand => Library.UndoDeleteCommand;
    public ICommand DismissUndoToastCommand => Library.DismissUndoToastCommand;
    public ICommand DismissLaunchToastCommand => Library.DismissLaunchToastCommand;

    // Multi-select / batch editing - see LibraryViewModel. Property change notifications are
    // relayed generically from Library.PropertyChanged above.
    public bool HasSelection => Library.HasSelection;
    public int SelectedCount => Library.SelectedCount;
    public string SelectionSummary => Library.SelectionSummary;
    public string SelectionHint => Library.SelectionHint;
    public bool BatchAllFavorite => Library.BatchAllFavorite;
    public bool BatchAllHidden => Library.BatchAllHidden;
    public string BatchFavoriteLabel => Library.BatchFavoriteLabel;
    public string BatchHideLabel => Library.BatchHideLabel;
    public void ClearSelection() => Library.ClearSelection();
    public ICommand SelectAllCommand => Library.SelectAllCommand;
    public ICommand ClearSelectionCommand => Library.ClearSelectionCommand;
    public ICommand BatchFavoriteCommand => Library.BatchFavoriteCommand;
    public ICommand BatchHideCommand => Library.BatchHideCommand;
    public ICommand BatchChangeCategoryCommand => Library.BatchChangeCategoryCommand;
    public ICommand BatchRemoveCommand => Library.BatchRemoveCommand;
    public ICommand BatchSetProfileCommand => Library.BatchSetProfileCommand;
    public ICommand BatchRefreshMetadataCommand => Library.BatchRefreshMetadataCommand;
    public bool BatchProfileIsOff => Library.BatchProfileIsOff;
    public bool BatchProfileIsOptimized => Library.BatchProfileIsOptimized;
    public bool BatchProfileIsAggressive => Library.BatchProfileIsAggressive;

    public void LoadLibrary() => Library.LoadLibrary();
    public GameCardViewModel CreateCardViewModel(GameEntry game, bool deferHeavyInit = false) => Library.CreateCardViewModel(game, deferHeavyInit);
    public void OpenGameDetails(GameCardViewModel card) => Library.OpenGameDetails(card);
    public void LaunchGame(GameCardViewModel card) => Library.LaunchGame(card);
    public void RelocateGame(GameCardViewModel card) => Library.RelocateGame(card);
    public void ChangeGameIcon(GameCardViewModel card) => Library.ChangeGameIcon(card);
    public void ChangeGameCover(GameCardViewModel card) => Library.ChangeGameCover(card);
    public void EditSteamAppId(GameCardViewModel card) => Library.EditSteamAppId(card);
    public Task UpdateGameSteamAppIdAsync(GameCardViewModel card, string? newAppId) => Library.UpdateGameSteamAppIdAsync(card, newAppId);
    public Task RefreshGameMetadataAsync(GameCardViewModel card) => Library.RefreshGameMetadataAsync(card);
    public void FetchExeNameForGame(GameCardViewModel card) => Library.FetchExeNameForGame(card);
    public Task FetchExeNameForGameAsync(GameCardViewModel card) => Library.FetchExeNameForGameAsync(card);
    public void ApplyRename(GameCardViewModel card, string newName) => Library.ApplyRename(card, newName);
    public void ApplyCategory(GameCardViewModel card, string newCategory) => Library.ApplyCategory(card, newCategory);
    public void ApplyCategoryToMany(List<GameCardViewModel> cards, string newCategory) => Library.ApplyCategoryToMany(cards, newCategory);
    public void SelectAllVisible() => Library.SelectAllVisible();
    public void SetCardSelected(GameCardViewModel card, bool selected) => Library.SetCardSelected(card, selected);
    public void DeleteGame(GameCardViewModel card) => Library.DeleteGame(card);
    public void UndoDelete() => Library.UndoDelete();

    // --- Forwarded to ImportCoordinator; see L-13 ---
    public bool IsRefreshingAllPosters => Import.IsRefreshingAllPosters;
    public bool CanRefreshAllPosters => Import.CanRefreshAllPosters;
    public Task RefreshAllPostersAsync(IProgress<string>? progress = null) => Import.RefreshAllPostersAsync(progress);
    public Task EnrichLibraryAsync() => Import.EnrichLibraryAsync();
    public void HandleFileDrop(string[] files) => Import.HandleFileDrop(files);
    public Task HandleFileDropAsync(string[] files) => Import.HandleFileDropAsync(files);
    public Task ProcessFolderAddBatchAsync(List<string> folderPaths) => Import.ProcessFolderAddBatchAsync(folderPaths);
    public void ProcessFolderAdd(string folderPath) => Import.ProcessFolderAdd(folderPath);
    public Task ProcessFolderAddAsync(string folderPath) => Import.ProcessFolderAddAsync(folderPath);
    public void ImportBatchGames(List<GameCandidate> candidates) => Import.ImportBatchGames(candidates);
    public Task ImportBatchGamesAsync(List<GameCandidate> candidates) => Import.ImportBatchGamesAsync(candidates);
    public void AddCandidate(GameCandidate candidate) => Import.AddCandidate(candidate);
    public Task AddCandidateAsync(GameCandidate candidate) => Import.AddCandidateAsync(candidate);
    public void ImportSteamGames(List<DiscoveredSteamGame> discoveredGames) => Import.ImportSteamGames(discoveredGames);
    public Task ImportSteamGamesAsync(List<DiscoveredSteamGame> discoveredGames) => Import.ImportSteamGamesAsync(discoveredGames);
    public void ImportGogGames(List<DiscoveredGogGame> discoveredGames) => Import.ImportGogGames(discoveredGames);
    public Task ImportGogGamesAsync(List<DiscoveredGogGame> discoveredGames) => Import.ImportGogGamesAsync(discoveredGames);
    public void ImportEaGames(List<DiscoveredEaGame> discoveredGames) => Import.ImportEaGames(discoveredGames);
    public Task ImportEaGamesAsync(List<DiscoveredEaGame> discoveredGames) => Import.ImportEaGamesAsync(discoveredGames);
    public void ImportEpicGames(List<DiscoveredEpicGame> discoveredGames) => Import.ImportEpicGames(discoveredGames);
    public Task ImportEpicGamesAsync(List<DiscoveredEpicGame> discoveredGames) => Import.ImportEpicGamesAsync(discoveredGames);
    public void ImportUbisoftGames(List<DiscoveredUbisoftGame> discoveredGames) => Import.ImportUbisoftGames(discoveredGames);
    public Task ImportUbisoftGamesAsync(List<DiscoveredUbisoftGame> discoveredGames) => Import.ImportUbisoftGamesAsync(discoveredGames);
    public void ImportXboxGames(List<DiscoveredXboxGame> discoveredGames) => Import.ImportXboxGames(discoveredGames);
    public Task ImportXboxGamesAsync(List<DiscoveredXboxGame> discoveredGames) => Import.ImportXboxGamesAsync(discoveredGames);
    public Task ImportScanResultsAsync(List<DiscoveredSteamGame> steamGames, List<DiscoveredGogGame> gogGames, List<DiscoveredEaGame> eaGames, List<DiscoveredEpicGame> epicGames, List<DiscoveredUbisoftGame> ubisoftGames, List<DiscoveredXboxGame> xboxGames, List<GameCandidate> folderCandidates) => Import.ImportScanResultsAsync(steamGames, gogGames, eaGames, epicGames, ubisoftGames, xboxGames, folderCandidates);
    public bool IsScanLocation(string path) => Import.IsScanLocation(path);

    public void IgnoreGamePath(string exePath, string name)
    {
        Import.IgnoreGamePath(exePath, name);
        // Import mutates the same AppSettings.IgnoredGamePaths list SettingsVM displays in its
        // own management list - see the matching comment on AddManualScanLocationIfNew above.
        SettingsVM.RefreshIgnoredGamePaths();
    }

    /// <summary>Ignores a folder-scan candidate by platform ID when it resolved to a launcher
    /// game, by exe path otherwise - see ImportCoordinator.IgnoreCandidate.</summary>
    public void IgnoreCandidate(GameCandidate candidate)
    {
        Import.IgnoreCandidate(candidate);
        SettingsVM.RefreshIgnoredGamePaths();
    }

    public void IgnoreSteamGame(string appId, string name)
    {
        Import.IgnoreSteamGame(appId, name);
        SettingsVM.RefreshIgnoredGamePaths();
    }

    public void IgnoreGogGame(string gameId, string name)
    {
        Import.IgnoreGogGame(gameId, name);
        SettingsVM.RefreshIgnoredGamePaths();
    }

    public void IgnoreEaGame(string contentId, string name)
    {
        Import.IgnoreEaGame(contentId, name);
        SettingsVM.RefreshIgnoredGamePaths();
    }

    public void IgnoreEpicGame(string appName, string name)
    {
        Import.IgnoreEpicGame(appName, name);
        SettingsVM.RefreshIgnoredGamePaths();
    }

    public void IgnoreUbisoftGame(string gameId, string name)
    {
        Import.IgnoreUbisoftGame(gameId, name);
        SettingsVM.RefreshIgnoredGamePaths();
    }

    public void IgnoreXboxGame(string aumid, string name)
    {
        Import.IgnoreXboxGame(aumid, name);
        SettingsVM.RefreshIgnoredGamePaths();
    }

    public void AddManualScanLocationIfNew(string path)
    {
        Import.AddManualScanLocationIfNew(path);
        // Import mutates the same AppSettings.ScanLocations list SettingsVM displays, but
        // SettingsVM's own ObservableCollection only refreshes itself - it has no way to know
        // about a change made from outside it, so it has to be told explicitly here.
        SettingsVM.RefreshScanLocations();
    }

    private void PromptExitApplication()
    {
        Window? owner = WindowHelper.ActiveOwner();
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

    // --- Forwarded to LibraryViewModel; see L-13 ---
    public void SaveLibrary([CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "") => Library.SaveLibrary(callerMember, callerFile);
    public void UpdateHotkeys() => Library.UpdateHotkeys();
    public void RebuildCategories() => Library.RebuildCategories();
    public void RebuildCategoryTabs() => Library.RebuildCategoryTabs();
    public void SelectCategoryTab(string category) => Library.SelectCategoryTab(category);
    private void ApplySort() => Library.ApplySort();
}
