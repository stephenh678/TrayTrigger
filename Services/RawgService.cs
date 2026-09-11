using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Outcome of a RAWG lookup, so the details window can say *why* there is nothing to
/// show instead of silently falling back to "No synopsis".</summary>
public enum RawgLookupStatus
{
    Found,
    /// <summary>The search returned nothing, or nothing similar enough to the game's name.</summary>
    NoMatch,
    /// <summary>RAWG rejected the API key (HTTP 401) - the user needs to fix it in Settings.</summary>
    Unauthorized,
    /// <summary>Network / timeout / unexpected response.</summary>
    Failed
}

/// <summary>One row of a RAWG search, for the "Change match" picker.</summary>
public sealed record RawgSearchHit(int Id, string Name, string Year, string Platforms)
{
    public string Subtitle => string.IsNullOrEmpty(Year)
        ? Platforms
        : string.IsNullOrEmpty(Platforms) ? Year : $"{Year}  •  {Platforms}";
}

public sealed record RawgLookupResult(RawgLookupStatus Status, RawgGameDetails? Details)
{
    public static readonly RawgLookupResult NoMatch = new(RawgLookupStatus.NoMatch, null);
    public static readonly RawgLookupResult Unauthorized = new(RawgLookupStatus.Unauthorized, null);
    public static readonly RawgLookupResult Failed = new(RawgLookupStatus.Failed, null);
}

/// <summary>
/// Metadata from the RAWG video-games database (rawg.io) for games with no Steam listing
/// (Game Pass exclusives, Roblox, Fortnite, obscure indies, console ports). RAWG is a single-key
/// REST API - the user supplies their own free key (rawg.io/apidocs), stored encrypted alongside
/// the SteamGridDB key. Two calls per lookup: search the name, pick the candidate whose title is
/// most similar to the game's name (RAWG's own ranking favours popularity, so "Halo" can outrank
/// "Halo Infinite"), then fetch that game's detail (search results don't carry
/// developer/description). A third, optional call resolves store URLs for the details window.
/// Details are cached on disk (<c>rawg-cache.json</c>) so re-opens are instant, offline and cost
/// no quota; misses are cached per name for the process lifetime. RAWG's terms require an active
/// "Data from RAWG" link wherever this shows.
/// </summary>
public class RawgService
{
    private const string BaseUrl = "https://api.rawg.io/api";

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>Beside the poster cache, so "clear cache" in Settings covers it too.</summary>
    public static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrayTrigger", "rawg-cache.json");

    private static readonly object CacheLock = new();
    private static Dictionary<int, RawgGameDetails>? _cache;

    /// <summary>Names that produced no acceptable match this session - reopening the same game's
    /// details shouldn't cost another two requests against the monthly quota.</summary>
    private static readonly HashSet<string> NoMatchCache = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ lookups

    /// <summary>
    /// Finds a game by name and returns its full RAWG detail. Of the search results, the one whose
    /// title is most similar to <paramref name="gameName"/> is chosen (via
    /// <see cref="SteamSearchService.CalculateSimilarity"/>) and it must clear
    /// <paramref name="minConfidence"/>; otherwise <see cref="RawgLookupStatus.NoMatch"/>.
    /// </summary>
    public async Task<RawgLookupResult> LookUpByNameAsync(string gameName, string apiKey, double minConfidence = SteamSearchService.DefaultMinConfidence, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(apiKey))
            return RawgLookupResult.NoMatch;

        lock (NoMatchCache)
        {
            if (NoMatchCache.Contains(gameName))
                return RawgLookupResult.NoMatch;
        }

        try
        {
            // search_precise tightens RAWG's fuzzy matching so short names don't drown in
            // loosely related hits; page_size 10 leaves room for the similarity pick below.
            string searchUrl = $"{BaseUrl}/games?key={Uri.EscapeDataString(apiKey)}&search={Uri.EscapeDataString(gameName)}&search_precise=true&page_size=10";
            using var response = await HttpClient.GetAsync(searchUrl, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                LoggingService.Warn("RawgService", "RAWG rejected the API key (401).");
                return RawgLookupResult.Unauthorized;
            }
            response.EnsureSuccessStatusCode();

            using var searchStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: ct).ConfigureAwait(false);

