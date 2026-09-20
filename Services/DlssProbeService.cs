using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TrayTrigger.Services;

/// <summary>
/// Reads what the DLSS card needs to know about one game, without changing anything: no DRS write,
/// no registry write, no game file touched. See docs/dlss-plan.md.
/// </summary>
public static class DlssProbeService
{
    /// <summary>
    /// The DLSS settings in NVIDIA's driver database. Ids are NVIDIA's; the names are Profile
    /// Inspector's, kept because they are what a user searching the web will find.
    ///
    /// <para><c>0x00634291</c>, "DLSS - Forced Model Preset Profile", is often listed alongside
    /// these and is <b>not a DRS setting</b>: <c>NvAPI_DRS_GetSettingNameFromId</c> does not
    /// recognise it and <c>SetSetting</c> refuses it (driver 616.64). The preset letters work
    /// without it.</para>
    /// </summary>
    public static readonly DlssSettingDefinition[] Settings =
    {
        // 1 substitutes the driver's runtime for the game's; 0x00FFFFFF is "NVIDIA's recommended
        // preset" - except for Frame Generation, whose sentinel is 0x00FFFFFE.
        new(0x10E41E01, "DLSS - Enable DLL Override",     "Super Resolution",   "SR", 1),
        new(0x10E41DF3, "DLSS - Forced Preset Letter",    "Super Resolution",   "SR", 0x00FFFFFF),
        new(0x10E41E02, "DLSS-RR - Enable DLL Override",  "Ray Reconstruction", "RR", 1),
        new(0x10E41DF7, "DLSS-RR - Forced Preset Letter", "Ray Reconstruction", "RR", 0x00FFFFFF),
        new(0x10E41E03, "DLSS-FG - Enable DLL Override",  "Frame Generation",   "FG", 1),
        new(0x10E41DF1, "DLSS-FG - Forced Preset Letter", "Frame Generation",   "FG", 0x00FFFFFE)
    };

    /// <summary>
    /// One DLSS driver setting: what it is, the short feature code the ownership record stores, and
    /// the value the override writes.
    /// </summary>
    public sealed record DlssSettingDefinition(uint Id, string Name, string Feature, string FeatureCode, uint RecommendedValue);

    /// <summary>A DLSS runtime the game itself ships, found in its install folder.</summary>
    public sealed record ShippedRuntime(string Feature, string RelativePath, string? FileVersion);

    /// <summary>A DLSS-related module observed loaded in a running game.</summary>
    public sealed record LoadedRuntime(string ModuleName, string Path, string? FileVersion, bool FromDriverStore);

    /// <summary>What the card is built from.</summary>
    public sealed record ProbeResult
    {
        /// <summary>
        /// False when the driver's settings database could not be opened at all - no NVIDIA driver,
        /// which is every AMD and Intel machine.
        /// </summary>
        public bool DriverAvailable { get; init; } = true;

        public IReadOnlyList<ShippedRuntime> ShippedRuntimes { get; init; } = Array.Empty<ShippedRuntime>();
        public IReadOnlyList<NgxModelStore.StoredRuntime> DriverRuntimes { get; init; } = Array.Empty<NgxModelStore.StoredRuntime>();
    }

    /// <summary>Never throws: a layer that cannot be read comes back empty.</summary>
    public static ProbeResult Probe(string executablePath)
    {
        string? gameDir = SafeDirectoryName(executablePath);

        return new ProbeResult
        {
            DriverAvailable = IsDriverAvailable(),
            ShippedRuntimes = gameDir == null ? Array.Empty<ShippedRuntime>() : FindShippedRuntimes(gameDir),
            DriverRuntimes = NgxModelStore.Enumerate()
        };
    }

    private static bool IsDriverAvailable()
    {
        using var session = NvApi.Session.TryOpen(out _);
        return session != null;
    }

