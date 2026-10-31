using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The banner behind Game Details' header: where it is looked for, what is kept, and when a miss is asked about again.</summary>
public class HeroArtServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"), "Heroes");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    /// <summary>A real JPEG of the given size, with enough noise to be over the size a banner must have.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        using var bmp = new Bitmap(width, height);
        var rng = new Random(7);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.DarkSlateBlue);
            for (int i = 0; i < 400; i++)
                g.FillRectangle(new SolidBrush(Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256))), rng.Next(width), rng.Next(height), 40, 40);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    private sealed class Calls
    {
        public List<string> Urls = new();
        public int GridById;
        public int GridByName;
    }

    private (HeroArtService Service, Calls Calls) Make(byte[]? steam = null, byte[]? gridById = null, NamedGridArt? gridByName = null)
    {
        var calls = new Calls();
        var service = new HeroArtService(_dir)
        {
            DownloadBytes = (url, _) => { calls.Urls.Add(url); return Task.FromResult(steam); },
            GridByAppId = (_, _, _) => { calls.GridById++; return Task.FromResult(gridById); },
            GridByName = (_, _, _) => { calls.GridByName++; return Task.FromResult(gridByName); },
        };
        return (service, calls);
    }

    private static GameEntry Game(string name = "Hades", string? appId = "1145360") => new() { Name = name, SteamAppId = appId };

    [Fact]
    public async Task SteamsBanner_IsKept_AndAskedForOnlyOnce()
    {
        var (service, calls) = Make(steam: Jpeg(1920, 620));
        var game = Game();

        string? first = await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5);
        string? second = await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(_dir, "1145360.jpg")));
        Assert.Single(calls.Urls);
        Assert.Contains("/apps/1145360/library_hero.jpg", calls.Urls[0]);
        Assert.Equal(first, service.CachedPath("1145360", game.Id));
    }

    [Fact]
    public async Task ABannerThatIsNotWide_IsNotKept()
    {
        var (service, _) = Make(steam: Jpeg(600, 900));
        var game = Game();
        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5));
        Assert.Null(service.CachedPath("1145360", game.Id));
    }

    [Fact]
    public async Task WhenNothingIsFound_TheMissIsRemembered_AndNotAskedAgainThatWeek()
    {
        var (service, calls) = Make();
        var game = Game();

        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));
        int askedOnce = calls.Urls.Count + calls.GridById + calls.GridByName;
        Assert.True(askedOnce > 0);
        Assert.True(File.Exists(Path.Combine(_dir, "1145360.none")));

        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));
        Assert.Equal(askedOnce, calls.Urls.Count + calls.GridById + calls.GridByName);

        // A note older than a week no longer counts, so the next open looks again.
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "1145360.none"), DateTime.UtcNow - HeroArtService.RetryAfter - TimeSpan.FromHours(1));
        await service.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5);
        Assert.True(calls.Urls.Count + calls.GridById + calls.GridByName > askedOnce);
    }

    [Fact]
    public async Task SteamGridDb_FillsInForAGameSteamHasNoBannerFor_ButOnlyWithAKey()
    {
        var (withKey, _) = Make(gridById: Jpeg(1920, 620));
        var game = Game();
        Assert.NotNull(await withKey.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));

        var other = new HeroArtService(Path.Combine(_dir, "nokey"))
        {
            DownloadBytes = (_, _) => Task.FromResult<byte[]?>(null),
            GridByAppId = (_, _, _) => Task.FromResult<byte[]?>(Jpeg(1920, 620)),
            GridByName = (_, _, _) => Task.FromResult<NamedGridArt?>(null),
        };
        Assert.Null(await other.GetHeroPathAsync(game, game.SteamAppId, null, 0.5));
    }

    [Fact]
    public async Task AGameWithNoSteamListing_IsLookedForByName_AndALooseMatchIsRefused()
    {
        var game = Game("Clive Barker's Hellraiser: Revival", appId: null);

        var (good, goodCalls) = Make(gridByName: new NamedGridArt(Jpeg(1920, 620), "Clive Barker's Hellraiser: Revival"));
        string? path = await good.GetHeroPathAsync(game, null, "key", 0.5);
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(_dir, $"{game.Id}.jpg"), path);
        Assert.Empty(goodCalls.Urls);

        var (loose, _) = Make(gridByName: new NamedGridArt(Jpeg(1920, 620), "Some Completely Unrelated Racing Game"));
        var other = Game("Clive Barker's Hellraiser: Revival", appId: null);
        Assert.Null(await loose.GetHeroPathAsync(other, null, "key", 0.5));
    }

    [Fact]
    public async Task AnAppIdThatCouldNotBeAFileName_IsNeverUsed()
    {
        var (service, calls) = Make(steam: Jpeg(1920, 620));
        var game = Game(appId: @"..\..\evil");
        // Not a valid App ID, so Steam is not asked, and the file is filed under the game's own id.
        await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5);
        Assert.Empty(calls.Urls);
        Assert.False(File.Exists(Path.Combine(_dir, "..", "evil.jpg")));
    }

    [Fact]
    public void KeyFor_PrefersTheAppId_ThenTheGamesOwnId_AndRefusesAnUnsafeOne()
    {
        Assert.Equal("1145360", HeroArtService.KeyFor("1145360", "abc"));
        Assert.Equal("abc", HeroArtService.KeyFor(null, "abc"));
        Assert.Equal("abc", HeroArtService.KeyFor("not-a-number", "abc"));
        Assert.Null(HeroArtService.KeyFor(null, @"..\x"));
    }

    [Fact]
    public async Task RemovingAGame_TakesItsBannerAndItsNote_ButNotOneAnotherGameStillUses()
    {
        Directory.CreateDirectory(_dir);
        string covers = Path.Combine(Path.GetDirectoryName(_dir)!, "Covers");
        string icons = Path.Combine(Path.GetDirectoryName(_dir)!, "Icons");
        Directory.CreateDirectory(covers);
        Directory.CreateDirectory(icons);

        var gone = new GameEntry { Name = "Gone", SteamAppId = "111" };
        var kept = new GameEntry { Name = "Kept", SteamAppId = "222" };
        File.WriteAllText(Path.Combine(_dir, "111.jpg"), "x");
        File.WriteAllText(Path.Combine(_dir, "111.none"), "x");
        File.WriteAllText(Path.Combine(_dir, "222.jpg"), "x");
        File.WriteAllText(Path.Combine(_dir, $"{gone.Id}.jpg"), "x");

        GameDataCleanup.DeleteRemovedGameData([gone], [kept], icons, covers, _dir);

        Assert.False(File.Exists(Path.Combine(_dir, "111.jpg")));
        Assert.False(File.Exists(Path.Combine(_dir, "111.none")));
        Assert.False(File.Exists(Path.Combine(_dir, $"{gone.Id}.jpg")));
        Assert.True(File.Exists(Path.Combine(_dir, "222.jpg")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ALookThatCouldNotFinish_IsNotRemembered_AsNone_SoTheNextOpenAsksAgain()
    {
        int asked = 0;
        var service = new HeroArtService(_dir)
        {
            DownloadBytes = (_, _) => { asked++; throw new HttpRequestException("offline"); },
            GridByAppId = (_, _, _) => Task.FromResult<byte[]?>(null),
            GridByName = (_, _, _) => Task.FromResult<NamedGridArt?>(null),
        };
        var game = Game();

        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5));
        Assert.False(File.Exists(Path.Combine(_dir, "1145360.none")));

        int first = asked;
        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5));
        Assert.True(asked > first);
    }

    [Fact]
    public async Task FindAgain_KeepsTheBannerAlreadyKept_WhenTheLookFails()
    {
        var (service, _) = Make(steam: Jpeg(1920, 620));
        var game = Game();
        string? kept = await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5);
        Assert.NotNull(kept);
        byte[] before = File.ReadAllBytes(kept!);

        service.DownloadBytes = (_, _) => throw new HttpRequestException("offline");
        string? again = await service.RefetchAsync(game, game.SteamAppId, null, 0.5);

        Assert.Equal(kept, again);
        Assert.Equal(before, File.ReadAllBytes(kept!));
        Assert.False(File.Exists(Path.Combine(_dir, "1145360.none")));
    }

    [Fact]
    public async Task FindAgain_ReplacesTheBanner_WhenANewOneArrives()
    {
        var (service, _) = Make(steam: Jpeg(1920, 620));
        var game = Game();
        string? kept = await service.GetHeroPathAsync(game, game.SteamAppId, null, 0.5);
        byte[] before = File.ReadAllBytes(kept!);

        byte[] newer = Jpeg(1600, 520);
        service.DownloadBytes = (_, _) => Task.FromResult<byte[]?>(newer);
        string? again = await service.RefetchAsync(game, game.SteamAppId, null, 0.5);

        Assert.Equal(kept, again);
        Assert.NotEqual(before, File.ReadAllBytes(again!));
    }

    [Fact]
    public async Task AServerThatIsStruggling_IsNotAnAnswerOfNone()
    {
        // The web download itself: a 404 is "none", a 503 is "try again" (a thrown error the look turns into "unfinished").
        var service = new HeroArtService(_dir)
        {
            DownloadBytes = (_, _) => throw new HttpRequestException("HTTP 503"),
            GridByAppId = (_, _, _) => Task.FromResult<byte[]?>(null),
            GridByName = (_, _, _) => Task.FromResult<NamedGridArt?>(null),
        };
        var game = Game();
        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));
        Assert.False(File.Exists(Path.Combine(_dir, "1145360.none")));
    }

    [Fact]
    public async Task ANoteMadeWithoutAKey_DoesNotStand_OnceThereIsOne()
    {
        var game = Game();
        var (without, _) = Make();
        Assert.Null(await without.GetHeroPathAsync(game, game.SteamAppId, null, 0.5));
        Assert.True(File.Exists(Path.Combine(_dir, "1145360.none")));

        // The same folder, now with a key and a site that has what Steam has not.
        var (withKey, _) = Make(gridById: Jpeg(1920, 620));
        Assert.NotNull(await withKey.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));
    }

    [Fact]
    public async Task ANoteMadeWithAKey_StandsForTheWeek()
    {
        var game = Game();
        var (service, calls) = Make();
        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));
        int asked = calls.Urls.Count + calls.GridById + calls.GridByName;

        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));
        Assert.Equal(asked, calls.Urls.Count + calls.GridById + calls.GridByName);
    }

    [Fact]
    public async Task ANameSearch_IsOnlyForAGameWithNoSteamListing()
    {
        // Steam lists this game, so its banner is looked for under its App ID; a title that merely resembles its name is not asked about.
        var (service, calls) = Make(gridByName: new NamedGridArt(Jpeg(1920, 620), "Hades"));
        var game = Game("Hades");
        Assert.Null(await service.GetHeroPathAsync(game, game.SteamAppId, "key", 0.5));
        Assert.Equal(0, calls.GridByName);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void ARateLimitOrAStrugglingServer_IsNotAnAnswerOfNone(HttpStatusCode status, bool transient) =>
        Assert.Equal(transient, SteamGridDbService.IsTransient(status));
}
