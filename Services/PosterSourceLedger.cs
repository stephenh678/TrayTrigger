using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Where a cached Steam poster (<c>Covers\{appId}.jpg</c>) came from.</summary>
public enum PosterSource
{
    /// <summary>Steam's own vertical library art. Nothing beats it.</summary>
    Steam,
    /// <summary>SteamGridDB community art, used because Steam had none.</summary>
    SteamGridDb,
    /// <summary>A banner composited into a poster, fetched with no SteamGridDB key to try first.</summary>
    Banner,
    /// <summary>A banner composited into a poster after SteamGridDB was asked and had nothing.</summary>
    BannerAfterSteamGridDb
}

/// <summary>
/// Remembers which tier of <see cref="SteamMetadataService.DownloadAndCachePosterAsync"/> each
/// cached Steam poster came from, in <c>poster-sources.json</c> beside the Steam details cache.
///
/// <para>The poster file alone can't say. A banner stand-in cached before the user had a
/// SteamGridDB key looked exactly like a finished poster, so it was served from the cache for
/// good: a game added after the key was entered still got the banner whenever an earlier run had
/// cached one, and only Refresh All Game Posters, which skips the cache, replaced it. With the
/// source on record, a banner (or a poster from before this was recorded) is looked at again once
/// there is a key that might do better, and a poster from Steam or SteamGridDB never is.</para>
/// </summary>
public static class PosterSourceLedger
{
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrayTrigger", "poster-sources.json");

    private static string _filePath = DefaultFilePath;
    private static readonly object Lock = new();
    private static Dictionary<string, PosterSource>? _sources;

    /// <summary>Test seam: points the ledger at another file (null restores the real one).</summary>
    internal static void UseFileForTests(string? path)
    {
        lock (Lock)
        {
            _filePath = path ?? DefaultFilePath;
            _sources = null;
        }
    }

    public static PosterSource? Get(string appId)
    {
        lock (Lock)
        {
            return LoadLocked().TryGetValue(appId.Trim(), out var source) ? source : null;
        }
    }

    public static void Set(string appId, PosterSource source)
    {
        lock (Lock)
        {
            var sources = LoadLocked();
            if (sources.TryGetValue(appId.Trim(), out var existing) && existing == source)
                return;
            sources[appId.Trim()] = source;
            SaveLocked();
        }
    }

    /// <summary>
    /// True when the cached poster for <paramref name="appId"/> should be looked at again because
    /// a SteamGridDB key is now available: it's a banner fetched without one, or it was cached
    /// before sources were recorded. Each is retried once; the retry records the answer.
    /// </summary>
    public static bool MayImprove(string appId, string? steamGridDbApiKey) =>
        !string.IsNullOrWhiteSpace(steamGridDbApiKey) && Get(appId) is null or PosterSource.Banner;

    private static Dictionary<string, PosterSource> LoadLocked()
    {
        if (_sources != null)
            return _sources;

        _sources = new Dictionary<string, PosterSource>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_filePath))
            {
                var stored = JsonSerializer.Deserialize(File.ReadAllText(_filePath), AppJsonContext.Default.DictionaryStringString);
                foreach (var (appId, name) in stored ?? new Dictionary<string, string>())
                {
                    if (Enum.TryParse(name, out PosterSource source))
                        _sources[appId] = source;
                }
            }
        }
        catch (Exception ex)
        {
            // Losing the record only means each cached poster is checked once more.
            LoggingService.Warn("SteamMetadata", $"Poster source record unreadable, starting fresh: {ex.Message}");
        }
        return _sources;
    }

    private static void SaveLocked()
    {
        if (_sources == null)
            return;

        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (appId, source) in _sources)
                stored[appId] = source.ToString();

            string tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(stored, AppJsonContext.Default.DictionaryStringString));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamMetadata", $"Could not save the poster source record: {ex.Message}");
        }
    }
}