    /// <summary>
    /// DLSS DLLs the game ships. Reported for context only - the driver path never reads them for
    /// anything but their version, and never writes them.
    /// </summary>
    public static List<ShippedRuntime> FindShippedRuntimes(string gameDirectory)
    {
        var result = new List<ShippedRuntime>();
        if (!Directory.Exists(gameDirectory)) return result;

        // The walk is recursive, so a folder that is not one game's own - a drive root, Downloads,
        // the top of Program Files - would find other games' DLLs, and ResolveRenderingExecutable
        // would then hand back another game's executable to write the override to.
        if (ProcessPathResolver.IsUnsafeProcessFolder(gameDirectory, out string why))
        {
            LoggingService.Verbose("Dlss", $"Not scanning {gameDirectory} for DLSS DLLs: it is {why}.");
            return result;
        }

        try
        {
            // One unreadable subfolder must not end the walk and lose everything after it.
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (string file in Directory.EnumerateFiles(gameDirectory, "nvngx_dlss*.dll", options))
            {
                string? version = null;
                try { version = NormalizeFileVersion(FileVersionInfo.GetVersionInfo(file)); }
                catch { /* an unreadable DLL is still worth listing */ }

                result.Add(new ShippedRuntime(FeatureForShippedFile(Path.GetFileName(file)), Path.GetRelativePath(gameDirectory, file), version));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Dlss", $"Scanning {gameDirectory} for DLSS DLLs failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// A DLSS DLL's version as "major.minor.build".
    ///
    /// <para>Not <see cref="FileVersionInfo.FileVersion"/>, which returns the version resource's
    /// own string: NVIDIA writes that comma-separated, so Cyberpunk 2077's DLSS reads back as
    /// "310,1,0,0". The numeric parts have no such formatting. The fourth part is dropped when it
    /// is zero, which it always is on NVIDIA's runtimes, so this matches the "310.9.0" form the
    /// driver's own model store uses.</para>
    /// </summary>
    public static string NormalizeFileVersion(FileVersionInfo info)
    {
        string version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        return info.FilePrivatePart == 0 ? version : $"{version}.{info.FilePrivatePart}";
    }

    /// <summary>
    /// Names that are never the thing doing the rendering, however large they are.
    /// </summary>
    private static readonly string[] NotRenderers =
    {
        "launcher", "prelauncher", "crash", "error", "report", "setup", "unins",
        "redist", "helper", "updater", "installer", "config", "benchmark", "editor"
    };

    /// <summary>
    /// The executable the driver will actually match a DLSS profile against.
    ///
    /// <para>Driver profiles key on the executable that renders, and for a launcher-based game
    /// that is <b>not</b> the one TrayTrigger launches. Cyberpunk 2077's entry points at
    /// <c>REDprelauncher.exe</c>, whose driver profile is "RED Launcher Service"; the renderer is
    /// <c>bin\x64\Cyberpunk2077.exe</c>, whose profile is "Cyberpunk 2077". Writing DLSS settings
    /// against the launcher would put them on a profile for a process that never renders a
    /// frame - silently doing nothing, with no error anywhere.</para>
    ///
    /// <para>The signal used is the strongest one available: <b>the renderer is the executable
    /// that sits beside the game's DLSS DLLs</b>, because that is how NGX finds them. Among those
    /// the largest wins, after discarding names that are never renderers - a 60 MB game executable
    /// against a 260 KB crash reporter. Falls back to the given path when the game ships no DLSS,
    /// or when nothing in that folder looks better.</para>
    ///
    /// <para>A heuristic, and one the verification step exists to catch: if this picks wrong, the
    /// module scan on a running game will show no substituted runtime.</para>
    /// </summary>
    /// <param name="installDirectory">
    /// Where to look when <paramref name="gameExecutablePath"/> is not a file at all. A Steam
    /// import's is <c>steam://rungameid/...</c>, and the same goes for any platform launched by
    /// link - so without this the whole feature would be invisible for most of a library. The
    /// importer records the install folder as the entry's working directory.
    /// </param>
    public static string ResolveRenderingExecutable(string gameExecutablePath, string? installDirectory = null)
    {
        bool isFile = IsFilePath(gameExecutablePath);
        string? dir = isFile ? SafeDirectoryName(gameExecutablePath) : installDirectory;
        if (string.IsNullOrWhiteSpace(dir)) return gameExecutablePath;

        var shipped = FindShippedRuntimes(dir);
        if (shipped.Count == 0) return gameExecutablePath;

        // The folders holding DLSS DLLs, most-shipped first; usually exactly one.
        var candidateDirs = shipped
            .Select(s => SafeDirectoryName(Path.Combine(dir, s.RelativePath)))
            .Where(d => d != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? givenName = isFile ? Path.GetFileName(gameExecutablePath) : null;

        foreach (string? candidateDir in candidateDirs)
        {
            string[] exes;
            try { exes = Directory.GetFiles(candidateDir!, "*.exe"); }
            catch { continue; }

            // The launched executable already living beside the DLLs means it is the renderer.
            var self = givenName == null ? null
                : exes.FirstOrDefault(e => string.Equals(Path.GetFileName(e), givenName, StringComparison.OrdinalIgnoreCase));
            if (self != null) return self;

            var best = exes
                .Where(e => !NotRenderers.Any(n => Path.GetFileNameWithoutExtension(e).Contains(n, StringComparison.OrdinalIgnoreCase)))
                .Select(e => (Path: e, Size: SafeLength(e)))
                .OrderByDescending(e => e.Size)
                .FirstOrDefault();

            if (best.Path != null) return best.Path;
        }

        return gameExecutablePath;
    }

    /// <summary>True for a path on disk; false for empty, or a launch link such as steam://.</summary>
    public static bool IsFilePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !ProcessLauncherService.IsNonFileProtocolUrl(path);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>Maps a shipped DLL name to its feature. Exposed for tests.</summary>
    public static string FeatureForShippedFile(string fileName) =>
        // Longest prefix first: "nvngx_dlss" is also the start of the other two.
        (NgxModelStore.Features
            .OrderByDescending(f => f.DllPrefix.Length)
            .FirstOrDefault(f => fileName.StartsWith(f.DllPrefix, StringComparison.OrdinalIgnoreCase))
         ?? NgxModelStore.Features[0]).Name;

    /// <summary>
    /// Enumerates the DLSS-related modules a running game has loaded.
    ///
    /// <para><b>Never match on file name.</b> When the driver substitutes a runtime, what appears
    /// in the process is a hashed .bin from the driver's store - <c>160_E658700.bin</c>, say - not
    /// <c>nvngx_dlss.dll</c>. Filtering on the DLL names reports "not
    /// substituted" on a machine where substitution is plainly working. Match on path and
    /// ProductName; the name filters are only a last resort for the game's own copy.</para>
    /// </summary>
    public static List<LoadedRuntime> ScanLoadedModules(Process? process, out string? note)
    {
        note = null;
        var result = new List<LoadedRuntime>();
        if (process == null) { note = "No running process supplied."; return result; }

        ProcessModuleCollection modules;
        try
        {
            process.Refresh();
            modules = process.Modules;
        }
        catch (Win32Exception ex)
        {
            // The expected result on anti-cheat titles. It is a finding, not a failure: it is what
            // makes the log-parsing and overlay layers mandatory rather than optional.
            note = $"Module enumeration denied ({ex.Message}). Expected on protected titles.";
            return result;
        }
        catch (Exception ex)
        {
            note = $"Module enumeration failed: {ex.Message}";
            return result;
        }

        foreach (ProcessModule module in modules)
        {
            string path = module.FileName ?? string.Empty;
            string name = module.ModuleName ?? string.Empty;
            string? product = null;
            try { product = module.FileVersionInfo.ProductName; } catch { /* optional */ }

            bool fromStore = path.Contains(Models.DlssLastRun.NgxStoreMarker, StringComparison.OrdinalIgnoreCase);
            bool byProduct = product != null &&
                             (product.Contains("DLSS", StringComparison.OrdinalIgnoreCase) ||
                              product.Contains("NGX", StringComparison.OrdinalIgnoreCase) ||
                              product.Contains("Streamline", StringComparison.OrdinalIgnoreCase));
            bool byName = name.StartsWith("nvngx", StringComparison.OrdinalIgnoreCase) ||
                          name.StartsWith("sl.", StringComparison.OrdinalIgnoreCase);

            if (!fromStore && !byProduct && !byName) continue;

            string? version = null;
            // Normalized, as the shipped DLLs are: NVIDIA's own string reads "310,1,0,0".
            try { version = NormalizeFileVersion(module.FileVersionInfo); } catch { /* optional */ }

            result.Add(new LoadedRuntime(name, path, version, fromStore));
        }

        if (result.Count == 0)
            note = "No DLSS modules loaded. The game may not be using DLSS in its current settings.";

        return result;
    }

    /// <summary>
    /// Turns the modules seen in a running game into the one thing the card reports. Null when no
    /// DLSS runtime was among them - not running, not in use yet, or refused by anti-cheat, none of
    /// which is a result worth recording.
    ///
    /// <para>The driver's substituted runtime is a hashed <c>.bin</c>, identical in name across
    /// all three features and told apart only by the feature folder in its path; the game's own
    /// copy is a differently named DLL per feature. So the store is matched by directory and the
    /// game folder by file name. Where both are loaded for a feature the store's wins: NGX can
    /// have the game's DLL open as well as the one it substituted.</para>
    /// </summary>
    public static Models.DlssLastRun? ReadLastRun(IReadOnlyList<LoadedRuntime> modules, string? gameVersion)
    {
        var result = new Models.DlssLastRun { GameVersion = gameVersion };

        foreach (var feature in NgxModelStore.Features)
        {
            var match = modules
                .Where(m => m.FromDriverStore
                    ? m.Path.Contains($@"\models\{feature.StoreFolder}\", StringComparison.OrdinalIgnoreCase)
                    // nvngx_dlss must not match the dlssd or dlssg prefixes, so compare the stem exactly.
                    : string.Equals(Path.GetFileNameWithoutExtension(m.Path), feature.DllPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.FromDriverStore)
                .FirstOrDefault();
            if (match == null) continue;

            // A store runtime's version is its versions\<n> folder: the .bin has no version resource.
            string? version = match.FromDriverStore ? VersionFromStorePath(match.Path) : match.FileVersion;
            if (string.IsNullOrEmpty(version)) continue;

            var list = match.FromDriverStore ? result.FromNvidia : result.FromGame;
            if (!list.Contains(version)) list.Add(version);
        }

        return result.FromNvidia.Count + result.FromGame.Count == 0 ? null : result;
    }

    /// <summary>Decodes the NGX store's numeric version directory, or null when the path has none.</summary>
    internal static string? VersionFromStorePath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!string.Equals(parts[i], "versions", StringComparison.OrdinalIgnoreCase)) continue;
            if (uint.TryParse(parts[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint encoded))
                return NgxModelStore.DecodeVersion(encoded);
        }
        return null;
    }

    /// <summary>
    /// The oldest version among what a game ships - the one with the most to gain, and the one the
    /// card shows. Compared numerically: "310.9.0" beats "310.7.128", which a string compare gets
    /// backwards.
    /// </summary>
    public static string? OldestVersion(IEnumerable<ShippedRuntime> shipped) =>
        shipped.Select(s => s.FileVersion).Where(v => !string.IsNullOrEmpty(v)).OrderBy(VersionKey).FirstOrDefault();

    /// <summary>Unparseable parts sort lowest rather than throwing.</summary>
    public static (int Major, int Minor, int Patch) VersionKey(string? version)
    {
        var parts = (version ?? string.Empty).Split('.');
        int At(int i) => parts.Length > i && int.TryParse(parts[i], out int n) ? n : 0;
        return (At(0), At(1), At(2));
    }

    private static string? SafeDirectoryName(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }
}
