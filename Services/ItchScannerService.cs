using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TrayTrigger.Services;

public record DiscoveredItchGame(
    string GameId,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported,
    // The game's itch.io page, from its receipt: Game Details' store link.
    string? StoreUrl = null
);

/// <summary>
/// Discovers games installed through the itch app. Each install is a folder under one of the app's
/// install locations (<c>%AppData%\itch\apps</c> by default) holding <c>.itch\receipt.json.gz</c>: gzipped
/// JSON whose <c>game</c> object has itch's numeric game id, the title and the store page URL. The id
/// is the game's identity. The receipt names no exe. The app's own database (butler.db, SQLite) keeps,
/// per install, a JSON "verdict" naming the exes it launches; TrayTrigger has no SQLite reader, so
/// <see cref="ReadLaunchTargets"/> finds those JSON values in the database files' bytes, and the first
/// Windows exe is the game's. Without one, the exe comes from <see cref="FolderScannerService.ScanFolder"/>;
/// a folder with neither, such as an archive the app never unpacked, is skipped. Install locations added
/// in the app are read from the <c>installLocations</c> map in its <c>preferences.json</c>; newer versions of
/// the app keep them only in butler.db's tables, which aren't read.
/// itch games are DRM-free and the app itself just runs the exe, while its only launch link asks
/// for confirmation first, so TrayTrigger always starts them directly (<see cref="LaunchRoute.ItchDirect"/>).
/// </summary>
public class ItchScannerService
{
    internal const string ReceiptRelativePath = @".itch\receipt.json.gz";

    /// <summary>butler.db is a few MB even for a large library; anything far bigger isn't searched.</summary>
    private const long MaxDatabaseBytes = 256L * 1024 * 1024;

    private static readonly byte[] VerdictStart = Encoding.UTF8.GetBytes("{\"basePath\":");

    private readonly FolderScannerService _folderScannerService = new();
    private readonly Func<IReadOnlyList<string>> _installLocations;
    private readonly Func<IEnumerable<byte[]>> _databases;

    public ItchScannerService() : this(ReadInstallLocations, ReadDatabases) { }

    /// <summary>Test seam: the app's install locations, and the bytes of its database files.</summary>
    internal ItchScannerService(Func<IReadOnlyList<string>> installLocations, Func<IEnumerable<byte[]>>? databases = null)
    {
        _installLocations = installLocations;
        _databases = databases ?? (() => []);
    }

