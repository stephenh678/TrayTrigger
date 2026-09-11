using System;
using System.Collections.Generic;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

public class MetadataFreshnessTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(MetadataRefreshInterval.EveryOpen, 0, true)]     // always stale
    [InlineData(MetadataRefreshInterval.Daily, 23, false)]
    [InlineData(MetadataRefreshInterval.Daily, 24, true)]
    [InlineData(MetadataRefreshInterval.Every3Days, 24 * 3 - 1, false)]
    [InlineData(MetadataRefreshInterval.Every3Days, 24 * 3, true)]
    [InlineData(MetadataRefreshInterval.Weekly, 24 * 6, false)]
    [InlineData(MetadataRefreshInterval.Weekly, 24 * 7, true)]
    [InlineData(MetadataRefreshInterval.Monthly, 24 * 29, false)]
    [InlineData(MetadataRefreshInterval.Monthly, 24 * 30, true)]
    [InlineData(MetadataRefreshInterval.Never, 24 * 400, false)]
    public void IsStale_HonoursTheInterval(MetadataRefreshInterval interval, int ageHours, bool expected)
    {
        var fetched = Now.AddHours(-ageHours);
        Assert.Equal(expected, MetadataFreshness.IsStale(fetched, interval, Now));
    }

    [Fact]
    public void IsStale_EntryWithNoTimestamp_IsStaleUnlessNever()
    {
        // Entries written before the disk cache carried FetchedUtc pick up a timestamp on
        // their next open - except under Never, which means exactly that.
        Assert.True(MetadataFreshness.IsStale(default, MetadataRefreshInterval.Daily, Now));
        Assert.True(MetadataFreshness.IsStale(default, MetadataRefreshInterval.Monthly, Now));
        Assert.False(MetadataFreshness.IsStale(default, MetadataRefreshInterval.Never, Now));
    }

    [Theory]
    [InlineData(0, "Updated just now")]
    [InlineData(30, "Updated just now")]
    [InlineData(60, "Updated 1 minute ago")]
    [InlineData(45 * 60, "Updated 45 minutes ago")]
    [InlineData(60 * 60, "Updated 1 hour ago")]
    [InlineData(3 * 60 * 60, "Updated 3 hours ago")]
    [InlineData(30 * 60 * 60, "Updated yesterday")]
    [InlineData(5 * 24 * 60 * 60, "Updated 5 days ago")]
    [InlineData(21 * 24 * 60 * 60, "Updated 3 weeks ago")]
    [InlineData(90 * 24 * 60 * 60, "Updated 3 months ago")]
    [InlineData(800 * 24 * 60 * 60, "Updated 2 years ago")]
    public void Describe_UsesCoarseRelativeWording(int ageSeconds, string expected)
    {
        var fetched = Now.AddSeconds(-ageSeconds);
        Assert.Equal(expected, MetadataFreshness.Describe(fetched, Now));
    }

    [Fact]
    public void Describe_NoTimestamp_IsEmpty_AndClockSkewIsJustNow()
    {
        Assert.Equal(string.Empty, MetadataFreshness.Describe(default, Now));
        Assert.Equal("Updated just now", MetadataFreshness.Describe(Now.AddMinutes(5), Now));
    }

    [Fact]
    public void SteamAppDetails_RoundTripsThroughTheDiskCacheContext()
    {
        var original = new Dictionary<string, SteamAppDetails>
        {
            ["620"] = new SteamAppDetails
            {
                AppId = "620", Name = "Portal 2", Developers = "Valve", Genres = { "Puzzle" },
                PlayModes = { "Single-player", "Co-op" }, MetacriticScore = 95, ReviewSummary = "Overwhelmingly Positive",
                PcRequirementsMin = "<strong>OS:</strong> Windows 7",
                NewsItems = { new SteamNewsItem { Title = "Update", Url = "https://store.steampowered.com/news/app/620", Date = Now, IsPatchNotes = true } },
                FetchedUtc = Now,
            }
        };

        string json = JsonSerializer.Serialize(original, AppJsonContext.Default.DictionaryStringSteamAppDetails);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringSteamAppDetails);

        Assert.NotNull(back);
        var d = back!["620"];
        Assert.Equal("Portal 2", d.Name);
        Assert.Equal("Puzzle", d.PrimaryGenre);
        Assert.Equal(new[] { "Single-player", "Co-op" }, d.PlayModes);
        Assert.Equal(95, d.MetacriticScore);
        Assert.Single(d.NewsItems);
        Assert.True(d.NewsItems[0].IsPatchNotes);
        Assert.Equal(Now, d.FetchedUtc);
        // Computed properties are not stored.
        Assert.DoesNotContain("PrimaryGenre", json);
        Assert.DoesNotContain("StoreUrl", json);
        Assert.DoesNotContain("DateDisplay", json);
    }

    [Fact]
    public void AppSettings_MetadataRefreshInterval_RoundTripsAsAName_AndDefaultsToEvery3Days()
    {
        Assert.Equal(MetadataRefreshInterval.Every3Days, new AppSettings().MetadataRefreshInterval);

        var s = new AppSettings { MetadataRefreshInterval = MetadataRefreshInterval.Weekly };
        string json = JsonSerializer.Serialize(s, AppJsonContext.Default.AppSettings);
        Assert.Contains("\"MetadataRefreshInterval\": \"Weekly\"", json);

        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
        Assert.Equal(MetadataRefreshInterval.Weekly, back!.MetadataRefreshInterval);

        // A settings file from before the option existed keeps the default.
        var old = JsonSerializer.Deserialize("{\"UseRawgMetadata\":true}", AppJsonContext.Default.AppSettings);
        Assert.Equal(MetadataRefreshInterval.Every3Days, old!.MetadataRefreshInterval);
    }
}
