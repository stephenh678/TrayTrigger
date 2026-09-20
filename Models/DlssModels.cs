using System;
using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>Where a DLSS setting's value came from before TrayTrigger touched it.</summary>
public enum DlssSettingOrigin
{
    /// <summary>Nothing was set at any layer. Undo means deleting, not writing a zero.</summary>
    Absent,
    /// <summary>A user or another tool set it on this game's own profile.</summary>
    UserSet,
    /// <summary>NVIDIA's own value for this game.</summary>
    Predefined,
    /// <summary>Inherited from the Global or base profile, not set on this game.</summary>
    Inherited
}

/// <summary>
/// The ownership record for one DLSS driver setting: what was there before, what TrayTrigger put
/// there, and where. See docs/dlss-plan.md - a single "we enabled it" boolean is not enough,
/// because it cannot tell a value the user chose from one TrayTrigger wrote, and so cannot undo
/// safely.
///
/// <para>Persisted on <see cref="GameEntry"/>. Plain properties with setters because it round
/// trips through the library's JSON.</para>
/// </summary>
public class DlssSettingRecord
{
    /// <summary>"SR", "RR" or "FG".</summary>
    public string Feature { get; set; } = string.Empty;

    public uint SettingId { get; set; }

    /// <summary>The value before TrayTrigger wrote. Null when the setting was absent entirely.</summary>
    public uint? PreviousValue { get; set; }

    public DlssSettingOrigin PreviousOrigin { get; set; } = DlssSettingOrigin.Absent;

    /// <summary>What TrayTrigger wrote. Undo only proceeds while the driver still reports this.</summary>
    public uint WrittenValue { get; set; }

    /// <summary>The DRS profile the setting landed in. Settings live on profiles, not games.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>The executable the setting was attached to, as the driver matches it.</summary>
    public string ApplicationName { get; set; } = string.Empty;

    public DateTime WrittenUtc { get; set; }
}

/// <summary>
/// What was observed about one DLSS feature, in order of how much it establishes. These are
/// observations, never causation: a loaded runtime shows what the process has open, not that
/// TrayTrigger put it there. See docs/dlss-plan.md - an earlier draft overclaimed here.
/// </summary>
public enum DlssObservationState
{
    /// <summary>
    /// The values are in the profile database and read back correctly. Says nothing about
    /// behaviour: the game has not been seen using them.
    /// </summary>
    SettingsSaved,
    /// <summary>A DLSS runtime was seen loaded, with its version and the path it came from.</summary>
    RuntimeObserved,
    /// <summary>A preset letter was actually read from the on-screen overlay.</summary>
    PresetObserved,
    /// <summary>
    /// Nothing could be read - enumeration refused, the feature not in use, or the game not
    /// running. Explicitly <b>not</b> the same as "the override failed".
    /// </summary>
    UnableToVerify
}

/// <summary>How an observation was obtained.</summary>
public enum DlssObservationMethod
{
    /// <summary>Read from the driver settings database after writing.</summary>
    WriteBack,
    /// <summary>The running process's loaded module list - the primary method.</summary>
    ModuleEnumeration,
    /// <summary>NVIDIA's NGX log, for games that refuse module enumeration.</summary>
    NgxLog,
    /// <summary>Read off the on-screen indicator.</summary>
    Overlay
}

/// <summary>
/// One feature's verification result, persisted because it can only be learned by playing.
/// Plain properties with setters - it round trips through the library's JSON.
/// </summary>
public class DlssObservation
{
    /// <summary>"SR", "RR" or "FG".</summary>
    public string Feature { get; set; } = string.Empty;

    public DlssObservationState State { get; set; } = DlssObservationState.UnableToVerify;

    /// <summary>The runtime version seen, when one was.</summary>
    public string? Version { get; set; }

    /// <summary>Where it was loaded from - the driver's NGX store, or the game folder. The proof.</summary>
    public string? LoadedFromPath { get; set; }

    /// <summary>Only set when a preset letter was actually read, never inferred.</summary>
    public string? Preset { get; set; }

