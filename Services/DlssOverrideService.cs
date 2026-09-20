using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Applies and undoes the DLSS driver override, keeping the ownership record that makes undo safe.
/// See docs/dlss-plan.md, The ownership record.
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
        // Not the launched executable: the one that renders. For a launcher-based game they differ,
        // and the driver keys its profiles on the renderer. See ResolveRenderingExecutable.
        string exeName = Path.GetFileName(DlssProbeService.ResolveRenderingExecutable(executablePath));
        if (string.IsNullOrWhiteSpace(exeName)) return DlssOperationResult.Failure("No executable to target.");

        // The write happens in its own scope so the session is closed before the check below.
        // NVIDIA's own documentation warns that DRS sessions do not merge, and reading through the
        // session that just wrote would only show its own in-memory copy - confirming nothing.
        var written = WriteRecipe(exeName, gameName);
        if (written.Error != null) return DlssOperationResult.Failure(written.Error);

        var verified = VerifyWriteBack(exeName, written.Records, written.Details);

        LoggingService.Info("Dlss", $"Applied DLSS override to {exeName} in profile '{written.ProfileName}' ({written.Records.Count} settings).");
        return new DlssOperationResult(written.Records.Count > 0, null, verified, written.Records);
    }

    private sealed record WriteOutcome(
        string ProfileName,
        List<DlssSettingOutcomeDetail> Details,
        List<DlssSettingRecord> Records,
        string? Error);

    private WriteOutcome WriteRecipe(string exeName, string gameName)
    {
        var noDetails = new List<DlssSettingOutcomeDetail>();
        var noRecords = new List<DlssSettingRecord>();

        using var session = _backend.OpenSession(out string? error);
        if (session == null)
            return new WriteOutcome("", noDetails, noRecords, error ?? "The NVIDIA driver settings database is unavailable.");

        var profile = session.FindProfileForExecutable(exeName, out _);
        if (profile == null)
        {
            // NVIDIA has no entry for this game. Creating one is normal, not a workaround: it is
            // how the driver is told about an executable it has never seen.
            profile = session.CreateProfileForExecutable(ProfileNameFor(gameName, exeName), exeName, out string? createError);
            if (profile == null)
                return new WriteOutcome("", noDetails, noRecords, $"Could not create a driver profile for {exeName}: {createError}");
        }

        var details = new List<DlssSettingOutcomeDetail>();
        var records = new List<DlssSettingRecord>();
        DateTime now = DateTime.UtcNow;

        foreach (var def in DlssProbeService.Settings)
        {
            // NVIDIA adds and retires setting ids between driver versions. An id this driver does
            // not have is stepped over, not reported as a failure - and never recorded, because
            // there is nothing to undo. This check exists because the recipe carried an id for
            // months that no driver has ever had; see DlssProbeService.Settings.
            if (_backend.GetSettingName(def.Id) == null)
            {
                details.Add(new DlssSettingOutcomeDetail(def.FeatureCode, def.Id, DlssSettingOutcome.NotSupportedByDriver, null));
                continue;
            }

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
            return new WriteOutcome("", noDetails, noRecords, $"The driver refused to save the settings: {saveError}");
        }

        return new WriteOutcome(profile.Name, details, records, null);
    }

    /// <summary>
    /// Layer 1 of verification: reopen the database and confirm each value is there and user-set.
    /// This proves the write landed in the profile database. It proves nothing about whether a
    /// game will honour it - that needs the game to run.
    /// </summary>
    private List<DlssSettingOutcomeDetail> VerifyWriteBack(
        string exeName, List<DlssSettingRecord> records, List<DlssSettingOutcomeDetail> details)
    {
        using var verify = _backend.OpenSession(out string? verifyError);
        if (verify == null)
        {
            LoggingService.Warn("Dlss", $"Could not reopen DRS to verify the write for {exeName}: {verifyError}");
            return details;
        }

        var profile = verify.FindProfileForExecutable(exeName, out _);
        if (profile == null)
        {
            return records
                .Select(r => new DlssSettingOutcomeDetail(r.Feature, r.SettingId, DlssSettingOutcome.WriteBackFailed,
                    "the profile is not there after saving"))
                .ToList();
        }

        var checkedDetails = new List<DlssSettingOutcomeDetail>(details.Count);
        var byId = records.ToDictionary(r => r.SettingId);

        foreach (var detail in details)
        {
            if (detail.Outcome != DlssSettingOutcome.Applied || !byId.TryGetValue(detail.SettingId, out var record))
            {
                checkedDetails.Add(detail);
                continue;
            }

            var actual = verify.GetSetting(profile, record.SettingId, out _);
            // User-set as well as equal: a matching value that reads as predefined or inherited is
            // not the one that was just written.
            bool landed = actual != null && actual.Value == record.WrittenValue && actual.Origin == DlssSettingOrigin.UserSet;

            checkedDetails.Add(landed
                ? detail
                : new DlssSettingOutcomeDetail(detail.Feature, detail.SettingId, DlssSettingOutcome.WriteBackFailed,
                    actual == null ? "absent after saving" : $"reads 0x{actual.Value:X8} [{actual.Origin}] after saving"));
        }

        return checkedDetails;
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
    /// The pre-launch step: put the settings back if something reverted them, moments before the
    /// game reads them. NVIDIA App reverts overrides on games not on its own allowlist when it
    /// starts, and TrayTrigger's advantage is that it launches the game.
    ///
    /// <para>The three-way rule from docs/dlss-plan.md, which resolves "always reapply" against
    /// "never overwrite an external change":</para>
    /// <list type="bullet">
    /// <item>the value TrayTrigger wrote - nothing to do;</item>
    /// <item>the value TrayTrigger captured, i.e. reverted - reapply silently;</item>
    /// <item>anything else - do not overwrite, and report a conflict.</item>
    /// </list>
    ///
    /// <para>Telling the second case from the third is exactly what the ownership record makes
    /// possible. <b>Everything is read before anything is written</b>: one conflicting setting
    /// means something else is managing this game, and writing the rest would be the overwrite
    /// this rule exists to prevent.</para>
    ///
    /// <para>Best-effort, not a guarantee. No published contract says when NVIDIA App reconciles
    /// DRS; reapplying here narrows the window, it does not close it.</para>
    /// </summary>
    public DlssOperationResult Reapply(IReadOnlyList<DlssSettingRecord> records)
    {
        if (records.Count == 0) return new DlssOperationResult(true, null, Array.Empty<DlssSettingOutcomeDetail>(), records);

        var planned = new List<(DlssSettingRecord Record, DlssSettingOutcome Outcome)>();

        using (var read = _backend.OpenSession(out string? readError))
        {
            if (read == null) return DlssOperationResult.Failure(readError ?? "The NVIDIA driver settings database is unavailable.");

            foreach (var group in records.GroupBy(r => r.ApplicationName, StringComparer.OrdinalIgnoreCase))
            {
                var profile = read.FindProfileForExecutable(group.Key, out _);
                foreach (var record in group)
                {
                    if (profile == null)
                    {
                        // The whole profile is gone - a driver reset, or someone deleted it. That is
                        // not another tool disagreeing with us, so it is not a conflict; recreating
                        // it is what a normal apply would do.
                        planned.Add((record, DlssSettingOutcome.Failed));
                        continue;
                    }

                    var current = read.GetSetting(profile, record.SettingId, out _);
                    planned.Add((record,
                        StillOursToUndo(current, record) ? DlssSettingOutcome.AlreadyCorrect
                        : LooksLikeWhatWeCaptured(current, record) ? DlssSettingOutcome.Applied
                        : DlssSettingOutcome.SkippedForeignChange));
                }
            }
        }

        if (planned.Any(p => p.Outcome == DlssSettingOutcome.SkippedForeignChange))
        {
            // Stop managing this game rather than fight over it. Nothing is written.
            var conflicts = planned
                .Select(p => new DlssSettingOutcomeDetail(p.Record.Feature, p.Record.SettingId,
                    p.Outcome == DlssSettingOutcome.Applied ? DlssSettingOutcome.SkippedForeignChange : p.Outcome, null))
                .ToList();
            LoggingService.Warn("Dlss", $"Something else has changed the DLSS settings for {records[0].ApplicationName}; TrayTrigger is leaving them alone.");
            return new DlssOperationResult(true, null, conflicts, records);
        }

        var toWrite = planned.Where(p => p.Outcome == DlssSettingOutcome.Applied).ToList();
        if (toWrite.Count == 0)
        {
            return new DlssOperationResult(true, null,
                planned.Select(p => new DlssSettingOutcomeDetail(p.Record.Feature, p.Record.SettingId, p.Outcome, null)).ToList(),
                records);
        }

        var details = new List<DlssSettingOutcomeDetail>();
        using (var write = _backend.OpenSession(out string? writeError))
        {
            if (write == null) return DlssOperationResult.Failure(writeError ?? "The NVIDIA driver settings database is unavailable.");

            foreach (var (record, _) in toWrite)
            {
                var profile = write.FindProfileForExecutable(record.ApplicationName, out string? findError);
                if (profile == null)
                {
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Failed, findError));
                    continue;
                }
                if (!write.SetSetting(profile, record.SettingId, record.WrittenValue, out string? setError))
                {
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Failed, setError));
                    continue;
                }
                details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Applied, null));
            }

            if (!write.Save(out string? saveError))
            {
                LoggingService.Warn("Dlss", $"Re-applying DLSS settings before launch failed: {saveError}");
                return new DlssOperationResult(false, saveError, details, records);
            }
        }

        foreach (var (record, outcome) in planned.Where(p => p.Outcome != DlssSettingOutcome.Applied))
            details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, outcome, null));

        LoggingService.Info("Dlss", $"Re-applied {toWrite.Count} DLSS setting(s) before launch - something had reverted them.");
        return new DlssOperationResult(true, null, details, records);
    }

    /// <summary>
    /// Whether the driver is reporting exactly the state the record captured - i.e. TrayTrigger's
    /// write has been reverted, rather than replaced with something new. Absent has to match
    /// absent: a setting that is now inherited was not what was captured if the capture said the
    /// setting did not exist.
    /// </summary>
    internal static bool LooksLikeWhatWeCaptured(DrsSettingReading? current, DlssSettingRecord record) =>
        record.PreviousOrigin == DlssSettingOrigin.Absent
            ? current == null
            : current != null && current.Value == record.PreviousValue && current.Origin == record.PreviousOrigin;

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
