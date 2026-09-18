using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class TrayMenuSectionsTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    private static (string Id, DateTime? LastPlayed)[] Games() =>
    [
        ("a", Now.AddDays(-1)),
        ("b", Now.AddHours(-1)),
        ("c", null),
        ("d", Now.AddDays(-3)),
        ("e", Now.AddDays(-2))
    ];

    [Fact]
    public void MostRecentFirst_AndNeverPlayedGamesAreLeftOut()
    {
        Assert.Equal(["b", "a", "e", "d"], TrayMenuSections.SelectRecent(Games(), [], 10));
    }

    [Fact]
    public void AGameTheFavoritesSectionShows_IsNotRepeatedUnderRecent()
    {
        Assert.Equal(["a", "e", "d"], TrayMenuSections.SelectRecent(Games(), ["b"], 10));
    }

    [Fact]
    public void FavoritesAreRemovedBeforeTheCap_SoRecentStillFillsUp()
    {
        Assert.Equal(["a", "e"], TrayMenuSections.SelectRecent(Games(), ["b"], 2));
    }

    [Fact]
    public void WhenEveryRecentGameIsAFavorite_RecentIsEmpty()
    {
        Assert.Empty(TrayMenuSections.SelectRecent(Games(), ["a", "b", "d", "e"], 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveLimit_GivesNothing(int max)
    {
        Assert.Empty(TrayMenuSections.SelectRecent(Games(), [], max));
    }
}
