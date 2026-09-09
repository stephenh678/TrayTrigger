using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public record DiscoveredGogGame(
    string GameId,
    string Name,
    string InstallDir,
    string? ExePath,
    string? LaunchParam,
    string? IconPath,
    bool IsAlreadyImported
);

/// <summary>
/// Discovers installed GOG games by reading the per-game registry entries GOG's Windows
/// installers write - both the Galaxy client's own installs and the standalone/offline
/// installers write the same keys, so this works whether or not Galaxy is even installed.
/// GOG's installer is 32-bit, so these always live under the WOW6432Node view on 64-bit
/// Windows; <see cref="RegistryView.Registry32"/> below reads that view transparently without
/// needing to hardcode "WOW6432Node" in the path.
/// </summary>
public class GogScannerService
{
    private const string GogRootPath = @"SOFTWARE\GOG.com";
    private const string GamesKeyPath = GogRootPath + @"\Games";
    private const string GalaxyClientPathsKeyPath = GogRootPath + @"\GalaxyClient\paths";

    /// <summary>Full path to GalaxyClient.exe, or null if GOG Galaxy isn't installed - callers
    /// should fall back to launching the game's own exe directly in that case.</summary>
    public string? GetGalaxyClientPath()
    {
        try
        {
            using var baseKey = RegistryHelper.OpenLocalMachine32();
            using var pathsKey = baseKey.OpenSubKey(GalaxyClientPathsKeyPath);
            if (pathsKey?.GetValue("client") is string clientDir && !string.IsNullOrWhiteSpace(clientDir))
            {
                string exePath = Path.Combine(clientDir, "GalaxyClient.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GogScannerService", $"Error locating GalaxyClient.exe: {ex.Message}");
        }
        return null;
    }

    public List<DiscoveredGogGame> ScanInstalledGames(IEnumerable<string> existingGameIds)
    {
        var existingSet = new HashSet<string>(existingGameIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredGogGame>();

        try
        {
            using var baseKey = RegistryHelper.OpenLocalMachine32();
            using var gamesKey = baseKey.OpenSubKey(GamesKeyPath);
            if (gamesKey == null)
            {
                return results;
            }

            foreach (var subKeyName in gamesKey.GetSubKeyNames())
            {
                var game = ParseGameKey(gamesKey, subKeyName, existingSet);
                if (game != null)
                {
                    results.Add(game);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GogScannerService", $"Error scanning installed GOG games: {ex.Message}");
        }

        return results.OrderBy(g => g.Name).ToList();
    }

    private DiscoveredGogGame? ParseGameKey(RegistryKey gamesKey, string subKeyName, HashSet<string> existingSet)
    {
        try
        {
            using var gameKey = gamesKey.OpenSubKey(subKeyName);
            if (gameKey == null) return null;

            string? gameId = gameKey.GetValue("gameID") as string;
            string? name = gameKey.GetValue("gameName") as string;
            string? installDir = gameKey.GetValue("path") as string;
            string? exePath = gameKey.GetValue("exe") as string;
            string? launchParam = gameKey.GetValue("launchParam") as string;
            string? dependsOn = gameKey.GetValue("dependsOn") as string;

            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(name))
                return null;

            // DLC/expansion entries depend on a base game's gameID and have no exe of their own -
            // they're not separately launchable, so they'd otherwise show up as phantom "games".
            if (!string.IsNullOrWhiteSpace(dependsOn))
                return null;

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return null;

            // GOG drops a goggame-<id>.ico right in the install dir for the base game - prefer
            // that (same "platform-provided artwork first" pattern as SteamScannerService's icon
            // resolution) and fall back to the exe's own icon otherwise.
            string? iconPath = null;
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                string candidateIcon = Path.Combine(installDir, $"goggame-{gameId}.ico");
                if (File.Exists(candidateIcon))
                {
                    iconPath = candidateIcon;
                }
            }
            iconPath ??= exePath;

            return new DiscoveredGogGame(
                GameId: gameId,
                Name: name,
                InstallDir: installDir ?? Path.GetDirectoryName(exePath) ?? string.Empty,
                ExePath: exePath,
                LaunchParam: string.IsNullOrWhiteSpace(launchParam) ? null : launchParam,
                IconPath: iconPath,
                IsAlreadyImported: existingSet.Contains(gameId)
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("GogScannerService", $"Error parsing GOG game registry key '{subKeyName}': {ex.Message}");
            return null;
        }
    }
}
