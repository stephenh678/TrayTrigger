using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>One line of the Collections dialog: a collection, and whether the games being edited are in it.</summary>
public sealed class CollectionChoice : ViewModelBase
{
    public string Name { get; }

    /// <summary>True: every game is in it. False: none is. Null: some are (a multi-selection).</summary>
    public bool? Initial { get; }

    private bool? _isChecked;
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>In for some of the selected games only (the dialog shows it as "some games").</summary>
    public bool IsMixed => IsChecked == null;

    public CollectionChoice(string name, bool? initial)
    {
        Name = name;
        Initial = initial;
        _isChecked = initial;
    }
}

/// <summary>
/// Collections: a manual grouping, any number per game, shown as a row of chips below the category tabs.
/// A collection exists only through the games that name it - there is no list of empty ones to keep tidy -
/// so making one means putting a game in it, and the chip goes when its last game leaves. TrayTrigger never
/// fills one; categories (one per game, from store data) are separate and untouched.
/// </summary>
public partial class LibraryViewModel
{
    public const int MaxCollectionNameLength = 40;

    private string _selectedCollection = string.Empty;

    /// <summary>The chips, "All" first; empty when no game is in any collection (the row is then hidden).</summary>
    public ObservableCollection<CategoryTabItem> CollectionTabs { get; } = new();

    public bool HasCollections => CollectionTabs.Count > 0;

    /// <summary>A collection chip (not All Collections) is selected: the empty-state panels then say so, since a game outside it may match the tab.</summary>
    public bool IsCollectionSelected => _selectedCollection.Length > 0;

