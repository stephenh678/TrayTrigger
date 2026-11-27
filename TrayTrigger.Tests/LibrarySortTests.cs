using TrayTrigger.Models;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The Library's order: every sort through one comparer, turned round by the arrow beside the sort box or a second click on a
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
        Assert.Equal(["Charlie", "Bravo", "Alpha"], Sorted(LibraryViewModel.SortCategory, false, games));
        Assert.Equal(["Alpha", "Bravo", "Charlie"], Sorted(LibraryViewModel.SortCategory, true, games));
    }

    [Fact]
    public void FavoritesStayFirst_WhenTheRestIsReversed()
    {
        var games = new[] { Game("Alpha"), Game("Zulu", favorite: true), Game("Mike") };

        Assert.Equal(["Zulu", "Alpha", "Mike"], Sorted(LibraryViewModel.SortFavoritesFirst, false, games));
        Assert.Equal(["Zulu", "Mike", "Alpha"], Sorted(LibraryViewModel.SortFavoritesFirst, true, games));
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
    public void FavoritesFirst_HasNoColumn()
    {
        Assert.Null(LibraryViewModel.ColumnOf(LibraryViewModel.SortFavoritesFirst));
        Assert.Null(LibraryViewModel.ColumnOf(LibraryViewModel.SortFavoritesFirstRecent));
        Assert.Null(LibraryViewModel.OptionForColumn("Actions"));
    }

    [Theory]
    [InlineData("Alphabetical (A - Z)", false, LibraryViewModel.SortAlphabetical, false)]
    [InlineData("Alphabetical (Z - A)", false, LibraryViewModel.SortAlphabetical, true)]
    [InlineData("Alphabetical (Z - A)", true, LibraryViewModel.SortAlphabetical, false)]
    [InlineData("Category (A - Z)", false, LibraryViewModel.SortCategory, false)]
    [InlineData("Favorites First (A - Z)", false, LibraryViewModel.SortFavoritesFirst, false)]
    [InlineData("Favorites First (Z - A)", false, LibraryViewModel.SortFavoritesFirst, true)]
    [InlineData("Most Recently Played", true, LibraryViewModel.SortMostRecentlyPlayed, true)]
    [InlineData("Cumulative Playtime", false, LibraryViewModel.SortCumulativePlaytime, false)]
    [InlineData("Largest First (Size on Disk)", false, LibraryViewModel.SortLargestFirst, false)]
    [InlineData("Favorites First (Recent)", true, LibraryViewModel.SortFavoritesFirstRecent, true)]
    [InlineData("Playtime", true, LibraryViewModel.SortCumulativePlaytime, true)]
    [InlineData("Nonsense", true, LibraryViewModel.SortAlphabetical, false)]
    [InlineData(null, false, LibraryViewModel.SortAlphabetical, false)]
    public void ASavedSort_OpensInTheSameOrder_UnderItsNewName(string? saved, bool savedReversed, string option, bool reversed)
    {
        Assert.Equal((option, reversed), LibraryViewModel.MigrateSortOption(saved, savedReversed));
    }

    [Fact]
    public void NoSortName_SaysWhichWayItRuns()
    {
        Assert.Equal(["Name", "Category", "Favorites First", "Favorites First (Recent)", "Last Played", "Playtime", "Size on Disk"],
            [LibraryViewModel.SortAlphabetical, LibraryViewModel.SortCategory, LibraryViewModel.SortFavoritesFirst, LibraryViewModel.SortFavoritesFirstRecent,
             LibraryViewModel.SortMostRecentlyPlayed, LibraryViewModel.SortCumulativePlaytime, LibraryViewModel.SortLargestFirst]);
    }
}
