namespace TrayTrigger.Models;

/// <summary>
/// Per-game CPU Cores choice - see <see cref="GameEntry.CpuAffinity"/>. Stored as a number in
/// games.json, so new values only ever go on the end.
/// </summary>
public enum CpuAffinityMode
{
    /// <summary>Leave the process on every core (Windows default).</summary>
    Default,
    /// <summary>Keep the process on the performance (P) cores of a hybrid CPU; no-op elsewhere.</summary>
    PerformanceCoresOnly,
    /// <summary>
    /// Whatever this PC's CPU is best served by: P-cores on a hybrid CPU, the 3D V-Cache CCD on a
    /// dual-CCD X3D, otherwise nothing. Worked out on each launch, so a library that moves to another
    /// PC does the right thing there.
    /// </summary>
    Auto,
    /// <summary>Keep the process on the CCD with 3D V-Cache (7950X3D, 9950X3D and the like); no-op elsewhere.</summary>
    VCacheCores,
    /// <summary>Keep the process on the CCD without 3D V-Cache, which clocks higher; no-op elsewhere.</summary>
    FrequencyCores,
    /// <summary>Keep the process on the first CCD of a CPU with several, so its threads share one L3 cache.</summary>
    OneCcd
}
