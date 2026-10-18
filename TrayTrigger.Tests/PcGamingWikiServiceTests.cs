using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The PCGamingWiki lookup: how a page is found, what is remembered, and what happens when the wiki says no.</summary>
public class PcGamingWikiServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-pcgw-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWiki _wiki = new();
    private DateTime _now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private string CacheFile => Path.Combine(_folder, "pcgw-cache.json");

    private PcGamingWikiService NewService() => new(_wiki, CacheFile, () => _now, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
    }

    private sealed class FakeWiki : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public List<string> UserAgents { get; } = new();
        public Func<string, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        /// <summary>When set, answers instead of <see cref="Respond"/>, so a test can hold a request open.</summary>
        public Func<string, Task<HttpResponseMessage>>? RespondAsync { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsoluteUri);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return RespondAsync != null
                ? RespondAsync(request.RequestUri.AbsoluteUri)
                : Task.FromResult(Respond(request.RequestUri.AbsoluteUri));
        }
    }

    /// <summary>An answer that claims to be bigger than the service's size cap and notes whether anyone read it.</summary>
    private sealed class OversizedContent : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            WasRead = true;
            return stream.WriteAsync("{}"u8.ToArray()).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 5L * 1024 * 1024;
            return true;
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PcGamingWiki", name + ".wikitext"));

    private const string MissingTitle = """{"error":{"code":"missingtitle","info":"The page you specified doesn't exist."}}""";

    private static string ParseAnswer(string title, string wikitext) => JsonSerializer.Serialize(new { parse = new { title, wikitext } });

    private static string SearchAnswer(params string[] titles) => JsonSerializer.Serialize(new { query = new { search = titles.Select(t => new { title = t }).ToArray() } });

    /// <summary>
    /// A wiki that knows Elden Ring: a text search for its Steam App ID finds a list page that only mentions the
    /// number and then the game's own page, and a title search finds it by name.
    /// </summary>
    private void WikiWithEldenRing()
    {
        _wiki.Respond = url =>
        {
            if (url.Contains("list=search") && url.Contains("srwhat=text"))
                return Json(SearchAnswer("The big list of games on Steam", "Elden Ring"));
            if (url.Contains("list=search") && url.Contains("srwhat=title"))
                return Json(SearchAnswer("Elden Ring: Nightreign", "Elden Ring"));
            if (url.Contains("action=parse") && url.Contains("page=Elden%20Ring"))
                return Json(ParseAnswer("Elden Ring", Fixture("elden-ring")));
            if (url.Contains("action=parse"))
                return Json(ParseAnswer("The big list of games on Steam", "A list that mentions 1245620 in passing."));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    // ------------------------------------------------------------------ finding a page

    [Fact]
    public async Task ASteamAppId_FindsThePageWhoseInfoboxListsIt()
    {
        WikiWithEldenRing();

        var result = await NewService().GetAsync("1245620", "Elden Ring");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
        Assert.Equal("Elden Ring", result.Facts!.PageTitle);
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Elden_Ring", result.Facts.PageUrl);
        Assert.True(result.Facts.Facts.Count >= 15);
        Assert.Equal(3, _wiki.Requests.Count);
        Assert.Contains("srwhat=text", _wiki.Requests[0]);
        Assert.Contains("srsearch=%221245620%22", _wiki.Requests[0]);
        Assert.Contains("page=The%20big%20list%20of%20games%20on%20Steam", _wiki.Requests[1]);
        Assert.Contains("page=Elden%20Ring", _wiki.Requests[2]);
        Assert.Contains("redirects=1", _wiki.Requests[2]);
    }

    [Fact]
    public async Task TheWikisAppIdRedirect_IsNeverUsed()
    {
        WikiWithEldenRing();
        await NewService().GetAsync("1245620", "Elden Ring");
        await NewService().GetAsync(null, "Elden Ring");

        Assert.DoesNotContain(_wiki.Requests, r => r.Contains("appid.php"));
    }

    [Fact]
    public async Task EveryRequest_NamesTrayTriggerAndWhereItLives()
    {
        WikiWithEldenRing();
        await NewService().GetAsync("1245620", "Elden Ring");

        Assert.All(_wiki.UserAgents, agent =>
        {
            Assert.StartsWith("TrayTrigger/", agent);
            Assert.Contains("github.com/stephenh678/TrayTrigger", agent);
        });
    }

    [Fact]
    public async Task OnlyTheFirstFewPagesOfAnIdSearchAreRead()
    {
        _wiki.Respond = url => url.Contains("srwhat=text")
            ? Json(SearchAnswer("A", "B", "C", "D", "E", "F", "G"))
            : url.Contains("action=parse") ? Json(ParseAnswer("Page", "no infobox here")) : Json(SearchAnswer());

        await NewService().GetAsync("1245620", null);

        Assert.Equal(1 + 4, _wiki.Requests.Count);
    }

    [Fact]
    public async Task WhenNoPageListsTheId_TheNameIsTriedAsATitle_ThenSearched_AndAnExactTitleOnlyIsTaken()
    {
        _wiki.Respond = url =>
        {
            if (url.Contains("srwhat=text")) return Json(SearchAnswer());
            if (url.Contains("srwhat=title")) return Json(SearchAnswer("Hades II", "Hades"));
            if (url.Contains("action=parse") && url.Contains("page=Hades&")) return Json(ParseAnswer("Hades", Fixture("hades")));
            if (url.Contains("action=parse")) return Json(MissingTitle);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var result = await NewService().GetAsync("1145360", "HADES");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
        Assert.Equal(4, _wiki.Requests.Count);
        Assert.Contains("srwhat=text", _wiki.Requests[0]);
        Assert.Contains("page=HADES&", _wiki.Requests[1]);
        Assert.Contains("srwhat=title", _wiki.Requests[2]);
        Assert.Contains("page=Hades&", _wiki.Requests[3]);
    }

    [Fact]
    public async Task AnExactTitle_IsFetchedDirectly_WithoutASearch()
    {
        _wiki.Respond = url => url.Contains("action=parse") && url.Contains("page=Hades&")
            ? Json(ParseAnswer("Hades", Fixture("hades")))
            : Json(MissingTitle);

        var result = await NewService().GetAsync(null, "Hades");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
        Assert.Single(_wiki.Requests);
    }

    [Fact]
    public async Task APageThatListsTheIdAmongItsOtherSteamEntries_IsTheRightPage()
    {
        const string remaster = "{{Infobox game\n|steam appid = 2362420\n|steam appid side = 24980, 47760\n}}\n{{Video\n|hdr = true\n}}";
        _wiki.Respond = url => url.Contains("srwhat=text")
            ? Json(SearchAnswer("Mass Effect 2"))
            : Json(ParseAnswer("Mass Effect 2", remaster));

        var result = await NewService().GetAsync("24980", "Mass Effect 2");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
    }

    [Fact]
    public async Task APageFoundByName_IsRejected_WhenItListsADifferentSteamId()
    {
        _wiki.Respond = url =>
        {
            if (url.Contains("srwhat=text")) return Json(SearchAnswer());
            if (url.Contains("srwhat=title")) return Json(SearchAnswer("Elden Ring"));
            return Json(ParseAnswer("Elden Ring", Fixture("elden-ring")));
        };

        var result = await NewService().GetAsync("999", "Elden Ring");

        Assert.Equal(PcGamingWikiStatus.NoFacts, result.Status);
    }

    [Fact]
    public async Task APageFoundByName_IsAccepted_WhenItListsNoSteamId()
    {
        const string noInfobox = "{{Video\n|hdr = true\n}}";
        _wiki.Respond = url =>
        {
            if (url.Contains("srwhat=text")) return Json(SearchAnswer());
            if (url.Contains("srwhat=title")) return Json(SearchAnswer("Some Game"));
            return Json(ParseAnswer("Some Game", noInfobox));
        };

        Assert.Equal(PcGamingWikiStatus.Found, (await NewService().GetAsync("555", "Some Game")).Status);
    }

    [Fact]
    public async Task ANameTheWikiRedirectsToAnotherTitle_IsNotUsed_WithNoIdToVouchForIt()
    {
        // The wiki's "DOOM" leads to the 1993 game; a game of that name with no Steam ID could be the 2016 one.
        _wiki.Respond = url => url.Contains("action=parse")
            ? Json(ParseAnswer("Doom (1993)", "{{Video\n|hdr = false\n}}"))
            : Json(SearchAnswer());

        Assert.Equal(PcGamingWikiStatus.NoFacts, (await NewService().GetAsync(null, "DOOM")).Status);
    }

    [Fact]
    public async Task ANameTheWikiRedirects_IsUsed_WhenThePageListsTheGamesSteamId()
    {
        _wiki.Respond = url => url.Contains("srwhat=text")
            ? Json(SearchAnswer())
            : Json(ParseAnswer("Alan Wake II", "{{Infobox game\n|steam appid = 77\n}}\n{{Video\n|hdr = true\n}}"));

        Assert.Equal(PcGamingWikiStatus.Found, (await NewService().GetAsync("77", "Alan Wake 2")).Status);
    }

    [Fact]
    public async Task ATitleThatOnlyResemblesTheName_IsNotUsed()
    {
        _wiki.Respond = url => url.Contains("list=search")
            ? Json(SearchAnswer("Doom (2016)", "Doom Eternal", "DOOM 3"))
            : Json(MissingTitle);

        var result = await NewService().GetAsync(null, "DOOM");

        Assert.Equal(PcGamingWikiStatus.NoFacts, result.Status);
        Assert.Equal(2, _wiki.Requests.Count);
    }

    [Fact]
    public async Task TheTrademarkSignAndCaseOfAName_DoNotStopAMatch()
    {
        _wiki.Respond = url =>
        {
            if (url.Contains("list=search")) return Json(SearchAnswer("Hades"));
            if (url.Contains("action=parse") && url.Contains("page=Hades&")) return Json(ParseAnswer("Hades", Fixture("hades")));
            return Json(MissingTitle);
        };

        var result = await NewService().GetAsync(null, "  HADES\u2122 ");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
        Assert.DoesNotContain(_wiki.Requests, r => r.Contains("%E2%84%A2"));
    }

    [Fact]
    public async Task ASteamIdThatIsNotANumber_IsIgnored_AndTheNameIsUsed()
    {
        WikiWithEldenRing();

        var result = await NewService().GetAsync("12ab", "Elden Ring");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
        Assert.DoesNotContain(_wiki.Requests, r => r.Contains("srwhat=text"));
    }

    [Fact]
    public async Task AGameWithNeitherAnIdNorAName_AsksNothing()
    {
        var result = await NewService().GetAsync(null, "  ");

        Assert.Equal(PcGamingWikiStatus.NoFacts, result.Status);
        Assert.Empty(_wiki.Requests);
    }

    [Fact]
    public async Task ASteamIdWithNoName_IsLookedUpByIdAlone()
    {
        WikiWithEldenRing();

        var result = await NewService().GetAsync("1245620", null);

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
    }

    // ------------------------------------------------------------------ pages with nothing to show

    [Fact]
    public async Task APageThatDoesNotExist_IsNoFacts_AndRemembered()
    {
        _wiki.Respond = url => url.Contains("action=parse")
            ? Json(MissingTitle)
            : Json(SearchAnswer("Gone"));
        var service = NewService();

        Assert.Equal(PcGamingWikiStatus.NoFacts, (await service.GetAsync(null, "Gone")).Status);
        int asked = _wiki.Requests.Count;
        Assert.Equal(PcGamingWikiStatus.NoFacts, (await service.GetAsync(null, "Gone")).Status);

        Assert.Equal(asked, _wiki.Requests.Count);
    }

    [Fact]
    public async Task AGameNameWithCharactersNoTitleCanHave_IsNoFacts_AndRemembered()
    {
        // Square brackets cannot be in a wiki title, so the wiki answers "invalidtitle" for "Skyrim [Modded]".
        // That is a game it has no page for, not a wiki that failed: no warning, and no asking again at every open.
        _wiki.Respond = url => url.Contains("action=parse")
            ? Json("""{"error":{"code":"invalidtitle","info":"Bad title \"Skyrim [Modded]\"."}}""")
            : Json(SearchAnswer());
        var service = NewService();

        Assert.Equal(PcGamingWikiStatus.NoFacts, (await service.GetAsync(null, "Skyrim [Modded]")).Status);
        int asked = _wiki.Requests.Count;
        Assert.Equal(PcGamingWikiStatus.NoFacts, (await service.GetAsync(null, "Skyrim [Modded]")).Status);

        Assert.Equal(asked, _wiki.Requests.Count);
    }

    [Fact]
    public async Task APageWithNoStructuredFacts_IsNoFacts()
    {
        _wiki.Respond = url => url.Contains("list=search")
            ? Json(SearchAnswer("Stub Game"))
            : Json(ParseAnswer("Stub Game", "{{Infobox game}} Only a stub."));

        Assert.Equal(PcGamingWikiStatus.NoFacts, (await NewService().GetAsync(null, "Stub Game")).Status);
    }

    // ------------------------------------------------------------------ remembering

    [Fact]
    public async Task AnAnswerIsRemembered_ForFourteenDays()
    {
        WikiWithEldenRing();
        var service = NewService();
        await service.GetAsync("1245620", "Elden Ring");
        int asked = _wiki.Requests.Count;

        _now += TimeSpan.FromDays(13);
        Assert.Equal(PcGamingWikiStatus.Found, (await service.GetAsync("1245620", "Elden Ring")).Status);
        Assert.Equal(asked, _wiki.Requests.Count);

        _now += TimeSpan.FromDays(2);
        Assert.Equal(PcGamingWikiStatus.Found, (await service.GetAsync("1245620", "Elden Ring")).Status);
        Assert.True(_wiki.Requests.Count > asked, "after 15 days the wiki should be asked again");
    }

    [Fact]
    public async Task ANoPageAnswer_IsRememberedForThreeDays()
    {
        _wiki.Respond = url => url.Contains("action=parse") ? Json(MissingTitle) : Json(SearchAnswer());
        var service = NewService();
        await service.GetAsync(null, "Nothing Here");
        int asked = _wiki.Requests.Count;

        _now += TimeSpan.FromDays(2);
        await service.GetAsync(null, "Nothing Here");
        Assert.Equal(asked, _wiki.Requests.Count);

        _now += TimeSpan.FromDays(2);
        await service.GetAsync(null, "Nothing Here");
        Assert.True(_wiki.Requests.Count > asked);
    }

    [Fact]
    public async Task RefreshNow_AsksAgainEvenWhenTheAnswerIsFresh()
    {
        WikiWithEldenRing();
        var service = NewService();
        await service.GetAsync("1245620", "Elden Ring");
        int asked = _wiki.Requests.Count;

        await service.GetAsync("1245620", "Elden Ring", forceRefresh: true);

        Assert.Equal(asked * 2, _wiki.Requests.Count);
    }

    [Fact]
    public async Task TheCache_SurvivesARestart()
    {
        WikiWithEldenRing();
        await NewService().GetAsync("1245620", "Elden Ring");
        int asked = _wiki.Requests.Count;

        var result = await NewService().GetAsync("1245620", "Elden Ring");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
        Assert.Equal(asked, _wiki.Requests.Count);
        Assert.Equal("Elden Ring", result.Facts!.PageTitle);
        Assert.Contains(result.Facts.Facts, f => f.Label == "Save location" && f.Detail.StartsWith("%APPDATA%"));
    }

    [Fact]
    public async Task AnUnreadableCacheFile_StartsFresh()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(CacheFile, "{ this is not json");
        WikiWithEldenRing();

        var result = await NewService().GetAsync("1245620", "Elden Ring");

        Assert.Equal(PcGamingWikiStatus.Found, result.Status);
    }

    [Fact]
    public async Task ClearCache_ForgetsEverything_AndDeletesTheFile()
    {
        WikiWithEldenRing();
        var service = NewService();
        await service.GetAsync("1245620", "Elden Ring");
        Assert.True(File.Exists(CacheFile));
        int asked = _wiki.Requests.Count;

        service.ClearCache();

        Assert.False(File.Exists(CacheFile));
        await service.GetAsync("1245620", "Elden Ring");
        Assert.True(_wiki.Requests.Count > asked);
    }

    // ------------------------------------------------------------------ when the wiki says no

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ARefusal_Fails_AndPausesLookupsForTenMinutes(HttpStatusCode refusal)
    {
        _wiki.Respond = _ => new HttpResponseMessage(refusal);
        var service = NewService();

        Assert.Equal(PcGamingWikiStatus.Failed, (await service.GetAsync("1245620", "Elden Ring")).Status);
        int asked = _wiki.Requests.Count;

        _now += TimeSpan.FromMinutes(5);
        Assert.Equal(PcGamingWikiStatus.Failed, (await service.GetAsync("570", "Dota 2")).Status);
        Assert.Equal(asked, _wiki.Requests.Count);

        _now += TimeSpan.FromMinutes(6);
        WikiWithEldenRing();
        Assert.Equal(PcGamingWikiStatus.Found, (await service.GetAsync("1245620", "Elden Ring")).Status);
    }

    [Fact]
    public async Task ALookupWaitingBehindARefusedRequest_DoesNotSendItsOwn()
    {
        // Game Two's lookup queues behind Game One's request. The wiki refuses that one, so the pause starts while
        // Game Two is still waiting, and Game Two has to honour it instead of asking the wiki as well.
        var release = new TaskCompletionSource();
        _wiki.RespondAsync = async _ =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        };
        var service = NewService();

        var first = service.GetAsync("1245620", "Game One");
        var second = service.GetAsync("570", "Game Two");
        Assert.True(SpinWait.SpinUntil(() => _wiki.Requests.Count == 1, TimeSpan.FromSeconds(5)), "the first request should be in flight");
        release.SetResult();

        Assert.Equal(PcGamingWikiStatus.Failed, (await first).Status);
        Assert.Equal(PcGamingWikiStatus.Failed, (await second).Status);
        Assert.Single(_wiki.Requests);
    }

    [Fact]
    public async Task AnAnswerBeyondTheSizeCap_IsNotRead()
    {
        // The cap on an answer only holds when the client buffers the whole answer; the body must not be read past it.
        var oversized = new OversizedContent();
        _wiki.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = oversized };

        var result = await NewService().GetAsync(null, "Big Page");

        Assert.Equal(PcGamingWikiStatus.Failed, result.Status);
        Assert.False(oversized.WasRead);
    }

    [Fact]
    public async Task ANetworkFailure_Fails_AndIsNotRemembered()
    {
        _wiki.Respond = _ => throw new HttpRequestException("no network");
        var service = NewService();

        Assert.Equal(PcGamingWikiStatus.Failed, (await service.GetAsync("1245620", "Elden Ring")).Status);

        WikiWithEldenRing();
        Assert.Equal(PcGamingWikiStatus.Found, (await service.GetAsync("1245620", "Elden Ring")).Status);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{}")]
    [InlineData("""{"parse":{"title":"X"}}""")]
    [InlineData("""{"error":{"code":"internal_api_error"}}""")]
    public async Task AnAnswerThatMakesNoSense_NeverThrows(string body)
    {
        _wiki.Respond = url => url.Contains("list=search") ? Json(SearchAnswer("X")) : Json(body);

        var result = await NewService().GetAsync(null, "X");

        Assert.NotEqual(PcGamingWikiStatus.Found, result.Status);
    }

    [Fact]
    public async Task ACancelledLookup_Cancels()
    {
        WikiWithEldenRing();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewService().GetAsync("1245620", "Elden Ring", ct: cts.Token));
    }

    // ------------------------------------------------------------------ keys and names

    [Theory]
    [InlineData("1245620", "Elden Ring", "app:1245620")]
    [InlineData(" 1245620 ", "x", "app:1245620")]
    [InlineData(null, "Elden  Ring™", "name:elden ring")]
    [InlineData("abc", "Elden Ring", "name:elden ring")]
    public void CacheKey_IsTheAppIdWhenThereIsOne_OtherwiseTheName(string? appId, string? name, string expected)
    {
        Assert.Equal(expected, PcGamingWikiService.CacheKey(appId, name));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "™")]
    public void CacheKey_IsNullWhenThereIsNothingToLookUp(string? appId, string? name)
    {
        Assert.Null(PcGamingWikiService.CacheKey(appId, name));
    }

    [Fact]
    public void PageUrl_IsTheFormTheWikiUses()
    {
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Elden_Ring", PcGamingWikiService.PageUrl("Elden Ring"));
        Assert.Equal("https://www.pcgamingwiki.com/wiki/Baldur%27s_Gate_3", PcGamingWikiService.PageUrl("Baldur's Gate 3"));
    }

    [Fact]
    public async Task TheRequestsAreSpacedOut()
    {
        WikiWithEldenRing();
        var service = new PcGamingWikiService(_wiki, CacheFile, () => DateTime.UtcNow, TimeSpan.FromMilliseconds(120));
        var started = DateTime.UtcNow;

        await service.GetAsync("1245620", "Elden Ring");

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(100), "two requests 120 ms apart cannot finish in under 100 ms");
    }
}
