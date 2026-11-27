using System;
using System.Collections;
using System.Windows.Data;
using System.Windows.Input;
using TrayTrigger.Models;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The library's order: the sort box's option, and whether it runs the other way (the button beside the box, or a
/// second click on the same Details List column header, the way Explorer's columns work). One comparer does every
/// sort, so a game with nothing to sort by - never played, no size yet - stays at the end in both directions.
/// </summary>
public partial class LibraryViewModel
{
    public const string SortAlphabetical = "Alphabetical (A - Z)";
    public const string SortAlphabeticalDescending = "Alphabetical (Z - A)";
    public const string SortCategory = "Category (A - Z)";
    public const string SortMostRecentlyPlayed = "Most Recently Played";
    public const string SortCumulativePlaytime = "Cumulative Playtime";

    /// <summary>The Details List columns a header click sorts by.</summary>
    public const string ColumnTitle = "Title";
    public const string ColumnCategory = "Category";
    public const string ColumnPlaytime = "Playtime";
    public const string ColumnLastPlayed = "LastPlayed";
    public const string ColumnSize = "Size";

    /// <summary>The sort box's option runs the other way. Saved, like the option itself.</summary>
    public bool IsSortReversed
    {
        get => _settings.LastSortReversed;
        set
        {
            if (_settings.LastSortReversed == value) return;
            _settings.LastSortReversed = value;
            _storageService.SaveSettings(_settings);
            ApplySort();
        }
    }

    /// <summary>The order runs smallest to largest, A to Z, oldest to newest: what the ▲ means.</summary>
    public bool IsSortAscending => IsNaturallyAscending(SelectedSortOption) ^ IsSortReversed;

    /// <summary>The button beside the sort box: Segoe's Up or Down arrow for the order as it runs now.</summary>
    public string SortDirectionGlyph => IsSortAscending ? "" : "";
    public string SortDirectionToolTip => IsSortAscending ? "Ascending order. Click to reverse it" : "Descending order. Click to reverse it";

    public string TitleSortArrow => ArrowFor(ColumnTitle);
    public string CategorySortArrow => ArrowFor(ColumnCategory);
    public string PlaytimeSortArrow => ArrowFor(ColumnPlaytime);
    public string LastPlayedSortArrow => ArrowFor(ColumnLastPlayed);
    public string SizeSortArrow => ArrowFor(ColumnSize);

    private ICommand? _reverseSortCommand;
    public ICommand ReverseSortCommand => _reverseSortCommand ??= new RelayCommand(() => IsSortReversed = !IsSortReversed);

    private ICommand? _sortByColumnCommand;
    public ICommand SortByColumnCommand => _sortByColumnCommand ??= new RelayCommand(p => SortByColumn(p as string));

    /// <summary>A column header was clicked: sort by it, or, when the list already is, turn the order round.</summary>
    internal void SortByColumn(string? column)
    {
        string? option = OptionForColumn(column);
        if (option == null) return;
        if (ColumnOf(SelectedSortOption) == column)
        {
            IsSortReversed = !IsSortReversed;
            return;
        }
        // A new column starts the way it reads most usefully (A to Z, most played, newest, largest).
        _settings.LastSortReversed = false;
        if (SelectedSortOption == option) ApplySort();
        else SelectedSortOption = option;
    }

    internal static string? OptionForColumn(string? column) => column switch
    {
        ColumnTitle => SortAlphabetical,
        ColumnCategory => SortCategory,
        ColumnPlaytime => SortCumulativePlaytime,
        ColumnLastPlayed => SortMostRecentlyPlayed,
        ColumnSize => SortLargestFirst,
        _ => null,
    };

    /// <summary>The Details List column a sort option orders by, or null (the Favorites First sorts have none).</summary>
    internal static string? ColumnOf(string? option) => option switch
    {
        SortAlphabetical or SortAlphabeticalDescending => ColumnTitle,
        SortCategory => ColumnCategory,
        SortCumulativePlaytime => ColumnPlaytime,
        SortMostRecentlyPlayed => ColumnLastPlayed,
        SortLargestFirst => ColumnSize,
        _ => null,
    };

    /// <summary>Which way an option runs before it is reversed: names A to Z; playtime, last played and size biggest
    /// or newest first. The Favorites First sorts say it of the order after the favorites.</summary>
    internal static bool IsNaturallyAscending(string? option) =>
        option is SortAlphabetical or SortCategory or "Favorites First (A - Z)";

    private string ArrowFor(string column) =>
        ColumnOf(SelectedSortOption) == column ? (IsSortAscending ? "▲" : "▼") : string.Empty;

    /// <summary>Puts the comparer for the sort box's option and direction on the list, and tells the arrows.</summary>
    private void ApplySortOrder()
    {
        if (FilteredGames is ListCollectionView view)
        {
            view.SortDescriptions.Clear();
            view.CustomSort = new GameSortComparer(SelectedSortOption, IsSortReversed);
        }
        foreach (string name in new[] { nameof(IsSortReversed), nameof(IsSortAscending), nameof(SortDirectionGlyph), nameof(SortDirectionToolTip),
                     nameof(TitleSortArrow), nameof(CategorySortArrow), nameof(PlaytimeSortArrow), nameof(LastPlayedSortArrow), nameof(SizeSortArrow) })
            OnPropertyChanged(name);
    }
}

/// <summary>
/// Every library sort as one comparison: the favorites first where the option asks for it, then the option's key in
/// its direction (turned round when reversed), with a game that has no value for the key - never played, no size yet,
/// no playtime - after every game that has one in both directions, then the name A to Z.
/// </summary>
internal sealed class GameSortComparer(string? option, bool reversed) : IComparer
{
    public int Compare(object? x, object? y) =>
        x is GameCardViewModel a && y is GameCardViewModel b ? Compare(a.Game, b.Game) : 0;

    internal int Compare(GameEntry a, GameEntry b)
    {
        int direction = LibraryViewModel.IsNaturallyAscending(option) ^ reversed ? 1 : -1;
        int result = option switch
        {
            "Favorites First (A - Z)" or "Favorites First (Z - A)" => Then(Favorites(a, b), () => direction * ByName(a, b)),
            LibraryViewModel.SortFavoritesFirstRecent => Then(Favorites(a, b), () => WithBlanksLast(a.LastPlayed, b.LastPlayed, direction)),
            LibraryViewModel.SortCategory => direction * StringComparer.CurrentCultureIgnoreCase.Compare(a.Category ?? string.Empty, b.Category ?? string.Empty),
            LibraryViewModel.SortCumulativePlaytime => WithBlanksLast(Positive(a.CumulativePlaytimeMinutes), Positive(b.CumulativePlaytimeMinutes), direction),
            LibraryViewModel.SortMostRecentlyPlayed => WithBlanksLast(a.LastPlayed, b.LastPlayed, direction),
            LibraryViewModel.SortLargestFirst => WithBlanksLast(a.InstallSizeBytes is > 0 ? a.InstallSizeBytes : null, b.InstallSizeBytes is > 0 ? b.InstallSizeBytes : null, direction),
            _ => direction * ByName(a, b),
        };
        return result != 0 ? result : ByName(a, b);
    }

    private static int ByName(GameEntry a, GameEntry b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty);

    private static int Favorites(GameEntry a, GameEntry b) => b.IsFavorite.CompareTo(a.IsFavorite);

    private static int Then(int first, Func<int> next) => first != 0 ? first : next();

    private static long? Positive(long value) => value > 0 ? value : null;

    private static int WithBlanksLast<T>(T? a, T? b, int direction) where T : struct, IComparable<T> =>
        a is { } x ? (b is { } y ? direction * x.CompareTo(y) : -1) : (b is null ? 0 : 1);
}
