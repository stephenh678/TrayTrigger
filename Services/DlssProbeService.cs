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
    /// <param name="searchRoot">
    /// The game's folder when the caller has already worked it out - see <see cref="Locate"/> -
    /// so the card reads the same folder the renderer was found in.
    /// </param>
    public static ProbeResult Probe(string executablePath, string? searchRoot = null)
    {
        string? gameDir = string.IsNullOrWhiteSpace(searchRoot) ? DlssSearchRoot(executablePath) : searchRoot;

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
    /// Folders that hold many games, one per subfolder: the manual scan locations, and each Steam
    /// library's <c>steamapps\common</c>. Supplied by whoever has the settings, so this service
    /// stays free of them. They do two jobs: the subfolder an executable sits under is exactly its
    /// game's folder, whatever the engine; and the library itself is never searched as one game.
    /// </summary>
    public static Func<IEnumerable<string>>? LibraryFolderProvider { get; set; }

    /// <summary>
    /// Folder names that only ever hold a game's binaries, never the game: the executable is in
    /// one and the rest of the game is above it. REDengine's <c>bin\x64</c>, CryEngine's
    /// <c>Bin64</c>, Source 2's <c>bin\win64</c>, Unreal's <c>Binaries\Win64</c>.
    /// </summary>
    private static readonly HashSet<string> BinaryFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "binaries", "x64", "x86", "win32", "win64", "wingdk",
        "win_x64", "win_x86", "retail", "shipping"
    };

    /// <summary>How far above the executable a game's folder is looked for.</summary>
    private const int MaxRootDepth = 4;

    /// <summary>
    /// The folder of the game <paramref name="executablePath"/> belongs to - where its DLSS DLLs
    /// are searched for - or null when the path has no folder.
    ///
    /// <para><b>Not the executable's own folder.</b> That was the first version, and it said "No
    /// DLSS files found" for any game that keeps its DLSS somewhere the executable is not: every
    /// Unreal game (the DLLs belong to a plugin under <c>Engine\Plugins</c>, the executable is in
    /// <c>&lt;Project&gt;\Binaries\Win64</c>), and any other engine with a <c>bin</c> folder
    /// beside its plugins. The game's whole folder is searched instead.</para>
    ///
    /// <para>Found by the strongest evidence there is, in order: the folder under Steam's
    /// <c>steamapps\common</c>; the folder under a known library (<see cref="LibraryFolderProvider"/>);
    /// the install folder the importer recorded; and, for a game added by its executable from
    /// anywhere else, by walking up out of binaries-only folders and an Unreal project folder.
    /// The walk never enters a library or a shared folder, so another game's DLLs are never
    /// found - that would put the override on another game's executable.</para>
    /// </summary>
    public static string? DlssSearchRoot(string executablePath, string? installDirectory = null)
    {
        string? exeDir = SafeDirectoryName(executablePath);
        if (string.IsNullOrEmpty(exeDir)) return exeDir;

        try
        {
            string full = Path.GetFullPath(exeDir);
            var libraries = LibraryFolders();

            // Which rule answered is logged every time: "No DLSS files found" on someone else's
            // machine is only explicable by knowing which folder was searched, and why that one.
            if (FolderUnder(full, SteamCommonFolder(full)) is { } steam)
                return Found(steam, "its folder under steamapps\\common");

            foreach (string library in libraries)
            {
                if (FolderUnder(full, library) is { } under)
                    return Found(under, $"its folder under the scan location '{library}'");
            }

            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                string install = Path.GetFullPath(installDirectory).TrimEnd('\\', '/');
                // Strictly above the executable: a game added by hand records the executable's own
                // folder here, which says nothing about where the game's folder starts.
                if (LevelsBelow(full, install) is > 0 and <= MaxRootDepth && !IsSharedFolder(install, libraries))
                    return Found(install, "the install folder recorded for the game");
            }

            var current = new DirectoryInfo(full);
            for (int i = 0; i < MaxRootDepth && BinaryFolderNames.Contains(current.Name) && CanStepTo(current.Parent, libraries); i++)
                current = current.Parent!;

            // An Unreal project folder: <root>\<Project>, with <root>\Engine beside it.
            bool unreal = false;
            if (current.Parent is { } parent && CanStepTo(parent, libraries) &&
                !string.Equals(current.Name, "Engine", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(parent.FullName, "Engine")))
            {
                current = parent;
                unreal = true;
            }

            return Found(current.FullName,
                unreal ? "the Unreal root (an Engine folder beside the project)"
                : string.Equals(current.FullName, full, StringComparison.OrdinalIgnoreCase) ? "the executable's own folder (nothing said the game starts higher)"
                : "walked up out of binaries-only folders");
        }
        catch (Exception ex)
        {
            // A path that cannot be walked is searched where it stands.
            LoggingService.Warn("Dlss", $"Could not work out the game folder for '{executablePath}' ({ex.Message}); searching '{exeDir}'.");
        }

        return exeDir;

        string Found(string root, string how)
        {
            LoggingService.Verbose("Dlss", $"Game folder for '{executablePath}' is '{root}': {how}.");
            return root;
        }
    }

    private static List<string> LibraryFolders()
    {
        try
        {
            return (LibraryFolderProvider?.Invoke() ?? Enumerable.Empty<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => Path.GetFullPath(l).TrimEnd('\\', '/'))
                .ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Dlss", $"Could not read the scan locations ({ex.Message}); game folders will be found by layout alone.");
            return new List<string>();
        }
    }

    /// <summary><c>...\steamapps\common</c> when <paramref name="dir"/> is inside one, else null.</summary>
    private static string? SteamCommonFolder(string dir)
    {
        int at = dir.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : dir[..(at + @"\steamapps\common".Length)];
    }

    /// <summary>The immediate subfolder of <paramref name="library"/> that holds <paramref name="dir"/>, or null.</summary>
    private static string? FolderUnder(string dir, string? library)
    {
        if (library == null || LevelsBelow(dir, library) is not > 0) return null;
        int end = dir.IndexOf('\\', library.Length + 1);
        return end < 0 ? dir : dir[..end];
    }

    /// <summary>How many folders <paramref name="dir"/> is below <paramref name="ancestor"/>: 0 for the same folder, null when it is not under it.</summary>
    private static int? LevelsBelow(string dir, string ancestor)
    {
        if (string.Equals(dir, ancestor, StringComparison.OrdinalIgnoreCase)) return 0;
        if (!dir.StartsWith(ancestor + '\\', StringComparison.OrdinalIgnoreCase)) return null;
        return dir[ancestor.Length..].Count(c => c == '\\');
    }

    private static bool CanStepTo(DirectoryInfo? dir, List<string> libraries) =>
        dir != null && !IsSharedFolder(dir.FullName, libraries);

    /// <summary>A folder that is not one game's own: a system or profile folder, a drive root, or a library of games.</summary>
    private static bool IsSharedFolder(string dir, List<string> libraries)
    {
        string trimmed = dir.TrimEnd('\\', '/');
        return ProcessPathResolver.IsUnsafeProcessFolder(dir, out _)
            || trimmed.EndsWith(@"\steamapps\common", StringComparison.OrdinalIgnoreCase)
            || libraries.Any(l => string.Equals(l, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// DLSS DLLs the game ships. Reported for context only - the driver path never reads them for
    /// anything but their version, and never writes them.
    /// </summary>
    public static List<ShippedRuntime> FindShippedRuntimes(string gameDirectory)
    {
        var result = new List<ShippedRuntime>();
        if (!Directory.Exists(gameDirectory))
        {
            LoggingService.Verbose("Dlss", $"Not scanning '{gameDirectory}' for DLSS DLLs: the folder does not exist.");
            return result;
        }

        // The walk is recursive, so a folder that is not one game's own - a drive root, Downloads,
        // the top of Program Files - would find other games' DLLs, and ResolveRenderingExecutable
        // would then hand back another game's executable to write the override to.
        bool unsafeFolder = ProcessPathResolver.IsUnsafeProcessFolder(gameDirectory, out string why);
        if (!unsafeFolder && IsSharedFolder(SafeFullPath(gameDirectory), LibraryFolders()))
        {
            unsafeFolder = true;
            why = "a library of games, not one game's folder";
        }
        if (unsafeFolder)
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

        if (LoggingService.IsVerboseEnabled)
        {
            LoggingService.Verbose("Dlss", result.Count == 0
                ? $"No nvngx_dlss*.dll anywhere under '{gameDirectory}'."
                : $"DLSS DLLs under '{gameDirectory}': {string.Join("; ", result.Select(r => $"{r.RelativePath} ({r.Feature} {r.FileVersion ?? "version unreadable"})"))}");
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
    public static string ResolveRenderingExecutable(string gameExecutablePath, string? installDirectory = null) =>
        Locate(gameExecutablePath, installDirectory).Renderer;

    /// <summary>Where a game is: the executable that renders it, and the folder its DLSS DLLs were looked for in.</summary>
    public sealed record GameLocation(string Renderer, string? Root);

    /// <summary>
    /// <see cref="ResolveRenderingExecutable"/>, together with the folder it searched - so the card
    /// can read the same folder rather than working one out again from the renderer.
    /// </summary>
    public static GameLocation Locate(string gameExecutablePath, string? installDirectory = null)
    {
        bool isFile = IsFilePath(gameExecutablePath);
        string? dir = isFile ? DlssSearchRoot(gameExecutablePath, installDirectory) : installDirectory;
        if (string.IsNullOrWhiteSpace(dir))
            return Located(gameExecutablePath, null, isFile ? "its path has no folder" : "it is launched by link and no install folder is recorded");

        var shipped = FindShippedRuntimes(dir);
        if (shipped.Count == 0) return Located(gameExecutablePath, dir, "the game ships no DLSS, so there is nothing to resolve");

        // The folders holding DLSS DLLs; usually exactly one. Then an Unreal game's own binaries:
        // its DLLs sit in a plugin folder with no executable in it, and the file at its root is a
        // stub that starts <Project>\Binaries\<platform>\...-Shipping.exe.
        var folders = shipped
            .Select(s => SafeDirectoryName(Path.Combine(dir, s.RelativePath)))
            .Where(d => d != null)
            .Select(d => d!)
            .Concat(UnrealBinariesFolders(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(d => (Folder: d, Exes: ExecutablesIn(d)))
            .ToList();

        // The launched executable already living beside the DLLs means it is the renderer. Looked
        // for in every folder before anything is guessed: a second copy of the DLLs elsewhere in
        // the game must not win over the folder the game is actually started from.
        if (isFile)
        {
            string givenName = Path.GetFileName(gameExecutablePath);
            var self = folders.SelectMany(f => f.Exes)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), givenName, StringComparison.OrdinalIgnoreCase));
            if (self != null) return Located(self, dir, "the launched executable sits beside the DLSS DLLs or in the game's binaries");
        }

        foreach (var (folder, exes) in folders)
        {
            if (LargestRenderer(exes) is { } best)
                return Located(best, dir, $"the largest executable in '{folder}' that is not a helper by name ({exes.Length} there)");
        }

        // DLLs in a folder of their own, whatever the engine. The executable the user launches is
        // the best answer there is, unless it says itself that it is a launcher - or there is no
        // executable at all (a game launched by link), and then the largest one in the game is.
        if (isFile && !IsNeverRenderer(gameExecutablePath))
            return Located(gameExecutablePath, dir, "no executable sits beside the DLSS DLLs, and the launched one does not call itself a launcher");

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = MaxRootDepth, IgnoreInaccessible = true };
            string engine = Path.Combine(dir, "Engine") + Path.DirectorySeparatorChar;
            var all = Directory.EnumerateFiles(dir, "*.exe", options)
                .Where(e => !e.StartsWith(engine, StringComparison.OrdinalIgnoreCase));
            if (LargestRenderer(all) is { } largest)
                return Located(largest, dir, "no executable sits beside the DLSS DLLs, so the largest in the game that is not a helper by name");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Dlss", $"Looking through '{dir}' for the game's executable failed: {ex.Message}");
        }

        // The game ships DLSS and nothing better than what was given turned up. For a link that
        // leaves nothing to write an override to, which is worth more than a verbose line.
        if (!isFile)
            LoggingService.Warn("Dlss", $"'{gameExecutablePath}' ships DLSS under '{dir}' but no executable to target was found there.");
        return Located(gameExecutablePath, dir, "nothing better was found");

        GameLocation Located(string renderer, string? root, string why)
        {
            bool moved = !string.Equals(renderer, gameExecutablePath, StringComparison.OrdinalIgnoreCase);
            LoggingService.Verbose("Dlss", moved
                ? $"Renderer for '{gameExecutablePath}' is '{renderer}': {why}."
                : $"Renderer for '{gameExecutablePath}' is the path as given: {why}.");
            return new GameLocation(renderer, root);
        }
    }

    private static string[] ExecutablesIn(string folder)
    {
        try { return Directory.GetFiles(folder, "*.exe"); }
        catch (Exception ex)
        {
            LoggingService.Verbose("Dlss", $"Could not list executables in '{folder}': {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static bool IsNeverRenderer(string exePath) =>
        NotRenderers.Any(n => Path.GetFileNameWithoutExtension(exePath).Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>The largest executable that is not a helper by name, or null.</summary>
    private static string? LargestRenderer(IEnumerable<string> exes) =>
        exes.Where(e => !IsNeverRenderer(e))
            .Select(e => (Path: e, Size: SafeLength(e)))
            .OrderByDescending(e => e.Size)
            .Select(e => e.Path)
            .FirstOrDefault();

    /// <summary>
    /// <c>&lt;root&gt;\&lt;Project&gt;\Binaries\&lt;platform&gt;</c> for each project under an Unreal
    /// root; empty for anything else. <c>Engine</c> is skipped: its binaries are the crash reporter
    /// and web helper, never the game.
    /// </summary>
    private static IEnumerable<string> UnrealBinariesFolders(string root)
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(Path.Combine(root, "Engine"))) return result;

            foreach (string project in Directory.GetDirectories(root))
            {
                if (string.Equals(Path.GetFileName(project), "Engine", StringComparison.OrdinalIgnoreCase)) continue;
                string binaries = Path.Combine(project, "Binaries");
                if (Directory.Exists(binaries)) result.AddRange(Directory.GetDirectories(binaries));
            }
        }
        catch { /* an unreadable root has no renderer to offer */ }
        return result;
    }

    /// <summary>True for a path on disk; false for empty, or a launch link such as steam://.</summary>
    public static bool IsFilePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !ProcessLauncherService.IsNonFileProtocolUrl(path);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { /* an unreadable file sorts as the smallest */ return 0; }
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

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { /* an invalid path is compared as written */ return path; }
    }

    private static string? SafeDirectoryName(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { /* an invalid path has no folder */ return null; }
    }
}
