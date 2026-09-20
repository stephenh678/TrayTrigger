using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Turns what was seen loaded in a running game into per-feature observations. Layer 2 of
/// verification in docs/dlss-plan.md, and the reason overriding games NVIDIA has not validated is
/// defensible: the product can say what actually happened.
///
/// <para><b>Observations, never causation.</b> A loaded runtime shows what the process has open.
/// It does not show that TrayTrigger caused it - an existing NVIDIA App setting or an over-the-air
/// model update produces the same reading - and seeing the game's own DLL does not establish that
/// the game refused the override. The wording here states what was read and stops.</para>
/// </summary>
public static class DlssVerificationService
{
    /// <summary>
    /// Observes a running game. Returns one observation per feature, always - a feature with
    /// nothing to say still reports why, because silence reads as failure.
    /// </summary>
    public static List<DlssObservation> Observe(
        Process? process, string? driverVersion,
        IReadOnlyList<DlssProbeService.ShippedRuntime>? shipped = null)
    {
        var modules = DlssProbeService.ScanLoadedModules(process, out string? note);
        return Interpret(modules, note, driverVersion, DateTime.UtcNow, shipped);
    }

    /// <summary>
    /// The pure half: module readings in, observations out. Exercised directly by tests, since the
    /// interesting cases - anti-cheat refusal, a feature not in use, the driver store versus the
    /// game folder - are all about interpretation rather than about reading a process.
    /// </summary>
    public static List<DlssObservation> Interpret(
        IReadOnlyList<DlssProbeService.LoadedRuntime> modules,
        string? scanNote,
        string? driverVersion,
        DateTime observedUtc,
        IReadOnlyList<DlssProbeService.ShippedRuntime>? shipped = null)
    {
        var result = new List<DlssObservation>();

        foreach (var feature in NgxModelStore.Features)
        {
            string? shippedVersion = shipped?.FirstOrDefault(r => r.Feature == feature.Name)?.FileVersion;
            var match = modules.FirstOrDefault(m => MatchesFeature(m, feature));

            if (match == null)
            {
                result.Add(new DlssObservation
                {
                    Feature = feature.Code,
                    State = DlssObservationState.UnableToVerify,
                    ObservedUtc = observedUtc,
                    DriverVersion = driverVersion,
                    GameRuntimeVersion = shippedVersion,
                    // The scan note when there was one - "enumeration denied" is a different fact
                    // from "this feature is not in use", and conflating them would be the
                    // "it failed" claim the plan forbids.
                    Note = scanNote ?? $"No {feature.Name.ToLowerInvariant()} activity observed."
                });
                continue;
            }

            result.Add(new DlssObservation
            {
                Feature = feature.Code,
                State = DlssObservationState.RuntimeObserved,
                Version = VersionOf(match),
                LoadedFromPath = match.Path,
                ObservedUtc = observedUtc,
                DriverVersion = driverVersion,
                GameRuntimeVersion = shippedVersion
            });
        }

        return result;
    }

    /// <summary>
    /// Whether a loaded module is this feature's runtime.
    ///
    /// <para>The driver's substituted runtime is a hashed <c>.bin</c> - <c>160_E658700.bin</c> -
    /// identical in name across all three features and distinguished only by the feature folder in
    /// its path. The game's own copy is a differently named DLL per feature. So the store is
    /// matched by directory and the game folder by file name, and never the other way round.</para>
    /// </summary>
    private static bool MatchesFeature(DlssProbeService.LoadedRuntime module, NgxModelStore.DlssFeature feature)
    {
        if (module.FromDriverStore)
            return module.Path.Contains($@"\models\{feature.StoreFolder}\", StringComparison.OrdinalIgnoreCase);

        // nvngx_dlss.dll must not match the dlssd or dlssg prefixes, so compare the stem exactly.
        string stem = Path.GetFileNameWithoutExtension(module.Path);
        return string.Equals(stem, feature.DllPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The runtime's version. For a store runtime this comes from the <c>versions\&lt;n&gt;</c>
    /// directory rather than the file: the hashed <c>.bin</c> carries no version resource of its
    /// own, so reading one off it would give nothing or something misleading.
    /// </summary>
    internal static string? VersionOf(DlssProbeService.LoadedRuntime module)
    {
        string? fromPath = VersionFromStorePath(module.Path);
        return fromPath ?? module.FileVersion;
    }

    /// <summary>Decodes the NGX store's numeric version directory, or null when the path has none.</summary>
    internal static string? VersionFromStorePath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!string.Equals(parts[i], "versions", StringComparison.OrdinalIgnoreCase)) continue;
            if (uint.TryParse(parts[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out uint encoded))
                return NgxModelStore.DecodeVersion(encoded);
        }
        return null;
    }

    /// <summary>
    /// One line describing an observation, in the plan's wording: state the reading, stop short of
    /// explaining it.
    /// </summary>
    public static string Describe(DlssObservation observation) => observation.State switch
    {
        DlssObservationState.RuntimeObserved when observation.FromDriverStore =>
            $"loaded {observation.Version} from NVIDIA",
        DlssObservationState.RuntimeObserved =>
            $"loaded {observation.Version} from the game's own files",
        _ => $"Unable to verify - {observation.Note ?? "nothing was readable"}"
    };

    /// <summary>
    /// Whether a stored observation still describes the current situation.
    ///
    /// <para>An observation is invalidated when the override changed or the game's own DLSS version
    /// changed - both mean it describes a setup that no longer exists. A <i>driver</i> change only
    /// makes it <b>stale</b>: what was seen was still seen, it just may not happen again.</para>
    /// </summary>
    public static bool IsStale(DlssObservation observation, string? currentDriverVersion) =>
        !string.IsNullOrEmpty(observation.DriverVersion) &&
        !string.IsNullOrEmpty(currentDriverVersion) &&
        !string.Equals(observation.DriverVersion, currentDriverVersion, StringComparison.Ordinal);

    /// <summary>
    /// Whether an observation should be discarded rather than shown. True once the game has shipped
    /// a different DLSS version - a patch - because the observation then describes a setup that no
    /// longer exists. Unknown-versus-known is not a change: observations recorded before this was
    /// tracked are kept rather than silently thrown away.
    /// </summary>
    public static bool IsInvalidated(DlssObservation observation, string? currentGameVersion) =>
        !string.IsNullOrEmpty(observation.GameRuntimeVersion) &&
        !string.IsNullOrEmpty(currentGameVersion) &&
        !string.Equals(observation.GameRuntimeVersion, currentGameVersion, StringComparison.Ordinal);
}
