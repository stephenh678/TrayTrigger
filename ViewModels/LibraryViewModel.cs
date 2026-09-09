using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.ViewModels;

public class CategoryTabItem : ViewModelBase
{
    public string Name { get; }
    public string DisplayName { get; }
    public bool IsFavoritesTab { get; }
    public bool IsHiddenTab { get; }

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
        IsHiddenTab = string.Equals(name, LibraryConstants.HiddenCategory, StringComparison.OrdinalIgnoreCase);
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(() => onSelect(Name));
    }
}

/// <summary>
/// Owns the game library: CRUD, filtering, sorting, selection, and the undo-delete toast. Split
/// out of MainViewModel per L-13. <see cref="ImportCoordinator"/> depends on this class (to add
/// entries, check duplicates, and trigger the shared post-mutation refresh), not the other way
/// around.
/// </summary>
public class LibraryViewModel : ViewModelBase
{
    private readonly StorageService _storageService;
    private readonly IconExtractorService _iconExtractorService;
    private readonly ProcessLauncherService _launcherService;
    private readonly HotkeyManager _hotkeyManager;
    private readonly SteamMetadataService _steamMetadataService;
    private readonly SteamSearchService _steamSearchService;
    private readonly AppSettings _settings;
    private readonly Func<bool> _getUseVerticalPosterArt;
    private readonly Func<string?> _getSteamGridDbApiKeyOrNull;

    private string _searchText = string.Empty;
    private string _selectedCategory = LibraryConstants.AllCategory;
    private string _selectedSortOption = "Alphabetical (A - Z)";
    private string _statusMessage = string.Empty;

    // Undo toast state
    private bool _isUndoToastVisible = false;
    private string _undoToastMessage = string.Empty;
    private GameEntry? _lastRemovedGame;
    private int _lastRemovedIndex = -1;
    private DispatcherTimer? _undoToastTimer;

    // Launch toast state
    private bool _isLaunchToastVisible = false;
    private string _launchToastMessage = string.Empty;
    private DispatcherTimer? _launchToastTimer;

    // Tracks which just-launched game we're waiting to minimize for (see DispatchLaunch/OnGameWindowReady)
    private string? _pendingMinimizeGameId;
    private DispatcherTimer? _minimizeFallbackTimer;

