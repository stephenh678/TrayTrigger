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
/// Discovers installed Epic Games Store titles from both Epic launchers:
/// <list type="bullet">
/// <item>The classic launcher writes one ".item" JSON manifest per installed game under
/// %ProgramData% (InstallLocation, LaunchExecutable, AppName, DisplayName all present).</item>
/// <item>The new per-user launcher ("eglv2", %LocalAppData%\com.epicgames.eglv2) writes no
/// ".item" files. It appends install records to installations.jsonl instead (dir,
/// launchExecutable, and the AppName as "artifactId"), and keeps titles in its catalog cache.</item>
/// </list>
/// </summary>
public class EpicScannerService
{
    // Under %ProgramData%, wherever that is: the system drive is not always C:.
    private static readonly string DefaultManifestsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    private static readonly string DefaultEglv2Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "com.epicgames.eglv2");

    private readonly string _manifestsDir;
    private readonly string _eglv2Dir;

    public EpicScannerService() : this(DefaultManifestsDir, DefaultEglv2Dir) { }

    /// <summary>Test seam: temp folders standing in for the two launchers' data.</summary>
    public EpicScannerService(string manifestsDir, string eglv2Dir)
    {
        _manifestsDir = manifestsDir;
        _eglv2Dir = eglv2Dir;
    }

    /// <summary>True if Epic Games Launcher's "com.epicgames.launcher://" protocol is registered
    /// and its handler exe still exists - callers should fall back to launching the game's own exe
    /// directly otherwise.</summary>
    public bool IsEpicLauncherInstalled() => UrlProtocolHelper.IsHandlerInstalled("com.epicgames.launcher", nameof(EpicScannerService));

    public List<DiscoveredEpicGame> ScanInstalledGames(IEnumerable<string> existingAppNames)
    {
        var existingSet = new HashSet<string>(existingAppNames, StringComparer.OrdinalIgnoreCase);

        // The new launcher's record wins over a classic manifest for the same AppName: a PC
        // moved to the new launcher can keep stale ".item" files from before.
        var byAppName = new Dictionary<string, DiscoveredEpicGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in ScanClassicManifests(existingSet))
            byAppName[game.AppName] = game;
        foreach (var game in ScanEglv2Installations(existingSet))
            byAppName[game.AppName] = game;

        return byAppName.Values.OrderBy(g => g.Name).ToList();
    }

    private List<DiscoveredEpicGame> ScanClassicManifests(HashSet<string> existingSet)
    {
        var results = new List<DiscoveredEpicGame>();

        if (!Directory.Exists(_manifestsDir))
        {
            LoggingService.Verbose("EpicScanner", $"No '{_manifestsDir}': the classic Epic Games Launcher has installed nothing here.");
            return results;
        }

        IEnumerable<string> manifestFiles;
        try
        {
            manifestFiles = Directory.EnumerateFiles(_manifestsDir, "*.item");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("EpicScanner", $"Error enumerating '{_manifestsDir}': {ex.Message}");
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

        return results;
    }

    /// <summary>
    /// installations.jsonl is an append-only journal: one JSON object per line, a header line
    /// first, then a record each time an install changes (an incomplete one when a download
    /// starts, a complete one when it finishes). The last record per installationId is the
    /// current state. A record for a game since removed is caught by its exe no longer existing.
    /// </summary>
    private List<DiscoveredEpicGame> ScanEglv2Installations(HashSet<string> existingSet)
    {
        var results = new List<DiscoveredEpicGame>();
        string journal = Path.Combine(_eglv2Dir, "installations.jsonl");
        if (!File.Exists(journal))
        {
            LoggingService.Verbose("EpicScanner", $"No '{journal}': the new Epic Games Launcher has installed nothing here.");
            return results;
        }

        string[] lines;
        try
        {
            // The launcher may hold the file open while appending.
            using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex)
        {
            LoggingService.Warn("EpicScanner", $"Error reading '{journal}': {ex.Message}");
            return results;
        }

        var latest = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                string? id = GetString(root, "installationId");
                if (string.IsNullOrWhiteSpace(id)) continue; // the header line
                latest[id] = root.Clone();
            }
            catch (JsonException)
            {
                // A line cut short mid-append: the next scan reads it whole.
                LoggingService.Verbose("EpicScanner", "Skipped an unreadable line in installations.jsonl.");
            }
        }

        if (latest.Count == 0) return results;

        var catalog = Eglv2Catalog.Load(_eglv2Dir);
        foreach (var (installationId, record) in latest)
        {
            var game = ParseEglv2Record(installationId, record, catalog, existingSet);
            if (game != null) results.Add(game);
        }
        return results;
    }

    private static DiscoveredEpicGame? ParseEglv2Record(string installationId, JsonElement record, Eglv2Catalog catalog, HashSet<string> existingSet)
    {
        DiscoveredEpicGame? Skipped(string why)
        {
            LoggingService.Verbose("EpicScanner", $"Skipped installation {installationId}: {why}.");
            return null;
        }

        if (!record.TryGetProperty("complete", out var complete) || complete.ValueKind != JsonValueKind.True)
            return Skipped("the install is incomplete");

        string? platform = GetString(record, "platform");
        if (!string.IsNullOrEmpty(platform) && !string.Equals(platform, "Windows", StringComparison.OrdinalIgnoreCase))
            return Skipped($"it is for {platform}");

        string? appName = GetString(record, "artifactId");
        string? itemId = GetString(record, "itemId");
        string? installDir = GetString(record, "dir");
        string? launchExe = GetString(record, "launchExecutable");
        if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(launchExe))
            return Skipped("it has no artifactId, dir or launchExecutable");

        var item = itemId != null ? catalog.FindItem(itemId) : null;
        if (item is { } info)
        {
            if (info.IsAddOn) return Skipped("it is an add-on, not a game of its own");
            if (!info.IsGame) return Skipped("its categories do not include \"games\"");
        }

        // Same containment rule as the classic manifest: the exe must sit inside its install.
        string exePath = Path.Combine(installDir, launchExe.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(exePath))
            return Skipped($"its executable '{exePath}' is not on disk");
        if (!PlatformLookupService.IsPathUnderDirectory(exePath, installDir))
        {
            LoggingService.Warn("EpicScanner", $"Ignoring Epic installation {installationId}: launchExecutable '{launchExe}' resolves outside '{installDir}'.");
            return null;
        }

        // The journal carries no title; the launcher's catalog and library caches do. Failing
        // both, the install folder's name is the best there is.
        string name = item?.Title
            ?? catalog.FindLibraryTitle(appName)
            ?? Path.GetFileName(installDir.TrimEnd('\\', '/'));

        string gameExe = ResolveMainGameExe(installDir, exePath, item?.MainWindowProcessName, appName);

        return new DiscoveredEpicGame(
            AppName: appName,
            Name: name,
            InstallDir: installDir,
            ExePath: gameExe,
            IconPath: gameExe,
            IsAlreadyImported: existingSet.Contains(appName)
        );
    }

    private static readonly EnumerationOptions MainExeSearch = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 4,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>
    /// The program that is the game itself. Epic's launch executable is sometimes only a redirector
    /// to another launcher (Red Dead Redemption 2's PlayRDR2.exe hands over to the Rockstar Games
    /// Launcher and exits), and a session tracked to it ended while the game was still running.
    /// Epic names the game's own process as MainWindowProcessName; when that exe is in the install,
    /// it is the one recorded, so the session waits past the redirector for the game. The launch
    /// still goes through Epic's link, so the redirector still runs.
    /// </summary>
    internal static string ResolveMainGameExe(string installDir, string launchExePath, string? mainWindowProcessName, string appName)
    {
        string? name = mainWindowProcessName?.Trim();
        // A bare file name only: no folder, and no * or ?, which the search below would take as wildcards.
        if (string.IsNullOrEmpty(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Path.GetFileName(launchExePath), StringComparison.OrdinalIgnoreCase))
            return launchExePath;

        try
        {
            // Beside the launch exe first, then the install root, then a bounded search below it.
            foreach (string? dir in new[] { Path.GetDirectoryName(launchExePath), installDir })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate) && PlatformLookupService.IsPathUnderDirectory(candidate, installDir))
                    return Found(candidate);
            }

            string? found = Directory.EnumerateFiles(installDir, name, MainExeSearch)
                .FirstOrDefault(p => PlatformLookupService.IsPathUnderDirectory(p, installDir));
            if (found != null) return Found(found);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("EpicScanner", $"Could not look for '{name}' under '{installDir}': {ex.Message}");
        }

        LoggingService.Verbose("EpicScanner", $"'{appName}': Epic names '{name}' as the game's process, but it is not in '{installDir}'; keeping '{Path.GetFileName(launchExePath)}'.");
        return launchExePath;

        string Found(string path)
        {
            LoggingService.Verbose("EpicScanner", $"'{appName}': launches through '{Path.GetFileName(launchExePath)}', but the game's own program is '{path}'; recording that.");
            return path;
        }
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>What the new launcher's caches say about a catalog item.</summary>
    private readonly record struct Eglv2Item(string? Title, bool IsGame, bool IsAddOn, string? MainWindowProcessName);

    /// <summary>
    /// Titles and categories from the new launcher's caches: cache\prod\catalog-cache.json
    /// (keyed by catalog item id) and each account's library-cache.json (keyed by AppName).
    /// Either may be missing; the scan still offers the game under its folder name.
    /// </summary>
    private sealed class Eglv2Catalog
    {
        private readonly Dictionary<string, Eglv2Item> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _libraryTitles = new(StringComparer.OrdinalIgnoreCase);

        public Eglv2Item? FindItem(string itemId) => _items.TryGetValue(itemId, out var i) ? i : null;
        public string? FindLibraryTitle(string appName) => _libraryTitles.TryGetValue(appName, out var t) ? t : null;

        public static Eglv2Catalog Load(string eglv2Dir)
        {
            var catalog = new Eglv2Catalog();
            string prod = Path.Combine(eglv2Dir, "cache", "prod");
            catalog.ReadCatalog(Path.Combine(prod, "catalog-cache.json"));

            string accounts = Path.Combine(prod, "account");
            try
            {
                if (Directory.Exists(accounts))
                    foreach (string accountDir in Directory.EnumerateDirectories(accounts))
                        catalog.ReadLibrary(Path.Combine(accountDir, "library-cache.json"));
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("EpicScanner", $"Could not list '{accounts}': {ex.Message}");
            }
            return catalog;
        }

        private static JsonDocument? ReadJson(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonDocument.Parse(stream);
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("EpicScanner", $"Could not read '{path}': {ex.Message}");
                return null;
            }
        }

        private void ReadCatalog(string path)
        {
            using var doc = ReadJson(path);
            if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
                return;

            foreach (var entry in items.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) continue;

                string? title = GetString(item, "title");
                bool hasCategories = item.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array;
                bool isGame = !hasCategories || cats.EnumerateArray().Any(c =>
                    c.ValueKind == JsonValueKind.Object && string.Equals(GetString(c, "path"), "games", StringComparison.OrdinalIgnoreCase));
                bool isAddOn = item.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object;

                string? mainWindow = item.TryGetProperty("customAttributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                    && attrs.TryGetProperty("MainWindowProcessName", out var mw) && mw.ValueKind == JsonValueKind.Object
                    ? GetString(mw, "value") : null;

                _items[entry.Name] = new Eglv2Item(string.IsNullOrWhiteSpace(title) ? null : title, isGame, isAddOn, mainWindow);
            }
        }

        private void ReadLibrary(string path)
        {
            using var doc = ReadJson(path);
            if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in items.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("appId", out var appId) || appId.ValueKind != JsonValueKind.Object)
                    continue;
                string? appName = GetString(appId, "appName");
                string? title = GetString(entry, "title");
                if (!string.IsNullOrWhiteSpace(appName) && !string.IsNullOrWhiteSpace(title))
                    _libraryTitles.TryAdd(appName, title);
            }
        }
    }

    private static DiscoveredEpicGame? ParseManifest(string manifestPath, HashSet<string> existingSet)
    {
        // Every manifest that is not offered says why: a game missing from a scan is otherwise
        // indistinguishable from one Epic has no manifest for.
        DiscoveredEpicGame? Skipped(string why)
        {
            LoggingService.Verbose("EpicScanner", $"Skipped '{Path.GetFileName(manifestPath)}': {why}.");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                return Skipped("the install is incomplete");
            if (root.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind != JsonValueKind.True)
                return Skipped("it is not an application");

            // Entries with a non-empty MainGameAppName are DLC/add-ons that depend on a base
            // game, not a separately launchable title - same reasoning as GOG's dependsOn filter.
            if (root.TryGetProperty("MainGameAppName", out var mainGame) && mainGame.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(mainGame.GetString()))
                return Skipped($"DLC for {mainGame.GetString()}, not a game of its own");

            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array)
            {
                bool isGame = categories.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase));
                if (!isGame) return Skipped("its categories do not include \"games\"");
            }

            string? appName = root.TryGetProperty("AppName", out var an) ? an.GetString() : null;
            string? displayName = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
            string? installDir = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
            string? launchExe = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;

            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(displayName) ||
                string.IsNullOrWhiteSpace(installDir) || string.IsNullOrWhiteSpace(launchExe))
                return Skipped("it has no AppName, DisplayName, InstallLocation or LaunchExecutable");

            // Path.Combine discards installDir entirely if LaunchExecutable is rooted, and happily
            // builds "..\" traversals - so a planted manifest could point at any exe on disk.
            // The exe must sit inside the install it claims to belong to.
            string exePath = Path.Combine(installDir, launchExe);
            if (!File.Exists(exePath) || !PlatformLookupService.IsPathUnderDirectory(exePath, installDir))
            {
                if (File.Exists(exePath))
                    LoggingService.Warn("EpicScanner", $"Ignoring Epic manifest '{manifestPath}': LaunchExecutable '{launchExe}' resolves outside InstallLocation '{installDir}'.");
                else
                    Skipped($"its executable '{exePath}' is not on disk");
                return null;
            }

            string gameExe = ResolveMainGameExe(installDir, exePath, GetString(root, "MainWindowProcessName"), appName);

            return new DiscoveredEpicGame(
                AppName: appName,
                Name: displayName,
                InstallDir: installDir,
                ExePath: gameExe,
                IconPath: gameExe,
                IsAlreadyImported: existingSet.Contains(appName)
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("EpicScanner", $"Error parsing Epic manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }
}
