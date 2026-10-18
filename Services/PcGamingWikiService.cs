using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public enum PcGamingWikiStatus
{
    /// <summary>The wiki has a page for the game and it has facts worth showing.</summary>
    Found,
    /// <summary>No page matched the game, or its page has none of the structured facts. Nothing to show.</summary>
    NoFacts,
    /// <summary>The wiki could not be reached or refused the request. Not remembered; the next open tries again.</summary>
    Failed,
}

public sealed record PcGamingWikiResult(PcGamingWikiStatus Status, PcGamingWikiFacts? Facts = null)
{
    public static readonly PcGamingWikiResult NoFacts = new(PcGamingWikiStatus.NoFacts);
    public static readonly PcGamingWikiResult Failed = new(PcGamingWikiStatus.Failed);
}

/// <summary>
/// Reads a game's facts (ultrawide, controller support, save folder...) from PCGamingWiki's public
/// MediaWiki API, only when the Game Details window opens and the user has turned PCGamingWiki facts
/// on. Nothing is sent but the game's Steam App ID or name, in the URL.
///
/// <para>How a page is found, from the most certain way down: a Steam App ID is searched as text and a
/// page is taken only when its own infobox lists that ID; failing that, the name is searched among page
/// titles and only an exact title is accepted, and then only if the page does not name a different Steam
/// App ID. A near match could be another game or edition, and showing the wrong game's facts is worse
/// than showing none. The page's wikitext is read by <see cref="PcGamingWikiParser"/>.</para>
///
/// <para>The wiki's own <c>api/appid.php</c> redirect is not used: it answers 403 to this app's HTTP
/// client although it answers a browser or curl, while the documented <c>api.php</c> answers all of them.</para>
///
/// <para>Manners: a User-Agent that names TrayTrigger and where it lives, at most one request every
/// 1.1 seconds, answers kept 14 days (and "no page" 3 days) in <c>pcgw-cache.json</c>, and a ten
/// minute pause after the wiki answers 403, 429 or 503. The wiki's content is licensed
/// CC BY-NC-SA 3.0: the card names the page and links to it.</para>
/// </summary>
public sealed class PcGamingWikiService
{
    private const string Host = "https://www.pcgamingwiki.com";
    private const string Category = "PcGamingWiki";
    private const long MaxResponseBytes = 4L * 1024 * 1024;

    internal static readonly TimeSpan FactsLifetime = TimeSpan.FromDays(14);
    internal static readonly TimeSpan NoFactsLifetime = TimeSpan.FromDays(3);
    internal static readonly TimeSpan PauseAfterRefusal = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultRequestGap = TimeSpan.FromMilliseconds(1100);

    /// <summary>The one the app uses; tests build their own with a fake handler and a temp cache file.</summary>
    public static PcGamingWikiService Shared { get; } = new();

    /// <summary>Beside the other game-info caches, so Settings' "clear cache" covers it.</summary>
    public static string DefaultCacheFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTrigger", "pcgw-cache.json");

    private readonly HttpClient _http;
    private readonly string _cacheFilePath;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _requestGap;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private Dictionary<string, PcGamingWikiCacheEntry>? _cache;
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private DateTime _pausedUntilUtc = DateTime.MinValue;

    public PcGamingWikiService() : this(null, null, null, null) { }

