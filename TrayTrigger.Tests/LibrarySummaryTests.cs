using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

public class LibrarySummaryTests
{
    [Theory]
    [InlineData(0, 0, 0, "0 games")]
    [InlineData(1, 0, 0, "1 game")]
    [InlineData(15, 0, 0, "15 games")]
    [InlineData(15, 1, 0, "15 games  ·  1 favorite")]
    [InlineData(15, 2, 1, "15 games  ·  2 favorites  ·  1 playing")]
    [InlineData(15, 0, 2, "15 games  ·  2 playing")]
    public void OnlyThePartsWithSomethingToSay_AreShown(int games, int favorites, int playing, string expected)
    {
        Assert.Equal(expected, LibraryViewModel.BuildLibrarySummary(games, favorites, playing));
    }
}
