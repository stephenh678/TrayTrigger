using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public record DiscoveredAmazonGame(
    string GameId,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported,
    // fuel.json's Main.Args as one command line: what the app starts the game with, so a direct launch passes them too.
    string? LaunchArgs = null
);

/// <summary>
/// Discovers games installed through the Amazon Games app. Amazon writes a per-user uninstall entry for each
/// game, named <c>AmazonGames/&lt;Title&gt;</c>, with <c>DisplayName</c>, <c>InstallLocation</c> and an
/// <c>UninstallString</c> that runs <c>Amazon Game Remover.exe -m Game -p &lt;product id&gt;</c>. That product id
/// is the game's identity and what the app's own shortcuts launch (<c>amazon-games://play/&lt;id&gt;</c>, seen in
/// its log). Older installs use the full <c>amzn1.adg.product.&lt;guid&gt;</c> form, newer ones the bare GUID;
/// either is used as written. The game's exe comes from <c>fuel.json</c> in the install folder (<c>Main.Command</c>,
/// the program the app itself starts), else from <see cref="FolderScannerService.ScanFolder"/>.
/// The app's SQLite databases hold the same facts plus owned-but-not-installed games; they aren't read.
/// </summary>
public class AmazonScannerService
{
    internal const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string EntryPrefix = "AmazonGames/";

    /// <summary>The app's URL scheme: registered by the app, so it is also the "is it installed" check.</summary>
    internal const string UrlScheme = "amazon-games";

    /// <summary>The app's own windows. "Amazon Games Services" (its background service) is left alone, like the other platforms' services.</summary>
    public static readonly string[] ClientProcessNames = ["Amazon Games", "Amazon Games UI"];

