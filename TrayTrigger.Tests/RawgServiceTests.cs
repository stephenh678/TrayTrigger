using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>The pure JSON-shaping parts of the RAWG lookup: picking the best search result,
/// parsing a game detail (tags, screenshot, stores), store-link merging and selection, date
/// formatting, cache round-trip, and the Steam/RAWG source-resolution rule. The HTTP calls hit
/// the live API and are only exercised with a key.</summary>
public class RawgServiceTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SelectBestGame_PrefersTitleClosestToQuery_NotRawgsFirstHit()
    {
        // RAWG ranks by popularity, so "Halo" (or the Master Chief Collection) often outranks the
        // game actually asked for.
        var (id, name, sim) = RawgService.SelectBestGame(Parse(
            """
            {"count":3,"results":[
              {"id":1,"name":"Halo: The Master Chief Collection"},
              {"id":2,"name":"Halo"},
              {"id":3,"name":"Halo Infinite"}]}
            """), "Halo Infinite");
        Assert.Equal(3, id);
        Assert.Equal("Halo Infinite", name);
        Assert.Equal(1.0, sim);
    }

    [Fact]
    public void SelectBestGame_ExactMatchWins_TiesKeepRawgOrder()
    {
        var (id, _, _) = RawgService.SelectBestGame(Parse(
            """{"results":[{"id":58175,"name":"Fortnite"},{"id":9999,"name":"Fortnite Festival"}]}"""), "Fortnite");
        Assert.Equal(58175, id);
    }

    [Fact]
    public void SelectBestGame_ReportsLowSimilarityForUnrelatedHit()
    {
        var (id, _, sim) = RawgService.SelectBestGame(Parse(
            """{"results":[{"id":5,"name":"Completely Different Title"}]}"""), "Roblox");
        Assert.Equal(5, id);
        Assert.True(sim < SteamSearchService.DefaultMinConfidence, $"similarity {sim} should be below the default guard");
    }

    [Theory]
    [InlineData("""{"count":0,"results":[]}""")]
    [InlineData("""{"count":0}""")]
    [InlineData("""{"results":[{"name":"no id"}]}""")]
    public void SelectBestGame_ReturnsNullWhenEmpty(string json)
    {
        var (id, name, sim) = RawgService.SelectBestGame(Parse(json), "Anything");
        Assert.Null(id);
        Assert.Null(name);
        Assert.Equal(0, sim);
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
              "rating": 3.42, "ratings_count": 1234,
              "background_image": "https://media.rawg.io/media/games/dcb/fortnite.jpg",
              "developers": [{"name":"Epic Games"}],
              "publishers": [{"name":"Epic Games"},{"name":"Warner Bros."}],
              "genres": [{"name":"Indie"},{"name":"Shooter"},{"name":"Action"}],
              "tags": [{"name":"Multiplayer"},{"name":"Atmospheric"},{"name":"Online Co-Op"},{"name":"Singleplayer"},{"name":"Full controller support"}],
              "stores": [{"id":1,"store":{"id":11,"name":"Epic Games","slug":"epic-games"}},{"id":2,"store":{"id":7,"name":"Xbox Store","slug":"xbox-store"}}],
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
        Assert.Equal(new[] { "Indie", "Shooter", "Action" }, d.Genres);
        Assert.Equal("Shooter", d.PrimaryGenre); // Indie skipped
        Assert.Equal(78, d.Metacritic);
        Assert.Equal(3.42, d.Rating);
        Assert.Equal(1234, d.RatingsCount);
        Assert.Equal("Teen", d.EsrbRating);
        Assert.Equal("https://media.rawg.io/media/games/dcb/fortnite.jpg", d.BackgroundImageUrl);
        Assert.Equal("https://rawg.io/games/fortnite", d.RawgPageUrl);
        // Play modes in display order, Steam wording, unrelated tags dropped.
        Assert.Equal(new[] { "Full controller support", "Single-player", "Multi-player", "Online Co-op" }, d.PlayModes);
    }

    [Fact]
    public void ParseDetail_MissingOptionalsDegradeGracefully()
    {
        var d = RawgService.ParseDetail(Parse(
            """{"id":42,"slug":"minimal","name":"Minimal Game","rating":0,"metacritic":null,"esrb_rating":null,"released":null,"background_image":null}"""));

        Assert.NotNull(d);
        Assert.Equal("Minimal Game", d!.Name);
        Assert.Equal(string.Empty, d.Developer);
        Assert.Equal(string.Empty, d.Publisher);
        Assert.Empty(d.Genres);
        Assert.Equal(string.Empty, d.PrimaryGenre); // nothing invented
        Assert.Null(d.Metacritic);
        Assert.Null(d.Rating);
        Assert.Equal(string.Empty, d.EsrbRating);
        Assert.Equal(string.Empty, d.ReleaseDate);
        Assert.Equal(string.Empty, d.BackgroundImageUrl);
        Assert.Empty(d.PlayModes);
        // The attribution link always has a target: the game's rawg.io page.
        Assert.Equal("https://rawg.io/games/minimal", d.RawgPageUrl);
    }

    [Fact]
    public void ParseDetail_OnlyIndieGenre_IsStillUsed()
    {
        var d = RawgService.ParseDetail(Parse("""{"id":1,"slug":"x","name":"X","genres":[{"name":"Indie"}]}"""));
        Assert.Equal("Indie", d!.PrimaryGenre);
    }

    [Fact]
    public void ParseDetail_RejectsNonHttpsBackgroundImage()
    {
        var d = RawgService.ParseDetail(Parse("""{"id":1,"slug":"x","name":"X","background_image":"file:///C:/evil.jpg"}"""));
        Assert.Equal(string.Empty, d!.BackgroundImageUrl);
    }

    [Theory]
    [InlineData("""{"slug":"x","name":"No id"}""")]
    [InlineData("""{"id":7,"slug":"x"}""")]
    [InlineData("""{"id":7,"name":"  "}""")]
    public void ParseDetail_ReturnsNullWithoutIdOrName(string json)
    {
        Assert.Null(RawgService.ParseDetail(Parse(json)));
    }

    [Fact]
    public void ParseSearchHits_MapsIdNameYearAndPlatforms_SkippingMalformedRows()
    {
        var hits = RawgService.ParseSearchHits(Parse(
            """
            {"results":[
              {"id":22509,"name":"Halo Infinite","released":"2021-12-08",
               "platforms":[{"platform":{"name":"PC"}},{"platform":{"name":"Xbox Series S/X"}}]},
              {"id":3,"name":"Unreleased Thing","released":null,"platforms":[]},
              {"name":"no id"},
              {"id":9,"name":""}
            ]}
            """));

        Assert.Equal(2, hits.Count);
        Assert.Equal(22509, hits[0].Id);
        Assert.Equal("Halo Infinite", hits[0].Name);
        Assert.Equal("2021", hits[0].Year);
        Assert.Equal("PC, Xbox Series S/X", hits[0].Platforms);
        Assert.Equal("2021  \u2022  PC, Xbox Series S/X", hits[0].Subtitle);
        Assert.Equal(string.Empty, hits[1].Year);
        Assert.Equal(string.Empty, hits[1].Subtitle);
    }

    [Fact]
    public void MapPlayModes_NormalisesAndDedupes()
    {
        var chips = RawgService.MapPlayModes(new[] { "online multiplayer", "Multiplayer", "MMO", "Massively Multiplayer", "Story Rich", "co-op" });
        Assert.Equal(new[] { "Multi-player", "Co-op", "MMO" }, chips);
    }

    [Fact]
    public void RawgGameDetails_RoundTripsThroughTheDiskCacheContext()
    {
        var original = new Dictionary<int, RawgGameDetails>
        {
            [58175] = new RawgGameDetails
            {
                RawgId = 58175, Slug = "fortnite", Name = "Fortnite", Developer = "Epic Games",
                Genres = { "Shooter" }, PlayModes = { "Multi-player" }, Rating = 3.4, Metacritic = 78,
                BackgroundImageUrl = "https://media.rawg.io/x.jpg",
                FetchedUtc = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc),
            }
        };

        string json = JsonSerializer.Serialize(original, AppJsonContext.Default.DictionaryInt32RawgGameDetails);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryInt32RawgGameDetails);

        Assert.NotNull(back);
        var d = back![58175];
        Assert.Equal("Fortnite", d.Name);
        Assert.Equal("Shooter", d.PrimaryGenre);
        Assert.Equal(new[] { "Multi-player" }, d.PlayModes);
        Assert.Equal(3.4, d.Rating);
        Assert.Equal(original[58175].FetchedUtc, d.FetchedUtc);
        Assert.DoesNotContain("PrimaryGenre", json); // computed, not stored
    }

    [Theory]
    [InlineData("2017-07-25", "25 Jul, 2017")]
    [InlineData("2024-01-05", "5 Jan, 2024")]
    [InlineData("TBA", "TBA")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FormatReleaseDate_MatchesSteamStyle(string? iso, string expected)
    {
        Assert.Equal(expected, RawgService.FormatReleaseDate(iso));
    }

    [Theory]
    // Auto: RAWG only for a game with no Steam App ID, and only when a key exists.
    [InlineData(MetadataSource.Auto, false, true, MetadataSource.Rawg)]
    [InlineData(MetadataSource.Auto, true, true, MetadataSource.Steam)]
    [InlineData(MetadataSource.Auto, false, false, MetadataSource.Steam)]
    // Explicit preference wins...
    [InlineData(MetadataSource.Steam, false, true, MetadataSource.Steam)]
    [InlineData(MetadataSource.Rawg, true, true, MetadataSource.Rawg)]
    // ...except RAWG can't be shown without a key.
    [InlineData(MetadataSource.Rawg, false, false, MetadataSource.Steam)]
    public void ResolveSource_FollowsPreferenceThenAvailability(MetadataSource preferred, bool hasAppId, bool rawgAvailable, MetadataSource expected)
    {
        Assert.Equal(expected, GameDetailsViewModel.ResolveSource(preferred, hasAppId, rawgAvailable));
    }
}
