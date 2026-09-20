using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// Gathers everything TrayTrigger can observe about DLSS on this machine, for one game, without
/// changing anything. Step 1 of the build order in docs/dlss-plan.md, plus the read half of the
/// verification layer.
///
/// <para>Strictly read-only: no DRS write, no registry write, no game file touched. It exists to
/// answer "what do we actually know before we act", which the plan makes a precondition for the
/// apply path rather than a nicety.</para>
/// </summary>
public static class DlssProbeService
{
    /// <summary>
    /// The DLSS settings in NVIDIA's driver database. Ids are NVIDIA's; the names are Profile
    /// Inspector's, kept because they are what a user searching the web will find.
    /// </summary>
    public static readonly DlssSettingDefinition[] Settings =
    {
        new(0x10E41E01, "DLSS - Enable DLL Override",           "Super Resolution", "Substitutes the driver's runtime for the game's"),
        new(0x00634291, "DLSS - Forced Model Preset Profile",   "Super Resolution", "The gate: without it the preset letters may do nothing"),
        new(0x10E41DF3, "DLSS - Forced Preset Letter",          "Super Resolution", "0x00FFFFFF = use NVIDIA's recommended preset"),
        new(0x10E41E02, "DLSS-RR - Enable DLL Override",        "Ray Reconstruction", "Substitutes the driver's runtime for the game's"),
        new(0x10E41DF7, "DLSS-RR - Forced Preset Letter",       "Ray Reconstruction", "0x00FFFFFF = use NVIDIA's recommended preset"),
        new(0x10E41E03, "DLSS-FG - Enable DLL Override",        "Frame Generation", "Substitutes the driver's runtime for the game's"),
        new(0x10E41DF1, "DLSS-FG - Forced Preset Letter",       "Frame Generation", "Sentinel is 0x00FFFFFE here, not 0x00FFFFFF")
    };

    /// <summary>One DLSS driver setting: what it is and why the plan writes it.</summary>
    public sealed record DlssSettingDefinition(uint Id, string Name, string Feature, string Purpose);

    /// <summary>NVIDIA's NGX diagnostics key. Only read here; the overlay toggle would write it later.</summary>
    private const string NgxCoreKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";

    // ---- Result shapes ---------------------------------------------------------------------

    /// <summary>A DLSS runtime the game itself ships, found in its install folder.</summary>
    public sealed record ShippedRuntime(string Feature, string FileName, string RelativePath, string? FileVersion, long SizeBytes);

    /// <summary>A DLSS-related module observed loaded in a running game.</summary>
    public sealed record LoadedRuntime(string ModuleName, string Path, string? FileVersion, string? ProductName, bool FromDriverStore);

    /// <summary>The state of one DLSS setting as the driver reports it for a game.</summary>
    public sealed record SettingState(DlssSettingDefinition Definition, NvApi.DrsSettingValue? Value, string? Note)
    {
        /// <summary>True when the driver has no value for this setting at any layer.</summary>
        public bool IsAbsent => Value == null;
    }

    /// <summary>Everything the probe could observe for one game.</summary>
    public sealed record ProbeResult
    {
        public required string ExecutablePath { get; init; }
        public required string ExecutableName { get; init; }
        public string? GpuName { get; init; }
        public string? DriverVersion { get; init; }

        /// <summary>Null when NVAPI could not be reached; the reason is in <see cref="DrsError"/>.</summary>
        public NvApi.DrsProfileInfo? Profile { get; init; }
        public string? DrsError { get; init; }
        /// <summary>True when the driver has no application profile for this executable at all.</summary>
        public bool ProfileMissing { get; init; }

        public IReadOnlyList<SettingState> SettingStates { get; init; } = Array.Empty<SettingState>();
        public IReadOnlyList<NvApi.DrsSettingValue> GlobalProfileDlssSettings { get; init; } = Array.Empty<NvApi.DrsSettingValue>();

        public IReadOnlyList<ShippedRuntime> ShippedRuntimes { get; init; } = Array.Empty<ShippedRuntime>();
        public IReadOnlyList<NgxModelStore.StoredRuntime> DriverRuntimes { get; init; } = Array.Empty<NgxModelStore.StoredRuntime>();
        public IReadOnlyList<NgxModelStore.AppVersionMapping> OverrideMappings { get; init; } = Array.Empty<NgxModelStore.AppVersionMapping>();

        public IReadOnlyList<LoadedRuntime> LoadedRuntimes { get; init; } = Array.Empty<LoadedRuntime>();
        /// <summary>Why module enumeration produced nothing - anti-cheat, not running, or no DLSS in use.</summary>
        public string? ModuleScanNote { get; init; }

        public IReadOnlyDictionary<string, object?> NgxRegistry { get; init; } = new Dictionary<string, object?>();
    }

    // ---- The probe -------------------------------------------------------------------------

    /// <summary>
    /// Collects the full picture for one game executable. Never throws: every layer degrades to a
    /// note, because a probe that fails loudly on one machine is useless as a diagnostic.
    /// </summary>
    public static ProbeResult Probe(string executablePath, Process? runningProcess = null)
    {
        string exeName = Path.GetFileName(executablePath);
        string? gameDir = SafeDirectoryName(executablePath);

        var (gpu, driver) = ReadGpu();
        var (profile, settingStates, globalDlss, drsError, profileMissing) = ReadDrs(exeName);

        return new ProbeResult
        {
            ExecutablePath = executablePath,
            ExecutableName = exeName,
            GpuName = gpu,
            DriverVersion = driver,
            Profile = profile,
            DrsError = drsError,
            ProfileMissing = profileMissing,
            SettingStates = settingStates,
            GlobalProfileDlssSettings = globalDlss,
            ShippedRuntimes = gameDir == null ? Array.Empty<ShippedRuntime>() : FindShippedRuntimes(gameDir),
            DriverRuntimes = NgxModelStore.Enumerate(),
            OverrideMappings = NgxModelStore.ReadConfig(),
            LoadedRuntimes = ScanLoadedModules(runningProcess, out string? note),
            ModuleScanNote = note,
            NgxRegistry = ReadNgxRegistry()
        };
    }

