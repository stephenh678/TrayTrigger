using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Which cached Steam posters get a second look once there is a SteamGridDB key. The bug this
/// guards: a banner stand-in cached before the key was entered was served from the cache for good,
/// so a game added after the key still showed the banner until Refresh All Game Posters.
/// </summary>
public class PosterSourceLedgerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"), "poster-sources.json");

    public PosterSourceLedgerTests() => PosterSourceLedger.UseFileForTests(_file);

    public void Dispose()
    {
        PosterSourceLedger.UseFileForTests(null);
        try { Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true); } catch { }
    }

    [Theory]
    // Unknown: cached before sources were recorded, so it may be a banner.
    [InlineData(null, true)]
    [InlineData(PosterSource.Banner, true)]
    [InlineData(PosterSource.Steam, false)]
    [InlineData(PosterSource.SteamGridDb, false)]
    // SteamGridDB was already asked and had nothing: asking again every time would cost requests for nothing.
    [InlineData(PosterSource.BannerAfterSteamGridDb, false)]
    public void WithAKey_OnlyABannerOrAnUnknownPosterIsLookedAtAgain(PosterSource? source, bool expected)
    {
        if (source is { } known)
            PosterSourceLedger.Set("123", known);

        Assert.Equal(expected, PosterSourceLedger.MayImprove("123", "a-key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithoutAKey_NothingIsLookedAtAgain(string? key)
    {
        PosterSourceLedger.Set("123", PosterSource.Banner);
        Assert.False(PosterSourceLedger.MayImprove("123", key));
        Assert.False(PosterSourceLedger.MayImprove("456", key));
    }

    [Fact]
    public void Sources_SurviveARestart()
    {
        PosterSourceLedger.Set("10", PosterSource.Steam);
        PosterSourceLedger.Set("20", PosterSource.BannerAfterSteamGridDb);

        PosterSourceLedger.UseFileForTests(_file);

        Assert.Equal(PosterSource.Steam, PosterSourceLedger.Get("10"));
        Assert.Equal(PosterSource.BannerAfterSteamGridDb, PosterSourceLedger.Get(" 20 "));
        Assert.Null(PosterSourceLedger.Get("30"));
    }

    [Fact]
    public void AnUnreadableRecord_StartsFresh()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ not json");
        PosterSourceLedger.UseFileForTests(_file);

        Assert.Null(PosterSourceLedger.Get("10"));
        PosterSourceLedger.Set("10", PosterSource.SteamGridDb);
        Assert.Equal(PosterSource.SteamGridDb, PosterSourceLedger.Get("10"));
    }
}
