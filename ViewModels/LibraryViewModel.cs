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
    /// <summary>The RAWG key when the feature is enabled and a key is set, else null.</summary>
    private readonly Func<string?> _getRawgApiKeyOrNull;
    private readonly RawgService _rawgService = new();

    private string _searchText = string.Empty;
    private string _selectedCategory = LibraryConstants.AllCategory;
    private string _selectedSortOption = "Alphabetical (A - Z)";
    private string _statusMessage = string.Empty;

    // Undo toast state
    private bool _isUndoToastVisible = false;
    private string _undoToastMessage = string.Empty;
    /// <summary>The games taken out by the most recent Remove (one, or a Select-mode batch) with
    /// their former positions, kept for the undo window. See <see cref="RemoveGames"/>.</summary>
    private readonly List<(GameEntry Game, int Index)> _lastRemoved = new();
    /// <summary>The undo window: 10 seconds, paused while the toast is hovered or focused.</summary>
    private readonly UndoTimer _undoTimer = new();

    // Launch toast state
    private bool _isLaunchToastVisible = false;
    private string _launchToastMessage = string.Empty;
    private DispatcherTimer? _launchToastTimer;

    /// <summary>Set by App: the popup a launch reports to while the window is out of sight. Null
    /// (tests, or before startup finishes) keeps the in-window toast and dialogs.</summary>
    public ILaunchPopup? LaunchPopup { get; set; }

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

    /// <summary>The toolbar's filter flyout - launcher, performance profile, and per-entry state.
    /// Runs on top of the category tabs and the search box, never instead of them.</summary>
    public LibraryFilterViewModel Filter { get; }

    /// <summary>
    /// True when the visible list is empty only because of the filter flyout - the library has
    /// games and the current tab and search would show some, but a tick box is hiding them. Drives
    /// the "clear filters" escape hatch in the empty state, so a filter saved from a previous run
    /// can never read as a library that lost its games.
    /// </summary>
    public bool IsEmptyBecauseOfFilters =>
        HasAnyGames && Filter.HasActiveFilters && !FilteredGames.Cast<object>().Any();

    private void OnFilterChanged()
    {
        FilteredGames.Refresh();
        // A selection made under one filter must not be acted on invisibly under another.
        ClearSelection();
        _settings.LibraryFilterKeys = Filter.ActiveKeys();
        _storageService.SaveSettings(_settings);
        OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
    }

    /// <summary>Clears every filter tick - the empty state's escape hatch and the flyout's button.</summary>
    public void ClearFilters() => Filter.ClearAll();

    public event Action<GameCardViewModel>? RequestEditGameDialog;
    public event Action<GameCardViewModel>? RequestQuickRename;
    public event Action<GameCardViewModel>? RequestQuickCategory;
    public event Action<GameCardViewModel>? RequestEditSteamAppId;
    /// <summary>Select mode's "Change Category": the window shows the category prompt for the
    /// given cards, then calls <see cref="ApplyCategoryToMany"/>.</summary>
    public event Action<List<GameCardViewModel>>? RequestBatchCategory;
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
        Func<string?> getSteamGridDbApiKeyOrNull,
        Func<string?>? getRawgApiKeyOrNull = null)
    {
        _getRawgApiKeyOrNull = getRawgApiKeyOrNull ?? (() => null);
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

        Filter = new LibraryFilterViewModel(OnFilterChanged);
        Filter.RestoreKeys(_settings.LibraryFilterKeys);

        FilteredGames = CollectionViewSource.GetDefaultView(Games);
        FilteredGames.Filter = FilterGameItem;
        ApplySort();
        Games.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAnyGames));
            OnPropertyChanged(nameof(TotalGameCountDisplay));
        };

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
            // A selection made under one filter must not be acted on invisibly under another.
            ClearSelection();
            OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
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
                ClearSelection();
                foreach (var tab in CategoryTabs)
                {
                    tab.IsSelected = string.Equals(tab.Name, value, StringComparison.OrdinalIgnoreCase);
                }
                FilteredGames.Refresh();
                OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
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
        set
        {
            if (_undoToastMessage != value) LoggingService.Shown("Undo toast", value);
            _undoToastMessage = value;
            OnPropertyChanged();
        }
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

    private string _launchToastIcon = "";
    /// <summary>Segoe MDL2 glyph shown at the left of the floating toast: Play (E768) for a launch,
    /// Accept (E8FB) for an import result.</summary>
    public string LaunchToastIcon
    {
        get => _launchToastIcon;
        set { _launchToastIcon = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            // Every status line the user is shown, at the one place it is set. See LoggingService.Shown.
            if (_statusMessage != value) LoggingService.Shown("Status", value);
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public int TotalGameCount => Games.Count;
    public string TotalGameCountDisplay => Games.Count == 1 ? "1 game" : $"{Games.Count} games";

    /// <summary>False only when the library itself is empty. Lets the view tell "add your first
    /// game" apart from "nothing matches this search or category tab".</summary>
    public bool HasAnyGames => Games.Count > 0;

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

        RecoverPendingRemoval();

        RebuildCategories();
        UpdateHotkeys();
        ApplySort();
        // Nothing to report: the status line stays empty rather than saying "Ready".
        StatusMessage = string.Empty;
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
    /// <param name="cards">The cards to load; every card in the library when null.</param>
    /// <summary>The most recent <see cref="LoadCardHeavyStateInBackground"/> run; completed once its
    /// results have been applied on the UI thread. The release screenshot waits on it.</summary>
    internal Task HeavyStateLoad { get; private set; } = Task.CompletedTask;

    internal void LoadCardHeavyStateInBackground(IReadOnlyList<GameCardViewModel>? cards = null)
    {
        var cardsSnapshot = cards?.ToList() ?? Games.ToList();
        HeavyStateLoad = Task.Run(() =>
        {
            // Once for the whole pass, before any card is asked: three launchers' registries and
            // manifest folders, not three per game.
            InstalledGameIndex.Current = InstalledGameIndex.Capture();

            var results = new System.Collections.Generic.List<(GameCardViewModel Card, GameAvailability Availability, System.Windows.Media.Imaging.BitmapImage? Icon, DateTime? IconWriteTimeUtc, System.Windows.Media.Imaging.BitmapImage? Cover, DateTime? CoverWriteTimeUtc)>();
            foreach (var card in cardsSnapshot)
            {
                var (availability, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc) = card.ComputeHeavyState();
                results.Add((card, availability, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc));
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (card, availability, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc) in results)
                {
                    card.ApplyHeavyState(availability, icon, iconWriteTimeUtc, cover, coverWriteTimeUtc);
                }

                // A saved "Executable missing" / "Not installed" tick was applied while every card
                // still read Available, so the filter has to run again now that it's known.
                if (Filter.HasActiveFilters)
                {
                    FilteredGames.Refresh();
                    OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
                }

                // The tray menu was built by LoadLibrary before any icon existed, and nothing else
                // rebuilds it until a launch or a library edit - so a user who starts minimized to
                // the tray saw the fallback glyph on every game. Its only subscriber is that rebuild.
                NotifyLibraryUpdated();
            });
        });
    }

    /// <summary>
    /// Re-reads what each launcher has installed and re-marks every card from it, without touching
    /// icons or covers. Runs after a scan, which is when a game the user uninstalled since startup
    /// is most likely to be noticed - the scan is already the moment TrayTrigger goes and looks at
    /// what is on the PC, and it is what the startup option runs too.
    /// </summary>
    internal Task RefreshAvailabilityInBackground()
    {
        var cardsSnapshot = Games.ToList();
        return Task.Run(() =>
        {
            // Nobody awaits this, so an exception here would vanish as an unobserved task - and a
            // tester reporting "the tags never updated after a scan" would leave no evidence.
            try
            {
                InstalledGameIndex.Current = InstalledGameIndex.Capture();
                var results = cardsSnapshot.Select(card => (Card: card, Availability: card.ComputeAvailability())).ToList();

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var (card, availability) in results) card.Availability = availability;

                    if (Filter.HasActiveFilters)
                    {
                        FilteredGames.Refresh();
                        OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
                    }

                    // The tray menu greys these games out too, so it has to be rebuilt to follow them.
                    NotifyLibraryUpdated();
                });
            }
            catch (Exception ex)
            {
                LoggingService.Warn("InstallCheck", $"Re-checking what is installed after a scan failed, so the library's markers are unchanged: {ex.Message}");
            }
        });
    }

    public GameCardViewModel CreateCardViewModel(GameEntry game, bool deferHeavyInit = false)
    {
        var card = new GameCardViewModel(
            game,
            // The hover Play button sits in the middle of a card, so a Ctrl/Shift+click aimed at
            // the card can land on it: treat that as the selection gesture it was meant to be,
            // never as a launch. While games are multi-selected the Play buttons are hidden (a
            // single-game action makes no sense against a selection); this also catches the
            // keyboard route (Ctrl+Enter). Tray and hotkey launches don't come through here.
            onLaunch: card =>
            {
                var modifiers = Keyboard.Modifiers;
                if (modifiers.HasFlag(ModifierKeys.Shift)) { OnCardRangeSelect(card); return; }
                if (modifiers.HasFlag(ModifierKeys.Control)) { OnCardToggleSelect(card); return; }
                if (!HasSelection) LaunchGame(card);
            },
            onEdit: card => RequestEditGameDialog?.Invoke(card),
            onDelete: DeleteGame,
            onRelocate: RelocateGame,
            onRename: card => RequestQuickRename?.Invoke(card),
            onChangeCategory: card => RequestQuickCategory?.Invoke(card),
            onChangeIcon: ChangeGameIcon,
            onChangeCover: ChangeGameCover,
            onViewDetails: c => OpenGameDetails(c),
            onChangeMatch: c => OpenGameDetails(c, openMatchPicker: true),
            onEditSteamAppId: EditSteamAppId,
            onRefreshMetadata: card => _ = RefreshGameMetadataAsync(card),
            onToggleFavorite: ToggleFavorite,
            onToggleHidden: ToggleHidden,
            getUseVerticalPosterArt: () => _getUseVerticalPosterArt(),
            deferHeavyInit: deferHeavyInit,
            onCloseGame: CloseGame,
            onForceClose: ForceCloseGame,
            onPrimaryClick: OnCardPrimaryClick,
            onToggleSelect: OnCardToggleSelect,
            onRangeSelect: OnCardRangeSelect,
            // The quick settings change the game entry and nothing else, so this is the narrow
            // save - no settings.json rewrite for a menu tick.
            onQuickSettingChanged: _ => SaveGamesOnly()
        );
        // Sessions outlive library reloads (a rescan while a game is running), so a fresh card
        // must pick up the live state rather than wait for the next SessionStarted event.
        card.IsPlaying = _launcherService.IsSessionActive(game.Id);
        return card;
    }

    /// <summary>Fired by ProcessLauncherService (background thread) when a tracked session starts.</summary>
    public void OnSessionStarted(ActiveGameSession session) => SetPlayingState(session.GameId, true);

    /// <summary>Fired by ProcessLauncherService (background thread) when a tracked session ends.</summary>
    public void OnSessionEnded(ActiveGameSession session) => SetPlayingState(session.GameId, false);

    private void SetPlayingState(string gameId, bool isPlaying)
    {
        RunOnUiThread(() =>
        {
            var card = Games.FirstOrDefault(g => g.Id == gameId);
            if (card != null) card.IsPlaying = isPlaying;
        });
    }

    /// <summary>
    /// "Close Game" from a card's menu or Game Details: asks the game to quit as its own close
    /// button would, and the session ends when it does. Off the UI thread, since it waits for the
    /// game and restoring a profile can involve an elevated Defender cmdlet.
    /// </summary>
    private void CloseGame(GameCardViewModel card)
    {
        StatusMessage = $"Closing {card.Name}...";
        string gameId = card.Game.Id;
        string name = card.Name;
        _ = Task.Run(() =>
        {
            var result = _launcherService.CloseGameNow(gameId);
            RunOnUiThread(() => StatusMessage = DescribeCloseGame(result, name));
        });
    }

    /// <summary>What Close Game did, for the status bar and the tray's notification.</summary>
    internal static string DescribeCloseGame(ProcessLauncherService.CloseGameResult result, string name) => result switch
    {
        ProcessLauncherService.CloseGameResult.Closed => $"Closed {name}; tweaks restored.",
        ProcessLauncherService.CloseGameResult.EndedBeforeStart => $"{name} hadn't started, so its session ended; tweaks restored.",
        ProcessLauncherService.CloseGameResult.StillRunning => $"{name} is still open - it may be asking to save or confirm. Close it there, or use Force Close. Tweaks are restored when it exits.",
        ProcessLauncherService.CloseGameResult.CouldNotAsk => $"Couldn't ask {name} to close. Close it yourself, or use Force Close. Tweaks are restored when it exits.",
        _ => $"{name} has no active session.",
    };

    /// <summary>"Force Close Game" from a card's menu or Game Details: confirms, kills the game, then
    /// ends the session. Off the UI thread, as above.</summary>
    private void ForceCloseGame(GameCardViewModel card)
    {
        bool confirmed = ModernDialog.Confirm(
            WindowHelper.ActiveOwner(),
            "Force Close Game",
            $"Force close \"{card.Name}\"?",
            "The game process will be killed immediately. Anything not saved in the game will be lost. TrayTrigger then restores the Performance Profile and runs the post-exit script.",
            confirmText: "Force Close",
            cancelText: "Cancel");
        if (!confirmed) return;

        StatusMessage = $"Force closing {card.Name}...";
        string gameId = card.Game.Id;
        string name = card.Name;
        _ = Task.Run(() =>
        {
            bool ended = _launcherService.EndSessionNow(gameId, forceCloseGame: true);
            RunOnUiThread(() => StatusMessage = ended ? $"Force closed {name}; tweaks restored." : $"{name} has no active session.");
        });
    }

    /// <summary>
    /// Marshals to the UI thread without throwing when the dispatcher is already shutting down -
    /// launcher callbacks arrive from thread-pool threads and a game can exit during app shutdown.
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        try
        {
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }
        catch (System.Threading.Tasks.TaskCanceledException) { /* the app is shutting down */ }
        catch (InvalidOperationException) { /* the app is shutting down */ }
    }

    // ------------------------------------------------------------------------------------
    // Multi-select (batch editing), Explorer-style
    //
    // Ctrl+click toggles a card, Shift+click selects a range, Ctrl+A selects every visible
    // card; selected cards show the accent border. Right-clicking one of two or more selected
    // cards opens the batch menu (MainWindow.OnCardContextMenuOpening), whose items apply
    // Favorite / Hide / Change Category / Remove to the whole selection through the same paths
    // the single-game menu uses. A plain click, Escape, or a filter change clears it.
    // Selection is UI-only state and is never saved.
    // ------------------------------------------------------------------------------------

    private GameCardViewModel? _selectionAnchor;

    private IEnumerable<GameCardViewModel> VisibleCards => FilteredGames.Cast<GameCardViewModel>();
    private List<GameCardViewModel> SelectedCards => Games.Where(g => g.IsSelected).ToList();

    public int SelectedCount => Games.Count(g => g.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionSummary => $"{SelectedCount} games selected";
    /// <summary>Status-bar nudge while a multi-selection exists; empty otherwise.</summary>
    public string SelectionHint => SelectedCount > 1
        ? $"{SelectedCount} selected - right-click one for batch actions"
        : string.Empty;

    /// <summary>Every selected game is a favorite - the batch menu's check mark, and what flips its label.</summary>
    public bool BatchAllFavorite => HasSelection && SelectedCards.All(c => c.Game.IsFavorite);
    /// <summary>Every selected game is hidden (the Hidden tab) - the batch menu's check mark.</summary>
    public bool BatchAllHidden => HasSelection && SelectedCards.All(c => c.Game.IsHidden);
    /// <summary>"Add to Favorites" unless every selected game already is one, then "Remove from Favorites".</summary>
    public string BatchFavoriteLabel => BatchAllFavorite ? "Remove from Favorites" : "Add to Favorites";
    /// <summary>"Hide" unless every selected game is already hidden, then "Unhide".</summary>
    public string BatchHideLabel => BatchAllHidden ? "Unhide" : "Hide";

    private ICommand? _cmdSelectAllCommand;
    public ICommand SelectAllCommand => _cmdSelectAllCommand ??= new RelayCommand(SelectAllVisible);
    private ICommand? _cmdClearSelectionCommand;
    public ICommand ClearSelectionCommand => _cmdClearSelectionCommand ??= new RelayCommand(ClearSelection);
    private ICommand? _cmdBatchFavoriteCommand;
    public ICommand BatchFavoriteCommand => _cmdBatchFavoriteCommand ??= new RelayCommand(BatchToggleFavorite);
    private ICommand? _cmdBatchHideCommand;
    public ICommand BatchHideCommand => _cmdBatchHideCommand ??= new RelayCommand(BatchToggleHidden);
    private ICommand? _cmdBatchChangeCategoryCommand;
    public ICommand BatchChangeCategoryCommand => _cmdBatchChangeCategoryCommand ??= new RelayCommand(() =>
    {
        var cards = SelectedCards;
        if (cards.Count > 0) RequestBatchCategory?.Invoke(cards);
    });
    private ICommand? _cmdBatchRemoveCommand;
    public ICommand BatchRemoveCommand => _cmdBatchRemoveCommand ??= new RelayCommand(BatchRemove);
    private ICommand? _cmdBatchSetProfileCommand;
    /// <summary>Parameter: a <see cref="PerformanceProfileMode"/> (from the batch menu's radio items).</summary>
    public ICommand BatchSetProfileCommand => _cmdBatchSetProfileCommand ??= new RelayCommand(p =>
    {
        if (p is PerformanceProfileMode mode) BatchSetProfile(mode);
    });
    private ICommand? _cmdBatchRefreshMetadataCommand;
    public ICommand BatchRefreshMetadataCommand => _cmdBatchRefreshMetadataCommand ??= new RelayCommand(() => _ = BatchRefreshMetadataAsync());
    private ICommand? _cmdBatchRunAsAdminCommand;
    public ICommand BatchRunAsAdminCommand => _cmdBatchRunAsAdminCommand ??= new RelayCommand(BatchToggleRunAsAdmin);
    private ICommand? _cmdBatchCloseLauncherCommand;
    public ICommand BatchCloseLauncherCommand => _cmdBatchCloseLauncherCommand ??= new RelayCommand(BatchToggleCloseLauncher);
    private ICommand? _cmdBatchSetCpuAffinityCommand;
    /// <summary>Parameter: a <see cref="CpuAffinityMode"/> (from the batch menu's radio items).</summary>
    public ICommand BatchSetCpuAffinityCommand => _cmdBatchSetCpuAffinityCommand ??= new RelayCommand(p =>
    {
        if (p is CpuAffinityMode mode) BatchSetCpuAffinity(mode);
    });

    /// <summary>The one profile every selected game shares, or null when they differ - drives
    /// which of the batch menu's Off / Optimized / Aggressive items shows a check.</summary>
    public PerformanceProfileMode? BatchProfile
    {
        get
        {
            var cards = SelectedCards;
            if (cards.Count == 0) return null;
            var first = cards[0].Game.PerformanceProfile;
            return cards.All(c => c.Game.PerformanceProfile == first) ? first : null;
        }
    }
    public bool BatchProfileIsOff => BatchProfile == PerformanceProfileMode.Off;
    public bool BatchProfileIsOptimized => BatchProfile == PerformanceProfileMode.Optimized;
    public bool BatchProfileIsAggressive => BatchProfile == PerformanceProfileMode.Aggressive;

    /// <summary>The one core mode every selected game shares, or null when they differ.</summary>
    public CpuAffinityMode? BatchCpuAffinity
    {
        get
        {
            var cards = SelectedCards;
            if (cards.Count == 0) return null;
            var first = cards[0].Game.CpuAffinity;
            return cards.All(c => c.Game.CpuAffinity == first) ? first : null;
        }
    }
    public bool BatchCpuAffinityIsDefault => BatchCpuAffinity == CpuAffinityMode.Default;
    public bool BatchCpuAffinityIsPerformanceCores => BatchCpuAffinity == CpuAffinityMode.PerformanceCoresOnly;

    /// <summary>Every selected game already runs elevated - the batch menu's check. Mixed selections show no check, and a click turns every game on.</summary>
    public bool BatchAllRunAsAdmin => HasSelection && SelectedCards.All(c => c.Game.RunAsAdmin);
    /// <summary>Every selected game already closes its launcher on exit.</summary>
    public bool BatchAllCloseLauncher => HasSelection && SelectedCards.All(c => c.Game.CloseLauncherOnExit);

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectionHint));
        OnPropertyChanged(nameof(BatchAllFavorite));
        OnPropertyChanged(nameof(BatchAllHidden));
        OnPropertyChanged(nameof(BatchFavoriteLabel));
        OnPropertyChanged(nameof(BatchHideLabel));
        OnPropertyChanged(nameof(BatchProfile));
        OnPropertyChanged(nameof(BatchProfileIsOff));
        OnPropertyChanged(nameof(BatchProfileIsOptimized));
        OnPropertyChanged(nameof(BatchProfileIsAggressive));
        OnPropertyChanged(nameof(BatchCpuAffinity));
        OnPropertyChanged(nameof(BatchCpuAffinityIsDefault));
        OnPropertyChanged(nameof(BatchCpuAffinityIsPerformanceCores));
        OnPropertyChanged(nameof(BatchAllRunAsAdmin));
        OnPropertyChanged(nameof(BatchAllCloseLauncher));
    }

    /// <summary>Same effect as picking the tier in Edit Game for each selected game. A game that
    /// is playing right now keeps its current session; the new tier applies from its next launch,
    /// exactly as an Edit Game save would.</summary>
    private void BatchSetProfile(PerformanceProfileMode mode)
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        foreach (var card in cards)
        {
            card.Game.PerformanceProfile = mode;
            card.RefreshProperties();
        }
        SaveGamesOnly();
        LoggingService.Info("Library", $"{cards.Count} game(s) performance profile set to {mode} (batch).");
        StatusMessage = $"Set Performance Profile of {cards.Count} game(s) to {mode}";
        NotifySelectionChanged();
    }

    /// <summary>
    /// Same effect as picking the core mode in Edit Game for each selected game. A game already
    /// running keeps the affinity it was launched with; the new mode applies from its next launch.
    /// </summary>
    private void BatchSetCpuAffinity(CpuAffinityMode mode)
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        foreach (var card in cards)
        {
            card.Game.CpuAffinity = mode;
            card.RefreshProperties();
        }
        SaveGamesOnly();
        LoggingService.Info("Library", $"{cards.Count} game(s) CPU affinity set to {mode} (batch).");
        StatusMessage = mode == CpuAffinityMode.PerformanceCoresOnly
            ? $"Set {cards.Count} game(s) to performance cores only"
            : $"Set {cards.Count} game(s) back to all cores";
        NotifySelectionChanged();
    }

    /// <summary>
    /// All-or-nothing, like the Favorite and Hide batch toggles: a mixed selection turns every
    /// game on, and only a selection where all of them are already on turns them off. The check
    /// mark in the menu is <see cref="BatchAllRunAsAdmin"/>, so what the click will do is on screen
    /// before it happens.
    /// </summary>
    private void BatchToggleRunAsAdmin()
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        bool target = !BatchAllRunAsAdmin;
        foreach (var card in cards)
        {
            card.Game.RunAsAdmin = target;
            card.RefreshProperties();
        }
        SaveGamesOnly();
        LoggingService.Info("Library", $"{cards.Count} game(s) set to {(target ? "run elevated" : "run normally")} (batch).");
        StatusMessage = target
            ? $"{cards.Count} game(s) will now run as administrator"
            : $"{cards.Count} game(s) will no longer run as administrator";
        NotifySelectionChanged();
    }

    private void BatchToggleCloseLauncher()
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        bool target = !BatchAllCloseLauncher;
        foreach (var card in cards)
        {
            card.Game.CloseLauncherOnExit = target;
            card.RefreshProperties();
        }
        SaveGamesOnly();
        LoggingService.Info("Library", $"{cards.Count} game(s) set to {(target ? "close" : "leave")} their launcher after exit (batch).");
        StatusMessage = target
            ? $"{cards.Count} game(s) will close their launcher after exit"
            : $"{cards.Count} game(s) will leave their launcher running";
        NotifySelectionChanged();
    }

    /// <summary>Runs the single-game refresh for each selected card in turn (the Steam store is
    /// rate-limited, so they are not fired in parallel) with progress in the status bar.</summary>
    public async Task BatchRefreshMetadataAsync()
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;

        int done = 0, failed = 0;
        foreach (var card in cards)
        {
            done++;
            StatusMessage = $"Refreshing poster & metadata ({done} of {cards.Count}): {card.Name}...";
            try
            {
                await RefreshGameMetadataAsync(card);
            }
            catch (Exception ex)
            {
                failed++;
                LoggingService.Warn("Library", $"Batch refresh failed for '{card.Name}': {ex.Message}");
            }
        }
        LoggingService.Info("Library", $"Batch refresh finished: {cards.Count - failed} of {cards.Count} game(s) refreshed.");
        StatusMessage = failed == 0
            ? $"Refreshed poster & metadata for {cards.Count} game(s)"
            : $"Refreshed {cards.Count - failed} of {cards.Count} game(s); {failed} failed (see log)";
    }

    /// <summary>Plain click: Details, as always - and, Explorer-style, it drops any multi-selection.</summary>
    private void OnCardPrimaryClick(GameCardViewModel card)
    {
        ClearSelection();
        OpenGameDetails(card);
    }

    private void OnCardToggleSelect(GameCardViewModel card)
    {
        ToggleCardSelection(card);
    }

    /// <summary>Shift+click: select every visible card between the last clicked one and this one.</summary>
    private void OnCardRangeSelect(GameCardViewModel card)
    {
        var visible = VisibleCards.ToList();
        int from = _selectionAnchor != null ? visible.IndexOf(_selectionAnchor) : -1;
        int to = visible.IndexOf(card);
        if (from < 0 || to < 0)
        {
            ToggleCardSelection(card);
            return;
        }

        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            visible[i].IsSelected = true;
        }
        NotifySelectionChanged();
    }

    private void ToggleCardSelection(GameCardViewModel card)
    {
        card.IsSelected = !card.IsSelected;
        _selectionAnchor = card;
        NotifySelectionChanged();
    }

    /// <summary>Programmatic selection (tests, screenshot modes) that keeps the counts in step.</summary>
    public void SetCardSelected(GameCardViewModel card, bool selected)
    {
        card.IsSelected = selected;
        NotifySelectionChanged();
    }

    public void SelectAllVisible()
    {
        foreach (var card in VisibleCards) card.IsSelected = true;
        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        bool any = false;
        foreach (var card in Games)
        {
            if (card.IsSelected) { card.IsSelected = false; any = true; }
        }
        _selectionAnchor = null;
        if (any) NotifySelectionChanged();
    }

    private void BatchToggleFavorite()
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        bool makeFavorite = !cards.All(c => c.Game.IsFavorite);
        foreach (var card in cards)
        {
            card.Game.IsFavorite = makeFavorite;
            card.RefreshProperties();
        }
        SaveLibrary();
        FilteredGames.Refresh();
        LoggingService.Info("Library", $"{cards.Count} game(s) favorite: {(makeFavorite ? "on" : "off")} (batch).");
        StatusMessage = makeFavorite ? $"Added {cards.Count} game(s) to Favorites" : $"Removed {cards.Count} game(s) from Favorites";
        NotifySelectionChanged();
    }

    private void BatchToggleHidden()
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        bool hide = !cards.All(c => c.Game.IsHidden);
        foreach (var card in cards)
        {
            card.Game.IsHidden = hide;
            card.IsSelected = false;
            card.RefreshProperties();
        }
        SaveLibrary();
        RebuildCategories();
        FilteredGames.Refresh();
        LoggingService.Info("Library", $"{cards.Count} game(s) hidden: {(hide ? "on" : "off")} (batch).");
        StatusMessage = hide ? $"Hid {cards.Count} game(s)" : $"Unhid {cards.Count} game(s)";
        NotifySelectionChanged();
    }

    /// <summary>Applies one category to every card - the window calls this after the batch
    /// category prompt (see <see cref="RequestBatchCategory"/>).</summary>
    public void ApplyCategoryToMany(List<GameCardViewModel> cards, string newCategory)
    {
        if (cards.Count == 0) return;
        newCategory = LibraryConstants.NormalizeCategory(newCategory);

        foreach (var card in cards)
        {
            card.Game.Category = newCategory;
            card.RefreshProperties();
        }
        RebuildCategories();
        SaveLibrary();
        FilteredGames.Refresh();
        LoggingService.Info("Library", $"{cards.Count} game(s) category changed to '{newCategory}' (batch).");
        StatusMessage = $"Set category of {cards.Count} game(s) to {newCategory}";
        NotifySelectionChanged();
    }

    /// <summary>The batch Remove confirmation. Defaults to the modal dialog; tests replace it.</summary>
    internal Func<List<GameCardViewModel>, bool> ConfirmBatchRemove { get; set; } = cards =>
    {
        Window? owner = WindowHelper.ActiveOwner();
        string what = cards.Count == 1 ? $"\"{cards[0].Name}\"" : $"these {cards.Count} games";
        return ModernDialog.ConfirmDelete(
            owner,
            "Remove from Library",
            $"Are you sure you want to remove {what} from your library?",
            "This will only remove the shortcuts from TrayTrigger. Your installed game files will not be deleted.",
            confirmText: cards.Count == 1 ? "Remove" : $"Remove {cards.Count}",
            cancelText: "Cancel");
    };

    private void BatchRemove()
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        if (!ConfirmBatchRemove(cards)) return;

        RemoveGames(cards);
        NotifySelectionChanged();
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

    /// <summary>
    /// Opens the details dialog for <paramref name="card"/>. With
    /// <paramref name="openMatchPicker"/> it goes straight into the Steam/RAWG match picker on
    /// arrival - that is the card context menu's "Change Match...", which exists so correcting a
    /// bad match doesn't take three steps. Routing between the two sources, and all the reload
    /// and poster-replacement bookkeeping a rematch triggers, stay in GameDetailsViewModel; there
    /// is no second copy of that logic at the library level.
    /// </summary>
    public void OpenGameDetails(GameCardViewModel card, bool openMatchPicker = false)
    {
        bool requestedLaunch = false;
        bool requestedEdit = false;
        bool requestedDelete = false;
        bool requestedCloseGame = false;
        bool requestedForceClose = false;

        var vm = new GameDetailsViewModel(
            card.Game,
            _steamMetadataService,
            _steamSearchService,
            launchAction: _ => requestedLaunch = true,
            editAction: _ => requestedEdit = true,
            deleteAction: _ => requestedDelete = true,
            steamGridDbApiKey: _getSteamGridDbApiKeyOrNull(),
            minConfidence: _settings.OnlineMatchConfidenceThreshold,
            rawgApiKey: _getRawgApiKeyOrNull(),
            saveGame: _ => SaveLibrary(),
            autoCategorize: _settings.AutoCategorizeFromSteam,
            fetchPosterByName: (game, preferredName, replace) => TryFetchGridArtByNameAsync(game, preferredName, replace),
            refreshInterval: _settings.MetadataRefreshInterval,
            isPlaying: card.IsPlaying,
            closeGameAction: _ => requestedCloseGame = true,
            forceCloseAction: _ => requestedForceClose = true);

        var dlg = new Views.GameDetailsDialog(vm);
        dlg.Owner = WindowHelper.ActiveOwner();
        if (openMatchPicker) dlg.OpenMatchPickerWhenReady();
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
        else if (requestedCloseGame)
        {
            card.CloseGameCommand.Execute(null);
        }
        else if (requestedForceClose)
        {
            card.ForceCloseCommand.Execute(null);
        }

        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
    }

    /// <summary>
    /// How long a launch is held after its notice (in-window toast or launch popup) appears and
    /// before it is dispatched. A fast-launching game can take focus or go fullscreen within a few
    /// hundred ms of its process spawning, covering the notice before it is ever read. Raised from
    /// 0.8 s in 1.4.4; the planned Tools launcher uses the same value so both feel identical.
    /// </summary>
    internal static readonly TimeSpan LaunchDispatchDelay = TimeSpan.FromSeconds(2);

    /// <summary>How long the in-window launch toast stays up after the launch is dispatched.</summary>
    private static readonly TimeSpan LaunchToastAfterDispatch = TimeSpan.FromSeconds(2);

    public void LaunchGame(GameCardViewModel card)
    {
        Window? owner = WindowHelper.ActiveOwner();
        if (card.IsUnavailable)
        {
            // Both markers come from a check made earlier - at startup, or the last scan - so by
            // now the game may have been reinstalled, or the drive it lives on plugged back in.
            // Ask again before refusing: a card that is only dimmed must never be one that cannot
            // be played, and being told to go and find a file that is sitting right there is the
            // most annoying way to get that wrong.
            var was = card.Availability;
            card.Availability = InstalledGameIndex.Recheck(card.Game);
            if (card.IsAvailable)
            {
                LoggingService.Info("GameCard", was == GameAvailability.NotInstalled
                    ? $"'{card.Name}' was marked not installed, but its launcher has it again - launching."
                    : $"'{card.Name}' was marked missing, but '{card.Game.ExecutablePath}' is back - launching.");
            }
            else if (card.IsNotInstalled)
            {
                ShowNotInstalled(card, owner);
                return;
            }
            else
            {
                // From a hotkey or the tray there's no window for the dialog to sit on.
                if (LaunchPopup?.TryShowFailure(LaunchTarget.ForGame(card.Game), "Its executable wasn't found.", "Locate executable...",
                        () => PromptLocateMissingExecutable(card, WindowHelper.ActiveOwner())) != true)
                {
                    PromptLocateMissingExecutable(card, owner);
                }
                return;
            }
        }

        // Shown before dispatching, then held for LaunchDispatchDelay via a non-blocking timer
        // before the actual launch happens, so a game that takes focus or goes fullscreen almost
        // at once can't cover the notice before it is read. With the window hidden or behind
        // another app the toast can't be seen at all, so the launch popup stands in for it.
        if (LaunchPopup?.TryBeginLaunch(LaunchTarget.ForGame(card.Game)) != true)
        {
            ShowLaunchToast($"Launching \"{card.Name}\"...",
                seconds: (int)Math.Ceiling((LaunchDispatchDelay + LaunchToastAfterDispatch).TotalSeconds));
        }

        var launchDelayTimer = new DispatcherTimer { Interval = LaunchDispatchDelay };
        launchDelayTimer.Tick += (s, e) =>
        {
            launchDelayTimer.Stop();
            DispatchLaunch(card, owner);
        };
        launchDelayTimer.Start();
    }

    /// <summary>
    /// What Play does for a game its launcher no longer has installed. Nothing is removed and
    /// nothing is offered to remove: reinstalling is the ordinary next step, and the entry is what
    /// carries this game's playtime, hotkey, scripts and profile through it.
    /// </summary>
    private void ShowNotInstalled(GameCardViewModel card, Window? owner)
    {
        IsLaunchToastVisible = false;
        string launcher = LauncherDisplayName(card.Game);
        string message = $"\"{card.Name}\" isn't installed in {launcher} any more.";

        // Only Steam can be sent to one game's page; the other clients have no such address, so
        // their popup gets the plain acknowledgement rather than a button that opens a library
        // the user then has to search by hand.
        bool canOpenInSteam = card.Game.IsSteamGame && UrlProtocolHelper.IsValidSteamAppId(card.Game.SteamAppId);
        bool shownInPopup = LaunchPopup?.TryShowFailure(
            LaunchTarget.ForGame(card.Game),
            $"It isn't installed in {launcher} any more.",
            canOpenInSteam ? "Open in Steam" : "Open TrayTrigger",
            canOpenInSteam ? () => card.OpenInSteamLibraryCommand.Execute(null) : null) == true;

        StatusMessage = message;
        if (shownInPopup) return;

        // From a hotkey or the tray there may be no window for this to sit on; ModernDialog falls
        // back to the active owner itself.
        if (canOpenInSteam)
        {
            if (ModernDialog.Confirm(owner, "Game Not Installed", message,
                    $"Reinstall it in {launcher} and it goes back to normal on the next scan.\n\nOpen it in Steam now?",
                    "Open in Steam", "Close"))
            {
                card.OpenInSteamLibraryCommand.Execute(null);
            }
            return;
        }

        ModernDialog.ShowInfo(owner, "Game Not Installed", message,
            $"Reinstall it in {launcher} and it goes back to normal on the next scan. Its playtime, hotkey, scripts and performance profile are kept in the meantime.");
    }

    /// <summary>The launcher to name in front of the user for an entry one of them owns.</summary>
    private static string LauncherDisplayName(GameEntry game) =>
        game.IsSteamGame ? "Steam"
        : game.IsXboxGame ? "the Xbox app"
        : game.IsBattleNetGame ? "Battle.net"
        : game.IsGogGame ? "GOG Galaxy"
        : game.IsEaGame ? "the EA app"
        : game.IsEpicGame ? "the Epic Games Launcher"
        : game.IsUbisoftGame ? "Ubisoft Connect"
        : "its launcher";

    private void PromptLocateMissingExecutable(GameCardViewModel card, Window? owner)
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
    }

    private void DispatchLaunch(GameCardViewModel card, Window? owner)
    {
        // LaunchGame can block for a long time - a UAC prompt plus an elevated Defender cmdlet
        // for an Aggressive profile, then a pre-launch script wait - so it runs off the UI
        // thread. The launcher's events already marshal back here on their own.
        _ = Task.Run(() =>
        {
            bool ok;
            string? err;
            bool isMissing;
            try
            {
                ok = _launcherService.LaunchGame(card.Game, out err, out isMissing);
            }
            catch (Exception ex)
            {
                ok = false;
                err = ex.Message;
                isMissing = false;
                LoggingService.Error("Library", $"Launch of '{card.Name}' threw: {ex.Message}", ex);
            }
            RunOnUiThread(() => OnLaunchDispatched(card, owner, ok, err, isMissing));
        });
    }

    private void OnLaunchDispatched(GameCardViewModel card, Window? owner, bool launched, string? err, bool isMissing)
    {
        if (launched)
        {
            // No RefreshProperties()/SaveLibrary() here: LaunchGame already raised GameUpdated
            // synchronously (LastPlayed change), which OnGameUpdatedFromLauncher just handled -
            // doing it again here was a redundant second full games.json + settings.json rewrite
            // on every launch.
            StatusMessage = $"Launched {card.Name}";
            LaunchPopup?.LaunchDispatched(card.Game.Id);

            if (_settings.MinimizeOnGameLaunch)
            {
                // Hidden as soon as the launch is dispatched. The launch popup stands in for the
                // in-window notice whenever this option is on (see App's LaunchPopupCoordinator),
                // and ProcessLauncherService gives the game's window focus as soon as it appears,
                // over whatever window Windows handed focus to when this one hid.
                RequestMinimizeToTray?.Invoke();
            }
        }
        else if (isMissing)
        {
            IsLaunchToastVisible = false;
            card.RefreshProperties();
            if (LaunchPopup?.TryShowFailure(LaunchTarget.ForGame(card.Game), "Its executable wasn't found.", "Locate executable...",
                    () => PromptLocateMissingExecutable(card, WindowHelper.ActiveOwner())) != true)
            {
                PromptLocateMissingExecutable(card, owner);
            }
        }
        else
        {
            IsLaunchToastVisible = false;
            StatusMessage = $"Error: {err}";
            string message = err ?? "Failed to launch game.";
            if (LaunchPopup?.TryShowFailure(LaunchTarget.ForGame(card.Game), message, "Open TrayTrigger", action: null) != true)
            {
                ModernDialog.ShowWarning(owner, "Launch Error", message);
            }
        }
    }

    /// <summary>
    /// Set by MainViewModel: whether the Library section (and therefore its status bar) is the one
    /// currently on screen. Used by <see cref="AnnounceImportResult"/> to decide whether a status
    /// message needs the floating toast to be seen at all.
    /// </summary>
    public Func<bool>? IsLibraryVisible { get; set; }

    /// <summary>
    /// Reports the outcome of an import/scan the way a completion message should be reported:
    /// into the Library status bar always, and additionally as the floating toast when the user is
    /// on another section (Settings, System, About) where that status bar is collapsed - a drop
    /// onto the Settings page or "Scan for Games" from Settings would otherwise finish silently.
    /// </summary>
    public void AnnounceImportResult(string message)
    {
        StatusMessage = message;
        if (IsLibraryVisible?.Invoke() == false)
        {
            ShowLaunchToast(message, icon: "", seconds: 5);
        }
    }

    /// <summary>Shows the floating launch toast for a few seconds - same non-blocking overlay
    /// pattern as the undo-delete toast, but auto-dismissing since there's no action to take.
    /// The toast lives outside the per-section grids, so it renders on every section.</summary>
    /// <summary>The in-window "Launching..." notice for a launch outside the library (a tool), with a game launch's timing.</summary>
    public void ShowLaunchNotice(string message) =>
        ShowLaunchToast(message, seconds: (int)Math.Ceiling((LaunchDispatchDelay + LaunchToastAfterDispatch).TotalSeconds));

    private void ShowLaunchToast(string message, string icon = "", int seconds = 3)
    {
        LoggingService.Shown("Toast", message);
        _launchToastTimer?.Stop();

        LaunchToastIcon = icon;
        LaunchToastMessage = message;
        IsLaunchToastVisible = true;

        _launchToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
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

        if (FileDialogCloak.Show(dialog) == true)
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

        if (FileDialogCloak.Show(dialog) == true)
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

        if (FileDialogCloak.Show(dialog) == true)
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

        if (!UrlProtocolHelper.IsValidSteamAppId(trimmed))
        {
            ModernDialog.ShowWarning(Application.Current?.MainWindow, "Invalid App ID", "Steam App ID must be a numeric ID (e.g. 1245620).");
            return;
        }

        card.Game.SteamAppId = trimmed;
        // IsSteamGame is left alone: a Local game links an App ID purely for metadata and art,
        // and a Steam game set to launch its executable directly (a real path, not steam://)
        // must not be demoted to Local by re-linking its ID.

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
            // The same setting and rule as the enrichment pass and Game Details.
            if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(card.Game.Category) &&
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
            // A manual refresh re-fetches the RAWG entry too (bypassing the disk cache) before
            // the normal pass, which then serves it from the fresh cache.
            if (card.Game.RawgId > 0)
                await TryEnrichFromRawgAsync(card.Game, forceRefresh: true);
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
            // Same RAWG fallback as the background pass for a still-uncategorized game.
            if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(card.Game.Category))
                await TryEnrichFromRawgAsync(card.Game, forceRefresh: card.Game.RawgId > 0);
        }

        card.RefreshProperties();
        RebuildCategories();
        SaveLibrary();
        StatusMessage = $"Refreshed metadata for \"{card.Name}\"";
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
                        if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                            entry.Category = details.PrimaryGenre;
                        if (string.IsNullOrWhiteSpace(entry.CoverImagePath) && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                            entry.CoverImagePath = details.CoverImagePath;
                    }

                    // A Steam App ID with no store page (delisted, region-locked) or a page that
                    // lists no genres leaves the category empty - let RAWG fill it.
                    if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category))
                        await TryEnrichFromRawgAsync(entry);
                }
                return;
            }

            if (!_settings.SearchOfficialTitleOnline && !_settings.AutoCategorizeFromSteam && !_settings.UseVerticalPosterArt
                && string.IsNullOrWhiteSpace(_getRawgApiKeyOrNull()))
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
                preferExe: GameNameExtractor.PreferExeForGameName,
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
                    if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
                        entry.Category = details.PrimaryGenre;
                    if (string.IsNullOrWhiteSpace(entry.CoverImagePath) && !string.IsNullOrWhiteSpace(details.CoverImagePath))
                        entry.CoverImagePath = details.CoverImagePath;
                }
                return;
            }

            // No Steam App ID resolved (Roblox, Fortnite, Game Pass exclusives, obscure indies):
            // RAWG is the peer source here - resolve (and remember) its match and let its genre
            // fill the category. Then SteamGridDB poster art by name, searched with RAWG's
            // canonical title first since the scanner's folder-derived name often misses.
            string? rawgTitle = await TryEnrichFromRawgAsync(entry);
            await TryFetchGridArtByNameAsync(entry, rawgTitle);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Library", $"Enrichment error for {entry.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// SteamGridDB-by-name poster fetch for an entry with no Steam App ID. A no-op unless vertical
    /// art is enabled, a SteamGridDB key is set, and the cover is currently empty. The community
    /// autocomplete can return a loosely-related game, so the art is applied only when the matched
    /// title actually resembles the game's name (same guard as the Steam title match) - otherwise
    /// a search for "Roblox" grabbing some unrelated poster would stick.
    /// </summary>
    internal async Task TryFetchGridArtByNameAsync(GameEntry entry, string? preferredName = null, bool replaceExisting = false)
    {
        if (!_settings.UseVerticalPosterArt
            || (!replaceExisting && !string.IsNullOrWhiteSpace(entry.CoverImagePath))
            || string.IsNullOrWhiteSpace(entry.Name))
            return;

        string? key = _getSteamGridDbApiKeyOrNull();
        if (string.IsNullOrWhiteSpace(key))
            return;

        // RAWG's canonical title first (when we have one and it differs), then the entry's own
        // name. Either way the result must resemble the entry's name before it sticks.
        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredName) && !preferredName.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
            queries.Add(preferredName);
        queries.Add(entry.Name);

        foreach (string query in queries)
        {
            var art = await _steamMetadataService.DownloadAndCacheGridArtByNameAsync(entry.Id, query, key);
            if (art == null)
                continue;

            double similarity = Math.Max(
                SteamSearchService.CalculateSimilarity(entry.Name, art.Value.MatchedName),
                SteamSearchService.CalculateSimilarity(query, art.Value.MatchedName));
            if (similarity >= _settings.OnlineMatchConfidenceThreshold)
            {
                entry.CoverImagePath = art.Value.Path;
                LoggingService.Info("Library", $"Applied SteamGridDB poster for '{entry.Name}' (searched '{query}', matched '{art.Value.MatchedName}', similarity {similarity:F2}).");
                return;
            }

            LoggingService.Verbose("Library", $"Rejected SteamGridDB poster for '{entry.Name}' (searched '{query}'): matched title '{art.Value.MatchedName}' too dissimilar (similarity {similarity:F2} < {_settings.OnlineMatchConfidenceThreshold:F2}).");
        }
    }

    /// <summary>
    /// RAWG enrichment for an entry with no Steam App ID: resolves (and remembers) the RAWG
    /// match, and fills the category from RAWG's primary genre under the same
    /// "auto-categorize" setting and Uncategorized-only rule as Steam. A no-op unless RAWG is
    /// enabled with a key. Returns RAWG's canonical title for the poster search, or null.
    /// </summary>
    private async Task<string?> TryEnrichFromRawgAsync(GameEntry entry, bool forceRefresh = false)
    {
        string? key = _getRawgApiKeyOrNull();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(entry.Name))
            return null;

        RawgLookupResult result;
        if (entry.RawgId > 0)
        {
            result = await _rawgService.GetByIdAsync(entry.RawgId, key, forceRefresh);
            if (result.Status == RawgLookupStatus.NoMatch)
            {
                entry.RawgId = 0;
                result = await _rawgService.LookUpByNameAsync(entry.Name, key, _settings.OnlineMatchConfidenceThreshold);
            }
        }
        else
        {
            result = await _rawgService.LookUpByNameAsync(entry.Name, key, _settings.OnlineMatchConfidenceThreshold);
        }

        if (result.Details is not { } details)
            return null;

        if (entry.RawgId != details.RawgId)
        {
            entry.RawgId = details.RawgId;
            LoggingService.Info("Library", $"'{entry.Name}' matched RAWG entry '{details.Name}' (id {details.RawgId}).");
        }

        if (_settings.AutoCategorizeFromSteam && LibraryConstants.IsEnrichableCategory(entry.Category) && !string.IsNullOrWhiteSpace(details.PrimaryGenre))
        {
            entry.Category = details.PrimaryGenre;
            LoggingService.Info("Library", $"'{entry.Name}' categorized as '{details.PrimaryGenre}' from RAWG.");
        }

        return details.Name;
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
        string oldCategory = card.Category;
        card.Game.Category = LibraryConstants.NormalizeCategory(newCategory);
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

        RemoveGames(new List<GameCardViewModel> { card });
    }

    /// <summary>
    /// Takes the given cards out of the library as one undoable step (the single-game Remove and
    /// Select mode's batch Remove both land here). The caller has already confirmed. The games'
    /// cached files and fetched details are only deleted once the undo window ends - see
    /// <see cref="FinalizePendingRemoval"/>.
    /// </summary>
    private void RemoveGames(List<GameCardViewModel> cards)
    {
        if (cards.Count == 0) return;

        // Only one pending removal can be undone at a time. If another one is still sitting in
        // its undo window when this new one arrives, it's about to be overwritten and can no
        // longer be undone anyway - finalize its cleanup now instead of leaking it.
        FinalizePendingRemoval();

        // Record positions before anything moves so undo can put every game back where it was.
        foreach (var card in cards.OrderBy(c => Games.IndexOf(c)))
        {
            _lastRemoved.Add((card.Game, Games.IndexOf(card)));
        }
        foreach (var card in cards)
        {
            card.IsSelected = false;
            Games.Remove(card);
        }

        // Before the library is saved without them: from that save on, this file is the only
        // place these games' cached files and DLSS records are written down, and a crash inside
        // the undo window would otherwise strand both. See RecoverPendingRemoval.
        _storageService.SavePendingRemoval(_lastRemoved.Select(r => r.Game));

        RebuildCategories();
        SaveLibrary();
        UpdateHotkeys();

        string what = cards.Count == 1 ? $"\"{cards[0].Name}\"" : $"{cards.Count} games";
        LoggingService.Info("Library", $"Removed {what} from library (undoable for {UndoCountdown.DefaultWindow.TotalSeconds:0}s).");

        UndoToastMessage = $"Removed {what}";
        IsUndoToastVisible = true;

        _undoTimer.Start(() =>
        {
            // The undo window has expired - the removal is now final.
            LoggingService.Verbose("Library", $"Undo window expired for {what} - deleting cached data.");
            FinalizePendingRemoval();
        });

        StatusMessage = $"Removed {what}";
        NotifyGameCountChanged();
    }

    /// <summary>
    /// Ends the undo window now and deletes the removed games' cached files and fetched details.
    /// Called when the window lapses, when another removal replaces it, before a bulk removal
    /// that could otherwise be raced by an undo, and on app exit - the pending removal lives only
    /// in memory, so exiting inside the window used to leave that data behind for good.
    /// </summary>
    public void FinalizePendingRemoval()
    {
        _undoTimer.Stop();
        IsUndoToastVisible = false;
        if (_lastRemoved.Count == 0) return;

        var removed = _lastRemoved.Select(r => r.Game).ToList();
        _lastRemoved.Clear();
        DeleteRemovedGameData(removed);
        _storageService.DeletePendingRemoval();
    }

    /// <summary>
    /// Finishes a removal that a previous run never got to the end of - a crash, or a kill, inside
    /// the undo window. The library was already saved without those games, so nothing else knows
    /// about their cached files, or holds the DLSS records that can put a driver override back.
    ///
    /// <para>A game that is in the library is skipped: the crash came before the library was saved
    /// without it, or after an undo had put it back, and either way it was never removed.</para>
    /// </summary>
    internal void RecoverPendingRemoval()
    {
        // A removal still inside its window owns the file; its own finalize or undo deals with it.
        if (_lastRemoved.Count > 0) return;

        var pending = _storageService.LoadPendingRemoval();
        if (pending.Count == 0) return;

        var inLibrary = Games.Select(c => c.Game.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = pending.Where(g => !inLibrary.Contains(g.Id)).ToList();

        if (gone.Count > 0)
        {
            LoggingService.Warn("Library", $"Found {gone.Count} game(s) a previous run removed without finishing (likely an abnormal exit) - cleaning up after them now.");
            DeleteRemovedGameData(gone);
        }
        _storageService.DeletePendingRemoval();
    }

    /// <summary>Games imported through <paramref name="launcher"/>'s integration, whatever
    /// their current category, and how many of those are hidden from every tab.</summary>
    public (int Total, int Hidden) CountPlatformGames(DetectedLauncher launcher)
    {
        int total = 0, hidden = 0;
        foreach (var g in Games)
        {
            if (!IsPlatformGame(g.Game, launcher)) continue;
            total++;
            if (g.Game.IsHidden) hidden++;
        }
        return (total, hidden);
    }

    /// <summary>True if a library entry is already linked to the platform game
    /// <paramref name="match"/> describes (by platform ID) - the batch dialog's "IN LIBRARY"
    /// check for a candidate that resolved to a platform, where the exe-path comparison can't
    /// work (a Steam entry's ExecutablePath is a steam:// URL, not the exe on disk).</summary>
    public bool IsPlatformGameInLibrary(PlatformMatch? match)
    {
        if (match == null) return false;
        return Games.Any(g => match switch
        {
            { Steam: { } s } => string.Equals(g.Game.SteamAppId, s.AppId, StringComparison.OrdinalIgnoreCase),
            { Gog: { } p } => string.Equals(g.Game.GogGameId, p.GameId, StringComparison.OrdinalIgnoreCase),
            { Ea: { } e } => string.Equals(g.Game.EaContentId, e.ContentId, StringComparison.OrdinalIgnoreCase),
            { Epic: { } p } => string.Equals(g.Game.EpicAppName, p.AppName, StringComparison.OrdinalIgnoreCase),
            { Ubisoft: { } u } => string.Equals(g.Game.UbisoftGameId, u.GameId, StringComparison.OrdinalIgnoreCase),
            { Xbox: { } x } => string.Equals(g.Game.XboxAumid, x.Aumid, StringComparison.OrdinalIgnoreCase),
            { BattleNet: { } b } => string.Equals(g.Game.BattleNetUid, b.Uid, StringComparison.OrdinalIgnoreCase),
            _ => false
        });
    }

    /// <summary>
    /// Bulk-removes every game imported through <paramref name="launcher"/>'s integration - the
    /// "also remove its games" half of turning that integration off in Settings. Unlike
    /// <see cref="DeleteGame"/> this is not undoable (the caller has already confirmed it) and
    /// cleans up cached artwork immediately. Installed game files are never touched.
    /// </summary>
    /// <returns>How many games were removed.</returns>
    public int RemovePlatformGames(DetectedLauncher launcher)
    {
        var toRemove = Games.Where(g => IsPlatformGame(g.Game, launcher)).ToList();
        if (toRemove.Count == 0) return 0;

        // A pending single-game undo would otherwise be able to resurrect a game whose artwork
        // is about to be cleaned up below (if it shares a cached file) - finalize it first.
        FinalizePendingRemoval();

        foreach (var card in toRemove)
        {
            // Same as RemoveGames: a selected card leaving the library must not keep counting
            // towards Select mode's "N selected".
            card.IsSelected = false;
            Games.Remove(card);
        }

        // Persist the removal *before* deleting cached files: a crash between the two would
        // otherwise leave games.json still listing these games with icon/cover paths that point
        // at deleted files, and nothing re-fetches art for an entry whose path is merely broken.
        // Written down first, as in RemoveGames, so a crash between the save and the cleanup is
        // finished on the next start instead of stranding artwork and DLSS overrides.
        _storageService.SavePendingRemoval(toRemove.Select(c => c.Game));
        RebuildCategories();
        SaveLibrary();
        UpdateHotkeys();
        NotifyGameCountChanged();
        NotifySelectionChanged();

        DeleteRemovedGameData(toRemove.Select(c => c.Game).ToList());
        _storageService.DeletePendingRemoval();
        LoggingService.Info("Library", $"Removed {toRemove.Count} {launcher} game(s) from the library after the {launcher} integration was turned off.");
        return toRemove.Count;
    }

    /// <summary>
    /// Provenance, not launch method: an entry belongs to a platform's integration when it was
    /// imported (or later linked) by it - <see cref="GameEntry.ImportedFrom"/>. Entries from
    /// before that field existed fall back to the launch flags, which is what set them at the
    /// time; a hand-dropped Steam shortcut marked "launch via Steam" in Edit Game after this
    /// change has ImportedFrom == null and is therefore never swept.
    /// </summary>
    private static bool IsPlatformGame(GameEntry game, DetectedLauncher launcher)
    {
        if (game.ImportedFrom is { } from)
        {
            return from == ToPlatform(launcher);
        }

        return launcher switch
        {
            DetectedLauncher.Steam => game.IsSteamGame,
            DetectedLauncher.Gog => game.IsGogGame,
            DetectedLauncher.Ea => game.IsEaGame,
            DetectedLauncher.Epic => game.IsEpicGame,
            DetectedLauncher.Ubisoft => game.IsUbisoftGame,
            DetectedLauncher.Xbox => game.IsXboxGame,
            DetectedLauncher.BattleNet => game.IsBattleNetGame,
            _ => false
        };
    }

    private static LauncherPlatform ToPlatform(DetectedLauncher launcher) => launcher switch
    {
        DetectedLauncher.Steam => LauncherPlatform.Steam,
        DetectedLauncher.Gog => LauncherPlatform.Gog,
        DetectedLauncher.Ea => LauncherPlatform.Ea,
        DetectedLauncher.Epic => LauncherPlatform.Epic,
        DetectedLauncher.Xbox => LauncherPlatform.Xbox,
        DetectedLauncher.BattleNet => LauncherPlatform.BattleNet,
        _ => LauncherPlatform.Ubisoft
    };

    /// <summary>The pointer is over the undo toast, or it has keyboard focus: hold the window open.</summary>
    public void SetUndoToastHeld(bool held) => _undoTimer.Hold(held);

    public void UndoDelete()
    {
        _undoTimer.Stop();
        IsUndoToastVisible = false;

        if (_lastRemoved.Count == 0) return;

        // Ascending index order so each insert lands at its original slot relative to the
        // games already put back before it.
        var restored = new List<GameCardViewModel>();
        foreach (var (game, index) in _lastRemoved.OrderBy(r => r.Index))
        {
            var card = CreateCardViewModel(game);
            if (index >= 0 && index <= Games.Count)
            {
                Games.Insert(index, card);
            }
            else
            {
                Games.Add(card);
            }
            restored.Add(card);
        }
        _lastRemoved.Clear();

        RebuildCategories();
        SaveLibrary();
        // After the save, not before: a crash in between finds the games back in the library and
        // skips them, where the other order would find nothing and have lost them.
        _storageService.DeletePendingRemoval();
        UpdateHotkeys();
        ApplySort();

        string what = restored.Count == 1 ? $"\"{restored[0].Name}\"" : $"{restored.Count} games";
        LoggingService.Info("Library", $"Undid removal of {what}.");
        StatusMessage = $"Restored {what} to library.";
        NotifyGameCountChanged();
    }

    /// <summary>
    /// Deletes what removed games leave behind outside games.json: their cached icon and poster
    /// files (including art they no longer pointed at) and their Steam/RAWG details. The files
    /// are always TrayTrigger-owned copies in the icon/cover cache folders - even "Change
    /// Icon"/"Change Cover" copy the chosen file in - so a game's installed files or a user's
    /// original image are never touched. Anything a remaining entry still uses (the same Steam
    /// App ID added twice, a Steam entry plus a local exe entry) is kept; otherwise re-adding the
    /// game would be served cached details pointing at a poster that was just deleted.
    /// </summary>
    private void DeleteRemovedGameData(IReadOnlyCollection<GameEntry> removed)
    {
        if (removed.Count == 0) return;

        var result = GameDataCleanup.DeleteRemovedGameData(
            removed,
            Games.Select(c => c.Game).ToList(),
            _storageService.IconsDirectory,
            _storageService.CoversDirectory);
        LoggingService.Verbose("Library", $"Cleaned up after {removed.Count} removed game(s): {result}.");

        UndoDlssOverridesOf(removed);
    }

    /// <summary>
    /// The DLSS override service. A property rather than a constructor argument, as on
    /// <see cref="ProcessLauncherService"/>; tests substitute a fake backend.
    /// </summary>
    internal DlssOverrideService DlssOverrides { get; set; } = new();

    /// <summary>How many games have the DLSS Override on. Read by the System page, possibly off the UI thread.</summary>
    public int DlssOverrideGameCount
    {
        get
        {
            try { return Games.Count(c => c.Game.DlssSettings.Count > 0); }
            catch (InvalidOperationException) { return 0; }   // the library changed mid-count
        }
    }

    /// <summary>Puts every game's DLSS Override back and returns a line for the status bar.</summary>
    public string RestoreAllDlssOverrides()
    {
        var result = DlssOverrides.RestoreAll(Games.Select(c => c.Game).ToList());
        if (result.Games == 0) return "No game has the DLSS Override on.";

        SaveGamesOnly();
        string games = result.Games == 1 ? "1 game" : $"{result.Games} games";
        return result.Failed == 0
            ? $"DLSS Override put back for {games}."
            : $"DLSS Override put back for {result.Games - result.Failed} of {games}. The rest could not be changed - see the log.";
    }

    /// <summary>
    /// Puts back the driver settings TrayTrigger wrote for games that are now gone for good. The
    /// ownership records leave with the library entry, and they are the only thing that can undo
    /// the override - so without this, removing a game would leave its settings, and any profile
    /// TrayTrigger created for it, in NVIDIA's database with nothing able to take them out again.
    ///
    /// <para>Here rather than in <see cref="RemoveGames"/>, for the same reason the cached files
    /// are deleted here: inside the undo window the game may yet come back, records and all.</para>
    ///
    /// <para>A setting another library entry also holds a record for - two entries for the same
    /// executable - is left alone. It is still in use, and that entry's own undo will restore it.</para>
    /// </summary>
    private void UndoDlssOverridesOf(IReadOnlyCollection<GameEntry> removed)
    {
        var stillHeld = Games
            .SelectMany(c => c.Game.DlssSettings)
            .Select(r => (r.ApplicationName.ToLowerInvariant(), r.SettingId))
            .ToHashSet();

        foreach (var game in removed)
        {
            var records = game.DlssSettings
                .Where(r => !stillHeld.Contains((r.ApplicationName.ToLowerInvariant(), r.SettingId)))
                .ToList();
            if (records.Count == 0) continue;

            try
            {
                var undone = DlssOverrides.Undo(records);
                game.DlssSettings.Clear();
                game.DlssSettings.AddRange(undone.Records);

                // Settings something else has since changed are no longer TrayTrigger's to undo, so
                // only a failure leaves anything behind that should not be there.
                var failed = undone.Details.Where(d => d.Outcome == DlssSettingOutcome.Failed).ToList();
                if (!undone.Succeeded || failed.Count > 0)
                {
                    string why = undone.Error ?? failed.FirstOrDefault()?.Error ?? "the driver refused";
                    string ids = string.Join(", ", undone.Records.Select(r => $"0x{r.SettingId:X8}"));
                    LoggingService.Warn("Library", $"Could not put back the DLSS override for removed game '{game.Name}' ({why}). Still set on profile '{records[0].ProfileName}' for {records[0].ApplicationName}: {ids}.");
                }
                else
                {
                    LoggingService.Info("Library", $"Put back the DLSS override for removed game '{game.Name}'.");
                }
            }
            catch (Exception ex)
            {
                // Removing a game must never fail because of this.
                LoggingService.Warn("Library", $"Undoing the DLSS override for removed game '{game.Name}' threw: {ex.Message}");
            }
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

    /// <summary>
    /// Persists the library without touching settings.json. <see cref="SaveLibrary"/> writes both
    /// because the import paths that call it do change settings - scan locations, the one-time
    /// prompt flags - but a game starting or exiting changes only the game itself (LastPlayed,
    /// cumulative playtime). Writing settings there cost a full encrypt-and-replace twice per
    /// session for a file whose contents had not moved.
    /// </summary>
    public void SaveGamesOnly([CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
    {
        _storageService.SaveGames(Games.Select(g => g.Game), callerMember, callerFile);
        NotifyLibraryUpdated();
    }

    /// <summary>Every game's launch hotkey, for the one place that registers all of TrayTrigger's hotkeys.</summary>
    public IEnumerable<HotkeyBinding> HotkeyBindings => Games.Select(g => HotkeyBinding.ForGame(g.Game));

    /// <summary>
    /// Set by MainViewModel, which registers games' and tools' hotkeys together. A library used on
    /// its own (the tests) leaves it null and registers its games directly.
    /// </summary>
    public Action? RefreshHotkeys { get; set; }

    public void UpdateHotkeys()
    {
        if (RefreshHotkeys != null)
        {
            RefreshHotkeys();
            return;
        }
        _hotkeyManager.RegisterHotkeys(_settings.GlobalManageHotkey, HotkeyBindings, trayMenuHotkeyStr: _settings.TrayMenuHotkey);
    }

    public void OnGameUpdatedFromLauncher(GameEntry game)
    {
        RunOnUiThread(() =>
        {
            var card = Games.FirstOrDefault(g => g.Id == game.Id);
            card?.RefreshProperties();
            SaveGamesOnly();
            ApplySort();
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

        // Categories are deduped case-insensitively above, so the kept spelling can differ from
        // the selected one ("RPG" vs "rpg"); match the same way.
        string? kept = Categories.FirstOrDefault(c => string.Equals(c, previous, StringComparison.OrdinalIgnoreCase));
        if (kept != null)
        {
            _selectedCategory = kept;
        }
        else
        {
            _selectedCategory = LibraryConstants.AllCategory;
            _settings.LastCategoryFilter = LibraryConstants.AllCategory;
            FilteredGames.Refresh();
        }
        OnPropertyChanged(nameof(SelectedCategory));

        RebuildCategoryTabs();
        // Same trigger points as the category tabs, and for the same reason: the Launcher options
        // only list launchers the library actually contains. The refresh afterwards is not
        // optional: this is where a launcher tick saved from the last run is re-applied (its
        // option did not exist until now), and where one whose launcher has left the library is
        // dropped. Either changes what should be on screen.
        Filter.RebuildLauncherOptions(Games);
        FilteredGames.Refresh();
        OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
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

        // Flyout filters: launcher, performance profile, per-entry state. AND'd with the tab and
        // the search box, so they narrow what those already chose rather than reaching past them.
        if (!Filter.Matches(card)) return false;

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
