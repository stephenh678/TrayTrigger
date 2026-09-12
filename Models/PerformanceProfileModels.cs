using System.Collections.Generic;

namespace TrayTrigger.Models;

public enum PerformanceProfileMode
{
    Off,
    Optimized,
    Aggressive
}

/// <summary>
/// Tweaks that apply starting at the Optimized tier - and therefore also under Aggressive, since
/// Aggressive always builds on top of Optimized rather than configuring the same tweak twice.
/// Add a new bool here when a future tweak should apply to both tiers.
/// </summary>
public class OptimizedProfileTweakConfig
{
    public bool PowerPlanEnabled { get; set; }
    public bool GpuPreferenceEnabled { get; set; }

    /// <summary>
    /// Defaults to false even within Optimized - forcing HDR on changes how the screen looks for
    /// every app while the game runs, and can look worse if the game itself doesn't render HDR
    /// content well, so it's an explicit opt-in rather than something Optimized enables outright.
    /// </summary>
    public bool HdrEnabled { get; set; }

    /// <summary>
    /// Turns Windows toast notifications off for the length of the session (the notification
    /// centre's global toast switch), restoring the prior value on exit. Defaults to false: a user
    /// waiting on a message mid-game would rather see it.
    /// </summary>
    public bool DoNotDisturbEnabled { get; set; }
}

/// <summary>
/// Tweaks Aggressive adds on top of whatever Optimized already applies. These are never shown or
/// configured under Optimized - Aggressive's effective tweak set is Optimized's enabled tweaks
/// plus these. Add a new bool here when a future tweak should be Aggressive-only.
/// </summary>
public class AggressiveProfileTweakConfig
{
    public bool SystemResponsivenessEnabled { get; set; }
    public bool MmcssGamesPriorityEnabled { get; set; }
    public bool AboveNormalPriorityEnabled { get; set; }

    /// <summary>
    /// Defaults to false even within Aggressive - unlike the other tweaks this narrows real-time
    /// antivirus coverage for the game's own exe/folder while active, so it's an explicit opt-in
    /// rather than something enabled by picking Aggressive.
    /// </summary>
    public bool DefenderExclusionEnabled { get; set; }

    /// <summary>
    /// Holds a 0.5 ms system timer resolution request (NtSetTimerResolution) for the length of
    /// the session - what TimerTool/ISLC do. On Windows 10 2004+/11 this only reaches other
    /// processes when the permanent "System Timer Resolution" tweak (GlobalTimerResolutionRequests)
    /// is on; TrayTrigger's own request is released when the last session ends.
    /// </summary>
    public bool TimerResolutionEnabled { get; set; }
}

/// <summary>
/// Crash-recovery record for one game whose session applied a per-executable tweak (GPU
/// preference, Defender exclusion). Kept separate from the machine-wide tweaks below because
/// these are independent per game - two concurrently running games each get their own entry,
/// restored individually as each one's own session ends (not gated on every session ending).
/// </summary>
public class PerGameProfileSnapshot
{
    public string GameId { get; set; } = "";

    public bool GpuPreferenceCaptured { get; set; }
    public string? GpuPreferenceExecutablePath { get; set; }
    public string? PreviousGpuPreferenceValue { get; set; }

    public bool DefenderExclusionCaptured { get; set; }
    public string? DefenderExclusionPath { get; set; }

    /// <summary>True if the path was already excluded before we touched it - if so, restoring
    /// must leave it alone rather than removing an exclusion the user (or another tool) set.</summary>
    public bool DefenderExclusionWasPreExisting { get; set; }
}

/// <summary>
/// Crash-recovery record of one display's advanced-color (HDR) state, captured before Enable HDR
/// turns it on so restore can put that exact display back the way it was - a display already in
/// HDR before the game launched must stay in HDR after, not get forced off.
/// </summary>
public class HdrDisplaySnapshot
{
    public uint AdapterIdLowPart { get; set; }
    public int AdapterIdHighPart { get; set; }
    public uint TargetId { get; set; }
    public bool WasEnabled { get; set; }

    /// <summary>
    /// Whether the display was in WCG (wide colour gamut) rather than plain SDR before HDR was
    /// forced on. Recorded for the log only - restoring is still just "clear the HDR bit", because
    /// WCG versus SDR is decided by Windows' own per-display Auto Color Management setting, which
    /// TrayTrigger never touches, so the display drops back to whichever of the two it was in.
    /// Without this the restore line read "HDR state to Off", which looks like it left a
    /// WCG display in SDR.
    /// </summary>
    public bool WasWcg { get; set; }
}

/// <summary>
/// Crash-recovery record for an in-progress Performance Profile session: written to disk right
/// before any registry/power-plan value is changed, and deleted once everything is restored.
/// If found on the next startup, it means TrayTrigger was closed abnormally while a profile was
/// still active, so the recorded values (not a hard-coded default) are restored from it. Each
/// "Captured" flag scopes its restore to only the tweak(s) that were actually touched, since
/// Optimized and Aggressive don't always touch the same set.
///
/// PowerPlan/SystemResponsiveness/SchedulingCategory are machine-wide singletons - only the
/// first tracked session captures/applies them ("first wins"), and only the last tracked session
/// ending restores them. PerGameSnapshots are independent per game and are restored as soon as
/// that specific game's own session ends, regardless of other sessions still running.
/// </summary>
public class PerformanceProfileSessionSnapshot
{
    public bool PowerPlanCaptured { get; set; }
    public string? PreviousPowerSchemeGuid { get; set; }

    public bool SystemResponsivenessCaptured { get; set; }
    public int? PreviousSystemResponsiveness { get; set; }

    public bool SchedulingCategoryCaptured { get; set; }
    public string? PreviousSchedulingCategory { get; set; }

    public bool HdrCaptured { get; set; }
    public List<HdrDisplaySnapshot> PreviousHdrStates { get; set; } = new();

    /// <summary>Do Not Disturb: the global toast switch before the session (null = value absent = on).</summary>
    public bool ToastsCaptured { get; set; }
    public int? PreviousToastsEnabled { get; set; }

    /// <summary>A timer-resolution request is held by this TrayTrigger process. Nothing to recover
    /// after a crash (the request dies with the process) - tracked so the last session releases it.</summary>
    public bool TimerResolutionRequested { get; set; }

    public List<PerGameProfileSnapshot> PerGameSnapshots { get; set; } = new();
}
