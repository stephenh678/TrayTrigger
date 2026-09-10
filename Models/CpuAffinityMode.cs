namespace TrayTrigger.Models;

/// <summary>Per-game CPU affinity choice - see <see cref="GameEntry.CpuAffinity"/>.</summary>
public enum CpuAffinityMode
{
    /// <summary>Leave the process on every core (Windows default).</summary>
    Default,
    /// <summary>Pin the process to performance (P) cores on a hybrid CPU; no-op elsewhere.</summary>
    PerformanceCoresOnly
}