    private static (string? Gpu, string? Driver) ReadGpu()
    {
        try
        {
            var gpu = new SystemInfoService().GetGpuInfoList().FirstOrDefault(g =>
                g.ModelName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            return (gpu?.ModelName, gpu?.DriverVersion);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Dlss", $"GPU query failed: {ex.Message}");
            return (null, null);
        }
    }

    private static (NvApi.DrsProfileInfo?, List<SettingState>, List<NvApi.DrsSettingValue>, string?, bool)
        ReadDrs(string exeName)
    {
        var states = new List<SettingState>();
        var globalDlss = new List<NvApi.DrsSettingValue>();

        using var session = NvApi.Session.TryOpen(out string? error);
        if (session == null) return (null, states, globalDlss, error, false);

        var profile = session.FindProfileForExecutable(exeName, out IntPtr handle, out string? findError);
        bool missing = profile == null;

        if (profile != null)
        {
            foreach (var def in Settings)
            {
                var value = session.GetSetting(handle, def.Id, out string? note);
                states.Add(new SettingState(def, value, value == null ? note : null));
            }
        }

        // The Global profile is read regardless: a value there applies to every game that has no
        // per-game override, and on the development machine four DLSS settings were already
        // present from another tool. Anything reporting only the per-game layer would miss it.
        var known = Settings.Select(s => s.Id).ToHashSet();
        if (session.GetGlobalProfile(out IntPtr globalHandle, out _) != null)
        {
            globalDlss = session.EnumSettings(globalHandle, out _)
                .Where(s => known.Contains(s.SettingId))
                .ToList();
        }

        return (profile, states, globalDlss, findError, missing);
    }

    /// <summary>
    /// DLSS DLLs the game ships. Reported for context only - the driver path never reads them for
    /// anything but their version, and never writes them.
    /// </summary>
    public static List<ShippedRuntime> FindShippedRuntimes(string gameDirectory)
    {
        var result = new List<ShippedRuntime>();
        if (!Directory.Exists(gameDirectory)) return result;

        try
        {
            foreach (string file in Directory.EnumerateFiles(gameDirectory, "nvngx_dlss*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                string feature = FeatureForShippedFile(name);
                string? version = null;
                long size = 0;
                try
                {
                    version = FileVersionInfo.GetVersionInfo(file).FileVersion;
                    size = new FileInfo(file).Length;
                }
                catch { /* an unreadable DLL is still worth listing */ }

                result.Add(new ShippedRuntime(feature, name, Path.GetRelativePath(gameDirectory, file), version, size));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Dlss", $"Scanning {gameDirectory} for DLSS DLLs failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>Maps a shipped DLL name to its feature. Exposed for tests.</summary>
    public static string FeatureForShippedFile(string fileName) =>
        fileName.StartsWith("nvngx_dlssg", StringComparison.OrdinalIgnoreCase) ? "Frame Generation"
        : fileName.StartsWith("nvngx_dlssd", StringComparison.OrdinalIgnoreCase) ? "Ray Reconstruction"
        : "Super Resolution";

    /// <summary>
    /// Enumerates the DLSS-related modules a running game has loaded.
    ///
    /// <para><b>Never match on file name.</b> When the driver substitutes a runtime, what appears
    /// in the process is a hashed .bin from the driver's store - <c>160_E658700.bin</c> on the
    /// development machine - not <c>nvngx_dlss.dll</c>. An earlier version of this check filtered
    /// on the DLL names and reported "not substituted" on a machine where substitution was plainly
    /// working. Match on path and ProductName; the name filters are only a last resort for the
    /// game's own copy.</para>
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

            bool fromStore = path.Contains(@"\NVIDIA\NGX\models\", StringComparison.OrdinalIgnoreCase);
            bool byProduct = product != null &&
                             (product.Contains("DLSS", StringComparison.OrdinalIgnoreCase) ||
                              product.Contains("NGX", StringComparison.OrdinalIgnoreCase) ||
                              product.Contains("Streamline", StringComparison.OrdinalIgnoreCase));
            bool byName = name.StartsWith("nvngx", StringComparison.OrdinalIgnoreCase) ||
                          name.StartsWith("sl.", StringComparison.OrdinalIgnoreCase);

            if (!fromStore && !byProduct && !byName) continue;

            string? version = null;
            try { version = module.FileVersionInfo.FileVersion; } catch { /* optional */ }

            result.Add(new LoadedRuntime(name, path, version, product, fromStore));
        }

        if (result.Count == 0)
            note = "No DLSS modules loaded. The game may not be using DLSS in its current settings.";

        return result;
    }

    /// <summary>NVIDIA's NGX diagnostics values, read only. Absent values are reported as null.</summary>
    public static Dictionary<string, object?> ReadNgxRegistry()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ShowDlssIndicator"] = null,
            ["LogLevel"] = null,
            ["EnableLogPathOverride"] = null,
            ["LogPath"] = null
        };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(NgxCoreKey);
            if (key == null) return values;
            foreach (string name in values.Keys.ToList()) values[name] = key.GetValue(name);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Dlss", $"Reading NGXCore failed: {ex.Message}");
        }
        return values;
    }

    private static string? SafeDirectoryName(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }
}
