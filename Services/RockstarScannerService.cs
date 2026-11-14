using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public record DiscoveredRockstarGame(
    string GameId,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported
);

/// <summary>
/// Discovers games installed through the Rockstar Games Launcher. The launcher's own title list
/// (titles.dat) is encrypted, but Rockstar records every game it knows of in the registry, under
/// HKLM\SOFTWARE\WOW6432Node\Rockstar Games\&lt;Title&gt;, with one install-folder value per store:
/// <c>InstallFolder</c> for the Rockstar launcher, <c>InstallFolderEpic</c> and <c>InstallFolderSteam</c>
/// for copies bought on those stores. Only <c>InstallFolder</c> is read here: an Epic or Steam copy
/// (Red Dead Redemption 2 bought on Epic has only <c>InstallFolderEpic</c>) belongs to that store's
/// scanner, which already imports it. The key name is the title, so it is the trusted name and the
/// game's identity; the exe comes from <see cref="FolderScannerService.ScanFolder"/>, and the session
/// is tracked by install folder, since Rockstar's entry programs (PlayGTAV.exe) hand over to the game.
/// </summary>
public class RockstarScannerService
{
    internal const string GamesKeyPath = @"SOFTWARE\Rockstar Games";

    /// <summary>The launcher's own key; its <c>InstallFolder</c> holds Launcher.exe.</summary>
    internal const string LauncherKeyName = "Launcher";

    /// <summary>The launcher's process name (Launcher.exe). Generic, so anything that ends it checks the path too.</summary>
    public const string ClientProcessName = "Launcher";

    /// <summary>Rockstar's own software, which records an InstallFolder like a game does.</summary>
    private static readonly string[] ClientComponentKeys = [LauncherKeyName, "Rockstar Games Social Club"];

    private readonly FolderScannerService _folderScannerService = new();
    private readonly Func<IReadOnlyList<(string Key, string? InstallFolder)>> _readKeys;

    public RockstarScannerService() : this(ReadRegistryKeys) { }

    /// <summary>Test seam: each Rockstar registry key's name and its <c>InstallFolder</c> value (null when absent).</summary>
    internal RockstarScannerService(Func<IReadOnlyList<(string Key, string? InstallFolder)>> readKeys)
    {
        _readKeys = readKeys;
    }

    /// <summary>Launcher.exe, from the launcher's own registry key, or null when it isn't installed.</summary>
    public string? GetLauncherPath()
    {
        try
        {
            var launcher = _readKeys().FirstOrDefault(k => k.Key.Equals(LauncherKeyName, StringComparison.OrdinalIgnoreCase));
            string? folder = Normalize(launcher.InstallFolder);
            if (folder == null) return null;
            string exe = Path.Combine(folder, "Launcher.exe");
            return File.Exists(exe) ? exe : null;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("RockstarScanner", $"Could not read the Rockstar Games Launcher's location: {ex.Message}");
            return null;
        }
    }

    public bool IsRockstarLauncherInstalled() => GetLauncherPath() != null;

    public List<DiscoveredRockstarGame> ScanInstalledGames(IEnumerable<string> existingGameIds)
    {
        var existingSet = new HashSet<string>(existingGameIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredRockstarGame>();
        foreach (var (gameId, installDir) in GetInstallDirs())
        {
            var game = ResolveInstall(gameId, installDir, existingSet);
            if (game != null) results.Add(game);
        }
        return results.OrderBy(g => g.Name).ToList();
    }

    /// <summary>Every Rockstar-launcher install's (title key, InstallFolder) from the registry alone - no
    /// folder scan. Skips Rockstar's own software and titles installed only through Epic or Steam.</summary>
    public List<(string GameId, string InstallDir)> GetInstallDirs()
    {
        var results = new List<(string, string)>();
        try
        {
            foreach (var (key, value) in _readKeys())
            {
                if (ClientComponentKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                string? installDir = Normalize(value);
                if (installDir == null)
                {
                    LoggingService.Verbose("RockstarScanner", $"Skipped [{key}]: not installed through the Rockstar Games Launcher (another store's copy, or not installed).");
                    continue;
                }
                results.Add((key, installDir));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RockstarScanner", $"Error reading Rockstar install list: {ex.Message}");
        }
        return results;
    }

    /// <summary>The folder Rockstar records for <paramref name="gameId"/> now, or null when it has none.</summary>
    public string? FindInstallDir(string? gameId) =>
        string.IsNullOrWhiteSpace(gameId) ? null
        : GetInstallDirs().FirstOrDefault(d => d.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase)).InstallDir;

    /// <summary>The full discovery record for one install (runs the folder scan for its exe), or null.</summary>
    public DiscoveredRockstarGame? ResolveInstall(string gameId, string installDir)
        => ResolveInstall(gameId, installDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private DiscoveredRockstarGame? ResolveInstall(string gameId, string installDir, HashSet<string> existingSet)
    {
        try
        {
            if (!Directory.Exists(installDir))
            {
                LoggingService.Verbose("RockstarScanner", $"Skipped [{gameId}]: its install folder '{installDir}' is not on disk.");
                return null;
            }

            if (existingSet.Contains(gameId))
            {
                LoggingService.Verbose("RockstarScanner", $"[{gameId}] at '{installDir}': already in library, not rescanned.");
                return null;
            }

            var best = _folderScannerService.ScanFolder(installDir).FirstOrDefault();
            if (best == null)
            {
                LoggingService.Verbose("RockstarScanner", $"Skipped [{gameId}]: no game executable was found under '{installDir}'.");
                return null;
            }

            return new DiscoveredRockstarGame(
                GameId: gameId,
                Name: gameId,
                InstallDir: installDir,
                ExePath: best.ExePath,
                IconPath: best.ExePath,
                IsAlreadyImported: false
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("RockstarScanner", $"Error reading Rockstar install '{gameId}': {ex.Message}");
            return null;
        }
    }

    private static string? Normalize(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : folder.Trim().Replace('/', '\\').TrimEnd('\\');

    /// <summary>Both registry views: Rockstar's installers have written to the 32-bit one so far
    /// (WOW6432Node), and the 64-bit one costs nothing to check. A key named in both takes the first
    /// InstallFolder either view has, so one view's empty key can't hide the other's install.</summary>
    private static IReadOnlyList<(string Key, string? InstallFolder)> ReadRegistryKeys()
    {
        var keys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var gamesKey = baseKey.OpenSubKey(GamesKeyPath);
            if (gamesKey == null) continue;
            foreach (string name in gamesKey.GetSubKeyNames())
            {
                using var titleKey = gamesKey.OpenSubKey(name);
                string? folder = titleKey?.GetValue("InstallFolder") as string;
                if (!keys.TryGetValue(name, out string? existing)) order.Add(name);
                if (string.IsNullOrWhiteSpace(existing)) keys[name] = folder;
            }
        }
        return order.Select(name => (name, keys[name])).ToList();
    }
}
