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
    private readonly ProcessLauncherService _launcherService;
    private readonly HotkeyManager _hotkeyManager;
    private readonly StartupManager _startupManager;
    private readonly TrayPromotionService _trayPromotionService;
    private readonly FolderScannerService _folderScannerService;
    private readonly SystemInfoService _systemInfoService = new();
    private readonly SystemTweaksService _systemTweaksService = new();

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
    public event Action<string, string>? RequestTrayNotification;

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
            getSteamGridDbApiKeyOrNull: () => SettingsVM?.SteamGridDbApiKeyOrNull
        );

        Import = new ImportCoordinator(
            Library,
            _shortcutService,
            _iconExtractorService,
            _folderScannerService,
            _steamSearchService,
            _steamMetadataService,
            _storageService,
            _settings,
            getSteamGridDbApiKeyOrNull: () => SettingsVM?.SteamGridDbApiKeyOrNull
        );

        SettingsVM = new SettingsViewModel(
            _settings,
            _storageService,
            _startupManager,
            _trayPromotionService,
            onTrayMenuSettingChanged: () => Library.NotifyLibraryUpdated(),
            onPosterArtSettingChanged: () => Library.NotifyAllCardsPosterArtChanged(),
            onHotkeySettingChanged: UpdateHotkeys,
            onRequestEnrichLibrary: Import.EnrichLibraryAsync,
            onRequestRefreshAllPosters: progress => Import.RefreshAllPostersAsync(progress),
            onRequestOpenSteamImport: Import.OpenSteamImport,
            onCheckForUpdates: () => Update.CheckForUpdatesAsync(true),
            getUpdateStatusText: () => Update.UpdateStatusBadgeText
        );

        Library.RequestEditGameDialog += card => RequestEditGameDialog?.Invoke(card);
        Library.RequestQuickRename += card => RequestQuickRename?.Invoke(card);
        Library.RequestQuickCategory += card => RequestQuickCategory?.Invoke(card);
        Library.RequestEditSteamAppId += card => RequestEditSteamAppId?.Invoke(card);
        Library.RequestMinimizeToTray += () => RequestMinimizeToTray?.Invoke();
        Library.LibraryUpdated += () => LibraryUpdated?.Invoke();

        Import.RequestOpenSteamDialog += () => RequestOpenSteamDialog?.Invoke();
        Import.RequestCandidatePicker += (path, candidates) => RequestCandidatePicker?.Invoke(path, candidates);
        Import.RequestFolderBatchImport += (path, candidates) => RequestFolderBatchImport?.Invoke(path, candidates);

        // XAML binds these forwarded property names (SearchText, StatusMessage, TotalGameCount,
        // CanRefreshAllPosters, etc.) against MainViewModel directly - relay Library's/Import's
        // own notifications so those bindings still refresh, the same way SettingsVM's view-mode
        // properties are relayed above.
        Library.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);
        Import.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);

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
        ExitApplicationCommand = new RelayCommand(PromptExitApplication);

        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenTaskbarSettingsCommand = new RelayCommand(TrayPromotionService.OpenWindowsTaskbarSettings);
        OpenSteamGridDbSiteCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://www.steamgriddb.com/profile/preferences") { UseShellExecute = true }));

        _launcherService.GameUpdated += Library.OnGameUpdatedFromLauncher;
        _hotkeyManager.GameHotkeyTriggered += Library.OnGameHotkeyTriggered;

        Library.LoadLibrary();
        _ = Import.EnrichLibraryAsync();

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
                OnPropertyChanged(nameof(SidebarToggleTooltip));
                OnPropertyChanged(nameof(SidebarToggleLabel));
                OnPropertyChanged(nameof(SidebarToggleChevron));
                SettingsVM.AutoSaveSettings();
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

    public string StatusMessage
    {
        get => Library.StatusMessage;
        set => Library.StatusMessage = value;
    }

    public int TotalGameCount => Library.TotalGameCount;
    public string TotalGameCountDisplay => Library.TotalGameCountDisplay;

    public ICommand AddGameCommand => Import.AddGameCommand;
    public ICommand AddFolderCommand => Import.AddFolderCommand;
    public ICommand OpenSteamImportCommand => Import.OpenSteamImportCommand;
    public ICommand RefreshAllPostersCommand => Import.RefreshAllPostersCommand;
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenTaskbarSettingsCommand { get; }
    public ICommand OpenSteamGridDbSiteCommand { get; }
    public ICommand RefreshCategoriesCommand => Library.RefreshCategoriesCommand;
    public ICommand UndoDeleteCommand => Library.UndoDeleteCommand;
    public ICommand DismissUndoToastCommand => Library.DismissUndoToastCommand;

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
    public void SaveLibrary() => Library.SaveLibrary();
    public void UpdateHotkeys() => Library.UpdateHotkeys();
    public void RebuildCategories() => Library.RebuildCategories();
    public void RebuildCategoryTabs() => Library.RebuildCategoryTabs();
    public void SelectCategoryTab(string category) => Library.SelectCategoryTab(category);
    private void ApplySort() => Library.ApplySort();
}
