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

/// <summary>Selecting several cards and editing them together.</summary>
public partial class LibraryViewModel
{
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
    private ICommand? _cmdBatchDlssOverrideCommand;
    /// <summary>The batch menu's DLSS Override; refuses a second click while a batch is running.</summary>
    public ICommand BatchDlssOverrideCommand => _cmdBatchDlssOverrideCommand ??= new RelayCommand(
        () => _ = BatchToggleDlssOverrideAsync(), () => !_isBatchDlssRunning);
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
    public bool BatchCpuAffinityIsAuto => BatchCpuAffinity == CpuAffinityMode.Auto;
    public bool BatchCpuAffinityIsPerformanceCores => BatchCpuAffinity == CpuAffinityMode.PerformanceCoresOnly;
    public bool BatchCpuAffinityIsVCache => BatchCpuAffinity == CpuAffinityMode.VCacheCores;
    public bool BatchCpuAffinityIsFrequency => BatchCpuAffinity == CpuAffinityMode.FrequencyCores;
    public bool BatchCpuAffinityIsOneCcd => BatchCpuAffinity == CpuAffinityMode.OneCcd;

    /// <summary>Every selected game already runs elevated - the batch menu's check. Mixed selections show no check, and a click turns every game on.</summary>
    public bool BatchAllRunAsAdmin => HasSelection && SelectedCards.All(c => c.Game.RunAsAdmin);
    /// <summary>Every selected game already closes its launcher on exit.</summary>
    public bool BatchAllCloseLauncher => HasSelection && SelectedCards.All(c => c.Game.CloseLauncherOnExit);
    /// <summary>Every selected game already has the DLSS Override on - the batch menu's check.</summary>
    public bool BatchAllDlssOverrideOn => HasSelection && SelectedCards.All(c => c.DlssOverrideOn);

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
        OnPropertyChanged(nameof(BatchCpuAffinityIsAuto));
        OnPropertyChanged(nameof(BatchCpuAffinityIsPerformanceCores));
        OnPropertyChanged(nameof(BatchCpuAffinityIsVCache));
        OnPropertyChanged(nameof(BatchCpuAffinityIsFrequency));
        OnPropertyChanged(nameof(BatchCpuAffinityIsOneCcd));
        OnPropertyChanged(nameof(BatchAllRunAsAdmin));
        OnPropertyChanged(nameof(BatchAllCloseLauncher));
        OnPropertyChanged(nameof(BatchAllDlssOverrideOn));
    }

    /// <summary>Same effect as picking the tier in Edit Game for each selected game. A game that
    /// is playing right now keeps its current session; the new tier applies from its next launch,
    /// exactly as an Edit Game save would.</summary>
    private void BatchSetProfile(PerformanceProfileMode mode)
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        var changed = new List<(GameEntry, PerformanceProfileMode)>();
        foreach (var card in cards)
        {
            if (card.Game.PerformanceProfile != mode) changed.Add((card.Game, card.Game.PerformanceProfile));
            card.Game.PerformanceProfile = mode;
            card.RefreshProperties();
        }
        SaveGamesOnly();
        LoggingService.Info("Library", $"{cards.Count} game(s) performance profile set to {mode} (batch).");
        PerformanceActivity.GamesChanged(changed, mode);
        StatusMessage = $"Set Performance Profile of {cards.Count} game(s) to {mode}";
        NotifySelectionChanged();
    }

    /// <summary>
    /// Same effect as picking the core mode in Edit Game for each selected game. A game already
    /// running keeps the cores it was launched with; the new mode applies from its next launch.
    /// </summary>
    private void BatchSetCpuAffinity(CpuAffinityMode mode)
    {
        var cards = SelectedCards;
        if (cards.Count == 0) return;
        var changed = new List<(GameEntry, CpuAffinityMode)>();
        foreach (var card in cards)
        {
            if (card.Game.CpuAffinity != mode) changed.Add((card.Game, card.Game.CpuAffinity));
            card.Game.CpuAffinity = mode;
            card.RefreshProperties();
        }
        SaveGamesOnly();
        LoggingService.Info("Library", $"{cards.Count} game(s) CPU cores set to {mode} (batch).");
        PerformanceActivity.GamesChanged(changed, mode);
        StatusMessage = mode == CpuAffinityMode.Default
            ? $"Set {cards.Count} game(s) back to all cores"
            : $"Set CPU Cores of {cards.Count} game(s) to {CpuTopology.MenuLabel(mode)}";
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
            "This will only remove the shortcuts from TrayTrigger. Your installed game files will not be deleted. You can undo this for 10 seconds.",
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
}
