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
        var existingSet = new HashSet<string>(existingAppIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredSteamGame>();

        string? steamPath = GetSteamInstallPath();
        if (string.IsNullOrEmpty(steamPath))
        {
            return results;
        }

        var libraryFolders = GetLibraryFolders(steamPath);

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

    private DiscoveredSteamGame? ParseManifest(string manifestPath, string libraryFolder, string steamPath, HashSet<string> existingSet)
    {
        try
        {
            var lines = File.ReadAllLines(manifestPath);
            string? appId = null;
            string? name = null;
            string? installdir = null;

            foreach (var line in lines)
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
        Process.Start(new ProcessStartInfo($"https://store.steampowered.com/app/{appId}") { UseShellExecute = true });
    }

    public static void OpenInSteamLibrary(string appId)
    {
        Process.Start(new ProcessStartInfo($"steam://nav/games/details/{appId}") { UseShellExecute = true });
    }

    public static void VerifyGameFiles(string appId)
    {
        Process.Start(new ProcessStartInfo($"steam://validate/{appId}") { UseShellExecute = true });
    }
}
