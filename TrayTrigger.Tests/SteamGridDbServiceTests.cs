using System.Text.Json;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The pure JSON-shaping parts of the SteamGridDB name lookup: picking the first
/// autocomplete game and the highest-scoring static grid. The HTTP calls read the live API and
/// are only exercised by hand with a real key.</summary>
public class SteamGridDbServiceTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void SelectFirstGame_ReturnsFirstIdAndName()
    {
        var (id, name) = SteamGridDbService.SelectFirstGame(Parse(
            """{"success":true,"data":[{"id":1234,"name":"Roblox"},{"id":5678,"name":"Roblox Studio"}]}"""));
        Assert.Equal(1234, id);
        Assert.Equal("Roblox", name);
    }

    [Theory]
    [InlineData("""{"success":false,"data":[]}""")]
    [InlineData("""{"success":true,"data":[]}""")]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":true,"data":[{"name":"No id here"}]}""")]
    public void SelectFirstGame_ReturnsNullOnEmptyOrFailed(string json)
    {
        var (id, name) = SteamGridDbService.SelectFirstGame(Parse(json));
        Assert.Null(id);
        Assert.Null(name);
    }

    [Fact]
    public void SelectBestGridUrl_PicksHighestScoreStatic()
    {
        string? url = SteamGridDbService.SelectBestGridUrl(Parse(
            """{"success":true,"data":[{"url":"https://cdn/low.png","score":3},{"url":"https://cdn/high.png","score":42},{"url":"https://cdn/mid.png","score":10}]}"""));
        Assert.Equal("https://cdn/high.png", url);
    }

    [Fact]
    public void SelectBestGridUrl_SkipsAnimatedWebm_EvenIfHigherScore()
    {
        string? url = SteamGridDbService.SelectBestGridUrl(Parse(
            """{"success":true,"data":[{"url":"https://cdn/animated.webm","score":99},{"url":"https://cdn/static.png","score":5}]}"""));
        Assert.Equal("https://cdn/static.png", url);
    }

    [Theory]
    [InlineData("""{"success":false,"data":[]}""")]
    [InlineData("""{"success":true,"data":[]}""")]
    [InlineData("""{"success":true,"data":[{"url":"https://cdn/only.webm","score":9}]}""")]
    public void SelectBestGridUrl_ReturnsNullWhenNothingUsable(string json)
    {
        Assert.Null(SteamGridDbService.SelectBestGridUrl(Parse(json)));
    }
}
