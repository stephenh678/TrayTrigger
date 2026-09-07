using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrayTrigger.Services;

/// <summary>
/// Optional community art source (steamgriddb.com) for vertical "grid" poster art, used when
/// Steam's own official library art isn't available for a game yet (e.g. new/upcoming titles).
/// Requires a free user-supplied API key from https://www.steamgriddb.com/profile/preferences.
/// </summary>
public class SteamGridDbService
{
    private const string BaseUrl = "https://www.steamgriddb.com/api/v2";

    // No client-wide Timeout: it would also cap the grid image download below, which needs
    // longer than the JSON lookup call. Each call applies its own per-request timeout instead.
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly TimeSpan JsonCallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ImageDownloadTimeout = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource LinkedTimeoutCts(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>
    /// Fetches the highest-scoring static vertical (600x900) grid image for a Steam AppId,
    /// or null if no key is configured, none exists, or the request fails.
    /// </summary>
    public async Task<byte[]?> GetBestVerticalGridBytesAsync(string appId, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(apiKey))
            return null;

        try
        {
            string? gridUrl = await FindBestGridUrlAsync(appId, apiKey, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(gridUrl))
                return null;

            using var timeoutCts = LinkedTimeoutCts(ct, ImageDownloadTimeout);
            using var imageResponse = await HttpClient.GetAsync(gridUrl, timeoutCts.Token).ConfigureAwait(false);
            if (!imageResponse.IsSuccessStatusCode)
                return null;

            return await imageResponse.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamGridDbService", $"Error fetching grid art for AppId {appId}: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> FindBestGridUrlAsync(string appId, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/grids/steam/{appId}?dimensions=600x900");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeoutCts = LinkedTimeoutCts(ct, JsonCallTimeout);
        using var response = await HttpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LoggingService.Verbose("SteamGridDbService", $"Grid lookup for AppId {appId} returned HTTP {(int)response.StatusCode}.");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
            return null;

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        string? bestUrl = null;
        int bestScore = int.MinValue;

        foreach (var item in data.EnumerateArray())
        {
            string? url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            // Skip animated grids - we want a single static poster image.
            if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                continue;

            int score = item.TryGetProperty("score", out var s) && s.TryGetInt32(out int scoreValue) ? scoreValue : 0;
            if (bestUrl == null || score > bestScore)
            {
                bestScore = score;
                bestUrl = url;
            }
        }

        return bestUrl;
    }
}