    /// <summary>The app's own folder (%LocalAppData%\itch), from its per-user uninstall entry's default location.</summary>
    internal static string ClientFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "itch");

    /// <summary>The app's data folder (%AppData%\itch): preferences.json and the default "apps" install location.</summary>
    internal static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch");

    /// <summary>itch.exe sits in a versioned "app-&lt;version&gt;" folder beside itch-setup.exe.</summary>
    public static bool IsItchAppInstalled() => File.Exists(Path.Combine(ClientFolder, "itch-setup.exe"));

    public List<DiscoveredItchGame> ScanInstalledGames(IEnumerable<string> existingGameIds)
    {
        var existingSet = new HashSet<string>(existingGameIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredItchGame>();
        var launchTargets = ReadLaunchTargets();
        foreach (var install in GetInstalls())
        {
            // A game installed twice (two of its uploads) has two folders with one id: the first that has
            // something to run is the game, so one without (a soundtrack, say) doesn't hide it.
            if (results.Any(r => r.GameId == install.GameId)) continue;
            var game = ResolveInstall(install, existingSet, launchTargets);
            if (game != null) results.Add(game);
        }
        return results.OrderBy(g => g.Name).ToList();
    }

    /// <summary>Every itch install's (game id, title, folder, store page) from the receipts alone - no folder scan.
    /// A game installed twice is listed once per folder.</summary>
    public List<(string GameId, string Name, string InstallDir, string? StoreUrl)> GetInstalls()
    {
        var results = new List<(string GameId, string Name, string InstallDir, string? StoreUrl)>();
        foreach (string location in _installLocations().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(location)) continue;
                foreach (string dir in Directory.EnumerateDirectories(location))
                {
                    var receipt = ReadReceipt(dir);
                    if (receipt == null) continue;
                    if (results.Any(r => string.Equals(r.InstallDir, dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) continue;
                    results.Add((receipt.Value.GameId, receipt.Value.Name, dir.TrimEnd('\\'), receipt.Value.StoreUrl));
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("ItchScanner", $"Error reading itch install location '{location}': {ex.Message}");
            }
        }
        return results;
    }

    /// <summary>The full discovery record for one install (reads its exe), or null.</summary>
    public DiscoveredItchGame? ResolveInstall((string GameId, string Name, string InstallDir, string? StoreUrl) install)
        => ResolveInstall(install, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ReadLaunchTargets());

    private DiscoveredItchGame? ResolveInstall((string GameId, string Name, string InstallDir, string? StoreUrl) install, HashSet<string> existingSet,
        IReadOnlyDictionary<string, string>? launchTargets = null)
    {
        var (gameId, name, installDir, storeUrl) = install;
        try
        {
            if (existingSet.Contains(gameId))
            {
                LoggingService.Verbose("ItchScanner", $"[{name}] at '{installDir}': already in library, not rescanned.");
                return null;
            }

            // The exe the app itself launches, when its database has one; else the folder scan's pick. The folder
            // scan passes over exes under 60 KB as stubs, which is what a Ren'Py game's own exe is.
            string? exe = (launchTargets ?? ReadLaunchTargets()).TryGetValue(FolderKey(installDir), out var target) ? target
                : _folderScannerService.ScanFolder(installDir).FirstOrDefault()?.ExePath;
            if (exe == null)
            {
                LoggingService.Verbose("ItchScanner", $"Skipped [{name}]: no game executable was found under '{installDir}'.");
                return null;
            }

            return new DiscoveredItchGame(gameId, name, installDir, exe, exe, IsAlreadyImported: false, StoreUrl: storeUrl);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("ItchScanner", $"Error reading itch install '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The itch install <paramref name="path"/> is in - the folder holding <c>.itch\receipt.json.gz</c> at or
    /// above it - or null. Lets a dropped exe from any install location, read or not, become an itch game.
    /// </summary>
    public static (string GameId, string Name, string InstallDir, string? StoreUrl)? FindInstallContaining(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string? dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            for (int depth = 0; dir != null && depth < 8; depth++, dir = Path.GetDirectoryName(dir))
            {
                var receipt = ReadReceipt(dir);
                if (receipt != null) return (receipt.Value.GameId, receipt.Value.Name, dir.TrimEnd('\\'), receipt.Value.StoreUrl);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ItchScanner", $"Could not look for an itch receipt above '{path}': {ex.Message}");
        }
        return null;
    }

    /// <summary>The game id, title and store page in <c>&lt;installDir&gt;\.itch\receipt.json.gz</c>, or null when there is no readable one.</summary>
    internal static (string GameId, string Name, string? StoreUrl)? ReadReceipt(string installDir)
    {
        string receipt = Path.Combine(installDir, ReceiptRelativePath);
        if (!File.Exists(receipt)) return null;
        try
        {
            using var file = File.OpenRead(receipt);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var doc = JsonDocument.Parse(gzip);
            if (!doc.RootElement.TryGetProperty("game", out var game) || game.ValueKind != JsonValueKind.Object) return null;
            if (!game.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out long id) || id <= 0) return null;

            string? title = game.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;
            string? url = game.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            bool webPage = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
            return (id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(title) ? Path.GetFileName(installDir.TrimEnd('\\')) : title,
                webPage ? url : null);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ItchScanner", $"Could not read '{receipt}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Install folder -> the exe the itch app launches there, from its database (see <see cref="ParseLaunchTargets"/>).</summary>
    private IReadOnlyDictionary<string, string> ReadLaunchTargets()
    {
        try
        {
            return ParseLaunchTargets(_databases());
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ItchScanner", $"Could not read the itch app's launch targets: {ex.Message}");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Every install's launch target in the database files, oldest first (butler.db, then the frames of its
    /// write-ahead log that are current, see <see cref="CurrentWalPages"/>), so the last one found for a folder wins.
    /// Each is a JSON verdict, <c>{"basePath":"C:\\...\\apps\\game","candidates":[{"path":"Game\\Game.exe","flavor":"windows",...}]}</c>,
    /// kept as text in the caves table; the first "windows" candidate that is an .exe on disk inside the folder, and
    /// not a crash reporter, installer or other helper, is taken. A verdict SQLite had to split across pages doesn't
    /// parse and is passed over, leaving that game to the folder scan. butler.db's free pages can still hold an old
    /// copy; its exe has to be on disk to count, which an update normally removes.
    /// </summary>
    internal static Dictionary<string, string> ParseLaunchTargets(IEnumerable<byte[]> databases)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (byte[] bytes in databases)
        {
            int from = 0;
            while (from < bytes.Length)
            {
                int start = bytes.AsSpan(from).IndexOf(VerdictStart);
                if (start < 0) break;
                start += from;
                from = start + VerdictStart.Length;
                try
                {
                    var reader = new Utf8JsonReader(bytes.AsSpan(start), isFinalBlock: true, state: default);
                    using var verdict = JsonDocument.ParseValue(ref reader);
                    if (LaunchTargetOf(verdict.RootElement) is { } target) targets[target.InstallDir] = target.Exe;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Not a whole, readable verdict here (split across database pages, a fragment left in a free page,
                    // or text torn mid-character): this one is passed over, the others still count.
                }
            }
        }
        return targets;
    }

    private static (string InstallDir, string Exe)? LaunchTargetOf(JsonElement verdict)
    {
        if (!verdict.TryGetProperty("basePath", out var basePath) || basePath.ValueKind != JsonValueKind.String) return null;
        if (!verdict.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return null;
        string? dir = basePath.GetString();
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir)) return null;

        string root = FolderKey(dir);
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            if (!candidate.TryGetProperty("flavor", out var flavor) || flavor.ValueKind != JsonValueKind.String || flavor.GetString() != "windows") continue;
            if (!candidate.TryGetProperty("path", out var relative) || relative.ValueKind != JsonValueKind.String) continue;

            string exe = Path.GetFullPath(Path.Combine(root, (relative.GetString() ?? string.Empty).Replace('/', '\\')));
            bool inside = exe.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            // The app drops crash handlers and the like only when it launches; the stored list can still have them.
            if (inside && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !ProcessPathResolver.IsKnownHelperProcess(exe) && File.Exists(exe))
                return (root, exe);
        }
        return null;
    }

    /// <summary>A folder as a lookup key: full path, no trailing backslash, so "D:/Games/itch/x" and butler's "D:\Games\itch\x" agree.</summary>
    private static string FolderKey(string folder) => Path.GetFullPath(folder).TrimEnd('\\');

    private const uint WalMagic = 0x377F0682;

    /// <summary>
    /// The pages of a SQLite write-ahead log that belong to its current run, oldest first. After a checkpoint
    /// SQLite writes the log again from the start under new salt values and leaves the old frames after the new
    /// ones, so a later frame isn't necessarily a newer one: the current run is the frames from the start whose
    /// salts match the header's. Anything that isn't a write-ahead log is returned as it is.
    /// </summary>
    internal static byte[] CurrentWalPages(byte[] wal)
    {
        const int headerSize = 32, frameHeaderSize = 24;
        if (wal.Length < headerSize || (BinaryPrimitives.ReadUInt32BigEndian(wal) & 0xFFFFFFFE) != WalMagic) return wal;

        uint pageSize = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8));
        if (pageSize == 1) pageSize = 65536;
        if (pageSize < 512 || pageSize > 65536) return [];
        uint salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(16));
        uint salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(20));

        using var pages = new MemoryStream();
        for (long offset = headerSize; offset + frameHeaderSize + pageSize <= wal.Length; offset += frameHeaderSize + pageSize)
        {
            var frame = wal.AsSpan((int)offset, frameHeaderSize);
            if (BinaryPrimitives.ReadUInt32BigEndian(frame[8..]) != salt1 || BinaryPrimitives.ReadUInt32BigEndian(frame[12..]) != salt2) break;
            pages.Write(wal, (int)(offset + frameHeaderSize), (int)pageSize);
        }
        return pages.ToArray();
    }

    /// <summary>butler.db and its write-ahead log, read without locking out the app, which may be running.</summary>
    private static IEnumerable<byte[]> ReadDatabases()
    {
        foreach (string name in new[] { "butler.db", "butler.db-wal" })
        {
            string path = Path.Combine(DataFolder, "db", name);
            byte[]? bytes = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length <= MaxDatabaseBytes)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("ItchScanner", $"Could not read '{path}': {ex.Message}");
            }
            if (bytes != null) yield return name.EndsWith("-wal", StringComparison.Ordinal) ? CurrentWalPages(bytes) : bytes;
        }
    }

    /// <summary>The default install location plus any listed in the app's preferences.json.</summary>
    private static IReadOnlyList<string> ReadInstallLocations() =>
        InstallLocationsFrom(DataFolder, ReadPreferences(Path.Combine(DataFolder, "preferences.json")));

    internal static IReadOnlyList<string> InstallLocationsFrom(string dataFolder, string? preferencesJson)
    {
        var locations = new List<string> { Path.Combine(dataFolder, "apps") };
        if (string.IsNullOrWhiteSpace(preferencesJson)) return locations;
        try
        {
            using var doc = JsonDocument.Parse(preferencesJson);
            if (!doc.RootElement.TryGetProperty("installLocations", out var map) || map.ValueKind != JsonValueKind.Object) return locations;
            foreach (var entry in map.EnumerateObject())
            {
                // Each entry is { "path": "D:\\Games\\itch" }; a bare string is taken too.
                string? path = entry.Value.ValueKind switch
                {
                    JsonValueKind.String => entry.Value.GetString(),
                    JsonValueKind.Object when entry.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String => p.GetString(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)) locations.Add(path.TrimEnd('\\'));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ItchScanner", $"Could not read the itch app's install locations: {ex.Message}");
        }
        return locations;
    }

    private static string? ReadPreferences(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ItchScanner", $"Could not read '{path}': {ex.Message}");
            return null;
        }
    }
}
