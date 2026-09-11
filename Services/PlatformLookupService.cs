using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TrayTrigger.Services;

/// <summary>
/// Which launcher platform a path was resolved to, plus that platform's own discovery record.
/// Exactly one of the record properties is non-null, matching <see cref="Platform"/>.
/// </summary>
public sealed class PlatformMatch
{
    public string Platform { get; }
    public DiscoveredSteamGame? Steam { get; init; }
    public DiscoveredGogGame? Gog { get; init; }
    public DiscoveredEaGame? Ea { get; init; }
    public DiscoveredEpicGame? Epic { get; init; }
    public DiscoveredUbisoftGame? Ubisoft { get; init; }
    public DiscoveredXboxGame? Xbox { get; init; }

    private PlatformMatch(string platform) => Platform = platform;

    public static PlatformMatch ForSteam(DiscoveredSteamGame g) => new("Steam") { Steam = g };
    public static PlatformMatch ForGog(DiscoveredGogGame g) => new("GOG") { Gog = g };
    public static PlatformMatch ForEa(DiscoveredEaGame g) => new("EA") { Ea = g };
    public static PlatformMatch ForEpic(DiscoveredEpicGame g) => new("Epic") { Epic = g };
    public static PlatformMatch ForUbisoft(DiscoveredUbisoftGame g) => new("Ubisoft") { Ubisoft = g };
    public static PlatformMatch ForXbox(DiscoveredXboxGame g) => new("Xbox") { Xbox = g };

    public string Name => Steam?.Name ?? Gog?.Name ?? Ea?.Name ?? Epic?.Name ?? Ubisoft?.Name ?? Xbox?.Name ?? string.Empty;
}

/// <summary>
/// Answers "is this exe/folder actually part of an installed Steam/GOG/EA/Epic/Ubisoft game?"
/// for the manual import routes (file/shortcut drop, Add Folder, folder drop, Batch Add). Those
/// routes otherwise run the generic exe heuristics in <see cref="FolderScannerService"/> and tag
/// the result as a Local game - which launches the exe directly, bypassing the launcher (many
/// EA/Ubisoft/Epic/Steam-DRM titles then fail or bounce through the launcher anyway), loses the
/// platform's own name/art/ID, and for Steam used to create a duplicate on the next "Scan for
/// Games". "Scan for Games" itself never had this problem because it discovers those platforms'
/// games from their own registry/manifest records rather than from a folder path.
///
/// Matching is by install-directory containment: the path is a hit if it sits under a platform
/// record's install directory. That runs per exe path, not per dropped folder, so a mixed parent
/// folder (one GOG game plus three genuinely local ones) resolves correctly per candidate.
///
/// Deliberately independent of the per-platform integration toggles: those gate automatic
/// scanning, but recognizing what a dropped folder <em>is</em> shouldn't depend on whether the
/// user wants that platform auto-scanned.
///
/// Known gap, inherited from <see cref="EaScannerService"/>: an EA game installed outside EA's
/// default install roots can't be resolved and still lands as Local.
/// </summary>
public class PlatformLookupService
{
    private readonly SteamScannerService _steam;
    private readonly GogScannerService _gog;
    private readonly EaScannerService _ea;
    private readonly EpicScannerService _epic;
    private readonly UbisoftScannerService _ubisoft;
    private readonly XboxScannerService _xbox;

    public PlatformLookupService(
        SteamScannerService steam,
        GogScannerService gog,
        EaScannerService ea,
        EpicScannerService epic,
        UbisoftScannerService ubisoft,
        XboxScannerService xbox)
    {
        _steam = steam;
        _gog = gog;
        _ea = ea;
        _epic = epic;
        _ubisoft = ubisoft;
        _xbox = xbox;
    }

    /// <summary>
    /// Builds a one-shot index for resolving many paths in one import operation (a batch of
    /// candidates). GOG/EA/Epic records are read once up front (all cheap registry/manifest
    /// reads); Steam and Ubisoft are resolved lazily per query since their full scans walk the
    /// filesystem for every installed game.
    /// </summary>
    public Index CreateIndex() => new(this);

    /// <summary>Convenience for a single path - see <see cref="Index.Match"/>.</summary>
    public PlatformMatch? FindByPath(string path) => CreateIndex().Match(path);

