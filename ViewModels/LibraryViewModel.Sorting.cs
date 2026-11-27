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
    // The sort box names what a sort orders by, never which way: the arrow beside it says that.
    public const string SortAlphabetical = "Name";
    public const string SortCategory = "Category";
    public const string SortFavoritesFirst = "Favorites First";
    public const string SortMostRecentlyPlayed = "Last Played";
    public const string SortCumulativePlaytime = "Playtime";
    /// <summary>Aggressive, then Optimized; games with the profile Off last in both directions.</summary>
    public const string SortPerformanceProfile = "Performance Profile";

    /// <summary>
    /// A saved sort as the sort box names it now, and whether it runs reversed. The names before 2.0.0-beta.2 carried
    /// their direction ("Alphabetical (Z - A)"); each becomes its sort, reversed where the old one ran the other way,
    /// so the Library opens in the same order. A name the box doesn't offer is Name, A to Z.
    /// </summary>
    internal static (string Option, bool Reversed) MigrateSortOption(string? saved, bool savedReversed)
    {
        (string option, bool descending)? legacy = saved?.Trim() switch
        {
            "Alphabetical (A - Z)" => (SortAlphabetical, false),
            "Alphabetical (Z - A)" => (SortAlphabetical, true),
            "Category (A - Z)" => (SortCategory, false),
            "Favorites First (A - Z)" => (SortFavoritesFirst, false),
            "Favorites First (Z - A)" => (SortFavoritesFirst, true),
            "Most Recently Played" => (SortMostRecentlyPlayed, false),
            "Cumulative Playtime" => (SortCumulativePlaytime, false),
            "Largest First (Size on Disk)" => (SortLargestFirst, false),
            _ => null,
        };
        if (legacy is { } old) return (old.option, old.descending ^ savedReversed);
        return saved is SortAlphabetical or SortCategory or SortFavoritesFirst or SortFavoritesFirstRecent
            or SortMostRecentlyPlayed or SortCumulativePlaytime or SortLargestFirst or SortPerformanceProfile
            ? (saved, savedReversed)
            : (SortAlphabetical, false);
    }

    /// <summary>The Details List columns a header click sorts by.</summary>
    public const string ColumnTitle = "Title";
    public const string ColumnCategory = "Category";
    public const string ColumnProfile = "Profile";
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
    public string ProfileSortArrow => ArrowFor(ColumnProfile);
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
        ColumnProfile => SortPerformanceProfile,
        ColumnPlaytime => SortCumulativePlaytime,
        ColumnLastPlayed => SortMostRecentlyPlayed,
        ColumnSize => SortLargestFirst,
        _ => null,
    };

    /// <summary>The Details List column a sort option orders by, or null (the Favorites First sorts have none).</summary>
    internal static string? ColumnOf(string? option) => option switch
    {
        SortAlphabetical => ColumnTitle,
        SortCategory => ColumnCategory,
        SortPerformanceProfile => ColumnProfile,
        SortCumulativePlaytime => ColumnPlaytime,
        SortMostRecentlyPlayed => ColumnLastPlayed,
        SortLargestFirst => ColumnSize,
        _ => null,
    };

    /// <summary>Which way an option runs before it is reversed: names A to Z; playtime, last played, size and profile
    /// biggest, newest or strongest first. The Favorites First sorts say it of the order after the favorites.</summary>
    internal static bool IsNaturallyAscending(string? option) =>
        option is SortAlphabetical or SortCategory or SortFavoritesFirst;

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
                     nameof(TitleSortArrow), nameof(CategorySortArrow), nameof(ProfileSortArrow), nameof(PlaytimeSortArrow), nameof(LastPlayedSortArrow), nameof(SizeSortArrow) })
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
            LibraryViewModel.SortFavoritesFirst => Then(Favorites(a, b), () => direction * ByName(a, b)),
            LibraryViewModel.SortFavoritesFirstRecent => Then(Favorites(a, b), () => WithBlanksLast(a.LastPlayed, b.LastPlayed, direction)),
            LibraryViewModel.SortCategory => direction * StringComparer.CurrentCultureIgnoreCase.Compare(a.Category ?? string.Empty, b.Category ?? string.Empty),
            LibraryViewModel.SortCumulativePlaytime => WithBlanksLast(Positive(a.CumulativePlaytimeMinutes), Positive(b.CumulativePlaytimeMinutes), direction),
            LibraryViewModel.SortMostRecentlyPlayed => WithBlanksLast(a.LastPlayed, b.LastPlayed, direction),
            LibraryViewModel.SortPerformanceProfile => WithBlanksLast(ProfileRank(a), ProfileRank(b), direction),
            LibraryViewModel.SortLargestFirst => WithBlanksLast(a.InstallSizeBytes is > 0 ? a.InstallSizeBytes : null, b.InstallSizeBytes is > 0 ? b.InstallSizeBytes : null, direction),
            _ => direction * ByName(a, b),
        };
        return result != 0 ? result : ByName(a, b);
    }

    private static int ByName(GameEntry a, GameEntry b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty);

    private static int Favorites(GameEntry a, GameEntry b) => b.IsFavorite.CompareTo(a.IsFavorite);

    private static int Then(int first, Func<int> next) => first != 0 ? first : next();

    private static long? Positive(long value) => value > 0 ? value : null;

    /// <summary>Off has nothing to sort by, so it stays last like a game never played.</summary>
    private static int? ProfileRank(GameEntry game) => game.PerformanceProfile == PerformanceProfileMode.Off ? null : (int)game.PerformanceProfile;

    private static int WithBlanksLast<T>(T? a, T? b, int direction) where T : struct, IComparable<T> =>
        a is { } x ? (b is { } y ? direction * x.CompareTo(y) : -1) : (b is null ? 0 : 1);
}
