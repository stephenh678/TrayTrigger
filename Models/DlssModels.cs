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

    /// <summary>
    /// The renderer's full path at the time. Profiles are looked up by this, not by
    /// <see cref="ApplicationName"/>: NVIDIA's own entries can be qualified by folder or launcher,
    /// and a bare file name such as game.exe can match a different game's profile. Empty on a
    /// record that predates it, which falls back to the name.
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// True when the profile did not exist and TrayTrigger created it. Undo then removes the
    /// profile too, once it is empty - otherwise every game ever overridden would leave one behind.
    /// </summary>
    public bool ProfileCreated { get; set; }
}

/// <summary>
/// What DLSS actually loaded the last time a game ran - the one thing that can only be learned by
/// playing. A reading, never a verdict: a runtime loaded from the game's own files shows what the
/// process had open, not that the override failed. Plain setters; it round trips through the
/// library's JSON.
/// </summary>
public class DlssLastRun
{
    /// <summary>Versions loaded out of the driver's own store - the override at work.</summary>
    public List<string> FromNvidia { get; set; } = new();

    /// <summary>Versions loaded from the game's own files.</summary>
    public List<string> FromGame { get; set; } = new();

    /// <summary>
    /// The oldest DLSS version the game itself shipped at the time. A game patch changes it, and
    /// the reading then describes a setup that no longer exists.
    /// </summary>
    public string? GameVersion { get; set; }

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