    internal PcGamingWikiService(HttpMessageHandler? handler, string? cacheFilePath, Func<DateTime>? utcNow, TimeSpan? requestGap)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            // The API does not redirect, so a redirect is unexpected and is not followed.
            AllowAutoRedirect = false,
        })
        {
            MaxResponseContentBufferSize = MaxResponseBytes,
            Timeout = TimeSpan.FromSeconds(12),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TrayTrigger", UpdateService.CurrentVersionDisplay.TrimStart('v')));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/stephenh678/TrayTrigger)"));

        _cacheFilePath = cacheFilePath ?? DefaultCacheFilePath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _requestGap = requestGap ?? DefaultRequestGap;
    }

    // ------------------------------------------------------------------ the lookup

    private const int MaxIdCandidates = 4;

    private sealed record Page(string Title, string Wikitext)
    {
        private PcGamingWikiParser.PageReading? _reading;

        /// <summary>The page read once: the Steam App IDs checked while finding it, and the facts shown once it is chosen.</summary>
        public PcGamingWikiParser.PageReading Reading => _reading ??= PcGamingWikiParser.Read(Wikitext);
    }

    /// <summary>
    /// The facts for a game: from the cache while the answer is fresh, otherwise from the wiki. Never
    /// throws except for the caller's own cancellation; every other problem is <see cref="PcGamingWikiStatus.Failed"/>.
    /// </summary>
    public async Task<PcGamingWikiResult> GetAsync(string? steamAppId, string? gameName, bool forceRefresh = false, CancellationToken ct = default)
    {
        string? key = CacheKey(steamAppId, gameName);
        if (key == null)
            return PcGamingWikiResult.NoFacts;

        if (!forceRefresh && TryGetFresh(key, out var cached))
        {
            LoggingService.Verbose(Category, $"'{gameName}': answered from the cache ({cached.Status}).");
            return cached;
        }

        if (_utcNow() < _pausedUntilUtc)
        {
            LoggingService.Verbose(Category, $"'{gameName}': not asked, lookups are paused until the wiki's refusal has passed.");
            return PcGamingWikiResult.Failed;
        }

        try
        {
            string? id = IsDigits(steamAppId) ? steamAppId!.Trim() : null;
            LoggingService.Verbose(Category, $"'{gameName}' (Steam App ID {id ?? "none"}): asking PCGamingWiki.");

            Page? page = null;
            if (id != null)
                page = await FindPageBySteamIdAsync(id, ct).ConfigureAwait(false);

            if (page == null && !string.IsNullOrWhiteSpace(gameName))
                page = await FindPageByTitleAsync(gameName!, id, ct).ConfigureAwait(false);

            if (page == null)
            {
                LoggingService.Verbose(Category, $"'{gameName}': no page found.");
                return Remember(key, null);
            }

            var facts = page.Reading.Facts;
            LoggingService.Verbose(Category, $"'{gameName}': read {facts.Count} facts from the page '{page.Title}'.");
            if (facts.Count == 0)
                return Remember(key, null);

            return Remember(key, new PcGamingWikiFacts
            {
                PageTitle = page.Title,
                PageUrl = PageUrl(page.Title),
                FetchedUtc = _utcNow(),
                Facts = facts,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Warn(Category, $"Could not read PCGamingWiki facts for '{gameName}': {ex.Message}");
            return PcGamingWikiResult.Failed;
        }
    }

    /// <summary>The page address for a title, the form the wiki itself uses.</summary>
    internal static string PageUrl(string title) => Host + "/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));

    /// <summary>
    /// A text search for the Steam App ID, then the first few pages found are read until one whose own
    /// infobox lists that ID turns up. A page that merely mentions the number is passed over.
    /// </summary>
    private async Task<Page?> FindPageBySteamIdAsync(string id, CancellationToken ct)
    {
        var titles = await SearchAsync("\"" + id + "\"", "text", ct).ConfigureAwait(false);
        foreach (string title in titles.Take(MaxIdCandidates))
        {
            var page = await FetchPageAsync(title, ct).ConfigureAwait(false);
            if (page != null && page.Reading.SteamAppIds.Contains(id))
            {
                LoggingService.Verbose(Category, $"Steam App ID {id}: page '{page.Title}', whose infobox lists it.");
                return page;
            }
        }

        LoggingService.Verbose(Category, $"Steam App ID {id}: no page lists it.");
        return null;
    }

    /// <summary>
    /// The page whose title is exactly the game's name: tried as a title first (a short name like "Portal" is
    /// buried among the search results), then through a title search for a name spelled in another case.
    /// A page is used only when the game's Steam App ID is among the ones the page lists, or, with no ID to check,
    /// when the page's own title is the name too: a name that the wiki redirects to a page with another title, or a
    /// page that lists other Steam IDs, is another game or edition and is not used.
    /// </summary>
    private async Task<Page?> FindPageByTitleAsync(string gameName, string? steamId, CancellationToken ct)
    {
        string name = WithoutMarks(gameName).Replace("\"", " ").Trim();
        string wanted = NormalizeName(gameName);
        if (wanted.Length == 0) return null;

        var page = await FetchPageAsync(name, ct).ConfigureAwait(false);
        if (page == null)
        {
            var titles = await SearchAsync("\"" + name + "\"", "title", ct).ConfigureAwait(false);
            string? exact = titles.FirstOrDefault(t => NormalizeName(t) == wanted);
            if (exact == null)
                return null;
            page = await FetchPageAsync(exact, ct).ConfigureAwait(false);
            if (page == null)
                return null;
        }

        var listed = steamId != null ? page.Reading.SteamAppIds : new List<string>();
        if (steamId != null && listed.Count > 0)
        {
            if (!listed.Contains(steamId))
            {
                LoggingService.Verbose(Category, $"'{gameName}': the page '{page.Title}' lists Steam App ID {string.Join(", ", listed)}, not {steamId}; not used.");
                return null;
            }
        }
        else if (NormalizeName(page.Title) != wanted)
        {
            // Nothing on the page vouches for it, and the wiki sent the name to a page with another title (the
            // wiki's "DOOM" is the 1993 game): that is a different game or edition, not this one.
            LoggingService.Verbose(Category, $"'{gameName}': the name leads to the page '{page.Title}', which nothing confirms is the same game; not used.");
            return null;
        }

        LoggingService.Verbose(Category, $"'{gameName}': page '{page.Title}' by exact title.");
        return page;
    }

    /// <summary>The titles of the pages a search finds, in the wiki's order. <paramref name="where"/> is "title" or "text".</summary>
    private async Task<List<string>> SearchAsync(string query, string where, CancellationToken ct)
    {
        var reply = await GetAsync($"{Host}/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srwhat={where}&srlimit=50&srprop=&format=json&formatversion=2", ct).ConfigureAwait(false);
        if (reply.Status != HttpStatusCode.OK)
            throw new HttpRequestException($"the search answered HTTP {(int)reply.Status}");

        var titles = new List<string>();
        using var doc = JsonDocument.Parse(reply.Body);
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return titles;

        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } title)
                titles.Add(title);
        }
        return titles;
    }

    /// <summary>The page's wikitext, following a redirect page to the page it names. Null for a page that does not exist.</summary>
    private async Task<Page?> FetchPageAsync(string title, CancellationToken ct)
    {
        var reply = await GetAsync($"{Host}/w/api.php?action=parse&page={Uri.EscapeDataString(title)}&prop=wikitext&redirects=1&format=json&formatversion=2", ct).ConfigureAwait(false);
        if (reply.Status != HttpStatusCode.OK)
            throw new HttpRequestException($"the page fetch answered HTTP {(int)reply.Status}");

        using var doc = JsonDocument.Parse(reply.Body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            string code = error.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            // "invalidtitle" is a name with characters no wiki title can have ("Skyrim [Modded]"): a game with no page, not a failure.
            if (code is "missingtitle" or "invalidtitle")
                return null;
            throw new HttpRequestException($"the wiki answered an error ({code})");
        }

        if (!root.TryGetProperty("parse", out var parse))
            throw new HttpRequestException("the wiki's answer had no page in it");

        string pageTitle = parse.TryGetProperty("title", out var pt) ? pt.GetString() ?? title : title;
        string? wikitext = parse.TryGetProperty("wikitext", out var wt) ? wt.GetString() : null;
        return string.IsNullOrEmpty(wikitext) ? null : new Page(pageTitle, wikitext);
    }

    // ------------------------------------------------------------------ the request

    private readonly record struct Reply(HttpStatusCode Status, string Body);

    /// <summary>One request, spaced from the last, with the pause after a refusal. The body is used only when the answer is 200.</summary>
    private async Task<Reply> GetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A lookup that queued here before another one's request was refused must not ask the wiki into the pause.
            if (_utcNow() < _pausedUntilUtc)
                throw new HttpRequestException("lookups are paused after the wiki's last refusal");

            var wait = _requestGap - (_utcNow() - _lastRequestUtc);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            // The whole answer is buffered, so the client's 12 second timeout and 4 MB cap cover the body as well. With
            // ResponseHeadersRead neither applies to it, and a body that stalls would hold the gate (and every lookup behind it) for good.
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            _lastRequestUtc = _utcNow();

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                _pausedUntilUtc = _utcNow() + PauseAfterRefusal;
                LoggingService.Warn(Category, $"PCGamingWiki answered HTTP {(int)response.StatusCode}; lookups are paused for {PauseAfterRefusal.TotalMinutes:0} minutes.");
                throw new HttpRequestException($"PCGamingWiki answered HTTP {(int)response.StatusCode}");
            }

            string body = response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                : string.Empty;
            return new Reply(response.StatusCode, body);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ names and the cache

    /// <summary>Lower case, without trademark signs and stray spaces, so "Hades" and "hades " are the same title.</summary>
    internal static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        return string.Join(' ', WithoutMarks(name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    /// <summary>The name without trademark, registered and copyright signs, which a wiki title never has.</summary>
    private static string WithoutMarks(string name) => name.Replace("™", string.Empty).Replace("®", string.Empty).Replace("©", string.Empty);

    private static bool IsDigits(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().All(char.IsAsciiDigit);

    /// <summary>The cache key: the Steam App ID when there is one (it names exactly one game), otherwise the name.</summary>
    internal static string? CacheKey(string? steamAppId, string? gameName)
    {
        if (IsDigits(steamAppId)) return "app:" + steamAppId!.Trim();
        string name = NormalizeName(gameName);
        return name.Length > 0 ? "name:" + name : null;
    }

    private bool TryGetFresh(string key, out PcGamingWikiResult result)
    {
        lock (_cacheLock)
        {
            if (LoadCacheLocked().TryGetValue(key, out var entry))
            {
                var age = _utcNow() - entry.FetchedUtc;
                if (age >= TimeSpan.Zero && age < (entry.NoFacts ? NoFactsLifetime : FactsLifetime))
                {
                    result = entry.NoFacts || entry.Facts == null
                        ? PcGamingWikiResult.NoFacts
                        : new PcGamingWikiResult(PcGamingWikiStatus.Found, entry.Facts);
                    return true;
                }
            }
        }
        result = PcGamingWikiResult.NoFacts;
        return false;
    }

    private PcGamingWikiResult Remember(string key, PcGamingWikiFacts? facts)
    {
        lock (_cacheLock)
        {
            LoadCacheLocked()[key] = new PcGamingWikiCacheEntry { FetchedUtc = _utcNow(), NoFacts = facts == null, Facts = facts };
            SaveCacheLocked();
        }
        return facts == null ? PcGamingWikiResult.NoFacts : new PcGamingWikiResult(PcGamingWikiStatus.Found, facts);
    }

    /// <summary>Drops every remembered answer (Settings' clear cache). The next open asks again.</summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache = new Dictionary<string, PcGamingWikiCacheEntry>();
            try
            {
                if (File.Exists(_cacheFilePath))
                    File.Delete(_cacheFilePath);
            }
            catch (Exception ex)
            {
                LoggingService.Warn(Category, $"Could not delete the PCGamingWiki cache: {ex.Message}");
            }
        }
    }

    private Dictionary<string, PcGamingWikiCacheEntry> LoadCacheLocked()
    {
        if (_cache != null)
            return _cache;

        try
        {
            if (File.Exists(_cacheFilePath))
            {
                _cache = JsonSerializer.Deserialize(File.ReadAllText(_cacheFilePath), AppJsonContext.Default.DictionaryStringPcGamingWikiCacheEntry)
                         ?? new Dictionary<string, PcGamingWikiCacheEntry>();
                return _cache;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn(Category, $"The PCGamingWiki cache is unreadable, starting fresh: {ex.Message}");
        }

        _cache = new Dictionary<string, PcGamingWikiCacheEntry>();
        return _cache;
    }

    private void SaveCacheLocked()
    {
        if (_cache == null)
            return;

        try
        {
            string? dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = _cacheFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, AppJsonContext.Default.DictionaryStringPcGamingWikiCacheEntry));
            File.Move(tmp, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LoggingService.Warn(Category, $"Could not save the PCGamingWiki cache: {ex.Message}");
        }
    }
}
