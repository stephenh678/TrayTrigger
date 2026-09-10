using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public record DiscoveredSteamGame(
    string AppId,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported
);

public partial class SteamScannerService
{
    [GeneratedRegex(@"^\s*""([^""]+)""\s*""([^""]*)""")]
    private static partial Regex VdfKeyValueRegex();

    public string? GetSteamInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
            {
                return steamPath.Replace('/', '\\');
            }

            using var hklmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ??
                                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (hklmKey?.GetValue("InstallPath") is string hklmPath && Directory.Exists(hklmPath))
            {
                return hklmPath.Replace('/', '\\');
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamScannerService", $"Error detecting Steam path: {ex.Message}");
        }

        // Common default path
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(defaultPath) ? defaultPath : null;
    }

    public List<string> GetLibraryFolders(string steamPath)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            steamPath
        };

        string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            return folders.ToList();
        }

        try
        {
            var lines = File.ReadAllLines(vdfPath);
            foreach (var line in lines)
            {
                var match = VdfKeyValueRegex().Match(line);
                if (match.Success)
                {
                    string key = match.Groups[1].Value;
                    string val = match.Groups[2].Value.Replace(@"\\", @"\");

                    if (key.Equals("path", StringComparison.OrdinalIgnoreCase) && Directory.Exists(val))
                    {
                        folders.Add(val);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamScannerService", $"Error parsing libraryfolders.vdf: {ex.Message}");
        }

        return folders.ToList();
    }

    public List<DiscoveredSteamGame> ScanInstalledGames(IEnumerable<string> existingAppIds)
    {
        string? steamPath = GetSteamInstallPath();
        if (string.IsNullOrEmpty(steamPath))
        {
            return new List<DiscoveredSteamGame>();
        }

        return ScanInstalledGames(GetLibraryFolders(steamPath), steamPath, existingAppIds);
    }

    /// <summary>
    /// Same manifest scan as <see cref="ScanInstalledGames(IEnumerable{string})"/>, but scoped to
    /// an explicit set of library folders - used by "Scan for Games" so a Steam library the user
    /// disabled in their scan locations is skipped rather than always scanning every library Steam
    /// itself reports.
    /// </summary>
    public List<DiscoveredSteamGame> ScanInstalledGames(IEnumerable<string> libraryFolders, string steamPath, IEnumerable<string> existingAppIds)
    {
        var existingSet = new HashSet<string>(existingAppIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredSteamGame>();

        foreach (var folder in libraryFolders)
        {
            string steamappsDir = Path.Combine(folder, "steamapps");
            if (!Directory.Exists(steamappsDir))
            {
                steamappsDir = folder;
            }

            if (!Directory.Exists(steamappsDir))
                continue;

            try
            {
                var manifestFiles = Directory.GetFiles(steamappsDir, "appmanifest_*.acf");
                foreach (var manifest in manifestFiles)
                {
                    var game = ParseManifest(manifest, folder, steamPath, existingSet);
                    if (game != null)
                    {
                        results.Add(game);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("SteamScannerService", $"Error scanning folder '{folder}': {ex.Message}");
            }
        }

        return results.OrderBy(g => g.Name).ToList();
    }

    /// <summary>
    /// Resolves the single installed Steam game whose install folder contains <paramref name="path"/>
    /// (an exe or any file/folder inside the game's steamapps\common\&lt;installdir&gt; tree), or null
    /// if the path isn't inside any Steam library's installed game. Used by
    /// <see cref="PlatformLookupService"/> so a game dropped/browsed into the library from a Steam
    /// install directory is imported with its real AppId rather than as a Local exe. Only the
    /// cheap manifest header is read per game; the exe/icon walk runs for the one match only.
    /// </summary>
    public DiscoveredSteamGame? FindGameByPath(string path)
    {
        foreach (var entry in GetInstalledGameEntries())
        {
            if (PlatformLookupService.IsPathUnderDirectory(path, entry.CommonDir))
            {
                return ResolveInstalledGame(entry);
            }
        }
        return null;
    }

    /// <summary>One installed Steam game's location, read from just its manifest header - the
    /// cheap half of discovery, for containment matching without the per-game exe/icon walk.</summary>
    public readonly record struct SteamInstallEntry(string ManifestPath, string LibraryFolder, string SteamPath, string CommonDir);

    /// <summary>
    /// Every installed game's install directory across all Steam libraries, from manifest
    /// headers only. <see cref="PlatformLookupService"/> caches this once per import operation
    /// so a batch of N candidates costs one manifest pass, not N.
    /// </summary>
    public List<SteamInstallEntry> GetInstalledGameEntries()
    {
        var entries = new List<SteamInstallEntry>();
        string? steamPath = GetSteamInstallPath();
        if (string.IsNullOrEmpty(steamPath)) return entries;

        foreach (var folder in GetLibraryFolders(steamPath))
        {
            // Same tolerance as ScanInstalledGames: a library configured as "...\steamapps"
            // itself, rather than its parent, is still a library.
            string steamappsDir = Path.Combine(folder, "steamapps");
            string libraryFolder = folder;
            if (!Directory.Exists(steamappsDir))
            {
                if (!Directory.Exists(Path.Combine(folder, "common"))) continue;
                steamappsDir = folder;
                libraryFolder = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder;
            }

            string[] manifestFiles;
            try
            {
                manifestFiles = Directory.GetFiles(steamappsDir, "appmanifest_*.acf");
            }
            catch (Exception ex)
            {
                LoggingService.Warn("SteamScannerService", $"Error enumerating '{steamappsDir}': {ex.Message}");
                continue;
            }

            foreach (var manifest in manifestFiles)
            {
                var header = ReadManifestHeader(manifest);
                if (header == null || string.IsNullOrWhiteSpace(header.Value.InstallDir)) continue;

                string commonDir = Path.Combine(steamappsDir, "common", header.Value.InstallDir);
                entries.Add(new SteamInstallEntry(manifest, libraryFolder, steamPath, commonDir));
            }
        }
        return entries;
    }

    /// <summary>The full discovery record (exe, icon, name) for one <see cref="SteamInstallEntry"/>,
    /// or null for a non-game manifest (redistributables, Proton) or an unreadable one.</summary>
    public DiscoveredSteamGame? ResolveInstalledGame(SteamInstallEntry entry)
        => ParseManifest(entry.ManifestPath, entry.LibraryFolder, entry.SteamPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The appid/name/installdir triple from an appmanifest_*.acf, or null if the file
    /// can't be read or lacks an appid/name. Cheap: no filesystem walk beyond the file itself.</summary>
    private static (string AppId, string Name, string? InstallDir)? ReadManifestHeader(string manifestPath)
    {
        try
        {
            string? appId = null;
            string? name = null;
            string? installdir = null;

            foreach (var line in File.ReadLines(manifestPath))
            {
                var match = VdfKeyValueRegex().Match(line);
                if (match.Success)
                {
                    string key = match.Groups[1].Value.ToLowerInvariant();
                    string val = match.Groups[2].Value;

                    if (key == "appid") appId = val;
                    else if (key == "name") name = val;
                    else if (key == "installdir") installdir = val;
                }
            }

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
                return null;

            return (appId, name, installdir);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamScannerService", $"Error reading manifest header '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    private DiscoveredSteamGame? ParseManifest(string manifestPath, string libraryFolder, string steamPath, HashSet<string> existingSet)
    {
        try
        {
            var header = ReadManifestHeader(manifestPath);
            if (header == null) return null;
            var (appId, name, installdir) = header.Value;

            // Filter out common Steam redistributables / tool runtimes
            if (name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string commonDir = Path.Combine(libraryFolder, "steamapps", "common", installdir ?? "");
            string? bestExe = null;
            string? bestIcon = null;

            // Search for game exe
            if (Directory.Exists(commonDir))
            {
                var enumOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                };

                var exes = Directory.EnumerateFiles(commonDir, "*.exe", enumOptions)
                    .Where(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("crash", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).StartsWith("unitycrash", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Contains("redist", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Contains("benchmark", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Contains("directx", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                    .ToList();

                bestExe = exes.FirstOrDefault();
            }

            // 1. Priority: Pull Steam's own cached icon / artwork for this appId
            // Check steam client games icon cache (e.g. steam\games\<hash or appid>.ico)
            string steamGamesIconDir = Path.Combine(steamPath, "steam", "games");
            if (Directory.Exists(steamGamesIconDir))
            {
                var iconFiles = Directory.GetFiles(steamGamesIconDir, $"*{appId}*.ico");
                if (iconFiles.Length > 0)
                {
                    bestIcon = iconFiles[0];
                }
            }

            // Check steam appcache library cache (e.g. appcache\librarycache\<appid>_icon.jpg, etc.)
            if (string.IsNullOrEmpty(bestIcon) || !File.Exists(bestIcon))
            {
                string libraryCacheDir = Path.Combine(steamPath, "appcache", "librarycache");
                if (Directory.Exists(libraryCacheDir))
                {
                    var cacheFiles = Directory.GetFiles(libraryCacheDir, $"{appId}_icon.*")
                        .Concat(Directory.GetFiles(libraryCacheDir, $"*{appId}*icon*.*"))
                        .Concat(Directory.GetFiles(libraryCacheDir, $"{appId}_header.*"))
                        .ToList();

                    if (cacheFiles.Count > 0)
                    {
                        bestIcon = cacheFiles[0];
                    }
                    else
                    {
                        // Check subfolder format
                        string appSubdir = Path.Combine(libraryCacheDir, appId);
                        if (Directory.Exists(appSubdir))
                        {
                            // Only accept files that look like an icon/logo asset; do not fall back
                            // to "any file in this folder", which can pick a multi-megabyte hero
                            // image (e.g. library_hero.jpg) as the tray icon. See M-23.
                            var subIcons = Directory.GetFiles(appSubdir, "*icon*.*")
                                .Concat(Directory.GetFiles(appSubdir, "*logo*.*"))
                                .Concat(Directory.GetFiles(appSubdir, "*.ico"))
                                .ToList();
                            if (subIcons.Count > 0)
                            {
                                bestIcon = subIcons[0];
                            }
                        }
                    }
                }
            }

            // 2. Fallback: Only use exe if no Steam artwork was found
            if ((string.IsNullOrEmpty(bestIcon) || !File.Exists(bestIcon)) && !string.IsNullOrEmpty(bestExe))
            {
                bestIcon = bestExe;
            }

            return new DiscoveredSteamGame(
                AppId: appId,
                Name: name,
                InstallDir: commonDir,
                ExePath: bestExe,
                IconPath: bestIcon,
                IsAlreadyImported: existingSet.Contains(appId)
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamScannerService", $"Error parsing manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }

    public static void OpenStorePage(string appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://store.steampowered.com/app/{appId}") { UseShellExecute = true });
            LoggingService.Verbose("SteamScannerService", $"Opened store page for App ID {appId}.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamScannerService", $"Failed to open store page for App ID {appId}: {ex.Message}");
        }
    }

    public static void OpenInSteamLibrary(string appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"steam://nav/games/details/{appId}") { UseShellExecute = true });
            LoggingService.Verbose("SteamScannerService", $"Opened Steam library page for App ID {appId}.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamScannerService", $"Failed to open Steam library for App ID {appId} (is Steam installed?): {ex.Message}");
        }
    }

    public static void VerifyGameFiles(string appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"steam://validate/{appId}") { UseShellExecute = true });
            LoggingService.Info("SteamScannerService", $"Requested file verification via Steam for App ID {appId}.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SteamScannerService", $"Failed to request file verification for App ID {appId} (is Steam installed?): {ex.Message}");
        }
    }
}
