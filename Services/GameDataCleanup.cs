using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Everything TrayTrigger keeps for a library entry outside games.json, and its cleanup once the
/// entry is gone. Per entry that is:
/// <list type="bullet">
/// <item><c>Icons\{Id}.png</c> - the extracted or custom icon.</item>
/// <item><c>Covers\{Id}.*</c> - a custom poster (keeps the chosen file's extension) or
/// SteamGridDB art fetched by name.</item>
/// <item><c>Covers\{SteamAppId}.jpg</c> - the Steam poster, shared by every entry with that App ID.</item>
/// <item><c>{key}_{ticks}.jpg</c> - the name <see cref="SteamMetadataService.WritePosterFileSafelyAsync"/>
/// falls back to when the normal file is locked.</item>
/// <item><c>steam-cache.json[SteamAppId]</c> and <c>rawg-cache.json[RawgId]</c> - fetched details,
/// again shared by every entry with that id.</item>
/// </list>
/// A file or cache entry is only removed when no remaining entry references its path or owns its
/// key, so removing one of two entries for the same game never blanks the other one's art.
/// </summary>
public static class GameDataCleanup
{
    public readonly record struct Result(int FilesDeleted, int SteamEntriesRemoved, int RawgEntriesRemoved)
    {
        public override string ToString() =>
            $"{FilesDeleted} file(s), {SteamEntriesRemoved} Steam and {RawgEntriesRemoved} RAWG cache entr{(SteamEntriesRemoved + RawgEntriesRemoved == 1 ? "y" : "ies")}";
    }

    /// <summary>Deletes the files and cache entries that belonged only to <paramref name="removed"/>.</summary>
    public static Result DeleteRemovedGameData(
        IReadOnlyCollection<GameEntry> removed,
        IReadOnlyCollection<GameEntry> remaining,
        string iconsDirectory,
        string coversDirectory)
    {
        if (removed.Count == 0) return default;

        var live = new LiveReferences(remaining);
        var iconKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coverKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamIds = new List<string>();
        var rawgIds = new List<int>();

        foreach (var game in removed)
        {
            if (IsSafeKey(game.Id))
            {
                iconKeys.Add(game.Id);
                coverKeys.Add(game.Id);
            }

            string? appId = game.SteamAppId?.Trim();
            if (!string.IsNullOrEmpty(appId) && !live.SteamAppIds.Contains(appId))
            {
                steamIds.Add(appId);
                if (IsSafeKey(appId)) coverKeys.Add(appId);
            }

            if (game.RawgId > 0 && !live.RawgIds.Contains(game.RawgId))
            {
                rawgIds.Add(game.RawgId);
            }

            AddIfDirectlyInside(candidates, game.IconPath, iconsDirectory);
            AddIfDirectlyInside(candidates, game.CoverImagePath, coversDirectory);
        }

        candidates.UnionWith(FilesWithKeys(iconsDirectory, iconKeys));
        candidates.UnionWith(FilesWithKeys(coversDirectory, coverKeys));

        int files = 0;
        foreach (var path in candidates)
        {
            if (live.Owns(path)) continue;
            if (TryDelete(path)) files++;
        }

        int steam = SteamMetadataService.InvalidateCache(steamIds);
        int rawg = RawgService.InvalidateCache(rawgIds);
        return new Result(files, steam, rawg);
    }

    // There is deliberately no "sweep everything no entry references" pass. One shipped briefly
    // in the 1.4.0 pre-release and deleted a user's real icons and posters: it trusted games.json
    // as the whole truth while the running library had never been saved to it. Only the removed
    // entries' own keys are ever touched here.

    /// <summary>The key a cached file belongs to: its name without extension, minus the
    /// <c>_{ticks}</c> suffix of a locked-file fallback write.</summary>
    internal static string KeyOf(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int underscore = stem.LastIndexOf('_');
        if (underscore > 0 && underscore < stem.Length - 1 &&
            stem.AsSpan(underscore + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            return stem[..underscore];
        }
        return stem;
    }

    private sealed class LiveReferences
    {
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fileNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> SteamAppIds = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<int> RawgIds = new();

        public LiveReferences(IEnumerable<GameEntry> games)
        {
            foreach (var game in games)
            {
                AddPath(game.IconPath);
                AddPath(game.CoverImagePath);
                if (!string.IsNullOrWhiteSpace(game.Id)) _keys.Add(game.Id);

                string? appId = game.SteamAppId?.Trim();
                if (!string.IsNullOrEmpty(appId))
                {
                    SteamAppIds.Add(appId);
                    _keys.Add(appId);
                }

                if (game.RawgId > 0) RawgIds.Add(game.RawgId);
            }
        }

        private void AddPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                _paths.Add(Path.GetFullPath(path));
                // By name as well: games.json can still hold a pre-%LocalAppData% path that
                // StorageService.RemapLegacyPaths hasn't redirected to this folder yet.
                _fileNames.Add(Path.GetFileName(path));
            }
            catch
            {
                // Not a usable path - it can't be pointing at one of our files either.
            }
        }

        /// <summary>True when a remaining entry references this file or owns its key.</summary>
        public bool Owns(string fullPath) =>
            _paths.Contains(fullPath) ||
            _fileNames.Contains(Path.GetFileName(fullPath)) ||
            _keys.Contains(KeyOf(fullPath));
    }

    private static bool IsSafeKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        key != "." && key != ".." &&
        key.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Adds <paramref name="path"/> only when it sits directly inside
    /// <paramref name="directory"/> - never a user's original image or a game's own files.</summary>
    private static void AddIfDirectlyInside(HashSet<string> set, string? path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (string.Equals(Path.GetDirectoryName(fullPath), fullDirectory, StringComparison.OrdinalIgnoreCase))
            {
                set.Add(fullPath);
            }
        }
        catch
        {
            // Malformed path - nothing of ours to delete.
        }
    }

    private static IEnumerable<string> FilesWithKeys(string directory, HashSet<string> keys) =>
        keys.Count == 0 ? [] : EnumerateFiles(directory).Where(p => keys.Contains(KeyOf(p)));

    private static List<string> EnumerateFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(Path.GetFullPath(directory)).ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDataCleanup", $"Could not list '{directory}': {ex.Message}");
            return [];
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GameDataCleanup", $"Failed to delete '{path}': {ex.Message}");
            return false;
        }
    }
}
