using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>The machine-wide tweaks of a profile session: power plan, Windows scheduler values, Do Not Disturb, audio, timer resolution and HDR - captured on the first session, put back with the last.</summary>
public partial class PerformanceProfileService
{
    /// <summary>True when the previous scheme was captured, and so has to be restored later.</summary>
    private bool ApplyPowerPlan(PerformanceProfileSessionSnapshot snapshot)
    {
        // Left on whatever plan is active: nothing is captured, so there is nothing to put back.
        if (_backend.IsOnBattery)
        {
            LoggingService.Verbose("PerformanceProfile", "Power Plan: on battery, so the active scheme was left unchanged.");
            Skipped("Ultimate Performance power plan, on battery to save power");
            return false;
        }
        string? previous = _backend.GetActivePowerSchemeGuid();
        if (previous == null)
        {
            // Nothing to put back afterwards, so switching now would leave the Ultimate plan on for good.
            LoggingService.Warn("PerformanceProfile", "Power Plan: could not read the active scheme, so it was left unchanged.");
            Skipped("power plan, the active plan couldn't be read");
            return false;
        }
        snapshot.PreviousPowerSchemeGuid = previous;
        snapshot.PowerPlanCaptured = true;

        if (!_backend.ActivateUltimatePowerPlan())
        {
            LoggingService.Warn("PerformanceProfile", "Could not create or locate the 'Ultimate Plan - TrayTrigger' power scheme.");
            Skipped("Ultimate Performance power plan couldn't be set");
            return true;
        }
        LoggingService.Verbose("PerformanceProfile", $"Power Plan: switched active scheme to 'Ultimate Plan - TrayTrigger' (was {snapshot.PreviousPowerSchemeGuid ?? "unknown"}).");
        Note("Ultimate Performance power plan");
        return true;
    }

