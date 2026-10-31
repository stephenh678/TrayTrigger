using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Steam's review rating, counted from its review histogram now that its own "Very Positive" page is gone.</summary>
public class SteamReviewHistogramTests
{
    private static SteamAppDetails Apply(string json)
    {
        var details = new SteamAppDetails();
        using var doc = JsonDocument.Parse(json);
        SteamMetadataService.ApplyReviewHistogram(doc.RootElement, details);
        return details;
    }

    private static string Histogram(params (int Up, int Down)[] rollups) =>
        "{\"success\":1,\"results\":{\"rollups\":[" +
        string.Join(",", rollups.Select(r => $"{{\"date\":1,\"recommendations_up\":{r.Up},\"recommendations_down\":{r.Down}}}")) +
        "]}}";

    [Fact]
    public void ThePositiveShare_IsCountedAcrossEveryRollup()
    {
        var details = Apply(Histogram((200, 10), (260, 30)));
        Assert.Equal(92, details.ReviewScorePercent);
        Assert.Equal("92% positive (500 reviews)", details.ReviewSummary);
    }

    [Fact]
    public void WithFewerThanTenReviews_OnlyTheCountIsSaid()
    {
        var few = Apply(Histogram((3, 2)));
        Assert.Null(few.ReviewScorePercent);
        Assert.Equal("5 user reviews", few.ReviewSummary);
        Assert.Equal("1 user review", Apply(Histogram((1, 0))).ReviewSummary);
    }

    [Fact]
    public void NoReviews_OrAReplyThatIsNotAHistogram_LeavesTheDetailsAlone()
    {
        Assert.Null(Apply(Histogram()).ReviewSummary);
        Assert.Null(Apply(Histogram((0, 0))).ReviewSummary);
        Assert.Null(Apply("{\"success\":0}").ReviewSummary);
        Assert.Null(Apply("{\"results\":{\"rollups\":\"none\"}}").ReviewSummary);
    }

    [Theory]
    [InlineData(0, MetadataRefreshInterval.Every3Days, true)]
    [InlineData(1, MetadataRefreshInterval.Daily, true)]
    [InlineData(SteamAppDetails.CurrentDataVersion, MetadataRefreshInterval.Every3Days, false)]
    [InlineData(0, MetadataRefreshInterval.Never, false)]
    public void ASavedCopyFromBeforeTheRating_IsFetchedAgainOnce_UnlessRefreshIsNever(int version, MetadataRefreshInterval interval, bool outdated)
    {
        var details = new SteamAppDetails { AppId = "1", Name = "X", DataVersion = version, FetchedUtc = DateTime.UtcNow };
        Assert.Equal(outdated, MetadataFreshness.IsOutdated(details, interval));
    }
}
