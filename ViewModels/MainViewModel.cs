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
        IsFavoritesTab = string.Equals(name, LibraryConstants.FavoritesCategory, StringComparison.OrdinalIgnoreCase);
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
    public UpdateCoordinator Update { get; }
    public LibraryViewModel Library { get; }

    // Navigation state
    private NavSection _currentSection = NavSection.Library;
    private bool _isSidebarExpanded = false;

    private AppSettings _settings;
    private readonly SteamSearchService _steamSearchService = new();
    private readonly SteamMetadataService _steamMetadataService = new();

    // Reentrancy guards: these async operations all add to / enrich the shared Games
    // collection, so overlapping invocations (e.g. rapid double-clicks, a settings toggle
    // re-triggering enrichment mid-import) could interleave duplicate-check and add logic.
    private bool _isImportInProgress;
    private bool _isEnrichmentInProgress;

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

        SettingsVM = new SettingsViewModel(
            _settings,
            _storageService,
            _startupManager,
            _trayPromotionService,
            onTrayMenuSettingChanged: () => Library.NotifyLibraryUpdated(),
            onPosterArtSettingChanged: () => Library.NotifyAllCardsPosterArtChanged(),
            onHotkeySettingChanged: UpdateHotkeys,
            onRequestEnrichLibrary: EnrichLibraryAsync,
            onRequestRefreshAllPosters: progress => RefreshAllPostersAsync(progress),
            onRequestOpenSteamImport: OpenSteamImport,
            onCheckForUpdates: () => Update.CheckForUpdatesAsync(true),
            getUpdateStatusText: () => Update.UpdateStatusBadgeText
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

        Library.RequestEditGameDialog += card => RequestEditGameDialog?.Invoke(card);
        Library.RequestQuickRename += card => RequestQuickRename?.Invoke(card);
        Library.RequestQuickCategory += card => RequestQuickCategory?.Invoke(card);
        Library.RequestEditSteamAppId += card => RequestEditSteamAppId?.Invoke(card);
        Library.RequestMinimizeToTray += () => RequestMinimizeToTray?.Invoke();
        Library.LibraryUpdated += () => LibraryUpdated?.Invoke();

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

        // Game Commands
        AddGameCommand = new RelayCommand(AddGameBrowse);
        AddFolderCommand = new RelayCommand(AddGameFolderBrowse);
        OpenSteamImportCommand = new RelayCommand(OpenSteamImport);
        RefreshAllPostersCommand = new AsyncRelayCommand(async () => await RefreshAllPostersAsync());
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenTaskbarSettingsCommand = new RelayCommand(TrayPromotionService.OpenWindowsTaskbarSettings);
        OpenSteamGridDbSiteCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://www.steamgriddb.com/profile/preferences") { UseShellExecute = true }));

        _launcherService.GameUpdated += Library.OnGameUpdatedFromLauncher;
        _hotkeyManager.GameHotkeyTriggered += Library.OnGameHotkeyTriggered;

        Library.LoadLibrary();
        _ = EnrichLibraryAsync();

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

    public ICommand AddGameCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand OpenSteamImportCommand { get; }
    public ICommand RefreshAllPostersCommand { get; }
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


    private Task EnrichGameWithSteamMetadataAsync(GameEntry entry) => Library.EnrichGameWithSteamMetadataAsync(entry);

    // Shared tail of every import pipeline (file drop, folder scan, batch import, Steam import):
    // cache the icon, then enrich from Steam metadata. See L-12.
    private async Task FinalizeNewEntryAsync(GameEntry entry, string iconSourcePath)
    {
        entry.IconPath = _iconExtractorService.ExtractAndCacheIcon(entry.Id, iconSourcePath, entry.Name);
        await EnrichGameWithSteamMetadataAsync(entry);
    }

    // Shared commit step of every import pipeline: add the prepared entries to the visible
    // library and refresh everything that depends on it. See L-12.
    private void CommitImportedEntries(IEnumerable<GameEntry> entries, string statusMessage)
    {
        foreach (var entry in entries)
        {
            Games.Add(CreateCardViewModel(entry));
        }

        RebuildCategories();
        SaveLibrary();
        UpdateHotkeys();
        ApplySort();
        StatusMessage = statusMessage;
        OnPropertyChanged(nameof(TotalGameCount));
        OnPropertyChanged(nameof(TotalGameCountDisplay));
    }

    private const int MaxConcurrentEnrichments = 4;
    private static readonly TimeSpan EnrichmentRetryInterval = TimeSpan.FromDays(7);

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
                    var details = await _steamMetadataService.GetAppDetailsAsync(appId, SettingsVM.SteamGridDbApiKeyOrNull, forceRefresh: true);
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
                .Where(card =>
                    (card.Game.Category == LibraryConstants.Uncategorized || card.Game.Category == LibraryConstants.SteamCategory || string.IsNullOrWhiteSpace(card.Game.CoverImagePath) || string.IsNullOrWhiteSpace(card.Game.SteamAppId)) &&
                    (card.Game.LastEnrichmentAttemptUtc == null || DateTime.UtcNow - card.Game.LastEnrichmentAttemptUtc.Value >= EnrichmentRetryInterval))
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
                    // Record the attempt regardless of outcome so a game that legitimately never
                    // matches (indie, emulator, tool) isn't re-searched online every single launch -
                    // and so this needs saving even when nothing else about the game changed.
                    card.Game.LastEnrichmentAttemptUtc = DateTime.UtcNow;
                    changed = true;

                    if (card.Game.Category != oldCat || card.Game.CoverImagePath != oldCover || card.Game.SteamAppId != oldAppId)
                    {
                        card.RefreshProperties();
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
            }
        }
        finally
        {
            _isEnrichmentInProgress = false;
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
            var addedEntries = new List<GameEntry>();
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
                        Window? dupOwner = WindowHelper.ActiveOwner();
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
                        Category = SelectedCategory != LibraryConstants.AllCategory ? SelectedCategory : LibraryConstants.Uncategorized,
                        IsSteamGame = shortcut.IsSteamUrl,
                        SteamAppId = onlineAppId
                    };

                    // Extract & cache icon
                    string iconSource = !string.IsNullOrEmpty(shortcut.IconLocation) ? shortcut.IconLocation : shortcut.TargetPath;
                    await FinalizeNewEntryAsync(entry, iconSource);
                    addedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("MainViewModel", $"Failed to ingest dropped file '{file}': {ex.Message}");
                }
            }

            if (addedEntries.Count > 0)
            {
                CommitImportedEntries(addedEntries, $"Added {addedEntries.Count} new game(s) instantly!");
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
            await ProcessFolderAddAsync(folders[0]);
        }
        else if (folders.Count > 1)
        {
            await ProcessFolderAddBatchAsync(folders);
        }
    }

    // Scans every dropped folder up front and merges the results into a single batch-import
    // prompt, instead of prompting once per folder (see ProcessFolderAdd, which still owns the
    // single-folder path so its "no games" / "one game" / "overwhelming match" shortcuts are
    // unaffected).
    public async Task ProcessFolderAddBatchAsync(List<string> folderPaths)
    {
        StatusMessage = "Scanning folders...";
        var aggregated = new List<GameCandidate>();

        foreach (var folderPath in folderPaths)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) continue;

            var scanResult = await Task.Run(() => _folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName));

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
            Window? owner = WindowHelper.ActiveOwner();
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

    public void ProcessFolderAdd(string folderPath) => _ = ProcessFolderAddAsync(folderPath);

    public async Task ProcessFolderAddAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        StatusMessage = "Scanning folder...";
        var scanResult = await Task.Run(() => _folderScannerService.ScanFolderOrLibrary(folderPath, _settings.PreferExeForGameName));

        // A. Multi-game parent folder detected (e.g. C:\Games, D:\SteamLibrary\steamapps\common, C:\GOG Games)
        if (scanResult.IsMultiGameLibrary)
        {
            if (scanResult.DiscoveredGames.Count == 0)
            {
                Window? owner = WindowHelper.ActiveOwner();
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
            Window? owner = WindowHelper.ActiveOwner();
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
                        Category = SelectedCategory != LibraryConstants.AllCategory ? SelectedCategory : LibraryConstants.Uncategorized,
                        SteamAppId = matchedAppId
                    };

                    await FinalizeNewEntryAsync(entry, entry.ExecutablePath);
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
                string status = skippedDuplicates > 0
                    ? $"Added {preparedEntries.Count} games from folder! ({skippedDuplicates} already in library, skipped)"
                    : $"Added {preparedEntries.Count} games from folder!";
                CommitImportedEntries(preparedEntries, status);
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
                Window? dupOwner = WindowHelper.ActiveOwner();
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
                Category = SelectedCategory != LibraryConstants.AllCategory ? SelectedCategory : LibraryConstants.Uncategorized,
                SteamAppId = matchedAppId
            };

            await FinalizeNewEntryAsync(entry, entry.ExecutablePath);
            CommitImportedEntries([entry], $"Added \"{entry.Name}\" to library!");
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

    private GameCardViewModel? FindDuplicateGame(string? executablePath, string? steamAppId) =>
        Library.FindDuplicateGame(executablePath, steamAppId);

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
            Window? owner = WindowHelper.ActiveOwner();
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
                        Category = LibraryConstants.SteamCategory,
                        WorkingDirectory = d.InstallDir
                    };

                    string iconSource = !string.IsNullOrEmpty(d.IconPath) ? d.IconPath : (d.ExePath ?? string.Empty);
                    await FinalizeNewEntryAsync(entry, iconSource);
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
                string status = skippedDuplicates > 0
                    ? $"Imported {preparedEntries.Count} Steam game(s)! ({skippedDuplicates} already in library, skipped)"
                    : $"Imported {preparedEntries.Count} Steam game(s)!";
                CommitImportedEntries(preparedEntries, status);
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
