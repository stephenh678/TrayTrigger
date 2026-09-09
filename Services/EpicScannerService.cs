using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TrayTrigger.Services;

public record DiscoveredEpicGame(
    string AppName,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported
);

/// <summary>
/// Discovers installed Epic Games Store titles by reading Epic Games Launcher's own ".item"
/// manifest files - unlike EA, Epic writes a complete, structured JSON manifest per installed
/// game (InstallLocation, LaunchExecutable, AppName, DisplayName all present directly), so there's
/// no "search default install roots" fallback needed and no custom-install-location limitation.
/// </summary>
public class EpicScannerService
{
    private const string ManifestsDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

    /// <summary>True if Epic Games Launcher's "com.epicgames.launcher://" protocol is registered
    /// and its handler exe still exists - callers should fall back to launching the game's own exe
    /// directly otherwise.</summary>
    public bool IsEpicLauncherInstalled() => UrlProtocolHelper.IsHandlerInstalled("com.epicgames.launcher", nameof(EpicScannerService));

    public List<DiscoveredEpicGame> ScanInstalledGames(IEnumerable<string> existingAppNames)
    {
        var existingSet = new HashSet<string>(existingAppNames, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredEpicGame>();

        if (!Directory.Exists(ManifestsDir))
        {
            return results;
        }

        IEnumerable<string> manifestFiles;
        try
        {
            manifestFiles = Directory.EnumerateFiles(ManifestsDir, "*.item");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("EpicScannerService", $"Error enumerating '{ManifestsDir}': {ex.Message}");
            return results;
        }

        foreach (var manifestPath in manifestFiles)
        {
            var game = ParseManifest(manifestPath, existingSet);
            if (game != null)
            {
                results.Add(game);
            }
        }

        return results.OrderBy(g => g.Name).ToList();
    }

    private static DiscoveredEpicGame? ParseManifest(string manifestPath, HashSet<string> existingSet)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                return null;
            if (root.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind != JsonValueKind.True)
                return null;

            // Entries with a non-empty MainGameAppName are DLC/add-ons that depend on a base
            // game, not a separately launchable title - same reasoning as GOG's dependsOn filter.
            if (root.TryGetProperty("MainGameAppName", out var mainGame) && mainGame.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(mainGame.GetString()))
                return null;

            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array)
            {
                bool isGame = categories.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase));
                if (!isGame) return null;
            }

            string? appName = root.TryGetProperty("AppName", out var an) ? an.GetString() : null;
            string? displayName = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
            string? installDir = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
            string? launchExe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;

            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(displayName) ||
                string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(launchExe))
                return null;

            string exePath = Path.Combine(installDir, launchExe);
            if (!File.Exists(exePath)) return null;

            return new DiscoveredEpicGame(
                AppName: appName,
                Name: displayName,
                InstallDir: installDir,
                ExePath: exePath,
                IconPath: exePath,
                IsAlreadyImported: existingSet.Contains(appName)
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("EpicScannerService", $"Error parsing Epic manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }
}
