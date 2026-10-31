using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The wide banner Game Details shows behind a game's name. Steam keeps one for most games
/// (<c>library_hero.jpg</c>, on the same servers as the library poster, so nothing new is contacted);
/// SteamGridDB, when the player has given it a key, fills in a game Steam has none for, and finds one
/// by name for a game with no Steam listing. Kept under <c>Heroes\</c> by Steam App ID, or by the
/// game's own id, so a game is asked at most once: a miss is remembered for a week.
/// </summary>
public sealed class HeroArtService
{
    /// <summary>Beside the poster cache. Anything here can be fetched again, so a backup leaves it out.</summary>
    public static readonly string HeroesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTrigger", "Heroes");

    /// <summary>A game nothing was found for is not asked again for this long.</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromDays(7);

    // A real banner is far wider than tall (Steam's is 1920x620); anything near square is some other picture.
    private const double MinAspect = 1.8;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    })
    {
        MaxResponseContentBufferSize = MetadataHttpLimits.MaxResponseBytes,
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly SteamGridDbService GridDb = new();

    private readonly string _directory;

    public HeroArtService(string? directory = null) => _directory = directory ?? HeroesDirectory;

    /// <summary>One for the app: the banners it keeps are filed by game, not by window.</summary>
    public static readonly HeroArtService Shared = new();

    // Test seams: the three places a banner can come from.
    internal Func<string, CancellationToken, Task<byte[]?>> DownloadBytes { get; set; } = DownloadFromWebAsync;
    internal Func<string, string, CancellationToken, Task<byte[]?>> GridByAppId { get; set; } =
        (appId, key, ct) => GridDb.GetBestHeroBytesAsync(appId, key, ct);
    internal Func<string, string, CancellationToken, Task<NamedGridArt?>> GridByName { get; set; } =
        (name, key, ct) => GridDb.GetBestHeroBytesByNameAsync(name, key, ct);

    /// <summary>What a banner is filed under: the Steam App ID, else the game's own id. Null when neither can be a file name.</summary>
    internal static string? KeyFor(string? steamAppId, string gameId)
    {
        if (UrlProtocolHelper.IsValidSteamAppId(steamAppId)) return steamAppId!.Trim();
        return GameDataCleanup.IsSafeKey(gameId) ? gameId : null;
    }

    /// <summary>The banner already on disk for this game, or null.</summary>
    public string? CachedPath(string? steamAppId, string gameId)
    {
        string? key = KeyFor(steamAppId, gameId);
        if (key == null) return null;
        string path = Path.Combine(_directory, $"{key}.jpg");
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 1000 ? path : null;
        }
        catch (IOException)
        {
            // A file that cannot be read now is the same as no banner yet: it is fetched again.
            return null;
        }
    }

    /// <summary>The banner the player chose for this game, when its file is still there.</summary>
    public static string? CustomPath(GameEntry game) =>
        !string.IsNullOrWhiteSpace(game.HeroImagePath) && File.Exists(game.HeroImagePath) ? game.HeroImagePath : null;

    /// <summary>
    /// The banner for a game: the one the player chose, else from disk, else fetched and kept. Null when there is none, which is
    /// remembered so the next open does not ask again until <see cref="RetryAfter"/> has passed.
    /// </summary>
    /// <param name="steamAppId">The game's Steam App ID, or the one Steam matched it to by name.</param>
    /// <param name="steamGridDbApiKey">The player's SteamGridDB key, or null for Steam's banner only.</param>
    /// <param name="minSimilarity">How closely a SteamGridDB title found by name must resemble the game's.</param>
    public async Task<string?> GetHeroPathAsync(GameEntry game, string? steamAppId, string? steamGridDbApiKey, double minSimilarity, CancellationToken ct = default)
    {
        if (CustomPath(game) is { } custom) return custom;
        return await GetAutomaticPathAsync(game, steamAppId, steamGridDbApiKey, minSimilarity, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets what was kept (and any note that none was found) and looks again, ignoring a banner the player chose:
    /// Edit Game's "Find again", which shows what Automatic would give.
    /// </summary>
    public async Task<string?> RefetchAsync(GameEntry game, string? steamAppId, string? steamGridDbApiKey, double minSimilarity, CancellationToken ct = default)
        => await GetAutomaticPathAsync(game, steamAppId, steamGridDbApiKey, minSimilarity, ct, ignoreKept: true).ConfigureAwait(false);

    /// <param name="ignoreKept">Look again whatever is kept or noted. What is kept stays until a new banner is in hand, so a look that fails (offline) loses nothing.</param>
    private async Task<string?> GetAutomaticPathAsync(GameEntry game, string? steamAppId, string? steamGridDbApiKey, double minSimilarity, CancellationToken ct, bool ignoreKept = false)
    {
        string? key = KeyFor(steamAppId, game.Id);
        if (key == null) return null;

        string miss = Path.Combine(_directory, $"{key}.none");
        bool hasKey = !string.IsNullOrWhiteSpace(steamGridDbApiKey);
        if (!ignoreKept)
        {
            if (CachedPath(steamAppId, game.Id) is { } cached) return cached;
            try
            {
                // A note made before there was a SteamGridDB key does not stand once there is one: that site may have what Steam has not.
                if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < RetryAfter && !(hasKey && !NoteWasMadeWithKey(miss))) return null;
            }
            catch (IOException)
            {
                // An unreadable note is the same as none.
            }
        }

        try
        {
            var found = await FetchAsync(game, steamAppId, steamGridDbApiKey, minSimilarity, ct).ConfigureAwait(false);
            if (found.Bytes is not { } bytes)
            {
                // Only an answer of "none" is remembered. A look that could not finish (offline, a server error) is asked again next time.
                if (!found.Incomplete) RememberMiss(miss, hasKey);
                return ignoreKept ? CachedPath(steamAppId, game.Id) : null;
            }

            string saved = await SteamMetadataService.WritePosterFileSafelyAsync(Path.Combine(_directory, $"{key}.jpg"), bytes, ct).ConfigureAwait(false);
            try { if (File.Exists(miss)) File.Delete(miss); } catch (IOException) { /* an old note beside a banner is harmless */ }
            return saved;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The page was closed before the banner arrived; nothing is kept and nothing is noted as missing.
            return null;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("HeroArt", $"Could not fetch the banner for '{game.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>What a look found: the banner, or nothing; and whether a source could not be asked (so "nothing" is not yet an answer).</summary>
    private readonly record struct Found(byte[]? Bytes, bool Incomplete);

    private async Task<Found> FetchAsync(GameEntry game, string? steamAppId, string? gridKey, double minSimilarity, CancellationToken ct)
    {
        bool incomplete = false;

        // A source that cannot be reached is skipped, and the look is marked unfinished.
        async Task<T?> Ask<T>(Func<Task<T?>> source) where T : class
        {
            try { return await source().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Not an answer: the look is unfinished, and nothing is noted as missing.
                incomplete = true;
                return null;
            }
        }

        if (UrlProtocolHelper.IsValidSteamAppId(steamAppId))
        {
            string id = steamAppId!.Trim();
            foreach (string url in new[]
            {
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{id}/library_hero.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/library_hero.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_hero.jpg",
            })
            {
                if (UsableBanner(await Ask(() => DownloadBytes(url, ct)).ConfigureAwait(false)) is { } steam) return new Found(steam, false);
            }

            if (!string.IsNullOrWhiteSpace(gridKey)
                && UsableBanner(await Ask(() => GridByAppId(id, gridKey, ct)).ConfigureAwait(false)) is { } byId)
                return new Found(byId, false);
        }

        // A game Steam lists has its banner under its App ID; a name search is for a game with no listing, where a loose hit could be another title's art.
        if (!UrlProtocolHelper.IsValidSteamAppId(steamAppId)
            && !string.IsNullOrWhiteSpace(gridKey) && !string.IsNullOrWhiteSpace(game.Name)
            && await Ask(async () => (object?)await GridByName(game.Name, gridKey, ct).ConfigureAwait(false)).ConfigureAwait(false) is NamedGridArt named)
        {
            // The community search can hand back a loosely related game; a wrong banner is worse than none.
            if (SteamSearchService.CalculateSimilarity(game.Name, named.MatchedName) >= minSimilarity)
                return new Found(UsableBanner(named.Bytes), incomplete);
            LoggingService.Verbose("HeroArt", $"Rejected the SteamGridDB banner for '{game.Name}': matched '{named.MatchedName}', too dissimilar.");
        }

        return new Found(null, incomplete);
    }

    /// <summary>The bytes when they are a real, wide picture; otherwise null.</summary>
    internal static byte[]? UsableBanner(byte[]? bytes)
    {
        if (bytes == null || bytes.Length <= 1000 || !SteamMetadataService.IsDecodableImage(bytes)) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = System.Drawing.Image.FromStream(ms);
            return img.Height > 0 && (double)img.Width / img.Height >= MinAspect ? bytes : null;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("HeroArt", ex, "measuring a downloaded banner");
            return null;
        }
    }

    /// <summary>The note says whether the look that made it had a SteamGridDB key (its last line is "key"); an older note does not.</summary>
    private static bool NoteWasMadeWithKey(string path)
    {
        try { return File.ReadAllText(path).EndsWith("\nkey", StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A note that cannot be read stands: asking again is not worth a guess.
            return true;
        }
    }

    private void RememberMiss(string path, bool hadKey)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O") + (hadKey ? "\nkey" : "\nnokey"));
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("HeroArt", ex, "noting that no banner was found");
        }
    }

    private static async Task<byte[]?> DownloadFromWebAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeout);
            using var response = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            // A server that is struggling has not said there is no banner; any other refusal (404) has.
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            return null;
        }
        // Unreachable or timed out: not an answer. The caller notes the look as unfinished and tries the next address.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("The request timed out.");
        }
    }
}
