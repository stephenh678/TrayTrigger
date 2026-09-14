using TrayTrigger.Models;

namespace TrayTrigger.Tests;

/// <summary>
/// Steam and RAWG must file the same game under the same genre. Steam lists "Indie" first for
/// most indie titles, and RAWG's rule (skip Indie when anything else is listed) applies to both.
/// </summary>
public class GenreRulesTests
{
    [Fact]
    public void PrimaryOf_SkipsIndieWhenAnotherGenreIsListed()
    {
        Assert.Equal("RPG", GenreRules.PrimaryOf(new List<string> { "Indie", "RPG", "Simulation" }));
        Assert.Equal("Action", GenreRules.PrimaryOf(new List<string> { "Action", "Indie" }));
    }

    [Fact]
    public void PrimaryOf_KeepsIndieWhenItIsTheOnlyGenre()
    {
        Assert.Equal("Indie", GenreRules.PrimaryOf(new List<string> { "Indie" }));
    }

    [Fact]
    public void PrimaryOf_IsEmptyWhenNothingIsListed()
    {
        Assert.Equal(string.Empty, GenreRules.PrimaryOf(new List<string>()));
    }

    [Fact]
    public void SteamAndRawg_AgreeOnTheSameGenreList()
    {
        var steam = new SteamAppDetails { Genres = { "Indie", "RPG", "Simulation" } };
        var rawg = new RawgGameDetails { Genres = { "Indie", "RPG", "Simulation" } };
        Assert.Equal("RPG", steam.PrimaryGenre);
        Assert.Equal(rawg.PrimaryGenre, steam.PrimaryGenre);
    }
}