            var best = SelectBestGame(searchDoc.RootElement, gameName);
            if (best.Id == null || best.Similarity < minConfidence)
            {
                if (best.Id != null)
                    LoggingService.Verbose("RawgService", $"Rejected RAWG match '{best.Name}' for '{gameName}' (similarity {best.Similarity:F2} < {minConfidence:F2}).");
                lock (NoMatchCache) NoMatchCache.Add(gameName);
                return RawgLookupResult.NoMatch;
            }

            return await GetByIdAsync(best.Id.Value, apiKey, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RawgService", $"Error looking up '{gameName}': {ex.Message}");
            return RawgLookupResult.Failed;
        }
    }

    /// <summary>
    /// Fetches a game's RAWG detail by its RAWG id - the remembered-match path. Served from the
    /// disk cache unless <paramref name="forceRefresh"/> (the manual "Refresh metadata" action).
    /// </summary>
    public async Task<RawgLookupResult> GetByIdAsync(int rawgId, string apiKey, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (rawgId <= 0)
            return RawgLookupResult.NoMatch;

        if (!forceRefresh && TryGetCached(rawgId, out var cached))
            return new RawgLookupResult(RawgLookupStatus.Found, cached);

        if (string.IsNullOrWhiteSpace(apiKey))
            return RawgLookupResult.NoMatch;

        try
        {
            string detailUrl = $"{BaseUrl}/games/{rawgId}?key={Uri.EscapeDataString(apiKey)}";
            using var response = await HttpClient.GetAsync(detailUrl, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                LoggingService.Warn("RawgService", "RAWG rejected the API key (401).");
                return RawgLookupResult.Unauthorized;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A remembered id that RAWG has since removed/merged: treat as no match so the
                // caller can drop the stale id and re-search by name.
                LoggingService.Verbose("RawgService", $"RAWG id {rawgId} no longer exists (404).");
                Remove(rawgId);
                return RawgLookupResult.NoMatch;
            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var details = ParseDetail(doc.RootElement);
            if (details == null)
                return RawgLookupResult.NoMatch;

            details.FetchedUtc = DateTime.UtcNow;
            Store(details);
            return new RawgLookupResult(RawgLookupStatus.Found, details);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RawgService", $"Error fetching RAWG id {rawgId}: {ex.Message}");
            return RawgLookupResult.Failed;
        }
    }

    /// <summary>
    /// Raw search results for the "Change match" picker: no similarity guard, RAWG's own
    /// ranking, up to 20 hits. Returns an empty list on failure or a bad key (the picker shows a
    /// status line either way).
    /// </summary>
    public async Task<(RawgLookupStatus Status, List<RawgSearchHit> Hits)> SearchAsync(string query, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(apiKey))
            return (RawgLookupStatus.NoMatch, new List<RawgSearchHit>());

        try
        {
            string searchUrl = $"{BaseUrl}/games?key={Uri.EscapeDataString(apiKey)}&search={Uri.EscapeDataString(query)}&page_size=20";
            using var response = await HttpClient.GetAsync(searchUrl, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (RawgLookupStatus.Unauthorized, new List<RawgSearchHit>());
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var hits = ParseSearchHits(doc.RootElement);
            return (hits.Count > 0 ? RawgLookupStatus.Found : RawgLookupStatus.NoMatch, hits);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RawgService", $"Error searching RAWG for '{query}': {ex.Message}");
            return (RawgLookupStatus.Failed, new List<RawgSearchHit>());
        }
    }

    // ------------------------------------------------------------------ disk cache

    /// <summary>Cached details for a RAWG id, from memory or the on-disk cache.</summary>
    public static bool TryGetCached(int rawgId, out RawgGameDetails details)
    {
        lock (CacheLock)
        {
            var cache = LoadCacheLocked();
            if (cache.TryGetValue(rawgId, out var found))
            {
                details = found;
                return true;
            }
        }
        details = null!;
        return false;
    }

    private static void Store(RawgGameDetails details)
    {
        lock (CacheLock)
        {
            LoadCacheLocked()[details.RawgId] = details;
            SaveCacheLocked();
        }
    }

    private static void Remove(int rawgId)
    {
        lock (CacheLock)
        {
            if (LoadCacheLocked().Remove(rawgId))
                SaveCacheLocked();
        }
    }

    /// <summary>Drops every cached entry (Settings › clear cache). The next open re-fetches.</summary>
    public static void ClearCache()
    {
        lock (CacheLock)
        {
            _cache = new Dictionary<int, RawgGameDetails>();
            try
            {
                if (File.Exists(CacheFilePath))
                    File.Delete(CacheFilePath);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("RawgService", $"Could not delete RAWG cache: {ex.Message}");
            }
        }
        lock (NoMatchCache) NoMatchCache.Clear();
    }

    private static Dictionary<int, RawgGameDetails> LoadCacheLocked()
    {
        if (_cache != null)
            return _cache;

        try
        {
            if (File.Exists(CacheFilePath))
            {
                string json = File.ReadAllText(CacheFilePath);
                _cache = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryInt32RawgGameDetails)
                         ?? new Dictionary<int, RawgGameDetails>();
                return _cache;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RawgService", $"RAWG cache unreadable, starting fresh: {ex.Message}");
        }

        _cache = new Dictionary<int, RawgGameDetails>();
        return _cache;
    }

    private static void SaveCacheLocked()
    {
        if (_cache == null)
            return;

        try
        {
            string? dir = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tmp = CacheFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, AppJsonContext.Default.DictionaryInt32RawgGameDetails));
            File.Move(tmp, CacheFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RawgService", $"Could not save RAWG cache: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// The search result whose title is most similar to <paramref name="query"/>. RAWG orders
    /// results by its own relevance/popularity, so the first hit for "Halo Infinite" can be the
    /// older, more popular "Halo"; comparing titles picks the right one. Ties keep RAWG's order.
    /// </summary>
    internal static (int? Id, string? Name, double Similarity) SelectBestGame(JsonElement root, string query)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return (null, null, 0);

        int? bestId = null;
        string? bestName = null;
        double bestScore = -1;

        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id))
                continue;

            string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            double score = SteamSearchService.CalculateSimilarity(query, name);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
                bestName = name;
            }
        }

        return (bestId, bestName, Math.Max(0, bestScore));
    }

    /// <summary>The id/name/year/platforms of every result in a RAWG <c>/games</c> search response.</summary>
    internal static List<RawgSearchHit> ParseSearchHits(JsonElement root)
    {
        var hits = new List<RawgSearchHit>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return hits;

        foreach (var item in results.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id))
                continue;
            string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string released = item.TryGetProperty("released", out var rel) && rel.ValueKind == JsonValueKind.String ? rel.GetString() ?? string.Empty : string.Empty;
            string year = released.Length >= 4 ? released.Substring(0, 4) : string.Empty;

            var platforms = new List<string>();
            if (item.TryGetProperty("platforms", out var plats) && plats.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in plats.EnumerateArray())
                {
                    if (p.TryGetProperty("platform", out var pl) && pl.TryGetProperty("name", out var pn) && !string.IsNullOrWhiteSpace(pn.GetString()))
                        platforms.Add(pn.GetString()!);
                }
            }

            hits.Add(new RawgSearchHit(id, name, year, string.Join(", ", platforms)));
        }

        return hits;
    }

    /// <summary>Parses a RAWG <c>/games/{id}</c> detail object, or null if it has no id/name.</summary>
    internal static RawgGameDetails? ParseDetail(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id))
            return null;

        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var details = new RawgGameDetails
        {
            RawgId = id,
            Name = name,
            Slug = root.TryGetProperty("slug", out var slug) ? slug.GetString() ?? string.Empty : string.Empty,
            Developer = JoinNames(root, "developers"),
            Publisher = JoinNames(root, "publishers"),
            ReleaseDate = root.TryGetProperty("released", out var rel) && rel.ValueKind == JsonValueKind.String ? rel.GetString() ?? string.Empty : string.Empty,
            Description = root.TryGetProperty("description_raw", out var desc) && desc.ValueKind == JsonValueKind.String ? desc.GetString() ?? string.Empty : string.Empty,
            Genres = CollectNames(root, "genres"),
            PlayModes = MapPlayModes(CollectNames(root, "tags")),
        };

        if (root.TryGetProperty("metacritic", out var mc) && mc.ValueKind == JsonValueKind.Number && mc.TryGetInt32(out int score))
            details.Metacritic = score;

        if (root.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Number && rating.TryGetDouble(out double r) && r > 0)
            details.Rating = r;

        if (root.TryGetProperty("ratings_count", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.TryGetInt32(out int count))
            details.RatingsCount = count;

        if (root.TryGetProperty("esrb_rating", out var esrb) && esrb.ValueKind == JsonValueKind.Object &&
            esrb.TryGetProperty("name", out var esrbName))
            details.EsrbRating = esrbName.GetString() ?? string.Empty;

        if (root.TryGetProperty("background_image", out var bg) && bg.ValueKind == JsonValueKind.String)
        {
            string? url = bg.GetString();
            if (Uri.TryCreate(url, UriKind.Absolute, out var bgUri) && bgUri.Scheme == Uri.UriSchemeHttps)
                details.BackgroundImageUrl = bgUri.AbsoluteUri;
        }

        details.RawgPageUrl = $"https://rawg.io/games/{details.Slug}";

        return details;
    }

    /// <summary>
    /// RAWG tag → play-mode chip, in display order, using the wording Steam's categories use so
    /// the chip row reads the same whichever source is active. Anything not listed (genres,
    /// moods, "Steam Achievements"-style store tags) is dropped.
    /// </summary>
    private static readonly (string Tag, string Chip)[] PlayModeTags =
    {
        ("full controller support", "Full controller support"),
        ("partial controller support", "Partial controller support"),
        ("controller support", "Controller support"),
        ("singleplayer", "Single-player"),
        ("multiplayer", "Multi-player"),
        ("online multiplayer", "Multi-player"),
        ("co-op", "Co-op"),
        ("online co-op", "Online Co-op"),
        ("local co-op", "Local Co-op"),
        ("local multiplayer", "Local Multiplayer"),
        ("split screen", "Split Screen"),
        ("pvp", "PvP"),
        ("online pvp", "Online PvP"),
        ("cross-platform multiplayer", "Cross-Platform Multiplayer"),
        ("mmo", "MMO"),
        ("massively multiplayer", "MMO"),
    };

    internal static List<string> MapPlayModes(IEnumerable<string> tags)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags)
            present.Add(t.Trim());

        var chips = new List<string>();
        foreach (var (tag, chip) in PlayModeTags)
        {
            if (present.Contains(tag) && !chips.Contains(chip))
                chips.Add(chip);
        }
        return chips;
    }

    /// <summary>
    /// RAWG returns ISO dates ("2017-07-25"); Steam's store returns "25 Jul, 2017". Match Steam's
    /// look so the release-date row reads the same whichever source is active. Anything that
    /// isn't a full ISO date is returned unchanged.
    /// </summary>
    internal static string FormatReleaseDate(string? isoDate)
    {
        if (DateTime.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("d MMM, yyyy", CultureInfo.InvariantCulture);
        return isoDate ?? string.Empty;
    }

    /// <summary>Comma-joins the "name" of each object in a named array (developers/publishers).</summary>
    private static string JoinNames(JsonElement root, string arrayProperty)
        => string.Join(", ", CollectNames(root, arrayProperty));

    private static List<string> CollectNames(JsonElement root, string arrayProperty)
    {
        var names = new List<string>();
        if (root.TryGetProperty(arrayProperty, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                    names.Add(n.GetString()!);
            }
        }
        return names;
    }
}