    public ObservableCollection<GameCardViewModel> Games { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ObservableCollection<CategoryTabItem> CategoryTabs { get; } = new();
    public ObservableCollection<string> SortOptions { get; } = new()
    {
        "Alphabetical (A - Z)",
        "Alphabetical (Z - A)",
        "Favorites First (A - Z)",
        "Favorites First (Z - A)",
        "Most Recently Played",
        "Cumulative Playtime"
    };
    public ICollectionView FilteredGames { get; }

    public event Action<GameCardViewModel>? RequestEditGameDialog;
    public event Action<GameCardViewModel>? RequestQuickRename;
    public event Action<GameCardViewModel>? RequestQuickCategory;
    public event Action<GameCardViewModel>? RequestEditSteamAppId;
    public event Action? RequestMinimizeToTray;
    public event Action? LibraryUpdated;

    public LibraryViewModel(
        StorageService storageService,
        IconExtractorService iconExtractorService,
        ProcessLauncherService launcherService,
        HotkeyManager hotkeyManager,
        SteamMetadataService steamMetadataService,
        SteamSearchService steamSearchService,
        AppSettings settings,
        Func<bool> getUseVerticalPosterArt,
        Func<string?> getSteamGridDbApiKeyOrNull)
    {
        _storageService = storageService;
        _iconExtractorService = iconExtractorService;
        _launcherService = launcherService;
        _hotkeyManager = hotkeyManager;
        _steamMetadataService = steamMetadataService;
        _steamSearchService = steamSearchService;
        _settings = settings;
        _getUseVerticalPosterArt = getUseVerticalPosterArt;
        _getSteamGridDbApiKeyOrNull = getSteamGridDbApiKeyOrNull;

        if (!string.IsNullOrWhiteSpace(_settings.LastSortOption) && SortOptions.Contains(_settings.LastSortOption))
        {
            _selectedSortOption = _settings.LastSortOption;
        }
        if (!string.IsNullOrWhiteSpace(_settings.LastCategoryFilter))
        {
            _selectedCategory = _settings.LastCategoryFilter;
        }

        FilteredGames = CollectionViewSource.GetDefaultView(Games);
        FilteredGames.Filter = FilterGameItem;
        ApplySort();

        RefreshCategoriesCommand = new RelayCommand(RebuildCategories);
        UndoDeleteCommand = new RelayCommand(UndoDelete);
        DismissUndoToastCommand = new RelayCommand(() => IsUndoToastVisible = false);
        DismissLaunchToastCommand = new RelayCommand(() => IsLaunchToastVisible = false);
    }

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
                _storageService.SaveSettings(_settings);
                LoggingService.Verbose("Settings", "Settings auto-saved.");
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

    public bool IsLaunchToastVisible
    {
        get => _isLaunchToastVisible;
        set { _isLaunchToastVisible = value; OnPropertyChanged(); }
    }

    public string LaunchToastMessage
    {
        get => _launchToastMessage;
        set { _launchToastMessage = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public int TotalGameCount => Games.Count;
    public string TotalGameCountDisplay => $"{Games.Count} game(s)";

    public ICommand RefreshCategoriesCommand { get; }
    public ICommand UndoDeleteCommand { get; }
    public ICommand DismissUndoToastCommand { get; }
    public ICommand DismissLaunchToastCommand { get; }

    /// <summary>
    /// Called by the composition root when a settings toggle affects the tray context menu
    /// (which reads library state from outside this class). Events can only be raised from their
    /// declaring class, hence this thin wrapper.
    /// </summary>
    public void NotifyLibraryUpdated() => LibraryUpdated?.Invoke();

    public void NotifyAllCardsPosterArtChanged()
    {
        foreach (var card in Games)
        {
            card.NotifyPosterArtChanged();
        }
    }

    /// <summary>
    /// Raises change notifications for TotalGameCount/TotalGameCountDisplay. Public so
    /// ImportCoordinator can call it after adding entries directly to <see cref="Games"/>.
    /// </summary>
    public void NotifyGameCountChanged()
    {
        OnPropertyChanged(nameof(TotalGameCount));
        OnPropertyChanged(nameof(TotalGameCountDisplay));
    }

    public void LoadLibrary()
    {
        Games.Clear();
        var rawGames = _storageService.LoadGames();

        foreach (var g in rawGames)
        {
            Games.Add(CreateCardViewModel(g, deferHeavyInit: true));
        }

        RebuildCategories();
        UpdateHotkeys();
        ApplySort();
        StatusMessage = "Ready";
        OnPropertyChanged(nameof(TotalGameCount));
        OnPropertyChanged(nameof(TotalGameCountDisplay));
        NotifyLibraryUpdated();

        LoadCardHeavyStateInBackground();
    }

    /// <summary>
    /// Checks "missing" status and decodes icon/cover for every card off the UI thread, then
    /// applies the results in one dispatcher hop. Startup used to do this per-card, synchronously,
    /// inside the constructor loop above, so the window stayed hidden until every File.Exists and
    /// bitmap decode in the whole library finished.
    /// </summary>
    private void LoadCardHeavyStateInBackground()
    {
        var cardsSnapshot = Games.ToList();
        _ = Task.Run(() =>
        {
            var results = new System.Collections.Generic.List<(GameCardViewModel Card, bool IsMissing, System.Windows.Media.Imaging.BitmapImage? Icon, DateTime? IconWriteTimeUtc, System.Windows.Media.Imaging.BitmapImage? Cover, DateTime? CoverWriteTimeUtc)>();
            foreach (var card in cardsSnapshot)
            {
                var (isMissing, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc) = card.ComputeHeavyState();
                results.Add((card, isMissing, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc));
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (card, isMissing, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc) in results)
                {
                    card.ApplyHeavyState(isMissing, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc);
                }
            });
        });
    }

    public GameCardViewModel CreateCardViewModel(GameEntry game, bool deferHeavyInit = false)
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
            onToggleHidden: ToggleHidden,
            getUseVerticalPosterArt: () => _getUseVerticalPosterArt(),
            deferHeavyInit: deferHeavyInit
        );
    }

    private void ToggleFavorite(GameCardViewModel card)
    {
        LoggingService.Info("Library", $"'{card.Name}' favorite: {(card.Game.IsFavorite ? "on" : "off")}.");
        SaveLibrary();
        FilteredGames.Refresh();
    }

    private void ToggleHidden(GameCardViewModel card)
    {
        LoggingService.Info("Library", $"'{card.Name}' hidden: {(card.Game.IsHidden ? "on" : "off")}.");
        SaveLibrary();
        // The "Hidden" filter tab only appears once at least one game is hidden (see
        // RebuildCategories), so toggling the very first/last hidden game needs the tab list
        // rebuilt. RebuildCategories doesn't always refresh FilteredGames itself (it skips it
        // when the selected category is still valid), so that's done explicitly too - the card
        // needs to disappear from view the instant it's hidden.
        RebuildCategories();
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
            steamGridDbApiKey: _getSteamGridDbApiKeyOrNull(),
            minConfidence: _settings.OnlineMatchConfidenceThreshold);

        var dlg = new Views.GameDetailsDialog(vm);
        dlg.Owner = WindowHelper.ActiveOwner();
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
    }

    public void LaunchGame(GameCardViewModel card)
    {
        Window? owner = WindowHelper.ActiveOwner();
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

        // Shown before dispatching, then held for a moment via a non-blocking timer before the
        // actual launch happens - a fast-launching game (e.g. Steam) can take focus/fullscreen
        // within a few hundred ms of the process spawning, which would cover this window before
        // the toast is ever noticed if it were shown at the same instant as the launch call.
        ShowLaunchToast($"Launching \"{card.Name}\"...");

        var launchDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        launchDelayTimer.Tick += (s, e) =>
        {
            launchDelayTimer.Stop();
            DispatchLaunch(card, owner);
        };
        launchDelayTimer.Start();
    }

    private void DispatchLaunch(GameCardViewModel card, Window? owner)
    {
        if (_launcherService.LaunchGame(card.Game, out string? err, out bool isMissing))
        {
            // No RefreshProperties()/SaveLibrary() here: LaunchGame already raised GameUpdated
            // synchronously (LastPlayed change), which OnGameUpdatedFromLauncher just handled -
            // doing it again here was a redundant second full games.json + settings.json rewrite
            // on every launch.
            StatusMessage = $"Launched {card.Name}";

            if (_settings.MinimizeOnGameLaunch)
            {
                // Don't minimize on a fixed guess - the actual game window (as opposed to its
                // process existing) can take many seconds to appear, and hiding TrayTrigger
                // before then just hands focus to whatever other window was next in line instead
                // of the game. Wait for ProcessLauncherService's confirmation that the game's
                // window has been found and given focus (see OnGameWindowReady below); the
                // fallback timer covers launch paths with no process to track at all (e.g. a bare
                // Steam dispatch) so TrayTrigger still eventually hides either way.
                _pendingMinimizeGameId = card.Game.Id;
                _minimizeFallbackTimer?.Stop();
                _minimizeFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                _minimizeFallbackTimer.Tick += (s, e) =>
                {
                    _minimizeFallbackTimer?.Stop();
                    _pendingMinimizeGameId = null;
                    RequestMinimizeToTray?.Invoke();
                };
                _minimizeFallbackTimer.Start();
            }
        }
        else if (isMissing)
        {
            IsLaunchToastVisible = false;
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
            IsLaunchToastVisible = false;
            StatusMessage = $"Error: {err}";
            ModernDialog.ShowWarning(owner, "Launch Error", err ?? "Failed to launch game.");
        }
    }

    /// <summary>Shows the floating launch toast for a few seconds - same non-blocking overlay
    /// pattern as the undo-delete toast, but auto-dismissing since there's no action to take.</summary>
    private void ShowLaunchToast(string message)
    {
        _launchToastTimer?.Stop();

        LaunchToastMessage = message;
        IsLaunchToastVisible = true;

        _launchToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _launchToastTimer.Tick += (s, e) =>
        {
            _launchToastTimer.Stop();
            IsLaunchToastVisible = false;
        };
        _launchToastTimer.Start();
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
            LoggingService.Info("Library", $"'{card.Name}' relocated to '{dialog.FileName}'.");
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
                LoggingService.Info("Library", $"'{card.Name}' icon updated from '{dialog.FileName}'.");
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
                LoggingService.Info("Library", $"'{card.Name}' poster artwork updated from '{dialog.FileName}'.");
                StatusMessage = $"Updated poster artwork for \"{card.Name}\"";
            }
            catch (Exception ex)
            {
                LoggingService.Error("Library", $"Failed to set poster artwork for '{card.Name}' from '{dialog.FileName}'", ex);
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
            LoggingService.Info("Library", $"'{card.Name}' Steam App ID cleared.");
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
        var details = await _steamMetadataService.GetAppDetailsAsync(trimmed, _getSteamGridDbApiKeyOrNull(), forceRefresh: true);
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
            if ((string.IsNullOrWhiteSpace(card.Game.Category) || card.Game.Category.Equals(LibraryConstants.Uncategorized, StringComparison.OrdinalIgnoreCase)) &&
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
        LoggingService.Info("Library", $"'{card.Name}' Steam App ID set to {trimmed}{(details != null ? " (metadata refreshed)" : "")}.");
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
            var details = await _steamMetadataService.GetAppDetailsAsync(card.Game.SteamAppId, _getSteamGridDbApiKeyOrNull(), forceRefresh: true);
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
                    var details = await _steamMetadataService.GetAppDetailsAsync(res.SteamAppId, _getSteamGridDbApiKeyOrNull(), forceRefresh: true);
                    if (details != null)
                    {
                        if (card.Game.Category == LibraryConstants.Uncategorized && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
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
            }
            else
            {
                StatusMessage = $"Game info is already up to date for \"{card.Name}\".";
            }
        }
        catch (Exception ex)
        {
            LoggingService.Error("LibraryViewModel", $"Error fetching exe/online name for '{card.Name}'", ex);
            StatusMessage = $"Failed to update info for \"{card.Name}\".";
        }
    }

    /// <summary>
    /// Fetches Steam metadata for an entry that already resolved (or was assigned) a Steam AppId,
    /// or - failing that - tries to resolve a title/AppId match via GameNameExtractor. Shared by
    /// LibraryViewModel (refresh actions) and ImportCoordinator (the import pipelines' enrichment
    /// tail), which is why this stays internal to LibraryViewModel rather than moving wholesale to
    /// ImportCoordinator: both need the same Steam metadata/search service instances.
    /// </summary>
    public async Task EnrichGameWithSteamMetadataAsync(GameEntry entry)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.SteamAppId))
            {
                if (_settings.AutoCategorizeFromSteam || _settings.UseVerticalPosterArt)
                {
                    var details = await _steamMetadataService.GetAppDetailsAsync(entry.SteamAppId, _getSteamGridDbApiKeyOrNull());
                    if (details != null)
                    {
                        if (_settings.AutoCategorizeFromSteam && (entry.Category == LibraryConstants.Uncategorized || entry.Category == LibraryConstants.SteamCategory || entry.Category == LibraryConstants.GogCategory || entry.Category == LibraryConstants.EaCategory || entry.Category == LibraryConstants.EpicCategory || entry.Category == LibraryConstants.UbisoftCategory) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
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

            // entry.Name is already an authoritative title for a platform import (GOG/EA/Epic all
            // set it from the platform's own metadata before this runs) - pass it as the known
            // name so the search tries it directly instead of relying solely on a guess derived
            // from the exe filename/folder (e.g. "OMD.exe"/"OrcsMustDie3" missing the real Steam
            // listing that searching "Orcs Must Die! 3" finds immediately). Harmless to pass for a
            // plain folder-scan/manual-drop entry too - it's just whatever guess was already there.
            var res = await GameNameExtractor.ResolveGameMatchAsync(
                entry.ExecutablePath,
                folder,
                preferExe: _settings.PreferExeForGameName,
                searchOnline: true,
                steamSearch: _steamSearchService,
                minConfidence: _settings.OnlineMatchConfidenceThreshold,
                knownName: entry.Name);

            if (!string.IsNullOrWhiteSpace(res.ResolvedTitle) && _settings.SearchOfficialTitleOnline)
            {
                entry.Name = res.ResolvedTitle;
            }

            if (!string.IsNullOrWhiteSpace(res.SteamAppId))
            {
                entry.SteamAppId = res.SteamAppId;

                var details = await _steamMetadataService.GetAppDetailsAsync(res.SteamAppId, _getSteamGridDbApiKeyOrNull());
                if (details != null)
                {
                    if (_settings.AutoCategorizeFromSteam && (entry.Category == LibraryConstants.Uncategorized || entry.Category == LibraryConstants.SteamCategory || entry.Category == LibraryConstants.GogCategory || entry.Category == LibraryConstants.EaCategory || entry.Category == LibraryConstants.EpicCategory || entry.Category == LibraryConstants.UbisoftCategory) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                        entry.Category = details.PrimaryGenre;
                    if (string.IsNullOrWhiteSpace(entry.CoverImagePath) && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                        entry.CoverImagePath = details.CoverImagePath;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("LibraryViewModel", $"Enrichment error for {entry.Name}: {ex.Message}");
        }
    }

    public void ApplyRename(GameCardViewModel card, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;

        string oldName = card.Name;
        card.Game.Name = newName.Trim();
        card.RefreshProperties();
        SaveLibrary();
        FilteredGames.Refresh();
        LoggingService.Info("Library", $"Renamed \"{oldName}\" to \"{card.Name}\".");
        StatusMessage = $"Renamed to \"{card.Name}\"";
    }

    public void ApplyCategory(GameCardViewModel card, string newCategory)
    {
        if (string.IsNullOrWhiteSpace(newCategory))
            newCategory = LibraryConstants.Uncategorized;

        string oldCategory = card.Category;
        card.Game.Category = newCategory.Trim();
        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
        FilteredGames.Refresh();
        LoggingService.Info("Library", $"'{card.Name}' category changed: '{oldCategory}' -> '{card.Category}'.");
        StatusMessage = $"Updated category for \"{card.Name}\" to {card.Category}";
    }

    public void DeleteGame(GameCardViewModel card)
    {
        Window? owner = WindowHelper.ActiveOwner();
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
        LoggingService.Info("Library", $"Removed '{card.Name}' from library (undoable for 6s).");

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
                LoggingService.Verbose("Library", $"Undo window expired for '{_lastRemovedGame.Name}' - deleting cached artwork.");
                DeleteCachedArtwork(_lastRemovedGame);
                _lastRemovedGame = null;
            }
        };
        _undoToastTimer.Start();

        StatusMessage = $"Removed {card.Name}";
        OnPropertyChanged(nameof(TotalGameCount));
        OnPropertyChanged(nameof(TotalGameCountDisplay));
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
            LoggingService.Info("Library", $"Undid removal of '{card.Name}'.");

            StatusMessage = $"Restored \"{card.Name}\" to library.";
            OnPropertyChanged(nameof(TotalGameCount));
            OnPropertyChanged(nameof(TotalGameCountDisplay));
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
            LoggingService.Warn("LibraryViewModel", $"Failed to delete cached artwork '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Finds an existing library entry matching the given exe path or Steam AppId. Public so
    /// ImportCoordinator's drop/candidate pipelines can duplicate-check before adding.
    /// </summary>
    public GameCardViewModel? FindDuplicateGame(string? executablePath, string? steamAppId)
    {
        return Games.FirstOrDefault(g =>
            (!string.IsNullOrWhiteSpace(steamAppId) && string.Equals(g.Game.SteamAppId, steamAppId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(executablePath) && string.Equals(g.Game.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)));
    }

    public void SaveLibrary([CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
    {
        _storageService.SaveGames(Games.Select(g => g.Game), callerMember, callerFile);
        _storageService.SaveSettings(_settings, callerMember, callerFile);
        NotifyLibraryUpdated();
    }

    public void UpdateHotkeys()
    {
        _hotkeyManager.RegisterHotkeys(_settings.GlobalManageHotkey, Games.Select(g => g.Game));
    }

    public void OnGameUpdatedFromLauncher(GameEntry game)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var card = Games.FirstOrDefault(g => g.Id == game.Id);
            card?.RefreshProperties();
            SaveLibrary();
            ApplySort();
        });
    }

    /// <summary>
    /// Fired by ProcessLauncherService once the just-launched game's window has been found and
    /// given focus (or a bounded wait for it gave up) - see DispatchLaunch. Only acts if it's for
    /// the game we're actually waiting on, in case a second launch started before this one's
    /// signal arrived.
    /// </summary>
    public void OnGameWindowReady(GameEntry game)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_pendingMinimizeGameId == null || game.Id != _pendingMinimizeGameId) return;

            _pendingMinimizeGameId = null;
            _minimizeFallbackTimer?.Stop();
            _minimizeFallbackTimer = null;
            RequestMinimizeToTray?.Invoke();
        });
    }

    public void OnGameHotkeyTriggered(string gameId)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var card = Games.FirstOrDefault(g => g.Id == gameId);
            if (card != null)
            {
                LoggingService.Info("Library", $"Hotkey triggered launch for '{card.Name}'.");
                LaunchGame(card);
            }
            else
            {
                LoggingService.Warn("Library", $"Hotkey fired for unknown game id '{gameId}' - no matching card in library.");
            }
        });
    }

    public void RebuildCategories()
    {
        string previous = SelectedCategory;
        Categories.Clear();
        Categories.Add(LibraryConstants.AllCategory);
        Categories.Add(LibraryConstants.FavoritesCategory);

        // Excludes hidden games - a category only hidden games belong to would otherwise still
        // show as a tab (since FilterGameItem excludes hidden games from every non-Hidden tab),
        // leaving a clickable tab that always renders empty.
        var distinctCategories = Games
            .Where(g => !g.Game.IsHidden)
            .Select(g => g.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c) && c != LibraryConstants.AllCategory && c != LibraryConstants.FavoritesCategory && c != LibraryConstants.HiddenCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c);

        foreach (var cat in distinctCategories)
        {
            Categories.Add(cat);
        }

        // Hidden is a filter, not a real category, so it's pinned last rather than sorted in
        // alphabetically among actual categories - same treatment as All/Favorites up front.
        if (Games.Any(g => g.Game.IsHidden))
        {
            Categories.Add(LibraryConstants.HiddenCategory);
        }

        if (Categories.Contains(previous))
        {
            _selectedCategory = previous;
        }
        else
        {
            _selectedCategory = LibraryConstants.AllCategory;
            _settings.LastCategoryFilter = LibraryConstants.AllCategory;
            FilteredGames.Refresh();
        }
        OnPropertyChanged(nameof(SelectedCategory));

        RebuildCategoryTabs();
    }

    public void RebuildCategoryTabs()
    {
        CategoryTabs.Clear();
        foreach (var cat in Categories)
        {
            string display = cat == LibraryConstants.AllCategory ? "All Games" : cat == LibraryConstants.FavoritesCategory ? LibraryConstants.FavoritesCategory : cat;
            bool isSelected = string.Equals(cat, SelectedCategory, StringComparison.OrdinalIgnoreCase);
            CategoryTabs.Add(new CategoryTabItem(cat, display, isSelected, SelectCategoryTab));
        }
    }

    public void SelectCategoryTab(string category)
    {
        SelectedCategory = category;
    }

    public void ApplySort()
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
            case "Favorites First (Z - A)":
                FilteredGames.SortDescriptions.Add(new SortDescription("Game.IsFavorite", ListSortDirection.Descending));
                FilteredGames.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
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

        // "Hidden" is its own filter tab, like Favorites, rather than a separate toggle - a
        // hidden game only ever shows there, and every other tab (including All) excludes it.
        // The record itself is untouched either way, so scans never treat it as new.
        if (SelectedCategory == LibraryConstants.HiddenCategory)
        {
            if (!card.Game.IsHidden) return false;
        }
        else
        {
            if (card.Game.IsHidden) return false;

            // Category filter
            if (SelectedCategory == LibraryConstants.FavoritesCategory)
            {
                if (!card.Game.IsFavorite) return false;
            }
            else if (SelectedCategory != LibraryConstants.AllCategory &&
                !card.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
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
