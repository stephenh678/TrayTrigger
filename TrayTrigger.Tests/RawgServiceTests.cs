using System.Text.Json;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The pure JSON-shaping parts of the RAWG lookup: picking the first search result and
/// parsing a game detail. The HTTP calls hit the live API and are only exercised with a key.</summary>
public class RawgServiceTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SelectFirstGame_ReturnsFirstResultIdAndName()
    {
        var (id, name) = RawgService.SelectFirstGame(Parse(
            """{"count":2,"results":[{"id":58175,"name":"Fortnite"},{"id":9999,"name":"Fortnite Festival"}]}"""));
        Assert.Equal(58175, id);
        Assert.Equal("Fortnite", name);
    }

    [Theory]
    [InlineData("""{"count":0,"results":[]}""")]
    [InlineData("""{"count":0}""")]
    [InlineData("""{"results":[{"name":"no id"}]}""")]
    public void SelectFirstGame_ReturnsNullWhenEmpty(string json)
    {
        var (id, name) = RawgService.SelectFirstGame(Parse(json));
        Assert.Null(id);
        Assert.Null(name);
    }

    [Fact]
    public void ParseDetail_MapsAllFields()
    {
        var d = RawgService.ParseDetail(Parse(
            """
            {
              "id": 58175, "slug": "fortnite", "name": "Fortnite",
              "description_raw": "A free-to-play battle royale.",
              "released": "2017-07-25", "metacritic": 78,
              "website": "https://www.epicgames.com/fortnite",
              "developers": [{"name":"Epic Games"}],
              "publishers": [{"name":"Epic Games"},{"name":"Warner Bros."}],
              "genres": [{"name":"Shooter"},{"name":"Action"}],
              "esrb_rating": {"name":"Teen"}
            }
            """));

        Assert.NotNull(d);
        Assert.Equal(58175, d!.RawgId);
        Assert.Equal("Fortnite", d.Name);
        Assert.Equal("Epic Games", d.Developer);
        Assert.Equal("Epic Games, Warner Bros.", d.Publisher);
        Assert.Equal("2017-07-25", d.ReleaseDate);
        Assert.Equal("A free-to-play battle royale.", d.Description);
        Assert.Equal(new[] { "Shooter", "Action" }, d.Genres);
        Assert.Equal(78, d.Metacritic);
        Assert.Equal("Teen", d.EsrbRating);
        Assert.Equal("https://www.epicgames.com/fortnite", d.Website);
    }

    [Fact]
    public void ParseDetail_MissingOptionalsDegradeGracefully()
    {
        var d = RawgService.ParseDetail(Parse(
            """{"id":42,"slug":"minimal","name":"Minimal Game"}"""));

        Assert.NotNull(d);
        Assert.Equal("Minimal Game", d!.Name);
        Assert.Equal(string.Empty, d.Developer);
        Assert.Equal(string.Empty, d.Publisher);
        Assert.Empty(d.Genres);
        Assert.Null(d.Metacritic);
        // Website falls back to the RAWG game page so the attribution link always has a target.
        Assert.Equal("https://rawg.io/games/minimal", d.Website);
    }

    [Theory]
    [InlineData("""{"slug":"x","name":"No id"}""")]
    [InlineData("""{"id":7,"slug":"x"}""")]
    [InlineData("""{"id":7,"name":"  "}""")]
    public void ParseDetail_ReturnsNullWithoutIdOrName(string json)
    {
        Assert.Null(RawgService.ParseDetail(Parse(json)));
    }
}
