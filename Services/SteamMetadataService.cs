using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public partial class SteamMetadataService
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly ConcurrentDictionary<string, SteamAppDetails> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AppLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SteamGridDbService GridDbService = new();

    public static readonly string CoversDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrayTrigger", "Covers");

    public SteamMetadataService()
    {
        try
        {
            if (!Directory.Exists(CoversDirectory))
            {
                Directory.CreateDirectory(CoversDirectory);
            }

            // Migrate covers from legacy Documents\TrayTrigger\Covers if present
            string legacyCovers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrayTrigger", "Covers");
            if (Directory.Exists(legacyCovers))
            {
                foreach (var file in Directory.GetFiles(legacyCovers))
                {
                    string dest = Path.Combine(CoversDirectory, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        try { File.Copy(file, dest, overwrite: false); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamMetadataService", $"Error initializing covers dir: {ex.Message}");
        }
    }

    /// <param name="steamGridDbApiKey">
    /// Optional SteamGridDB API key. When provided, community-sourced vertical grid art is
    /// tried only after Steam's own official library art comes up empty, and before falling
    /// back to a composited banner/header image as a last resort.
    /// </param>
    /// <param name="forceRefresh">
    /// Bypasses the in-memory details cache and the on-disk "poster file already exists" check,
    /// re-running the full lookup/download chain even if a (possibly fallback-quality) result was
    /// already cached. Without this, a game that already has a cover - even one from the
    /// composited-banner fallback tier - is never revisited, so e.g. adding a SteamGridDB key
    /// later would never improve an already-imported game's poster.
    /// </param>
    /// <summary>
    /// Removes a single AppId's cached details, so the next lookup re-runs the full fetch
    /// chain instead of returning a possibly-stale in-memory result (e.g. one whose
    /// CoverImagePath points at a poster file that was since deleted from disk).
    /// </summary>
    public static void InvalidateCache(string appId)
    {
        if (!string.IsNullOrWhiteSpace(appId))
        {
            Cache.TryRemove(appId.Trim(), out _);
        }
    }

    public async Task<SteamAppDetails?> GetAppDetailsAsync(string appId, string? steamGridDbApiKey = null, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        string trimmedId = appId.Trim();
        if (!forceRefresh && Cache.TryGetValue(trimmedId, out var cached))
            return cached;

        // Serialize all fetch/poster-download work per AppId so two callers enriching
        // duplicate GameEntry objects for the same Steam game never race on the same
        // cached poster file (or issue duplicate network requests).
        var appLock = AppLocks.GetOrAdd(trimmedId, _ => new SemaphoreSlim(1, 1));
        await appLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && Cache.TryGetValue(trimmedId, out cached))
                return cached;

            var details = new SteamAppDetails
            {
                AppId = trimmedId
            };

            try
            {
                // 1. Fetch Store AppDetails
                string appDetailsUrl = $"https://store.steampowered.com/api/appdetails?appids={trimmedId}&l=english";
                using var response = await HttpClient.GetAsync(appDetailsUrl, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (doc.RootElement.TryGetProperty(trimmedId, out var appElement) &&
                        appElement.TryGetProperty("success", out var successProp) &&
                        successProp.GetBoolean() &&
                        appElement.TryGetProperty("data", out var data))
                    {
                        ParseAppDetailsData(data, details);
                    }
                }

                // 2. Fetch Reviews Summary
                await FetchReviewSummaryAsync(trimmedId, details, cancellationToken).ConfigureAwait(false);

                // 3. Fetch News / Patch Notes
                await FetchNewsItemsAsync(trimmedId, details, cancellationToken).ConfigureAwait(false);

                // 4. Download and cache Cover Image (Poster 600x900 or Header)
                string? cachedCover = await DownloadAndCachePosterAsync(trimmedId, details.HeaderImageUrl ?? details.CapsuleImageUrl, steamGridDbApiKey, forceRefresh, cancellationToken).ConfigureAwait(false);
                details.CoverImagePath = cachedCover;

                // Only cache genuinely successful lookups. A transient failure or a
                // rate-limited/empty Steam response would otherwise be remembered as
                // "no data" for the rest of the process lifetime, permanently blocking
                // enrichment for this AppId until the app is restarted.
                if (!string.IsNullOrEmpty(details.Name))
                {
                    Cache[trimmedId] = details;
                    return details;
                }

                return null;
            }
            catch (Exception ex)
            {
                LoggingService.Warn("SteamMetadataService", $"Error fetching details for AppId {trimmedId}: {ex.Message}");
                return details.Name.Length > 0 ? details : null;
            }
        }
        finally
        {
            appLock.Release();
        }
    }

    private static void ParseAppDetailsData(JsonElement data, SteamAppDetails details)
    {
        if (data.TryGetProperty("name", out var n))
            details.Name = n.GetString() ?? string.Empty;

        // Developers & Publishers
        if (data.TryGetProperty("developers", out var devs) && devs.ValueKind == JsonValueKind.Array)
        {
            var devList = new List<string>();
            foreach (var d in devs.EnumerateArray())
            {
                string? str = d.GetString();
                if (!string.IsNullOrWhiteSpace(str)) devList.Add(str.Trim());
            }
            details.Developers = string.Join(", ", devList);
        }

        if (data.TryGetProperty("publishers", out var pubs) && pubs.ValueKind == JsonValueKind.Array)
        {
            var pubList = new List<string>();
            foreach (var p in pubs.EnumerateArray())
            {
                string? str = p.GetString();
                if (!string.IsNullOrWhiteSpace(str)) pubList.Add(str.Trim());
            }
            details.Publishers = string.Join(", ", pubList);
        }

        // Release Date
        if (data.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var rdd))
        {
            details.ReleaseDate = rdd.GetString() ?? string.Empty;
        }

        // Short Description
        if (data.TryGetProperty("short_description", out var sd))
        {
            details.ShortDescription = CleanHtmlText(sd.GetString() ?? string.Empty);
        }

        // Images
        if (data.TryGetProperty("header_image", out var hi))
            details.HeaderImageUrl = hi.GetString();

        if (data.TryGetProperty("capsule_imagev5", out var civ5))
            details.CapsuleImageUrl = civ5.GetString();
        else if (data.TryGetProperty("capsule_image", out var ci))
            details.CapsuleImageUrl = ci.GetString();

        // Metacritic
        if (data.TryGetProperty("metacritic", out var mc))
        {
            if (mc.TryGetProperty("score", out var scoreProp) && scoreProp.TryGetInt32(out int score))
                details.MetacriticScore = score;
            if (mc.TryGetProperty("url", out var urlProp))
                details.MetacriticUrl = urlProp.GetString();
        }

        // Genres
        if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in genres.EnumerateArray())
            {
                if (g.TryGetProperty("description", out var desc))
                {
                    string? gName = desc.GetString();
                    if (!string.IsNullOrWhiteSpace(gName) && !details.Genres.Contains(gName))
                    {
                        details.Genres.Add(gName.Trim());
                    }
                }
            }
        }

        // Categories / Play Modes
        if (data.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cats.EnumerateArray())
            {
                if (c.TryGetProperty("description", out var cDesc))
                {
                    string? catName = cDesc.GetString();
                    if (IsRelevantPlayMode(catName) && !details.PlayModes.Contains(catName!))
                    {
                        details.PlayModes.Add(catName!);
                    }
                }
            }
        }

        // Controller support explicitly on root
        if (data.TryGetProperty("controller_support", out var cs))
        {
            string? csVal = cs.GetString();
            if (!string.IsNullOrWhiteSpace(csVal))
            {
                string badge = csVal.Equals("full", StringComparison.OrdinalIgnoreCase) ? "Full controller support" : "Partial controller support";
                if (!details.PlayModes.Contains(badge))
                    details.PlayModes.Insert(0, badge);
            }
        }

        // PC Requirements
        if (data.TryGetProperty("pc_requirements", out var pcr) && pcr.ValueKind == JsonValueKind.Object)
        {
            if (pcr.TryGetProperty("minimum", out var minProp))
            {
                string minRaw = minProp.GetString() ?? string.Empty;
                details.PcRequirementsMin = CleanRequirementsHtml(minRaw, "Minimum:");
            }
            if (pcr.TryGetProperty("recommended", out var recProp))
            {
                string recRaw = recProp.GetString() ?? string.Empty;
                details.PcRequirementsRec = CleanRequirementsHtml(recRaw, "Recommended:");
            }
        }
    }

    private static async Task FetchReviewSummaryAsync(string appId, SteamAppDetails details, CancellationToken ct)
    {
        try
        {
            string url = $"https://store.steampowered.com/appreviews/{appId}?json=1&language=all&purchase_type=all";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("query_summary", out var qs))
            {
                string? reviewDesc = qs.TryGetProperty("review_score_desc", out var rsd) ? rsd.GetString() : null;
                int totalPositive = qs.TryGetProperty("total_positive", out var tp) && tp.TryGetInt32(out int p) ? p : 0;
                int totalReviews = qs.TryGetProperty("total_reviews", out var tr) && tr.TryGetInt32(out int r) ? r : 0;

                if (totalReviews > 0)
                {
                    int percent = (int)Math.Round((double)totalPositive * 100 / totalReviews);
                    details.ReviewScorePercent = percent;
                    details.ReviewSummary = $"{reviewDesc ?? "Positive"} ({percent}% of {totalReviews:N0} reviews)";
                }
                else if (!string.IsNullOrWhiteSpace(reviewDesc))
                {
                    details.ReviewSummary = reviewDesc;
                }
            }
        }
        catch
        {
            // Non-fatal
        }
    }

    private static async Task FetchNewsItemsAsync(string appId, SteamAppDetails details, CancellationToken ct)
    {
        try
        {
            string url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v0002/?appid={appId}&count=3";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("appnews", out var an) &&
                an.TryGetProperty("newsitems", out var ni) &&
                ni.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ni.EnumerateArray())
                {
                    string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    string newsUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                    string author = item.TryGetProperty("author", out var a) ? a.GetString() ?? string.Empty : string.Empty;
                    long unixDate = item.TryGetProperty("date", out var d) && d.TryGetInt64(out long val) ? val : 0;
                    string rawContents = item.TryGetProperty("contents", out var c) ? c.GetString() ?? string.Empty : string.Empty;

                    DateTime dt = unixDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixDate).LocalDateTime : DateTime.MinValue;
                    string snippet = CleanHtmlText(rawContents);
                    if (snippet.Length > 170)
                        snippet = snippet.Substring(0, 167).TrimEnd() + "...";

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        details.NewsItems.Add(new SteamNewsItem
                        {
                            Title = title.Trim(),
                            Url = newsUrl,
                            Author = author,
                            Date = dt,
                            Snippet = snippet
                        });
                    }
                }
            }
        }
        catch
        {
            // Non-fatal
        }
    }

    public async Task<string?> DownloadAndCachePosterAsync(string appId, string? fallbackUrl = null, string? steamGridDbApiKey = null, bool forceRefresh = false, CancellationToken ct = default)
    {
        try
        {
            string localPath = Path.Combine(CoversDirectory, $"{appId}.jpg");
            if (!forceRefresh && File.Exists(localPath) && new FileInfo(localPath).Length > 1000)
            {
                return localPath;
            }

            // 1. Steam's own official library art first - genuine vertical box art, no
            // processing needed.
            var officialUrls = new List<string>
            {
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900_2x.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                $"https://steamcdn-a.akamaihd.net/steam/apps/{appId}/library_600x900.jpg"
            };

            if (await TryDownloadFromUrlsAsync(officialUrls, localPath, ct).ConfigureAwait(false))
            {
                return localPath;
            }

            // 2. Optional: community-sourced vertical art from SteamGridDB, tried only once
            // Steam's own official art comes up empty (e.g. a new/upcoming game).
            if (!string.IsNullOrWhiteSpace(steamGridDbApiKey))
            {
                byte[]? gridBytes = await GridDbService.GetBestVerticalGridBytesAsync(appId, steamGridDbApiKey, ct).ConfigureAwait(false);
                if (gridBytes != null && gridBytes.Length > 1000 && IsDecodableImage(gridBytes))
                {
                    byte[] processed = EnsureVerticalPoster(gridBytes);
                    await File.WriteAllBytesAsync(localPath, processed, ct).ConfigureAwait(false);
                    return localPath;
                }
            }

            // 3. Last resort: whatever banner art Steam does have (hero image, then the
            // store page's header/capsule image), composited into a full vertical poster.
            var fallbackUrls = new List<string>
            {
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg"
            };

            if (!string.IsNullOrWhiteSpace(fallbackUrl))
            {
                fallbackUrls.Add(fallbackUrl);
            }

            if (await TryDownloadFromUrlsAsync(fallbackUrls, localPath, ct).ConfigureAwait(false))
            {
                return localPath;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamMetadataService", $"Error downloading cover for AppId {appId}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Tries each candidate URL in order, saving the first response that decodes as a real
    /// image (processed into a vertical poster) to <paramref name="localPath"/>.
    /// </summary>
    private static async Task<bool> TryDownloadFromUrlsAsync(List<string> urls, string localPath, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            try
            {
                using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if (bytes.Length > 1000 && IsDecodableImage(bytes))
                    {
                        byte[] verticalBytes = EnsureVerticalPoster(bytes);
                        await File.WriteAllBytesAsync(localPath, verticalBytes, ct).ConfigureAwait(false);
                        return true;
                    }
                }
            }
            catch
            {
                // Try next candidate
            }
        }

        return false;
    }

    private const int PosterWidth = 600;
    private const int PosterHeight = 900;

    /// <summary>
    /// Guards against caching a non-image response body (e.g. an HTML error/rate-limit page
    /// that happened to come back with a 200 status and exceed the byte-size check) as a poster.
    /// </summary>
    private static bool IsDecodableImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = System.Drawing.Image.FromStream(ms);
            return img.Width > 0 && img.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] EnsureVerticalPoster(byte[] inputBytes)
    {
        try
        {
            using var ms = new MemoryStream(inputBytes);
            using var src = System.Drawing.Image.FromStream(ms);

            double aspect = (double)src.Width / src.Height;
            // If it's already in vertical 2:3 proportion (aspect <= 0.85), it's genuine official
            // library art - retain original fidelity untouched.
            if (aspect <= 0.85)
            {
                return inputBytes;
            }

            // No native vertical art was available, so this is a horizontal banner (header,
            // capsule, or hero image). Rather than hard-cropping most of it away to fill a
            // 600x900 frame, composite a full 600x900 "poster" that always shows 100% of the
            // artwork: a blurred, zoomed copy fills the frame so there's never empty space,
            // with the complete, uncropped source letterboxed on top.
            return ComposeFallbackPoster(src);
        }
        catch
        {
            return inputBytes;
        }
    }

    private static byte[] ComposeFallbackPoster(System.Drawing.Image src)
    {
        using var bmp = new System.Drawing.Bitmap(PosterWidth, PosterHeight);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 1. Blurred, zoomed-to-fill background so the frame is never empty.
            using (var blurredBg = CreateBlurredCoverFill(src, PosterWidth, PosterHeight))
            {
                g.DrawImage(blurredBg, 0, 0, PosterWidth, PosterHeight);
            }

            // 2. Darken it so the crisp foreground reads clearly against it.
            using (var overlay = new SolidBrush(Color.FromArgb(140, 10, 10, 14)))
            {
                g.FillRectangle(overlay, 0, 0, PosterWidth, PosterHeight);
            }

            // 3. The complete, uncropped source image, letterboxed on top - every logo,
            //    character, and title survives, unlike a hard center-crop.
            double fitScale = Math.Min((double)PosterWidth / src.Width, (double)PosterHeight / src.Height);
            int fitW = Math.Max(1, (int)Math.Round(src.Width * fitScale));
            int fitH = Math.Max(1, (int)Math.Round(src.Height * fitScale));
            int fitX = (PosterWidth - fitW) / 2;
            int fitY = (PosterHeight - fitH) / 2;
            g.DrawImage(src, fitX, fitY, fitW, fitH);
        }

        using var outMs = new MemoryStream();
        bmp.Save(outMs, ImageFormat.Jpeg);
        return outMs.ToArray();
    }

    /// <summary>
    /// Produces a targetW x targetH bitmap: the source scaled + center-cropped to completely
    /// fill the frame (like CSS background-size:cover), then softened with a cheap
    /// downsample/upsample blur so it reads as ambient backdrop rather than a hard crop.
    /// </summary>
    private static System.Drawing.Bitmap CreateBlurredCoverFill(System.Drawing.Image src, int targetW, int targetH)
    {
        double coverScale = Math.Max((double)targetW / src.Width, (double)targetH / src.Height);
        int scaledW = Math.Max(1, (int)Math.Round(src.Width * coverScale));
        int scaledH = Math.Max(1, (int)Math.Round(src.Height * coverScale));
        int offsetX = (targetW - scaledW) / 2;
        int offsetY = (targetH - scaledH) / 2;

        using var covered = new System.Drawing.Bitmap(targetW, targetH);
        using (var g = System.Drawing.Graphics.FromImage(covered))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, offsetX, offsetY, scaledW, scaledH);
        }

        const int blurSampleWidth = 48;
        int blurSampleHeight = Math.Max(1, (int)Math.Round(blurSampleWidth * ((double)targetH / targetW)));

        using var tiny = new System.Drawing.Bitmap(blurSampleWidth, blurSampleHeight);
        using (var g = System.Drawing.Graphics.FromImage(tiny))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(covered, 0, 0, blurSampleWidth, blurSampleHeight);
        }

        var blurred = new System.Drawing.Bitmap(targetW, targetH);
        using (var g = System.Drawing.Graphics.FromImage(blurred))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(tiny, 0, 0, targetW, targetH);
        }

        return blurred;
    }

    private static bool IsRelevantPlayMode(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName)) return false;
        string lower = categoryName.ToLowerInvariant();
        return lower is "single-player" or "multi-player" or "co-op" or "online co-op" or "lan co-op"
            or "pvp" or "online pvp" or "cross-platform multiplayer" or "full controller support"
            or "steam achievements" or "steam cloud";
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagsRegex();

    [GeneratedRegex(@"\[/?(b|i|u|url|img|h1|h2|h3|quote|list|\*)[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex BbCodeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespaceRegex();

    private static string CleanHtmlText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Strip HTML & BBCode
        string text = HtmlTagsRegex().Replace(html, " ");
        text = BbCodeRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = MultipleWhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }

    private static string CleanRequirementsHtml(string html, string prefixToRemove)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Convert <li> to clean bullets
        string formatted = html.Replace("<li>", "\n• ").Replace("</li>", "");
        formatted = formatted.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
        formatted = HtmlTagsRegex().Replace(formatted, " ");
        formatted = WebUtility.HtmlDecode(formatted);

        // Remove prefix like "Minimum:" or "Recommended:"
        if (!string.IsNullOrWhiteSpace(prefixToRemove) && formatted.StartsWith(prefixToRemove, StringComparison.OrdinalIgnoreCase))
        {
            formatted = formatted.Substring(prefixToRemove.Length);
        }

        // Clean up blank lines
        var lines = formatted.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cleanLines = new List<string>();
        foreach (var line in lines)
        {
            string t = MultipleWhitespaceRegex().Replace(line, " ").Trim();
            if (!string.IsNullOrWhiteSpace(t))
                cleanLines.Add(t);
        }

        return string.Join("\n", cleanLines).Trim();
    }
}