    public DlssObservationMethod Method { get; set; } = DlssObservationMethod.ModuleEnumeration;

    public DateTime ObservedUtc { get; set; }

    /// <summary>The driver at the time. A driver change makes an observation stale, not wrong.</summary>
    public string? DriverVersion { get; set; }

    /// <summary>Why nothing could be read. Only meaningful for <see cref="DlssObservationState.UnableToVerify"/>.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The version the game itself shipped for this feature when the observation was taken. A game
    /// patch changes it, and an observation of the old setup is then describing something that no
    /// longer exists - not stale, wrong. Null for observations taken before this was recorded.
    /// </summary>
    public string? GameRuntimeVersion { get; set; }

    /// <summary>True when the runtime came out of the driver's own model store rather than the game.</summary>
    public bool FromDriverStore =>
        LoadedFromPath?.Contains(NgxStoreMarker, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The path fragment that identifies the driver's model store.</summary>
    public const string NgxStoreMarker = @"\NVIDIA\NGX\models\";
}

/// <summary>What happened to one setting during an apply or undo, for reporting and for tests.</summary>
public enum DlssSettingOutcome
{
    /// <summary>Written, or restored, as intended.</summary>
    Applied,
    /// <summary>Already exactly as TrayTrigger left it. Nothing to do.</summary>
    AlreadyCorrect,
    /// <summary>Restored to the captured previous value.</summary>
    Restored,
    /// <summary>Removed, because it was absent before TrayTrigger wrote it.</summary>
    Deleted,
    /// <summary>
    /// Left alone: the driver no longer reports the value TrayTrigger wrote, so something else
    /// changed it. Overwriting would destroy a choice TrayTrigger does not own.
    /// </summary>
    SkippedForeignChange,
    /// <summary>The driver refused. <see cref="DlssOperationResult.Error"/> says why.</summary>
    Failed,
    /// <summary>
    /// The save reported success, but reading the setting back from a fresh session did not show
    /// the value TrayTrigger wrote. The write did not land; nothing else can be trusted about it.
    /// </summary>
    WriteBackFailed,
    /// <summary>
    /// This driver does not have this setting. Not a failure - NVIDIA adds and retires setting ids
    /// between driver versions, and a recipe entry that no longer exists should be stepped over,
    /// not reported as something going wrong.
    /// </summary>
    NotSupportedByDriver
}

/// <summary>One setting's fate within an operation.</summary>
public sealed record DlssSettingOutcomeDetail(string Feature, uint SettingId, DlssSettingOutcome Outcome, string? Error);

/// <summary>The result of an apply or undo, including what was left alone and why.</summary>
public sealed record DlssOperationResult(
    bool Succeeded,
    string? Error,
    IReadOnlyList<DlssSettingOutcomeDetail> Details,
    IReadOnlyList<DlssSettingRecord> Records)
{
    /// <summary>True when at least one setting was left alone because something else had changed it.</summary>
    public bool HadForeignChanges => HasOutcome(DlssSettingOutcome.SkippedForeignChange);

    /// <summary>True when the driver accepted the save but did not report the values back.</summary>
    public bool HadWriteBackFailures => HasOutcome(DlssSettingOutcome.WriteBackFailed);

    private bool HasOutcome(DlssSettingOutcome outcome)
    {
        foreach (var d in Details)
            if (d.Outcome == outcome) return true;
        return false;
    }

    /// <summary>True when nothing needed doing - every setting was already as TrayTrigger left it.</summary>
    public bool WasAlreadyCorrect
    {
        get
        {
            if (Details.Count == 0) return false;
            foreach (var d in Details)
                if (d.Outcome != DlssSettingOutcome.AlreadyCorrect && d.Outcome != DlssSettingOutcome.NotSupportedByDriver)
                    return false;
            return true;
        }
    }

    public static DlssOperationResult Failure(string error) =>
        new(false, error, Array.Empty<DlssSettingOutcomeDetail>(), Array.Empty<DlssSettingRecord>());
}
