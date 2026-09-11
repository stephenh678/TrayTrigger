using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Metadata from the RAWG video-games database (rawg.io) for games with no Steam listing
/// (Game Pass exclusives, Roblox, Fortnite, obscure indies, console ports). RAWG is a single-key
/// REST API - the user supplies their own free key (rawg.io/apidocs), stored encrypted alongside
/// the SteamGridDB key. Text metadata only: developer, publisher, release date, synopsis, genres,
/// Metacritic. Two calls per lookup: search the name to a game id, then fetch that game's detail
/// (search results don't carry developer/description). Results are cached per RAWG id for the
/// process lifetime. RAWG's terms require an active "Data from RAWG" link wherever this shows.
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

    private static readonly ConcurrentDictionary<int, RawgGameDetails> Cache = new();

    /// <summary>
    /// Finds a game by name and returns its full RAWG detail, or null if no key, no match, or the
    /// request fails. The result's <see cref="RawgGameDetails.Name"/> is RAWG's own title for the
    /// match, so callers can reject a loose hit. Does not apply a similarity guard itself.
    /// </summary>
    public async Task<RawgGameDetails?> LookUpByNameAsync(string gameName, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(apiKey))
            return null;

        try
        {
            string searchUrl = $"{BaseUrl}/games?key={Uri.EscapeDataString(apiKey)}&search={Uri.EscapeDataString(gameName)}&page_size=5";
            using var searchStream = await HttpClient.GetStreamAsync(searchUrl, ct).ConfigureAwait(false);
            using var searchDoc = await JsonDocument.ParseAsync(searchStream, cancellationToken: ct).ConfigureAwait(false);

            (int? id, _) = SelectFirstGame(searchDoc.RootElement);
            if (id == null)
                return null;

            return await GetByIdAsync(id.Value, apiKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RawgService", $"Error looking up '{gameName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetches (and caches) a game's RAWG detail by its RAWG id - the remembered-match path.</summary>
    public async Task<RawgGameDetails?> GetByIdAsync(int rawgId, string apiKey, CancellationToken ct = default)
    {
        if (rawgId <= 0 || string.IsNullOrWhiteSpace(apiKey))
            return null;

        if (Cache.TryGetValue(rawgId, out var cached))
            return cached;

        try
        {
            string detailUrl = $"{BaseUrl}/games/{rawgId}?key={Uri.EscapeDataString(apiKey)}";
            using var stream = await HttpClient.GetStreamAsync(detailUrl, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var details = ParseDetail(doc.RootElement);
            if (details == null)
                return null;

            Cache[rawgId] = details;
            return details;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RawgService", $"Error fetching RAWG id {rawgId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The first (best-ranked) game id and name in a RAWG <c>/games</c> search response.</summary>
    internal static (int? Id, string? Name) SelectFirstGame(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int id))
            {
                string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                return (id, name);
            }
        }

        return (null, null);
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
            ReleaseDate = root.TryGetProperty("released", out var rel) ? rel.GetString() ?? string.Empty : string.Empty,
            Description = root.TryGetProperty("description_raw", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
            Genres = CollectNames(root, "genres"),
        };

        if (root.TryGetProperty("metacritic", out var mc) && mc.ValueKind == JsonValueKind.Number && mc.TryGetInt32(out int score))
            details.Metacritic = score;

        if (root.TryGetProperty("esrb_rating", out var esrb) && esrb.ValueKind == JsonValueKind.Object &&
            esrb.TryGetProperty("name", out var esrbName))
            details.EsrbRating = esrbName.GetString() ?? string.Empty;

        details.Website = root.TryGetProperty("website", out var web) && !string.IsNullOrWhiteSpace(web.GetString())
            ? web.GetString()!
            : $"https://rawg.io/games/{details.Slug}";

        return details;
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
