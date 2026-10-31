using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// How much room a game takes on disk, counted from the game's own folder: a read of what is there, on this PC,
/// with nothing sent anywhere. A folder that is too big to be one game, or one that cannot be read, gives no size.
/// </summary>
public static class InstallSizeService
{
    /// <summary>A folder with more files than this is not counted: it is probably not one game's, and counting it would take long.</summary>
    public const int MaxFiles = 400_000;

    /// <summary>A size read this recently is used again, so opening a game's page twice does not walk its folder twice.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private static readonly ConcurrentDictionary<string, (long Bytes, DateTime AtUtc)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder that holds the game, the same one the DLSS card looks in, or null when it has no local folder.</summary>
    public static string? GameRoot(GameEntry game)
    {
        try
        {
            string exe = game.ExecutablePath ?? string.Empty;
            string? root = Path.IsPathFullyQualified(exe) && File.Exists(exe)
                ? DlssProbeService.DlssSearchRoot(exe, game.WorkingDirectory)
                : SystemInfoService.GameFolder(game);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            // A drive's own root is never one game.
            return string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase) ? null : root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LoggingService.Verbose("InstallSize", $"Couldn't find the folder of '{game.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>The bytes in every file under <paramref name="root"/>, or null when there are too many files or it cannot be read.</summary>
    public static long? Measure(string root, CancellationToken ct = default, int maxFiles = MaxFiles)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Links lead out of the folder (or back into it); what they point at is not this game's.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        long total = 0;
        int files = 0;
        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                total += file.Length;
                if (++files > maxFiles) return null;
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LoggingService.Verbose("InstallSize", $"Couldn't count '{root}': {ex.Message}");
            return null;
        }
    }

    /// <summary><see cref="Measure"/>, remembered for <see cref="CacheFor"/>.</summary>
    public static long? MeasureCached(string root, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(root, out var seen) && DateTime.UtcNow - seen.AtUtc < CacheFor) return seen.Bytes;
        long? bytes = Measure(root, ct);
        if (bytes is long counted) Cache[root] = (counted, DateTime.UtcNow);
        return bytes;
    }

    /// <summary>"74.2 GB", "812 MB", "40 KB".</summary>
    public static string Format(long bytes)
    {
        const long KB = 1L << 10, MB = 1L << 20, GB = 1L << 30;
        var culture = CultureInfo.CurrentCulture;
        if (bytes >= GB) return string.Create(culture, $"{bytes / (double)GB:0.#} GB");
        if (bytes >= MB) return string.Create(culture, $"{bytes / (double)MB:0} MB");
        return string.Create(culture, $"{Math.Max(1, bytes / KB)} KB");
    }
}