    private void RestorePowerPlan(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.PowerPlanCaptured || string.IsNullOrWhiteSpace(snapshot.PreviousPowerSchemeGuid)) return;
        // Defensive even for in-memory snapshots: the GUID is interpolated into a powercfg command line.
        if (!ProfileSnapshotValidator.IsValidSchemeGuid(snapshot.PreviousPowerSchemeGuid))
        {
            LoggingService.Warn("PerformanceProfile", $"Power Plan: not restoring - captured scheme id '{snapshot.PreviousPowerSchemeGuid}' is not a GUID.");
            return;
        }
        string previous = snapshot.PreviousPowerSchemeGuid;
        if (SetPowerSchemeAndCheck(previous))
        {
            LoggingService.Verbose("PerformanceProfile", $"Power Plan: restored active scheme to {previous}.");
        }
        else
        {
            NoteUnrestored("the power plan", () => SetPowerSchemeAndCheck(previous));
        }
    }

    /// <summary>Sets the scheme and reads it back: powercfg reports nothing useful on failure, and a
    /// deleted scheme or a policy can leave the old one active.</summary>
    private bool SetPowerSchemeAndCheck(string schemeGuid)
    {
        try
        {
            _backend.SetActivePowerScheme(schemeGuid);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Power Plan: setting {schemeGuid} failed: {ex.Message}");
            return false;
        }
        string? now = _backend.GetActivePowerSchemeGuid();
        // An unreadable scheme isn't evidence of a failure; only a different one is.
        return now == null || string.Equals(now, schemeGuid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records what was there and returns the change to make, rather than making it. The two
    /// machine-wide HKLM tweaks are written together in one elevated step - see
    /// <see cref="ISystemTweakBackend.ApplyHklmChanges"/> - so the user answers one administrator
    /// prompt for the pair instead of one each. Capturing before the write also means the snapshot
    /// is on disk before anything changes, so a crash between the two can still be undone.
    /// </summary>
    private SystemTweaksService.RegFileEntry CaptureSystemResponsiveness(PerformanceProfileSessionSnapshot snapshot)
    {
        var change = new SystemTweaksService.RegFileEntry(
            SystemResponsivenessPath, "SystemResponsiveness", AggressiveSystemResponsiveness, RegistryValueKind.DWord, Delete: false);
        if (snapshot.SystemResponsivenessCaptured)
        {
            // An earlier put-back is still waiting: its record of what was there before the first game is the one to keep.
            LoggingService.Verbose("PerformanceProfile", "System Responsiveness: setting to " + AggressiveSystemResponsiveness + " (an earlier put-back is still waiting, so what it recorded stays).");
            return change;
        }
        int? current = _backend.ReadHklmDword(SystemResponsivenessPath, "SystemResponsiveness");
        // Already ours before the game - only the first session captures, so no other is running: most
        // likely left by an earlier session that never got to restore. Putting that back would keep
        // the tweak on for good, so it counts as unknown and Windows' default goes back instead.
        bool leftOver = current == AggressiveSystemResponsiveness;
        snapshot.PreviousSystemResponsiveness = leftOver ? null : current;
        snapshot.SystemResponsivenessCaptured = true;

        LoggingService.Verbose("PerformanceProfile", leftOver
            ? $"System Responsiveness: setting to {AggressiveSystemResponsiveness} (already {current}, TrayTrigger's own value left from an earlier game; Windows' default {WindowsDefaultSystemResponsiveness} goes back afterwards)."
            : $"System Responsiveness: setting to {AggressiveSystemResponsiveness} (was {current?.ToString() ?? "unset"}).");

        // Microsoft's MMCSS docs: values below 10 are clamped back up to 20, so 10 is the
        // lowest reserve Windows actually honors.
        return change;
    }

    /// <summary>
    /// The change that puts it back, or false when there is nothing captured to undo: what was there
    /// before, or Windows' default when that isn't known (absent, unreadable, not a number). Never a
    /// delete - see <see cref="WindowsDefaultSystemResponsiveness"/>.
    /// </summary>
    private static bool TryRestoreSystemResponsiveness(
        PerformanceProfileSessionSnapshot snapshot, out SystemTweaksService.RegFileEntry entry)
    {
        entry = default;
        if (!snapshot.SystemResponsivenessCaptured) return false;

        int value = snapshot.PreviousSystemResponsiveness ?? WindowsDefaultSystemResponsiveness;
        entry = new SystemTweaksService.RegFileEntry(SystemResponsivenessPath, "SystemResponsiveness",
            value, RegistryValueKind.DWord, Delete: false);

        LoggingService.Verbose("PerformanceProfile", snapshot.PreviousSystemResponsiveness.HasValue
            ? $"System Responsiveness: restoring to {value}."
            : $"System Responsiveness: what it was before isn't known; restoring Windows' default, {value}.");
        return true;
    }

    /// <summary>
    /// The write of the two scheduler values was refused (the administrator prompt declined). A capture
    /// made for this launch whose value is still not TrayTrigger's describes a change that never
    /// happened, so it goes; one whose value already is ours (left from an earlier game) stays, as
    /// does one an earlier put-back was already holding. The snapshot is saved again when anything went.
    /// </summary>
    private void ForgetUnwrittenSchedulerCaptures(PerformanceProfileSessionSnapshot snapshot, bool keepResponsiveness, bool keepScheduling)
    {
        bool forgot = false;
        if (!keepResponsiveness && snapshot.SystemResponsivenessCaptured
            && _backend.ReadHklmDword(SystemResponsivenessPath, "SystemResponsiveness") != AggressiveSystemResponsiveness)
        {
            snapshot.SystemResponsivenessCaptured = false;
            snapshot.PreviousSystemResponsiveness = null;
            forgot = true;
        }
        if (!keepScheduling && snapshot.SchedulingCategoryCaptured
            && !string.Equals(_backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"), AggressiveSchedulingCategory, StringComparison.OrdinalIgnoreCase))
        {
            snapshot.SchedulingCategoryCaptured = false;
            snapshot.PreviousSchedulingCategory = null;
            forgot = true;
        }
        if (forgot) _store.SaveProfileSessionSnapshot(snapshot);
    }

    /// <summary>Captures and returns the change; see <see cref="CaptureSystemResponsiveness"/>.</summary>
    private SystemTweaksService.RegFileEntry CaptureSchedulingCategory(PerformanceProfileSessionSnapshot snapshot)
    {
        var change = new SystemTweaksService.RegFileEntry(
            SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", AggressiveSchedulingCategory, RegistryValueKind.String, Delete: false);
        if (snapshot.SchedulingCategoryCaptured)
        {
            // An earlier put-back is still waiting: what it recorded stays, as for System Responsiveness above.
            LoggingService.Verbose("PerformanceProfile", $"MMCSS Scheduling Category: setting to '{AggressiveSchedulingCategory}' (an earlier put-back is still waiting, so what it recorded stays).");
            return change;
        }
        string? current = _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category");
        // Already ours before the game: left over, as for System Responsiveness above.
        bool leftOver = string.Equals(current, AggressiveSchedulingCategory, StringComparison.OrdinalIgnoreCase);
        snapshot.PreviousSchedulingCategory = leftOver ? null : current;
        snapshot.SchedulingCategoryCaptured = true;

        LoggingService.Verbose("PerformanceProfile", leftOver
            ? $"MMCSS Scheduling Category: setting to '{AggressiveSchedulingCategory}' (already '{current}', TrayTrigger's own value left from an earlier game; Windows' default '{WindowsDefaultSchedulingCategory}' goes back afterwards)."
            : $"MMCSS Scheduling Category: setting to '{AggressiveSchedulingCategory}' (was '{current ?? "unset"}').");

        // SFIO Priority is intentionally not written - Microsoft's MMCSS docs state it "is not used".
        return change;
    }

    /// <summary>
    /// The change that puts it back, or false when there is nothing captured to undo: what was there
    /// before when it's one of the categories Windows defines, otherwise Windows' default - for a
    /// value that isn't known, and for one no Windows version writes, which it would be wrong to put
    /// back. Never a delete, which would leave the Games task incomplete.
    /// </summary>
    private static bool TryRestoreSchedulingCategory(
        PerformanceProfileSessionSnapshot snapshot, out SystemTweaksService.RegFileEntry entry)
    {
        entry = default;
        if (!snapshot.SchedulingCategoryCaptured) return false;

        string? previous = snapshot.PreviousSchedulingCategory;
        string value;
        if (previous == null)
        {
            value = WindowsDefaultSchedulingCategory;
            LoggingService.Verbose("PerformanceProfile", $"MMCSS Scheduling Category: what it was before isn't known; restoring Windows' default, '{value}'.");
        }
        else if (!ProfileSnapshotValidator.IsValidSchedulingCategory(previous))
        {
            value = WindowsDefaultSchedulingCategory;
            LoggingService.Warn("PerformanceProfile", $"MMCSS Scheduling Category: captured value '{previous}' is not one Windows defines; restoring Windows' default, '{value}'.");
        }
        else
        {
            value = previous;
            LoggingService.Verbose("PerformanceProfile", $"MMCSS Scheduling Category: restoring to '{value}'.");
        }

        entry = new SystemTweaksService.RegFileEntry(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category",
            value, RegistryValueKind.String, Delete: false);
        return true;
    }

    /// <summary>
    /// Restores the machine-wide tweaks. With <paramref name="skipElevated"/> the HKLM values are
    /// left captured in the snapshot (for crash recovery to finish later) when a UAC prompt would
    /// be needed to write them; the power plan and HDR never need one and are always restored.
    /// </summary>
    /// <summary>
    /// Windows' global toast switch, the value the notification centre's "Do not disturb" toggle
    /// writes. Captured so a user who already had notifications off keeps them off after the game.
    /// </summary>
    private void ApplyDoNotDisturb(PerformanceProfileSessionSnapshot snapshot)
    {
        try
        {
            snapshot.PreviousToastsEnabled = _backend.GetToastsEnabled();
            snapshot.ToastsCaptured = true;
            _backend.SetToastsEnabled(0);
            LoggingService.Verbose("PerformanceProfile", $"Do Not Disturb: toasts off (was {snapshot.PreviousToastsEnabled?.ToString() ?? "unset/on"}).");
            Note("notifications off");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyDoNotDisturb failed: {ex.Message}");
            Skipped("notifications couldn't be turned off");
        }
    }

    private void RestoreDoNotDisturb(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.ToastsCaptured) return;
        try
        {
            _backend.SetToastsEnabled(snapshot.PreviousToastsEnabled);
            LoggingService.Verbose("PerformanceProfile", $"Do Not Disturb: toasts restored to {snapshot.PreviousToastsEnabled?.ToString() ?? "unset/on"}.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreDoNotDisturb failed: {ex.Message}");
            int? previous = snapshot.PreviousToastsEnabled;
            NoteUnrestored("Windows notifications", () => { _backend.SetToastsEnabled(previous); return true; });
        }
    }

    /// <summary>
    /// Unmutes the default playback device so a muted machine does not start a game in silence.
    /// Nothing is captured when it was not muted to begin with, so the common case leaves no state
    /// behind and cannot be put back wrongly.
    /// </summary>
    private void ApplyUnmuteAudio(PerformanceProfileSessionSnapshot snapshot)
    {
        try
        {
            bool? muted = _backend.GetDefaultPlaybackMuted();
            if (muted == null)
            {
                LoggingService.Verbose("PerformanceProfile", "Unmute audio: no default playback device; nothing to do.");
                Skipped("unmute, no playback device");
                return;
            }
            if (muted == false)
            {
                LoggingService.Verbose("PerformanceProfile", "Unmute audio: playback device is already unmuted.");
                Skipped("speakers already unmuted");
                return;
            }

            if (_backend.SetDefaultPlaybackMuted(false))
            {
                snapshot.PlaybackMuteCaptured = true;
                snapshot.PreviousPlaybackMuted = true;
                LoggingService.Info("PerformanceProfile", "Unmute audio: default playback device unmuted for this session.");
                Note("speakers unmuted");
            }
            else
            {
                LoggingService.Warn("PerformanceProfile", "Unmute audio: the default playback device refused the change.");
                Skipped("speakers couldn't be unmuted");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyUnmuteAudio failed: {ex.Message}");
        }
    }

    private void RestoreUnmuteAudio(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.PlaybackMuteCaptured) return;
        try
        {
            // Restores to whichever device is default now, not the one the session started on.
            // Following the user's current device is the lesser surprise: they moved the sound
            // somewhere on purpose, and re-muting a device they have since walked away from would
            // leave the one they are listening to in a state TrayTrigger never saw.
            bool previous = snapshot.PreviousPlaybackMuted;
            if (_backend.SetDefaultPlaybackMuted(previous))
            {
                LoggingService.Info("PerformanceProfile", $"Unmute audio: playback device restored to {(previous ? "muted" : "unmuted")}.");
            }
            else
            {
                NoteUnrestored("the speakers' mute", () => _backend.SetDefaultPlaybackMuted(previous));
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreUnmuteAudio failed: {ex.Message}");
        }
    }

    private void ApplyTimerResolution(PerformanceProfileSessionSnapshot snapshot)
    {
        uint granted = _backend.RequestHighTimerResolution();
        snapshot.TimerResolutionRequested = granted != 0;
        LoggingService.Verbose("PerformanceProfile", granted != 0
            ? $"Timer resolution: requested 0.5 ms, system now at {granted / 10000.0:0.###} ms."
            : "Timer resolution: request failed.");
        if (granted != 0) Note("0.5 ms timer resolution");
        else Skipped("timer resolution couldn't be requested");
    }

    private void RestoreTimerResolution(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.TimerResolutionRequested) return;
        _backend.ReleaseHighTimerResolution();
        snapshot.TimerResolutionRequested = false;
        LoggingService.Verbose("PerformanceProfile", "Timer resolution: request released.");
    }

    private void RestoreGlobalTweaks(PerformanceProfileSessionSnapshot snapshot, bool skipElevated)
    {
        RestorePowerPlan(snapshot);
        snapshot.PowerPlanCaptured = false;
        RestoreHdr(snapshot);
        snapshot.HdrCaptured = false;
        RestoreDoNotDisturb(snapshot);
        snapshot.ToastsCaptured = false;
        RestoreUnmuteAudio(snapshot);
        snapshot.PlaybackMuteCaptured = false;
        RestoreTimerResolution(snapshot);
        // The driver's settings need no administrator rights, so these go back at shutdown too.
        RestoreNvidiaSettings(snapshot);

        if (skipElevated && !_backend.IsElevated)
        {
            return;
        }
        RestoreAdminTweaks(snapshot);
    }

    /// <summary>
    /// What needs administrator rights, put back together as it was applied: one prompt for the registry values and the
    /// NVIDIA Resizable BAR setting. What can't be put back stays on record, and is noted for Restore Previous.
    /// </summary>
    private void RestoreAdminTweaks(PerformanceProfileSessionSnapshot snapshot)
    {
        var hklmChanges = new List<SystemTweaksService.RegFileEntry>(2);
        bool putBackResponsiveness = TryRestoreSystemResponsiveness(snapshot, out var responsiveness);
        if (putBackResponsiveness) hklmChanges.Add(responsiveness);
        bool putBackScheduling = TryRestoreSchedulingCategory(snapshot, out var scheduling);
        if (putBackScheduling) hklmChanges.Add(scheduling);

        // Cleared whether or not there was anything to write: a capture we have decided not to put
        // back must not be retried by crash recovery on the next start. (Set again below when the write fails.)
        snapshot.SystemResponsivenessCaptured = false;
        snapshot.SchedulingCategoryCaptured = false;

        var adminRecords = snapshot.NvidiaSettings.Where(r => IsAdminNvidia(r.SettingId)).ToList();
        if (hklmChanges.Count > 0 || adminRecords.Count > 0)
        {
            var (failedAdmin, registryOk) = PutBackAdminNvidia(adminRecords, hklmChanges);
            snapshot.NvidiaSettings = snapshot.NvidiaSettings.Where(r => !IsAdminNvidia(r.SettingId)).Concat(failedAdmin).ToList();
            foreach (var record in failedAdmin)
            {
                NoteAdminRecord(snapshot, record, tried: true);
            }
            if (hklmChanges.Count > 0 && !registryOk)
            {
                // Kept on record, as the NVIDIA setting is: put back in the same prompt as it, later.
                snapshot.SystemResponsivenessCaptured = putBackResponsiveness;
                snapshot.SchedulingCategoryCaptured = putBackScheduling;
                NoteSchedulerRecord(snapshot, tried: true);
            }
        }
    }

    // ---- NVIDIA, on the driver's Global profile --------------------------------------------
    // Global rather than the game's own profile: it covers every launch path, a Steam game's
    // included, without TrayTrigger having to know which executable renders, and it leaves the
    // per-game profiles to DLSS Override. A game whose own profile sets the value keeps its own.
    // Each is captured and saved to the snapshot before the write, so a crash in between still
    // leaves a record; restore only acts while the driver still holds what was written.

    /// <summary>One NVIDIA setting a session wants, and when a value already there is the user's to keep.</summary>
    private sealed record NvidiaWanted(uint SettingId, uint Value, string Label, string Describe, Func<DrsSettingReading?, string?> LeaveAlone);

    /// <summary>
    /// Turns on Windows' native HDR mode via the CCD advanced-color API (see
    /// <see cref="HdrControlService"/>) on every display that reports HDR support. Displays already
    /// in HDR are left alone but still recorded, so restore doesn't force them off either. Returns
    /// false (and captures nothing) if there's no HDR-capable display. A display currently in Auto
    /// Color Management's WCG mode IS forced to full HDR too (opt-in trade-off, by user request):
    /// the on/off-only HDR API has no direct "set WCG" call, so RestoreHdr can only put a
    /// WCG-at-session-start display back to plain "off," not back to WCG - it may render flat SDR
    /// after the game closes until Windows/ACM re-negotiates on its own (e.g. on the next app
    /// switch). See docs/Help/profiles/hdr.md and this tweak's Settings description.
    /// </summary>
    private bool ApplyHdr(PerformanceProfileSessionSnapshot snapshot)
    {
        var states = _backend.GetHdrDisplayStates().Where(s => s.Supported).ToList();
        if (states.Count == 0)
        {
            LoggingService.Verbose("PerformanceProfile", "No HDR-capable display detected; skipping Enable HDR.");
            Skipped("HDR, no HDR-capable display");
            return false;
        }
        snapshot.PreviousHdrStates = states.Select(s => new HdrDisplaySnapshot
        {
            AdapterIdLowPart = s.AdapterId.LowPart,
            AdapterIdHighPart = s.AdapterId.HighPart,
            TargetId = s.TargetId,
            WasEnabled = s.Enabled,
            WasWcg = s.IsWcg
        }).ToList();
        snapshot.HdrCaptured = true;

        int wcgCount = states.Count(s => s.IsWcg);
        LoggingService.Info("PerformanceProfile", $"Enable HDR: found {states.Count} HDR-capable display(s) ({states.Count(s => s.Enabled)} already on, {wcgCount} currently in WCG mode and being forced to HDR).");

        int turnedOn = 0, refused = 0;
        foreach (var s in states.Where(s => !s.Enabled))
        {
            if (_backend.SetDisplayHdrEnabled(s.AdapterId, s.TargetId, true))
            {
                turnedOn++;
                LoggingService.Info("PerformanceProfile", $"Enabled HDR on display target {s.TargetId}{(s.IsWcg ? " (was in WCG mode)" : "")}.");
            }
            else
            {
                refused++;
                LoggingService.Warn("PerformanceProfile", $"Failed to enable HDR on display target {s.TargetId}.");
            }
        }
        if (refused > 0) Skipped($"HDR couldn't be turned on on {refused} display(s)");
        else if (turnedOn == 0) Skipped("HDR already on");
        else Note("HDR on");

        return true;
    }

    private void RestoreHdr(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.HdrCaptured) return;

        foreach (var s in snapshot.PreviousHdrStates)
        {
            // ApplyHdr only ever enables displays that were off, so WasEnabled is the record of
            // what we left alone. Re-asserting HDR on a display that was already in HDR is not
            // free: writing the HDR bit makes Windows re-negotiate the display mode, which blanks
            // most monitors for a second or two. Someone who games with HDR on permanently was
            // getting that blank on every single game exit to restore a state that had never
            // changed. Skip those - there is nothing to undo.
            if (s.WasEnabled)
            {
                LoggingService.Verbose("PerformanceProfile", $"Display target {s.TargetId} was already in HDR at launch and was never changed; leaving it alone.");
                continue;
            }

            var adapterId = new HdrControlService.LUID { LowPart = s.AdapterIdLowPart, HighPart = s.AdapterIdHighPart };
            bool ok = _backend.SetDisplayHdrEnabled(adapterId, s.TargetId, false);
            if (!ok)
            {
                uint targetId = s.TargetId;
                NoteUnrestored("HDR on a display", () => _backend.SetDisplayHdrEnabled(adapterId, targetId, false));
            }

            // Name the mode the display goes back to, not just the HDR bit. "HDR state to Off"
            // read as though a display that had been in WCG was being dropped to plain SDR; it
            // is not - clearing HDR returns it to whichever non-HDR mode Auto Color Management
            // had it in.
            string restoredTo = s.WasWcg ? "WCG" : "SDR";
            LoggingService.Info("PerformanceProfile", ok
                ? $"Restored display target {s.TargetId} to {restoredTo}."
                : $"Failed to restore display target {s.TargetId} to {restoredTo}.");
        }
    }
}
