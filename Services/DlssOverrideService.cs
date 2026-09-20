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

    /// <summary>
    /// Writes the "use recommended" recipe for a game, capturing what each setting was first.
    ///
    /// <para>Existing records are replaced, and the capture describes the state immediately
    /// before <i>this</i> write - with one exception. Where a setting is still exactly as an
    /// <paramref name="existing"/> record left it, "the state before this write" is TrayTrigger's
    /// own value, and capturing that would make undo restore the override. There the original
    /// capture is carried over instead.</para>
    /// </summary>
    /// <param name="beforeSave">
    /// Handed the records about to be committed, before the driver is told to save. Persisting
    /// them here is what makes a crash between the two recoverable: a record with no write behind
    /// it undoes to nothing, a write with no record behind it cannot be undone at all.
    /// </param>
    public DlssOperationResult Apply(
        string executablePath, string gameName,
        IReadOnlyList<DlssSettingRecord>? existing = null,
        Action<IReadOnlyList<DlssSettingRecord>>? beforeSave = null)
    {
        // Not the launched executable: the one that renders. For a launcher-based game they differ,
        // and the driver keys its profiles on the renderer. See ResolveRenderingExecutable.
        string exePath = DlssProbeService.ResolveRenderingExecutable(executablePath);
        // A launch link that resolved to nothing has no file name worth writing a profile for.
        if (!DlssProbeService.IsFilePath(exePath)) return DlssOperationResult.Failure("No executable to target.");
        string exeName = Path.GetFileName(exePath);
        if (string.IsNullOrWhiteSpace(exeName)) return DlssOperationResult.Failure("No executable to target.");

        // The write happens in its own scope so the session is closed before the check below.
        // NVIDIA's own documentation warns that DRS sessions do not merge, and reading through the
        // session that just wrote would only show its own in-memory copy - confirming nothing.
        var written = WriteRecipe(exePath, exeName, gameName, existing, beforeSave);
        if (written.Error != null) return DlssOperationResult.Failure(written.Error);

        var verified = VerifyWriteBack(exePath, exeName, written.Records, written.Details);

        LoggingService.Info("Dlss", $"Applied DLSS override to {exeName} in profile '{written.ProfileName}' ({written.Records.Count} settings).");
        return new DlssOperationResult(written.Records.Count > 0, null, verified, written.Records);
    }

    private sealed record WriteOutcome(
        string ProfileName,
        List<DlssSettingOutcomeDetail> Details,
        List<DlssSettingRecord> Records,
        string? Error);

    /// <summary>
    /// By full path first, because that is what disambiguates NVIDIA's folder- and
    /// launcher-qualified entries; by name when the path finds nothing, for a game that has moved
    /// since. Null with <paramref name="error"/> set is a failed lookup, not a missing profile.
    /// </summary>
    private static DrsProfileHandle? FindProfile(IDrsSession session, string? exePath, string exeName, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(exePath) && !string.Equals(exePath, exeName, StringComparison.OrdinalIgnoreCase))
        {
            var byPath = session.FindProfileForExecutable(exePath, out error);
            if (byPath != null || error != null) return byPath;
        }
        return session.FindProfileForExecutable(exeName, out error);
    }

    private static DrsProfileHandle? FindProfile(IDrsSession session, DlssSettingRecord record, out string? error) =>
        FindProfile(session, record.ExecutablePath, record.ApplicationName, out error);

    private static bool SameApplication(DlssSettingRecord record, string exeName) =>
        string.Equals(record.ApplicationName, exeName, StringComparison.OrdinalIgnoreCase);

    private WriteOutcome WriteRecipe(
        string exePath, string exeName, string gameName,
        IReadOnlyList<DlssSettingRecord>? existing,
        Action<IReadOnlyList<DlssSettingRecord>>? beforeSave)
    {
        var noDetails = new List<DlssSettingOutcomeDetail>();
        var noRecords = new List<DlssSettingRecord>();

        using var session = _backend.OpenSession(out string? error);
        if (session == null)
            return new WriteOutcome("", noDetails, noRecords, error ?? "The NVIDIA driver settings database is unavailable.");

        var profile = FindProfile(session, exePath, exeName, out string? findError);
        if (profile == null && findError != null)
        {
            // Not "no profile": the lookup itself failed. Creating one now could shadow a profile
            // that is there, and everything captured against it would be wrong.
            return new WriteOutcome("", noDetails, noRecords, $"Could not look up the driver profile for {exeName}: {findError}");
        }

        bool created = false;
        if (profile == null)
        {
            // NVIDIA has no entry for this game. Creating one is normal, not a workaround: it is
            // how the driver is told about an executable it has never seen.
            profile = session.CreateProfileForExecutable(ProfileNameFor(gameName, exeName), exeName, out string? createError);
            if (profile == null)
                return new WriteOutcome("", noDetails, noRecords, $"Could not create a driver profile for {exeName}: {createError}");
            created = true;
        }

        // A profile TrayTrigger created stays TrayTrigger's across a re-apply.
        created |= existing?.Any(r => r.ProfileCreated && SameApplication(r, exeName)) == true;

        var details = new List<DlssSettingOutcomeDetail>();
        var records = new List<DlssSettingRecord>();

        foreach (var def in DlssProbeService.Settings)
        {
            // NVIDIA adds and retires setting ids between driver versions. An id this driver does
            // not have is stepped over, not reported as a failure - and never recorded, because
            // there is nothing to undo.
            if (_backend.GetSettingName(def.Id) == null)
            {
                details.Add(new DlssSettingOutcomeDetail(def.FeatureCode, def.Id, DlssSettingOutcome.NotSupportedByDriver, null));
                continue;
            }

            var before = session.GetSetting(profile, def.Id, out string? readError);
            if (before == null && readError != null)
            {
                // Unreadable is not absent. Writing over it would record "nothing was here", and
                // undo would then delete whatever actually was.
                details.Add(new DlssSettingOutcomeDetail(def.FeatureCode, def.Id, DlssSettingOutcome.Failed, readError));
                continue;
            }

            if (!session.SetSetting(profile, def.Id, def.RecommendedValue, out string? setError))
            {
                details.Add(new DlssSettingOutcomeDetail(def.FeatureCode, def.Id, DlssSettingOutcome.Failed, setError));
                continue;
            }

            // Still exactly as an earlier apply left it: what is there now is TrayTrigger's own
            // value, so the state to go back to is the one that apply captured, not this one.
            var carried = existing?.FirstOrDefault(r => r.SettingId == def.Id && SameApplication(r, exeName));
            bool stillOurs = carried != null && StillOursToUndo(before, carried);

            records.Add(new DlssSettingRecord
            {
                Feature = def.FeatureCode,
                SettingId = def.Id,
                PreviousValue = stillOurs ? carried!.PreviousValue : before?.Value,
                PreviousOrigin = stillOurs ? carried!.PreviousOrigin : before?.Origin ?? DlssSettingOrigin.Absent,
                WrittenValue = def.RecommendedValue,
                ProfileName = profile.Name,
                ApplicationName = exeName,
                ExecutablePath = exePath,
                ProfileCreated = created
            });
            details.Add(new DlssSettingOutcomeDetail(def.FeatureCode, def.Id, DlssSettingOutcome.Applied, null));
        }

        if (records.Count > 0) beforeSave?.Invoke(records);

        if (!session.Save(out string? saveError))
        {
            // Nothing reached disk, so the records describe a write that did not happen. Returning
            // them would leave the game claiming an override it does not have. A caller that
            // persisted them in beforeSave puts back what it had.
            LoggingService.Warn("Dlss", $"Saving DLSS settings for {exeName} failed: {saveError}");
            return new WriteOutcome("", noDetails, noRecords, $"The driver refused to save the settings: {saveError}");
        }

        return new WriteOutcome(profile.Name, details, records, null);
    }

    /// <summary>
    /// Reopens the database and confirms each value is there and user-set.
    /// This proves the write landed in the profile database. It proves nothing about whether a
    /// game will honour it - that needs the game to run.
    /// </summary>
    private List<DlssSettingOutcomeDetail> VerifyWriteBack(
        string exePath, string exeName, List<DlssSettingRecord> records, List<DlssSettingOutcomeDetail> details)
    {
        using var verify = _backend.OpenSession(out string? verifyError);
        if (verify == null)
        {
            LoggingService.Warn("Dlss", $"Could not reopen DRS to verify the write for {exeName}: {verifyError}");
            return details;
        }

        var profile = FindProfile(verify, exePath, exeName, out _);
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
    ///
    /// <para>"Put back" is three different calls, because a setting can have been three different
    /// things. A user's own value is written back. NVIDIA's predefined value is <i>restored</i> -
    /// deleting is not the same thing, and is not what the driver offers for it. Anything that was
    /// not on this profile at all - absent, or inherited from a lower layer - is deleted, so it
    /// goes back to following that layer. And a profile TrayTrigger had to create is removed once
    /// it is empty, or every game ever overridden would leave one behind.</para>
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
            var profile = FindProfile(session, group.First(), out string? findError);
            if (profile == null)
            {
                foreach (var record in group)
                {
                    if (findError != null)
                    {
                        // The lookup failed; the profile may well be there with our values in it.
                        // Dropping the records now would lose the only means of undoing them.
                        details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Failed, findError));
                        remaining.Add(record);
                    }
                    else
                    {
                        // The profile is gone - a driver reset, or someone deleted it. There is
                        // nothing to put back, and nothing to warn about either.
                        details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Deleted, null));
                    }
                }
                continue;
            }

            int stillHeld = 0;

            foreach (var record in group)
            {
                var current = session.GetSetting(profile, record.SettingId, out string? readError);
                if (current == null && readError != null)
                {
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Failed, readError));
                    remaining.Add(record);
                    stillHeld++;
                    continue;
                }

                if (!StillOursToUndo(current, record))
                {
                    if (LooksLikeWhatWeCaptured(current, record))
                    {
                        // Already back as it was - something reverted the write, or it never
                        // landed. Nothing to undo and nothing left to own.
                        details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.AlreadyCorrect, null));
                        continue;
                    }

                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.SkippedForeignChange, null));
                    remaining.Add(record);
                    stillHeld++;
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
                else if (record.PreviousOrigin == DlssSettingOrigin.Predefined)
                {
                    // NVIDIA's own value was here. Writing the number back would turn it into a
                    // user-set one, and deleting is for a value that was never there; the driver
                    // has a call for exactly this.
                    ok = session.RestoreSettingDefault(profile, record.SettingId, out undoError);
                    outcome = DlssSettingOutcome.Restored;
                }
                else
                {
                    // Absent or inherited: the setting did not exist on this profile, so removing
                    // it is what puts the machine back. Writing the value instead would pin it, and
                    // an inherited setting would stop following the Global profile.
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
                    stillHeld++;
                }
            }

            if (stillHeld == 0 && group.Any(r => r.ProfileCreated) && RemoveCreatedProfile(session, profile))
                anythingChanged = true;
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

    /// <summary>How a <see cref="RestoreAll"/> went: games that had an override, and games where some of it is still there.</summary>
    public sealed record RestoreAllResult(int Games, int Failed);

    /// <summary>
    /// Puts back every game's override - the way out before an uninstall, and the one button for
    /// "undo all of this". Each game's records are updated in place; the caller saves the library.
    ///
    /// <para>A setting something else has since changed is left alone and its record dropped, as
    /// Restore on the card does: it is no longer TrayTrigger's. Only a failure keeps a record.</para>
    /// </summary>
    public RestoreAllResult RestoreAll(IEnumerable<GameEntry> games)
    {
        int count = 0, failed = 0;
        foreach (var game in games.Where(g => g.DlssSettings.Count > 0).ToList())
        {
            count++;
            var undone = Undo(game.DlssSettings.ToList());

            var handedBack = undone.Details
                .Where(d => d.Outcome == DlssSettingOutcome.SkippedForeignChange)
                .Select(d => d.SettingId)
                .ToHashSet();
            var kept = undone.Succeeded
                ? undone.Records.Where(r => !handedBack.Contains(r.SettingId)).ToList()
                : undone.Records.ToList();

            game.DlssSettings.Clear();
            game.DlssSettings.AddRange(kept);
            game.DlssConflicted = false;
            game.DlssLastRun = null;

            if (kept.Count > 0)
            {
                failed++;
                LoggingService.Warn("Dlss", $"Could not put back the DLSS override for '{game.Name}': {undone.Error ?? undone.Details.FirstOrDefault(d => d.Error != null)?.Error ?? "the driver refused"}.");
            }
        }
        return new RestoreAllResult(count, failed);
    }

    /// <summary>
    /// Removes a profile TrayTrigger created, but only while it is still only TrayTrigger's: not
    /// one of NVIDIA's, holding no settings, and no application beyond the one it was made for.
    /// Anything a user has since put in it is theirs, and it stays.
    /// </summary>
    private static bool RemoveCreatedProfile(IDrsSession session, DrsProfileHandle profile)
    {
        if (profile.IsPredefined) return false;
        if (session.GetProfileCounts(profile) is not { Settings: 0, Applications: <= 1 }) return false;

        if (session.DeleteProfile(profile, out string? error)) return true;
        LoggingService.Warn("Dlss", $"Could not remove the driver profile '{profile.Name}' TrayTrigger created: {error}");
        return false;
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
    /// this rule exists to prevent. One session does both, so what is written is decided on
    /// exactly what was read - a second session would load the database afresh, and whatever had
    /// changed in between would be overwritten unseen.</para>
    ///
    /// <para>A read that <i>fails</i> stops the whole step. It is neither "reverted" nor "absent",
    /// and guessing either way writes to a profile on the strength of nothing.</para>
    ///
    /// <para>Best-effort, not a guarantee. No published contract says when NVIDIA App reconciles
    /// DRS; reapplying here narrows the window, it does not close it.</para>
    /// </summary>
    /// <remarks>
    /// When the profile had to be recreated, the records passed in are marked
    /// <see cref="DlssSettingRecord.ProfileCreated"/> in place, so a later undo removes it.
    /// </remarks>
    public DlssOperationResult Reapply(IReadOnlyList<DlssSettingRecord> records)
    {
        if (records.Count == 0) return new DlssOperationResult(true, null, Array.Empty<DlssSettingOutcomeDetail>(), records);

        using var session = _backend.OpenSession(out string? openError);
        if (session == null) return DlssOperationResult.Failure(openError ?? "The NVIDIA driver settings database is unavailable.");

        var planned = new List<(DlssSettingRecord Record, DlssSettingOutcome Outcome)>();
        var profiles = new Dictionary<string, DrsProfileHandle?>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in records.GroupBy(r => r.ApplicationName, StringComparer.OrdinalIgnoreCase))
        {
            var profile = FindProfile(session, group.First(), out string? findError);
            if (profile == null && findError != null)
                return DlssOperationResult.Failure($"Could not look up the driver profile for {group.Key}: {findError}");
            profiles[group.Key] = profile;

            foreach (var record in group)
            {
                if (profile == null)
                {
                    // The whole profile is gone - a driver reset, or someone deleted it. That is
                    // not another tool disagreeing with us, so it is not a conflict; recreating
                    // it is what a normal apply would do, and the write below does.
                    planned.Add((record, DlssSettingOutcome.Applied));
                    continue;
                }

                var current = session.GetSetting(profile, record.SettingId, out string? readError);
                if (current == null && readError != null)
                    return DlssOperationResult.Failure($"Could not read setting 0x{record.SettingId:X8} for {group.Key}: {readError}");

                planned.Add((record,
                    StillOursToUndo(current, record) ? DlssSettingOutcome.AlreadyCorrect
                    : LooksLikeWhatWeCaptured(current, record) ? DlssSettingOutcome.Applied
                    : DlssSettingOutcome.SkippedForeignChange));
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
        foreach (var (record, _) in toWrite)
        {
            var profile = profiles[record.ApplicationName];
            if (profile == null)
            {
                profile = session.CreateProfileForExecutable(
                    string.IsNullOrWhiteSpace(record.ProfileName) ? record.ApplicationName : record.ProfileName,
                    record.ApplicationName, out string? createError);
                if (profile == null)
                {
                    details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Failed, createError));
                    continue;
                }

                // Created once, and the rest of this game's records land in the same one.
                profiles[record.ApplicationName] = profile;
                foreach (var sibling in records.Where(r => SameApplication(r, record.ApplicationName)))
                    sibling.ProfileCreated = true;
            }

            if (!session.SetSetting(profile, record.SettingId, record.WrittenValue, out string? setError))
            {
                details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Failed, setError));
                continue;
            }
            details.Add(new DlssSettingOutcomeDetail(record.Feature, record.SettingId, DlssSettingOutcome.Applied, null));
        }

        if (!session.Save(out string? saveError))
        {
            LoggingService.Warn("Dlss", $"Re-applying DLSS settings before launch failed: {saveError}");
            return new DlssOperationResult(false, saveError, details, records);
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
