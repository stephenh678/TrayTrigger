using TrayTrigger.Models;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The Library's order: every sort through one comparer, turned round by the direction button or a second click on a
/// Details List column header, with games that have nothing to sort by kept at the end both ways.
/// </summary>
public class LibrarySortTests
{
    private static GameEntry Game(string name, string category = "Action", long playtime = 0, DateTime? played = null, long? size = null, bool favorite = false) =>
        new() { Name = name, Category = category, CumulativePlaytimeMinutes = playtime, LastPlayed = played, InstallSizeBytes = size, IsFavorite = favorite };

    private static List<string> Sorted(string option, bool reversed, params GameEntry[] games)
    {
        var comparer = new GameSortComparer(option, reversed);
        var list = games.ToList();
        list.Sort(comparer.Compare);
        return list.Select(g => g.Name).ToList();
    }

    [Fact]
    public void Size_IsLargestFirst_OrSmallestReversed_WithUncountedGamesLastBothWays()
    {
        var games = new[] { Game("Small", size: 10), Game("None"), Game("Big", size: 1_000), Game("Zero", size: 0) };

        Assert.Equal(["Big", "Small", "None", "Zero"], Sorted(LibraryViewModel.SortLargestFirst, false, games));
        Assert.Equal(["Small", "Big", "None", "Zero"], Sorted(LibraryViewModel.SortLargestFirst, true, games));
    }

    [Fact]
    public void Playtime_AndLastPlayed_KeepNeverPlayedLast()
    {
        var day = new DateTime(2026, 11, 1);
        var games = new[] { Game("Never"), Game("Old", playtime: 5, played: day), Game("New", playtime: 50, played: day.AddDays(5)) };

        Assert.Equal(["New", "Old", "Never"], Sorted(LibraryViewModel.SortCumulativePlaytime, false, games));
        Assert.Equal(["Old", "New", "Never"], Sorted(LibraryViewModel.SortCumulativePlaytime, true, games));
        Assert.Equal(["New", "Old", "Never"], Sorted(LibraryViewModel.SortMostRecentlyPlayed, false, games));
        Assert.Equal(["Old", "New", "Never"], Sorted(LibraryViewModel.SortMostRecentlyPlayed, true, games));
    }

    [Fact]
    public void Names_AndCategories_RunAToZ_OrZToAReversed()
    {
        var games = new[] { Game("Bravo", "RPG"), Game("Alpha", "Shooter"), Game("Charlie", "Action") };

        Assert.Equal(["Alpha", "Bravo", "Charlie"], Sorted(LibraryViewModel.SortAlphabetical, false, games));
        Assert.Equal(["Charlie", "Bravo", "Alpha"], Sorted(LibraryViewModel.SortAlphabetical, true, games));
        Assert.Equal(["Charlie", "Bravo", "Alpha"], Sorted(LibraryViewModel.SortAlphabeticalDescending, false, games));
        Assert.Equal(["Charlie", "Bravo", "Alpha"], Sorted(LibraryViewModel.SortCategory, false, games));
        Assert.Equal(["Alpha", "Bravo", "Charlie"], Sorted(LibraryViewModel.SortCategory, true, games));
    }

    [Fact]
    public void FavoritesStayFirst_WhenTheRestIsReversed()
    {
        var games = new[] { Game("Alpha"), Game("Zulu", favorite: true), Game("Mike") };

        Assert.Equal(["Zulu", "Alpha", "Mike"], Sorted("Favorites First (A - Z)", false, games));
        Assert.Equal(["Zulu", "Mike", "Alpha"], Sorted("Favorites First (A - Z)", true, games));
    }

    [Theory]
    [InlineData(LibraryViewModel.ColumnTitle, LibraryViewModel.SortAlphabetical)]
    [InlineData(LibraryViewModel.ColumnCategory, LibraryViewModel.SortCategory)]
    [InlineData(LibraryViewModel.ColumnPlaytime, LibraryViewModel.SortCumulativePlaytime)]
    [InlineData(LibraryViewModel.ColumnLastPlayed, LibraryViewModel.SortMostRecentlyPlayed)]
    [InlineData(LibraryViewModel.ColumnSize, LibraryViewModel.SortLargestFirst)]
    public void EachColumnHeader_SortsByItsOwnOption(string column, string option)
    {
        Assert.Equal(option, LibraryViewModel.OptionForColumn(column));
        Assert.Equal(column, LibraryViewModel.ColumnOf(option));
    }

    [Fact]
    public void ZToA_IsTheTitleColumn_AndFavoritesFirstHasNone()
    {
        Assert.Equal(LibraryViewModel.ColumnTitle, LibraryViewModel.ColumnOf(LibraryViewModel.SortAlphabeticalDescending));
        Assert.Null(LibraryViewModel.ColumnOf(LibraryViewModel.SortFavoritesFirstRecent));
        Assert.Null(LibraryViewModel.OptionForColumn("Actions"));
    }
}
