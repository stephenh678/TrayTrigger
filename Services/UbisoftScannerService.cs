using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public record DiscoveredUbisoftGame(
    string GameId,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported
);

/// <summary>
/// Discovers installed Ubisoft Connect games. Thinnest registry footprint of any platform
/// integrated so far: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\&lt;gameId&gt; gives only
/// an install directory (and language) - no display name, no exe pointer at all. Ubisoft's own
/// "uplay_install.manifest" file (present in each install folder) is a proprietary
/// compressed/encrypted blob, not readable JSON/XML like GOG/EA/Epic's manifests, so there's
/// nothing to parse there either. Rather than reinventing exe-discovery heuristics, this reuses
/// <see cref="FolderScannerService.ScanFolder"/> - the same scored candidate search "Add Folder"
/// already uses - against each install directory, and trusts its folder-derived name for the
/// display name, since Ubisoft's install folders are consistently named after the real game title
/// (e.g. "Tom Clancy's Rainbow Six Extraction").
/// </summary>
public class UbisoftScannerService
{
    private const string InstallsKeyPath = @"SOFTWARE\Ubisoft\Launcher\Installs";

    private readonly FolderScannerService _folderScannerService = new();

    /// <summary>True if Ubisoft Connect's "uplay://" launch protocol is registered and its handler
    /// exe still exists - callers should fall back to launching the game's own exe directly otherwise.</summary>
    public bool IsUbisoftConnectInstalled() => UrlProtocolHelper.IsHandlerInstalled("uplay", nameof(UbisoftScannerService));

    public List<DiscoveredUbisoftGame> ScanInstalledGames(IEnumerable<string> existingGameIds)
    {
        var existingSet = new HashSet<string>(existingGameIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredUbisoftGame>();

        try
        {
            using var baseKey = RegistryHelper.OpenLocalMachine32();
            using var installsKey = baseKey.OpenSubKey(InstallsKeyPath);
            if (installsKey == null) return results;

            foreach (var gameId in installsKey.GetSubKeyNames())
            {
                var game = ParseInstallEntry(installsKey, gameId, existingSet);
                if (game != null)
                {
                    results.Add(game);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("UbisoftScannerService", $"Error scanning installed Ubisoft games: {ex.Message}");
        }

        return results.OrderBy(g => g.Name).ToList();
    }

    /// <summary>
    /// Resolves the single installed Ubisoft game whose registry InstallDir contains
    /// <paramref name="path"/>, or null. Used by <see cref="PlatformLookupService"/> so a game
    /// dropped/browsed into the library from a Ubisoft install directory is imported with its real
    /// game ID rather than as a Local exe. Only the matched install pays for the folder scan.
    /// </summary>
    public DiscoveredUbisoftGame? FindGameByPath(string path)
    {
        foreach (var (gameId, installDir) in GetInstallDirs())
        {
            if (PlatformLookupService.IsPathUnderDirectory(path, installDir))
            {
                return ResolveInstall(gameId, installDir);
            }
        }
        return null;
    }

    /// <summary>Every registered Ubisoft install's (gameId, InstallDir) from the registry alone -
    /// no folder scan. <see cref="PlatformLookupService"/> caches this once per import operation.</summary>
    public List<(string GameId, string InstallDir)> GetInstallDirs()
    {
        var results = new List<(string, string)>();
        try
        {
            using var baseKey = RegistryHelper.OpenLocalMachine32();
            using var installsKey = baseKey.OpenSubKey(InstallsKeyPath);
            if (installsKey == null) return results;

            foreach (var gameId in installsKey.GetSubKeyNames())
            {
                string? installDir = ReadInstallDir(installsKey, gameId);
                if (installDir != null) results.Add((gameId, installDir));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("UbisoftScannerService", $"Error reading Ubisoft install list: {ex.Message}");
        }
        return results;
    }

    /// <summary>The full discovery record for one install (runs the folder scan for its exe), or null.</summary>
    public DiscoveredUbisoftGame? ResolveInstall(string gameId, string installDir)
        => ResolveInstall(gameId, installDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static string? ReadInstallDir(RegistryKey installsKey, string gameId)
    {
        using var gameKey = installsKey.OpenSubKey(gameId);
        string? installDir = gameKey?.GetValue("InstallDir") as string;
        if (string.IsNullOrWhiteSpace(installDir)) return null;

        // Ubisoft's own registry writes forward slashes (e.g. "C:/Program Files (x86)/...") -
        // normalize before any Path/Directory API call.
        return installDir.Replace('/', '\\').TrimEnd('\\');
    }

    private DiscoveredUbisoftGame? ParseInstallEntry(RegistryKey installsKey, string gameId, HashSet<string> existingSet)
    {
        try
        {
            string? installDir = ReadInstallDir(installsKey, gameId);
            if (installDir == null) return null;
            return ResolveInstall(gameId, installDir, existingSet);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("UbisoftScannerService", $"Error parsing Ubisoft install entry '{gameId}': {ex.Message}");
            return null;
        }
    }

    private DiscoveredUbisoftGame? ResolveInstall(string gameId, string installDir, HashSet<string> existingSet)
    {
        try
        {
            if (!Directory.Exists(installDir)) return null;

            // Skip the expensive folder scan entirely for a game already in the library - the
            // scan result would only be discarded (via IsAlreadyImported) after paying the cost.
            if (existingSet.Contains(gameId)) return null;

            var candidates = _folderScannerService.ScanFolder(installDir);
            var best = candidates.FirstOrDefault();
            if (best == null) return null;

            return new DiscoveredUbisoftGame(
                GameId: gameId,
                Name: best.Name,
                InstallDir: installDir,
                ExePath: best.ExePath,
                IconPath: best.ExePath,
                IsAlreadyImported: existingSet.Contains(gameId)
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("UbisoftScannerService", $"Error parsing Ubisoft install entry '{gameId}': {ex.Message}");
            return null;
        }
    }
}
