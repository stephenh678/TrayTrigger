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
    /// <summary>An icon before the name: the Tools page's Start with Games and Close with Games tabs. Blank on the Library's own tabs.</summary>
    public string Glyph { get; }
    public bool HasGlyph => Glyph.Length > 0;

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

    public CategoryTabItem(string name, string displayName, bool isSelected, Action<string> onSelect, string glyph = "")
    {
        Name = name;
        DisplayName = displayName;
        IsFavoritesTab = string.Equals(name, LibraryConstants.FavoritesCategory, StringComparison.OrdinalIgnoreCase);
        IsHiddenTab = string.Equals(name, LibraryConstants.HiddenCategory, StringComparison.OrdinalIgnoreCase);
        Glyph = glyph ?? string.Empty;
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
public partial class LibraryViewModel : ViewModelBase
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
    /// <summary>Favorites, then everything else, each most recently played first; never-played games
    /// last, alphabetically - "Most Recently Played" within each half.</summary>
    public const string SortFavoritesFirstRecent = "Favorites First (Recent)";

    public ObservableCollection<string> SortOptions { get; } = new()
    {
        "Alphabetical (A - Z)",
        "Alphabetical (Z - A)",
        "Favorites First (A - Z)",
        "Favorites First (Z - A)",
        SortFavoritesFirstRecent,
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

        Filter = new LibraryFilterViewModel(OnFilterChanged,
            saved: _settings.SavedLibraryFilters,
            onSavedChanged: filters =>
            {
                _settings.SavedLibraryFilters = filters.ToList();
                _storageService.SaveSettings(_settings);
            });
        Filter.RestoreKeys(_settings.LibraryFilterKeys);
        Filter.CountProvider = CountForFilterSummary;

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
                NoteAvailabilityChanges(results.Select(r => (r.Card, r.Availability)));

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
    /// Notes in Activity &amp; History the games that stopped being installed, or came back, since
    /// TrayTrigger last looked - one line each way per pass. A game never checked before is only
    /// remembered, so the first run after an update doesn't list every game already uninstalled.
    /// On the UI thread, which owns the library.
    /// </summary>
    internal void NoteAvailabilityChanges(IEnumerable<(GameCardViewModel Card, GameAvailability Availability)> results)
    {
        var lost = new List<string>();
        var found = new List<string>();
        bool changed = false;
        foreach (var (card, availability) in results)
        {
            bool installed = availability == GameAvailability.Available;
            bool? was = card.Game.LastKnownInstalled;
            if (was == installed) continue;
            card.Game.LastKnownInstalled = installed;
            // A first value rides along with the next save; only a real change is worth a write.
            if (was == null) continue;
            changed = true;
            if (!card.Game.IsHidden) (installed ? found : lost).Add(card.Game.Name);
        }
        if (lost.Count > 0)
        {
            ActivityService.Add(ActivityLevel.Activity,
                lost.Count == 1 ? $"{lost[0]} is no longer installed" : $"{lost.Count} games are no longer installed",
                detail: $"{string.Join(", ", lost)}. They're kept in the library, greyed out, with their playtime and settings.");
        }
        if (found.Count > 0)
        {
            ActivityService.Add(ActivityLevel.Activity,
                found.Count == 1 ? $"{found[0]} is installed again" : $"{found.Count} games are installed again",
                detail: string.Join(", ", found));
        }
        if (changed) SaveGamesOnly();
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
                    NoteAvailabilityChanges(results.Select(r => (r.Card, r.Availability)));

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
            onToggleSuspend: ToggleSuspend,
            onRestoreSettings: RestoreSettingsNow,
            onPrimaryClick: OnCardPrimaryClick,
            onToggleSelect: OnCardToggleSelect,
            onRangeSelect: OnCardRangeSelect,
            // The quick settings change the game entry and nothing else, so this is the narrow
            // save - no settings.json rewrite for a menu tick.
            onQuickSettingChanged: _ => SaveGamesOnly(),
            onToggleDlssOverride: card => _ = ToggleDlssOverrideAsync(card)
        );
        // Sessions outlive library reloads (a rescan while a game is running), so a fresh card
        // must pick up the live state rather than wait for the next SessionStarted event.
        card.IsPlaying = _launcherService.IsSessionActive(game.Id);
        card.IsSuspended = _launcherService.IsGameSuspended(game.Id);
        return card;
    }

    /// <summary>Fired by ProcessLauncherService (background thread) when a game is suspended or resumed.</summary>
    public void OnSessionSuspendChanged(ActiveGameSession session)
    {
        bool suspended = session.IsSuspended;
        RunOnUiThread(() =>
        {
            var card = Games.FirstOrDefault(g => g.Id == session.GameId);
            if (card != null) card.IsSuspended = suspended;
        });
    }

    /// <summary>
    /// "Suspend Game" / "Resume Game" from a card's menu. Off the UI thread: it lists the game's
    /// processes and talks to the audio engine.
    /// </summary>
    private void ToggleSuspend(GameCardViewModel card)
    {
        string gameId = card.Game.Id;
        bool resume = card.IsSuspended;
        _ = Task.Run(() =>
        {
            var outcome = resume ? _launcherService.ResumeGame(gameId) : _launcherService.SuspendGame(gameId);
            RunOnUiThread(() => StatusMessage = outcome.Result == ProcessLauncherService.SuspendResult.Suspended
                ? $"{outcome.Message} Resume it from here or the tray menu."
                : outcome.Message);
        });
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

    /// <summary>"Restore Settings Now": puts Windows back as it was and stops tracking, but leaves the
    /// game running. Off the UI thread, as above.</summary>
    private void RestoreSettingsNow(GameCardViewModel card)
    {
        StatusMessage = $"Restoring settings for {card.Name}...";
        string gameId = card.Game.Id;
        string name = card.Name;
        _ = Task.Run(() =>
        {
            bool ended = _launcherService.EndSessionNow(gameId, forceCloseGame: false);
            RunOnUiThread(() => StatusMessage = ended ? $"Settings restored for {name}." : $"{name} has no active session.");
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
        dlg.ApplySavedSize(_settings.GameDetailsWidth, _settings.GameDetailsHeight);
        if (openMatchPicker) dlg.OpenMatchPickerWhenReady();
        dlg.ShowDialog();
        RememberGameDetailsSize(dlg);

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

    private readonly HashSet<string> _launchesInFlight = new(StringComparer.Ordinal);

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

    /// <summary>Keeps the size the player left the details window at, for the next one to open at.</summary>
    private void RememberGameDetailsSize(Views.GameDetailsDialog dlg)
    {
        var size = dlg.SizeToRemember;
        double? width = size?.Width, height = size?.Height;
        if (_settings.GameDetailsWidth == width && _settings.GameDetailsHeight == height) return;
        _settings.GameDetailsWidth = width;
        _settings.GameDetailsHeight = height;
        _storageService.SaveSettings(_settings);
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

}
