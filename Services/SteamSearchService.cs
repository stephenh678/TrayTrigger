using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrayTrigger.Services;

public record SteamGameMatch(
    string Name,
    string AppId,
    string? ThumbnailUrl
);

public class SteamSearchService
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static readonly ConcurrentDictionary<string, List<SteamGameMatch>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<SteamGameMatch>> SearchGamesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<SteamGameMatch>();

        string trimmed = query.Trim();
        if (Cache.TryGetValue(trimmed, out var cached))
            return cached;

        string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(trimmed)}&l=english&cc=US";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new List<SteamGameMatch>();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var root = doc.RootElement;
            var results = new List<SteamGameMatch>();

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    string? type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type != null && !type.Equals("app", StringComparison.OrdinalIgnoreCase))
                        continue; // Skip DLCs, soundtracks, etc.

                    string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? id = null;
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        id = idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt64().ToString() : idProp.GetString();
                    }

                    string? tinyImage = item.TryGetProperty("tiny_image", out var img) ? img.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                    {
                        results.Add(new SteamGameMatch(name.Trim(), id, tinyImage));
                    }
                }
            }

            Cache[trimmed] = results;
            return results;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamSearchService", $"Search error for '{query}': {ex.Message}");
            return new List<SteamGameMatch>();
        }
    }

    public async Task<SteamGameMatch?> FindBestMatchAsync(string query, CancellationToken cancellationToken = default)
    {
        var results = await SearchGamesAsync(query, cancellationToken).ConfigureAwait(false);
        if (results.Count == 0) return null;

        return results[0];
    }
}