    private static readonly Regex ProductIdArgument = new(@"(?:^|\s)-p\s+""?(?<id>[A-Za-z0-9._-]+)""?", RegexOptions.CultureInvariant);
    private static readonly Regex ProductIdShape = new(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    private readonly FolderScannerService _folderScannerService = new();
    private readonly Func<IReadOnlyList<UninstallEntry>> _readEntries;

    /// <summary>One <c>AmazonGames/</c> uninstall entry as the registry has it.</summary>
    internal record UninstallEntry(string KeyName, string? DisplayName, string? InstallLocation, string? UninstallString);

    public AmazonScannerService() : this(ReadRegistryEntries) { }

    /// <summary>Test seam: the <c>AmazonGames/</c> uninstall entries.</summary>
    internal AmazonScannerService(Func<IReadOnlyList<UninstallEntry>> readEntries)
    {
        _readEntries = readEntries;
    }

    /// <summary>Amazon Games.exe, from the app's registered URL handler, or null when it isn't installed.</summary>
    public string? GetClientPath() => UrlProtocolHelper.GetHandlerExecutablePath(UrlScheme, nameof(AmazonScannerService));

    public bool IsAmazonGamesInstalled() => UrlProtocolHelper.IsHandlerInstalled(UrlScheme, nameof(AmazonScannerService));

    /// <summary>The link the Amazon Games app's own shortcuts use to start a game.</summary>
    public static string BuildLaunchUrl(string gameId) => $"{UrlScheme}://play/{Uri.EscapeDataString(gameId)}";

    /// <summary>
    /// The product id in an <c>amazon-games://play/&lt;id&gt;</c> link - what the app's own desktop and Start menu
    /// shortcuts point at - or null for anything else. A dropped shortcut names its game this way, not by a path.
    /// </summary>
    internal static string? ParseLaunchUrl(string? url)
    {
        const string prefix = UrlScheme + "://play/";
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string id = url[prefix.Length..];
        int end = id.IndexOfAny(['/', '?', '#']);
        if (end >= 0) id = id[..end];
        id = Uri.UnescapeDataString(id);
        return ProductIdShape.IsMatch(id) ? id : null;
    }

    public List<DiscoveredAmazonGame> ScanInstalledGames(IEnumerable<string> existingGameIds)
    {
        var existingSet = new HashSet<string>(existingGameIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredAmazonGame>();
        foreach (var install in GetInstalls())
        {
            var game = ResolveInstall(install, existingSet);
            if (game != null) results.Add(game);
        }
        return results.OrderBy(g => g.Name).ToList();
    }

    /// <summary>Every Amazon install's (product id, title, install folder) from the registry alone - no folder scan.</summary>
    public List<(string GameId, string Name, string InstallDir)> GetInstalls()
    {
        var results = new List<(string, string, string)>();
        try
        {
            foreach (var entry in _readEntries())
            {
                string? gameId = ParseProductId(entry.UninstallString);
                string? installDir = Normalize(entry.InstallLocation);
                if (gameId == null || installDir == null)
                {
                    LoggingService.Verbose("AmazonScanner", $"Skipped [{entry.KeyName}]: no product id or install folder in its uninstall entry.");
                    continue;
                }
                string name = !string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName.Trim()
                    : entry.KeyName.StartsWith(EntryPrefix, StringComparison.OrdinalIgnoreCase) ? entry.KeyName[EntryPrefix.Length..]
                    : entry.KeyName;
                if (results.Any(r => ((string)r.Item1).Equals(gameId, StringComparison.OrdinalIgnoreCase))) continue;
                results.Add((gameId, name, installDir));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AmazonScanner", $"Error reading Amazon Games install list: {ex.Message}");
        }
        return results;
    }

    /// <summary>The folder Amazon records for <paramref name="gameId"/> now, or null when it has none.</summary>
    public string? FindInstallDir(string? gameId) =>
        string.IsNullOrWhiteSpace(gameId) ? null
        : GetInstalls().FirstOrDefault(d => d.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase)).InstallDir;

    /// <summary>The full discovery record for one install (reads its exe), or null.</summary>
    public DiscoveredAmazonGame? ResolveInstall((string GameId, string Name, string InstallDir) install)
        => ResolveInstall(install, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private DiscoveredAmazonGame? ResolveInstall((string GameId, string Name, string InstallDir) install, HashSet<string> existingSet)
    {
        var (gameId, name, installDir) = install;
        try
        {
            if (!Directory.Exists(installDir))
            {
                LoggingService.Verbose("AmazonScanner", $"Skipped [{name}]: its install folder '{installDir}' is not on disk.");
                return null;
            }

            if (existingSet.Contains(gameId))
            {
                LoggingService.Verbose("AmazonScanner", $"[{name}] at '{installDir}': already in library, not rescanned.");
                return null;
            }

            var fuel = ReadFuel(installDir);
            string? exe = fuel?.Exe ?? _folderScannerService.ScanFolder(installDir).FirstOrDefault()?.ExePath;
            if (exe == null)
            {
                LoggingService.Verbose("AmazonScanner", $"Skipped [{name}]: no game executable was found under '{installDir}'.");
                return null;
            }

            return new DiscoveredAmazonGame(gameId, name, installDir, exe, exe, IsAlreadyImported: false, LaunchArgs: fuel?.Arguments);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("AmazonScanner", $"Error reading Amazon Games install '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>The <c>-p</c> value of Amazon Game Remover's command line, or null when it has none.</summary>
    internal static string? ParseProductId(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;

        // Only the arguments are searched: the remover's own path (a library under "D:\Games -p Old", say) could
        // hold " -p " too. Amazon quotes the path; an unquoted one ends at ".exe".
        string arguments = uninstallString.TrimStart();
        if (arguments.StartsWith('"'))
        {
            int close = arguments.IndexOf('"', 1);
            arguments = close > 0 ? arguments[(close + 1)..] : string.Empty;
        }
        else
        {
            int exe = arguments.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0) arguments = arguments[(exe + ".exe".Length)..];
        }

        var match = ProductIdArgument.Match(arguments);
        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>The exe <c>fuel.json</c> names (see <see cref="ReadFuel"/>), or null.</summary>
    internal static string? ReadFuelExe(string installDir) => ReadFuel(installDir)?.Exe;

    /// <summary>
    /// What <c>fuel.json</c> says the app starts: the exe named by <c>Main.Command</c> (relative to the install
    /// folder), when it is a real .exe inside the folder, and <c>Main.Args</c> as one command line. Null when there
    /// is no such exe, so the folder scan picks one (and no arguments are guessed for it).
    /// </summary>
    internal static (string Exe, string? Arguments)? ReadFuel(string installDir)
    {
        string fuel = Path.Combine(installDir, "fuel.json");
        if (!File.Exists(fuel)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fuel), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("Main", out var main) || !main.TryGetProperty("Command", out var command)) return null;
            string? relative = command.GetString();
            if (string.IsNullOrWhiteSpace(relative)) return null;

            string root = Path.GetFullPath(installDir).TrimEnd('\\') + "\\";
            string exe = Path.GetFullPath(Path.Combine(installDir, relative.Replace('/', '\\')));
            bool inside = exe.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            if (!inside || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return null;

            return (exe, main.TryGetProperty("Args", out var args) ? JoinArguments(args) : null);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("AmazonScanner", $"Could not read '{fuel}': {ex.Message}");
            return null;
        }
    }

    /// <summary><c>Main.Args</c> as one command line: an array is quoted argument by argument, a plain string is
    /// taken as it is. Null when there are none.</summary>
    private static string? JoinArguments(JsonElement args)
    {
        string joined = args.ValueKind switch
        {
            JsonValueKind.Array => string.Join(' ', args.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(a.GetString()))
                .Select(a => QuoteArgument(a.GetString()!))),
            JsonValueKind.String => args.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty,
        };
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>One argument as a Windows command line needs it: as it is when it has no space, tab or quote,
    /// otherwise quoted, with quotes and the backslashes before them escaped (CommandLineToArgvW's rules).</summary>
    internal static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var quoted = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static string? Normalize(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : folder.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    /// <summary>The app installs per user, so its entries are under HKCU; the machine-wide list costs nothing to check too.</summary>
    private static IReadOnlyList<UninstallEntry> ReadRegistryEntries()
    {
        var entries = new List<UninstallEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        };
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKeyPath);
                if (uninstall == null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    if (!name.StartsWith(EntryPrefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                    using var key = uninstall.OpenSubKey(name);
                    if (key == null) continue;
                    entries.Add(new UninstallEntry(name, key.GetValue("DisplayName") as string,
                        key.GetValue("InstallLocation") as string, key.GetValue("UninstallString") as string));
                }
            }
            catch (Exception ex)
            {
                LoggingService.Verbose("AmazonScanner", $"Could not read {hive} uninstall entries: {ex.Message}");
            }
        }
        return entries;
    }
}
