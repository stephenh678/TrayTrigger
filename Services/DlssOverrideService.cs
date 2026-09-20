using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Applies and undoes the DLSS driver override, keeping the ownership record that makes undo safe.
/// Step 3 of the build order in docs/dlss-plan.md.
///
/// <para>The rule the whole design turns on: <b>undo puts back what was captured, and only while
/// the driver still reports the value TrayTrigger wrote.</b> If something else changed it -
/// NVIDIA App, Profile Inspector, the user - TrayTrigger leaves it alone and says so. A single
/// "we enabled it" flag cannot express that, which is why every setting carries its own record.</para>
/// </summary>
public sealed class DlssOverrideService(IDrsBackend backend)
{
    private readonly IDrsBackend _backend = backend;

    public DlssOverrideService() : this(new NvApiDrsBackend()) { }

    public bool IsAvailable => _backend.IsAvailable;

    /// <summary>
    /// Writes the "use recommended" recipe for a game, capturing what each setting was first.
    ///
    /// <para>Existing records are replaced, not merged: re-applying re-captures, and the capture
    /// must describe the state immediately before <i>this</i> write.</para>
    /// </summary>
    public DlssOperationResult Apply(string executablePath, string gameName)
    {
        string exeName = Path.GetFileName(executablePath);
        if (string.IsNullOrWhiteSpace(exeName)) return DlssOperationResult.Failure("No executable to target.");

        using var session = _backend.OpenSession(out string? error);
        if (session == null) return DlssOperationResult.Failure(error ?? "The NVIDIA driver settings database is unavailable.");

        var profile = session.FindProfileForExecutable(exeName, out _);
        if (profile == null)
        {
            // NVIDIA has no entry for this game. Creating one is normal, not a workaround: it is
            // how the driver is told about an executable it has never seen.
            profile = session.CreateProfileForExecutable(ProfileNameFor(gameName, exeName), exeName, out string? createError);
            if (profile == null)
                return DlssOperationResult.Failure($"Could not create a driver profile for {exeName}: {createError}");
        }

        var details = new List<DlssSettingOutcomeDetail>();
        var records = new List<DlssSettingRecord>();
        DateTime now = DateTime.UtcNow;

        foreach (var def in DlssProbeService.Settings)
        {
            var before = session.GetSetting(profile, def.Id, out _);

            if (!session.SetSetting(profile, def.Id, def.RecommendedValue, out string? setError))
            {
                details.Add(new DlssSettingOutcomeDetail(def.FeatureCode, def.Id, DlssSettingOutcome.Failed, setError));
                continue;
            }

            records.Add(new DlssSettingRecord
            {
                Feature = def.FeatureCode,
                SettingId = def.Id,
                PreviousValue = before?.Value,
                PreviousOrigin = before?.Origin ?? DlssSettingOrigin.Absent,
                WrittenValue = def.RecommendedValue,
                ProfileName = profile.Name,
                ApplicationName = exeName,
                WrittenUtc = now
            });
            details.Add(new DlssSettingOutcomeDetail(def.FeatureCode, def.Id, DlssSettingOutcome.Applied, null));
        }

        if (!session.Save(out string? saveError))
        {
            // Nothing reached disk, so the records describe a write that did not happen. Returning
            // them would leave the game claiming an override it does not have.
            LoggingService.Warn("Dlss", $"Saving DLSS settings for {exeName} failed: {saveError}");
            return DlssOperationResult.Failure($"The driver refused to save the settings: {saveError}");
        }

        LoggingService.Info("Dlss", $"Applied DLSS override to {exeName} in profile '{profile.Name}' ({records.Count} settings).");
        return new DlssOperationResult(records.Count > 0, null, details, records);
    }

    /// <summary>
    /// Puts back what <see cref="Apply"/> captured, skipping any setting the driver no longer
    /// reports as TrayTrigger wrote it.
    /// </summary>
    public DlssOperationResult Undo(IReadOnlyList<DlssSettingRecord> records)
    {
        if (records.Count == 0) return new DlssOperationResult(true, null, Array.Empty<DlssSettingOutcomeDetail>(), Array.Empty<DlssSettingRecord>());

        using var session = _backend.OpenSession(out string? error);
        if (session == null) return DlssOperationResult.Failure(error ?? "The NVIDIA driver settings database is unavailable.");

        var details = new List<DlssSettingOutcomeDetail>();
        var remaining = new List<DlssSettingRecord>();
        bool anythingChanged = false;

        foreach (var group in records.GroupBy(r => r.ApplicationName, StringComparer.OrdinalIgnoreCase))
        {
            var profile = session.FindProfileForExecutable(group.Key, out string? findError);
            if (profile == null)
            {
                // The profile is gone - a driver reset, or someone deleted it. There is nothing to
                // put back, and nothing to warn about either.
                foreach (var record in group)
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Deleted, findError));
                continue;
            }

            foreach (var record in group)
            {
                var current = session.GetSetting(profile, record.SettingId, out _);

                if (!StillOursToUndo(current, record))
                {
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.SkippedForeignChange, null));
                    remaining.Add(record);
                    continue;
                }

                bool ok;
                string? undoError;
                DlssSettingOutcome outcome;

                if (record.PreviousOrigin == DlssSettingOrigin.UserSet && record.PreviousValue.HasValue)
                {
                    ok = session.SetSetting(profile, record.SettingId, record.PreviousValue.Value, out undoError);
                    outcome = DlssSettingOutcome.Restored;
                }
                else
                {
                    // Absent, inherited, or NVIDIA's own predefined value: in all three the setting
                    // did not exist on this profile as a user choice, so removing it is what puts
                    // the machine back. Writing the value instead would pin it - an inherited
                    // setting would stop following the Global profile, and a predefined one would
                    // become user-set. See the plan; an earlier draft said to delete only when the
                    // previous state was absent, which is too narrow.
                    ok = session.DeleteSetting(profile, record.SettingId, out undoError);
                    outcome = DlssSettingOutcome.Deleted;
                }

                if (ok)
                {
                    anythingChanged = true;
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, outcome, null));
                }
                else
                {
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Failed, undoError));
                    remaining.Add(record);
                }
            }
        }

        if (anythingChanged && !session.Save(out string? saveError))
        {
            LoggingService.Warn("Dlss", $"Saving the DLSS undo failed: {saveError}");
            return new DlssOperationResult(false, $"The driver refused to save the change: {saveError}", details, records);
        }

        // Records for settings that were skipped or failed are kept: they still describe values
        // TrayTrigger wrote, and dropping them would lose the only evidence of what it owns.
        return new DlssOperationResult(true, null, details, remaining);
    }

    /// <summary>
    /// Whether a setting is still the one TrayTrigger wrote. Requires both the value and that it
    /// sits on the game's own profile as a user-set value, which is what a TrayTrigger write looks
    /// like: a matching value arriving from the Global profile is somebody else's.
    /// </summary>
    internal static bool StillOursToUndo(DrsSettingReading? current, DlssSettingRecord record) =>
        current != null &&
        current.Value == record.WrittenValue &&
        current.Origin == DlssSettingOrigin.UserSet;

    /// <summary>
    /// The name given to a profile TrayTrigger has to create. The game's name is friendlier in
    /// Profile Inspector than the executable, but the executable disambiguates - two library
    /// entries can share a name.
    /// </summary>
    internal static string ProfileNameFor(string gameName, string exeName) =>
        string.IsNullOrWhiteSpace(gameName) ? exeName : $"{gameName.Trim()} ({exeName})";
}