    /// <summary>The collection chip the library is narrowed to; blank means all collections.</summary>
    public string SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            value = value?.Trim() ?? string.Empty;
            if (_selectedCollection == value) return;
            _selectedCollection = value;
            _settings.LastCollectionFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCollectionSelected));
            ClearSelection();
            foreach (var tab in CollectionTabs)
            {
                tab.IsSelected = string.Equals(tab.Name, value, StringComparison.OrdinalIgnoreCase);
            }
            FilteredGames.Refresh();
            OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
            _storageService.SaveSettings(_settings);
        }
    }

    /// <summary>Select mode's "Collections...", and a card's menu item: the window shows the dialog for these cards.</summary>
    public event Action<List<GameCardViewModel>>? RequestCollections;

    /// <summary>A collection was renamed (old name, new name): tools limited to it follow.</summary>
    public event Action<string, string>? CollectionRenamed;

    /// <summary>A collection was deleted: tools limited to it drop it, and stop starting or closing with games if it was their only one.</summary>
    public event Action<string>? CollectionDeleted;

    private ICommand? _cmdBatchCollectionsCommand;
    public ICommand BatchCollectionsCommand => _cmdBatchCollectionsCommand ??= new RelayCommand(() =>
    {
        var cards = SelectedCards;
        if (cards.Count > 0) RequestCollections?.Invoke(cards);
    });

    internal static bool IsInCollection(GameEntry game, string collection)
        => game.Collections != null && game.Collections.Any(c => string.Equals(c, collection, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every collection any game is in, alphabetical, one spelling each - what the dialogs offer and what Edit Tool lists.</summary>
    public List<string> CollectionNames() => DistinctCollectionNames(Games.Select(g => g.Game));

    private static List<string> DistinctCollectionNames(IEnumerable<GameEntry> games)
        => games.SelectMany(g => g.Collections ?? new List<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public void RebuildCollectionTabs()
    {
        // A chip only for a collection with a game to show: hidden games never appear outside the Hidden tab.
        var names = DistinctCollectionNames(Games.Where(g => !g.Game.IsHidden).Select(g => g.Game));

        string? kept = names.FirstOrDefault(n => string.Equals(n, _selectedCollection, StringComparison.OrdinalIgnoreCase));
        if (_selectedCollection.Length > 0 && kept == null)
        {
            // Its last game left, or the saved chip's games are gone: fall back to all collections.
            _selectedCollection = string.Empty;
            _settings.LastCollectionFilter = string.Empty;
            _storageService.SaveSettings(_settings);
            FilteredGames.Refresh();
            OnPropertyChanged(nameof(IsEmptyBecauseOfFilters));
        }
        else if (kept != null)
        {
            _selectedCollection = kept;
        }

        CollectionTabs.Clear();
        if (names.Count > 0)
        {
            CollectionTabs.Add(new CategoryTabItem(string.Empty, "All Collections", _selectedCollection.Length == 0, SelectCollectionTab));
            foreach (var name in names)
            {
                CollectionTabs.Add(new CategoryTabItem(name, name, string.Equals(name, _selectedCollection, StringComparison.OrdinalIgnoreCase), SelectCollectionTab));
            }
        }
        OnPropertyChanged(nameof(SelectedCollection));
        OnPropertyChanged(nameof(IsCollectionSelected));
        OnPropertyChanged(nameof(HasCollections));
    }

    public void SelectCollectionTab(string collection) => SelectedCollection = collection;

    /// <summary>
    /// What is wrong with a new collection name, or null when it is fine: blank, too long, or the same as a
    /// category or a library tab - two chips of one name in two rows would read as one thing.
    /// </summary>
    public string? ValidateNewCollectionName(string name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0) return "Type a name for the collection.";
        if (name.Length > MaxCollectionNameLength) return $"Collection names can be up to {MaxCollectionNameLength} characters.";
        if (string.Equals(name, LibraryConstants.AllCategory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, LibraryConstants.FavoritesCategory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, LibraryConstants.HiddenCategory, StringComparison.OrdinalIgnoreCase)
            || Categories.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"\"{name}\" is already a category. Pick a different name for the collection.";
        }
        return null;
    }

    /// <summary>The rows for the Collections dialog: every collection, ticked for the games that are in it.</summary>
    public List<CollectionChoice> BuildCollectionChoices(IReadOnlyList<GameCardViewModel> cards)
    {
        var list = new List<CollectionChoice>();
        foreach (var name in CollectionNames())
        {
            int inCount = cards.Count(c => IsInCollection(c.Game, name));
            bool? state = inCount == 0 ? false : inCount == cards.Count ? true : null;
            list.Add(new CollectionChoice(name, state));
        }
        return list;
    }

    /// <summary>
    /// Puts the games in the collections named in <paramref name="add"/> and takes them out of those in
    /// <paramref name="remove"/>. A name matching an existing collection keeps that spelling.
    /// </summary>
    public void ApplyCollections(IReadOnlyList<GameCardViewModel> cards, IEnumerable<string> add, IEnumerable<string> remove)
    {
        if (cards.Count == 0) return;
        var existing = CollectionNames();
        string Canon(string n) => existing.FirstOrDefault(e => string.Equals(e, n, StringComparison.OrdinalIgnoreCase)) ?? n.Trim();
        var toAdd = add.Select(Canon).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var toRemove = remove.Select(Canon).ToList();

        bool changed = false;
        foreach (var card in cards)
        {
            // A new list each time, never an edit in place: a launch on its own thread reads the game's
            // collections (ToolCatalog.AppliesToGame) and would fail on a list changed under it.
            var list = (card.Game.Collections ?? new List<string>()).ToList();
            bool cardChanged = false;
            foreach (var name in toRemove)
            {
                cardChanged |= list.RemoveAll(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) > 0;
            }
            foreach (var name in toAdd)
            {
                if (list.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(name);
                cardChanged = true;
            }
            if (!cardChanged) continue;
            card.Game.Collections = list;
            changed = true;
        }
        if (!changed) return;

        SaveLibrary();
        RebuildCollectionTabs();
        FilteredGames.Refresh();
        LoggingService.Info("Library", $"Collections changed for {cards.Count} game(s): added {toAdd.Count}, removed {toRemove.Count}.");
        StatusMessage = cards.Count == 1 ? $"Updated collections for {cards[0].Name}" : $"Updated collections for {cards.Count} games";
        NotifySelectionChanged();

        // A collection exists while a game is in it: taking out its last game ends it, the same as deleting it. Tools
        // limited to it hear so, or they would stay limited to a collection no game can be in and never apply.
        var now = CollectionNames();
        foreach (var gone in existing.Where(e => !now.Any(n => string.Equals(n, e, StringComparison.OrdinalIgnoreCase))))
        {
            LoggingService.Info("Library", $"Collection '{gone}' has no games left, so it ends.");
            CollectionDeleted?.Invoke(gone);
        }
    }

    /// <summary>Renames a collection on every game in it; returns what is wrong, or null when it was renamed.</summary>
    public string? RenameCollection(string oldName, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return null;

        bool onlyCase = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!onlyCase)
        {
            string? problem = ValidateNewCollectionName(newName);
            if (problem != null) return problem;
            if (CollectionNames().Any(c => string.Equals(c, newName, StringComparison.OrdinalIgnoreCase)))
            {
                return $"There is already a collection called \"{newName}\".";
            }
        }
        else if (newName.Length == 0 || newName.Length > MaxCollectionNameLength)
        {
            return ValidateNewCollectionName(newName);
        }

        foreach (var card in Games)
        {
            var list = card.Game.Collections;
            if (list == null || !list.Any(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase))) continue;
            // A new list, not an edit in place (see ApplyCollections).
            card.Game.Collections = list
                .Select(c => string.Equals(c, oldName, StringComparison.OrdinalIgnoreCase) ? newName : c)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        bool wasSelected = string.Equals(_selectedCollection, oldName, StringComparison.OrdinalIgnoreCase);
        if (wasSelected)
        {
            _selectedCollection = newName;
            _settings.LastCollectionFilter = newName;
            _storageService.SaveSettings(_settings);
        }
        SaveLibrary();
        RebuildCollectionTabs();
        FilteredGames.Refresh();
        CollectionRenamed?.Invoke(oldName, newName);
        LoggingService.Info("Library", $"Collection '{oldName}' renamed to '{newName}'.");
        StatusMessage = $"Renamed collection to {newName}";
        return null;
    }

    /// <summary>Takes every game out of a collection. The games themselves are never touched.</summary>
    public void DeleteCollection(string name)
    {
        foreach (var card in Games)
        {
            var list = card.Game.Collections;
            if (list == null || !list.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) continue;
            // A new list, not an edit in place (see ApplyCollections).
            card.Game.Collections = list.Where(c => !string.Equals(c, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        SaveLibrary();
        RebuildCollectionTabs();
        FilteredGames.Refresh();
        CollectionDeleted?.Invoke(name);
        LoggingService.Info("Library", $"Collection '{name}' deleted; its games stay in the library.");
        StatusMessage = $"Deleted collection {name}";
    }
}