    /// <summary>
    /// True if <paramref name="path"/> is <paramref name="directory"/> itself or anything
    /// beneath it. Tolerates mixed separators, trailing separators, relative segments, and case
    /// differences; never matches a sibling that merely shares a name prefix
    /// (C:\Games\Foo vs C:\Games\Foobar).
    /// </summary>
    public static bool IsPathUnderDirectory(string? path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;

        string? fullPath = Normalize(path);
        string? fullDir = Normalize(directory);
        if (fullPath == null || fullDir == null) return false;

        if (string.Equals(fullPath, fullDir, StringComparison.OrdinalIgnoreCase)) return true;

        string dirWithSeparator = fullDir.EndsWith('\\') ? fullDir : fullDir + '\\';
        return fullPath.StartsWith(dirWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string p)
    {
        try
        {
            return Path.GetFullPath(p.Replace('/', '\\')).TrimEnd('\\');
        }
        catch
        {
            return null;
        }
    }

    public sealed class Index
    {
        private readonly PlatformLookupService _owner;
        private List<SteamScannerService.SteamInstallEntry>? _steamEntries;
        private List<DiscoveredGogGame>? _gogGames;
        private List<DiscoveredEaGame>? _eaGames;
        private List<DiscoveredEpicGame>? _epicGames;
        private List<(string GameId, string InstallDir)>? _ubisoftInstalls;
        private List<DiscoveredXboxGame>? _xboxGames;
        // Resolved-once caches for the two platforms whose full record costs a filesystem walk.
        private readonly Dictionary<string, DiscoveredSteamGame?> _steamResolved = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DiscoveredUbisoftGame?> _ubisoftResolved = new(StringComparer.OrdinalIgnoreCase);

        internal Index(PlatformLookupService owner) => _owner = owner;

        /// <summary>
        /// The platform record whose install directory contains <paramref name="path"/>, or null
        /// if it's not part of any recognized launcher install. Every platform's lookup is
        /// isolated: a failure in one (logged) never prevents the others from being tried. Each
        /// platform's install list is read once per index; a batch of N candidates is one pass
        /// over the manifests/registry, and the Steam/Ubisoft exe walks run once per matched game.
        /// Not thread-safe - callers use one index sequentially per import operation.
        /// </summary>
        public PlatformMatch? Match(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // Steam first: its libraries are the most common thing users drag in, and Ubisoft
            // titles bought on Steam register with both - Steam is the one that launches them.
            _steamEntries ??= Try(() => _owner._steam.GetInstalledGameEntries(), "Steam", path) ?? [];
            foreach (var entry in _steamEntries)
            {
                if (!IsPathUnderDirectory(path, entry.CommonDir)) continue;
                if (!_steamResolved.TryGetValue(entry.ManifestPath, out var steam))
                {
                    steam = Try(() => _owner._steam.ResolveInstalledGame(entry), "Steam", path);
                    _steamResolved[entry.ManifestPath] = steam;
                }
                if (steam != null) return PlatformMatch.ForSteam(steam);
            }

            _gogGames ??= Try(() => _owner._gog.ScanInstalledGames([]), "GOG", path) ?? [];
            var gog = _gogGames.FirstOrDefault(g => IsPathUnderDirectory(path, g.InstallDir));
            if (gog != null) return PlatformMatch.ForGog(gog);

            _epicGames ??= Try(() => _owner._epic.ScanInstalledGames([]), "Epic", path) ?? [];
            var epic = _epicGames.FirstOrDefault(g => IsPathUnderDirectory(path, g.InstallDir));
            if (epic != null) return PlatformMatch.ForEpic(epic);

            _eaGames ??= Try(() => _owner._ea.ScanInstalledGames([]), "EA", path) ?? [];
            var ea = _eaGames.FirstOrDefault(g => IsPathUnderDirectory(path, g.InstallDir));
            if (ea != null) return PlatformMatch.ForEa(ea);

            _ubisoftInstalls ??= Try(() => _owner._ubisoft.GetInstallDirs(), "Ubisoft", path) ?? [];
            foreach (var (gameId, installDir) in _ubisoftInstalls)
            {
                if (!IsPathUnderDirectory(path, installDir)) continue;
                if (!_ubisoftResolved.TryGetValue(gameId, out var ubisoft))
                {
                    ubisoft = Try(() => _owner._ubisoft.ResolveInstall(gameId, installDir), "Ubisoft", path);
                    _ubisoftResolved[gameId] = ubisoft;
                }
                if (ubisoft != null) return PlatformMatch.ForUbisoft(ubisoft);
            }

            // Both the readable "D:\XboxGames\<Game>\Content" folder and the WindowsApps package
            // root count: a user can only ever drag the former in, but paths recorded from a
            // running process use the latter.
            _xboxGames ??= Try(() => _owner._xbox.ScanInstalledGames([]), "Xbox", path) ?? [];
            var xbox = _xboxGames.FirstOrDefault(g => IsPathUnderDirectory(path, g.InstallDir) || IsPathUnderDirectory(path, g.PackageRoot));
            if (xbox != null) return PlatformMatch.ForXbox(xbox);

            return null;
        }

        private static T? Try<T>(Func<T?> lookup, string platform, string path) where T : class
        {
            try
            {
                return lookup();
            }
            catch (Exception ex)
            {
                LoggingService.Warn("PlatformLookupService", $"{platform} lookup failed for '{path}': {ex.Message}");
                return null;
            }
        }
    }
}
