using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TrayTrigger.Services;

public record DiscoveredEaGame(
    string ContentId,
    string Name,
    string InstallDir,
    string? ExePath,
    string? IconPath,
    bool IsAlreadyImported
);

/// <summary>
/// Discovers installed EA games. Thinner and less reliable than <see cref="GogScannerService"/>
/// or <see cref="SteamScannerService"/>: EA's registry footprint (HKLM\SOFTWARE\WOW6432Node\Origin
/// Games\&lt;contentId&gt;) only gives a content ID and display name, not an install path - the
/// actual exe lives inside that install's own "__Installer\installerdata.xml" manifest file, whose
/// &lt;launcher&gt;&lt;filePath&gt; is itself expressed as a per-game, per-publisher registry
/// indirection (e.g. "[HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Jedi Fallen Order\Install Dir]..."),
/// not a consistent EA-wide key. Rather than resolve that indirection, this searches the small set
/// of default EA/Origin install roots for a subfolder whose manifest content ID matches one from
/// the registry list, and reads the launcher path relative to wherever that manifest was found.
/// This means a game installed to a custom (non-default) location will not be found - a real,
/// permanent limitation shared by every other third-party EA integration (Playnite, Lutris have
/// open issues about exactly this), not something fixable without parsing EA's own encrypted local
/// cache. Surfaced to the user via the Settings toggle's description rather than hidden.
/// </summary>
public partial class EaScannerService
{
    private const string OriginGamesKeyPath = @"SOFTWARE\Origin Games";

    private static readonly string[] DefaultInstallRoots =
    {
        @"C:\Program Files\EA Games",
        @"C:\Program Files (x86)\EA Games",
        @"C:\Program Files\Origin Games",
        @"C:\Program Files (x86)\Origin Games",
    };

    // Content IDs aren't always numeric - EA/Origin has historically used prefixed forms too
    // (e.g. "OFB-EAST:54866"), so this captures anything up to the closing tag rather than \d+.
    [GeneratedRegex(@"<contentID>\s*(.+?)\s*</contentID>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ContentIdRegex();

    [GeneratedRegex(@"<filePath>\s*(.+?)\s*</filePath>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FilePathRegex();

    /// <summary>True if EA App's "origin2://" launch protocol is registered and its handler exe
    /// still exists - callers should fall back to launching the game's own exe directly otherwise.</summary>
    public bool IsEaAppInstalled() => UrlProtocolHelper.IsHandlerInstalled("origin2", nameof(EaScannerService));

    public List<DiscoveredEaGame> ScanInstalledGames(IEnumerable<string> existingContentIds)
    {
        var existingSet = new HashSet<string>(existingContentIds, StringComparer.OrdinalIgnoreCase);
        var results = new List<DiscoveredEaGame>();

        var registered = ReadRegisteredGames();
        if (registered.Count == 0)
        {
            return results;
        }

        // Index every installerdata.xml under the default install roots by its own content ID,
        // once, rather than re-walking the roots per registered game.
        var manifestsByContentId = IndexInstallerManifests();

        foreach (var (contentId, name) in registered)
        {
            if (!manifestsByContentId.TryGetValue(contentId, out var manifestPath))
            {
                // Registered in EA App but not found under a default install root - most likely
                // installed to a custom location. See the class doc comment.
                continue;
            }

            var game = ParseManifest(contentId, name, manifestPath, existingSet);
            if (game != null)
            {
                results.Add(game);
            }
        }

        return results.OrderBy(g => g.Name).ToList();
    }

    private static List<(string ContentId, string Name)> ReadRegisteredGames()
    {
        var results = new List<(string, string)>();
        try
        {
            using var baseKey = RegistryHelper.OpenLocalMachine32();
            using var gamesKey = baseKey.OpenSubKey(OriginGamesKeyPath);
            if (gamesKey == null) return results;

            foreach (var contentId in gamesKey.GetSubKeyNames())
            {
                using var gameKey = gamesKey.OpenSubKey(contentId);
                if (gameKey?.GetValue("DisplayName") is string name && !string.IsNullOrWhiteSpace(name))
                {
                    results.Add((contentId, name));
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("EaScannerService", $"Error reading installed EA games from the registry: {ex.Message}");
        }
        return results;
    }

    private static Dictionary<string, string> IndexInstallerManifests()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DefaultInstallRoots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> gameFolders;
            try
            {
                gameFolders = Directory.EnumerateDirectories(root);
            }
            catch (Exception ex)
            {
                LoggingService.Warn("EaScannerService", $"Error enumerating '{root}': {ex.Message}");
                continue;
            }

            foreach (var folder in gameFolders)
            {
                string manifestPath = Path.Combine(folder, "__Installer", "installerdata.xml");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    string xml = File.ReadAllText(manifestPath);
                    var match = ContentIdRegex().Match(xml);
                    if (match.Success)
                    {
                        index[match.Groups[1].Value] = manifestPath;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("EaScannerService", $"Error reading '{manifestPath}': {ex.Message}");
                }
            }
        }
        return index;
    }

    private static DiscoveredEaGame? ParseManifest(string contentId, string name, string manifestPath, HashSet<string> existingSet)
    {
        try
        {
            // The manifest's own folder (two levels up from __Installer\installerdata.xml) is the
            // real install directory - simpler and more reliable than resolving the registry
            // indirection its <filePath> is expressed against (see class doc comment).
            string? installerDir = Path.GetDirectoryName(manifestPath);
            string? installDir = installerDir != null ? Path.GetDirectoryName(installerDir) : null;
            if (string.IsNullOrEmpty(installDir)) return null;

            string xml = File.ReadAllText(manifestPath);
            // A manifest can list more than one <filePath> (per-locale/per-build launcher
            // blocks) - try every match in document order rather than only the first, since
            // an earlier entry may point at a build/locale this install doesn't actually have.
            string? exePath = null;
            foreach (Match filePathMatch in FilePathRegex().Matches(xml))
            {
                string rawFilePath = filePathMatch.Groups[1].Value;
                // Strip a leading "[HKEY_LOCAL_MACHINE\...]" registry-indirection prefix if
                // present - the remainder is relative to installDir, which we already have.
                int bracketEnd = rawFilePath.IndexOf(']');
                string relativePath = bracketEnd >= 0 ? rawFilePath[(bracketEnd + 1)..] : rawFilePath;
                string candidateExe = Path.Combine(installDir, relativePath);
                if (File.Exists(candidateExe))
                {
                    exePath = candidateExe;
                    break;
                }
            }

            if (exePath == null) return null;

            return new DiscoveredEaGame(
                ContentId: contentId,
                Name: name,
                InstallDir: installDir,
                ExePath: exePath,
                IconPath: exePath,
                IsAlreadyImported: existingSet.Contains(contentId)
            );
        }
        catch (Exception ex)
        {
            LoggingService.Warn("EaScannerService", $"Error parsing EA manifest '{manifestPath}': {ex.Message}");
            return null;
        }
    }
}
