using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What needs an administrator to put back (Resizable BAR, the scheduler values, a Defender exclusion): kept on record, reported in Activity, retried when the player agrees.</summary>
public partial class PerformanceProfileService
{
    private static bool RegistryOnRecord(PerformanceProfileSessionSnapshot snapshot) =>
        snapshot.SystemResponsivenessCaptured || snapshot.SchedulingCategoryCaptured;

    /// <summary>A Defender exclusion on record that needs an administrator prompt to remove.</summary>
    private static bool DefenderOnRecord(PerGameProfileSnapshot perGame) =>
        perGame.DefenderExclusionCaptured && !perGame.DefenderExclusionWasPreExisting && perGame.DefenderExclusionPath != null;

    /// <summary>
    /// What a snapshot holds that needs administrator rights to put back, as Activity &amp; History names it: the NVIDIA
    /// Resizable BAR setting, the scheduler values and a Defender exclusion.
    /// </summary>
    private static List<string> AdminPutBacks(PerformanceProfileSessionSnapshot snapshot)
    {
        var names = snapshot.NvidiaSettings.Where(r => IsAdminNvidia(r.SettingId)).Select(r => NvidiaSessionSettings[r.SettingId].What).ToList();
        if (RegistryOnRecord(snapshot)) names.Add(SchedulerSettingsWhat);
        names.AddRange(snapshot.PerGameSnapshots.Where(DefenderOnRecord).Select(DefenderWhat));
        return names;
    }

    private static string DefenderWhat(PerGameProfileSnapshot perGame) => $"the Defender exclusion for {Path.GetFileName(perGame.DefenderExclusionPath!)}";

    /// <summary>
    /// Notes what is on record and needs administrator rights, without asking for it: Restore Previous, or "Put back now" in
    /// the prompt at start and exit, does that. Returns what it noted.
    /// </summary>
    private List<string> NoteAdminLeftovers(PerformanceProfileSessionSnapshot snapshot)
    {
        var names = new List<string>();
        foreach (var record in snapshot.NvidiaSettings.Where(r => IsAdminNvidia(r.SettingId)).ToList())
        {
            NoteAdminRecord(snapshot, record, tried: false);
            names.Add(NvidiaSessionSettings[record.SettingId].What);
        }
        if (RegistryOnRecord(snapshot))
        {
            NoteSchedulerRecord(snapshot, tried: false);
            names.Add(SchedulerSettingsWhat);
        }
        foreach (var perGame in snapshot.PerGameSnapshots.Where(DefenderOnRecord).ToList())
        {
            NoteUnrestored(DefenderWhat(perGame), () => RetryDefender(snapshot, perGame), key: perGame, tried: false);
            names.Add(DefenderWhat(perGame));
        }
        return names;
    }

    /// <summary>The entry that offers Restore Previous for an NVIDIA setting that needs administrator rights.</summary>
    private void NoteAdminRecord(PerformanceProfileSessionSnapshot snapshot, NvidiaSettingSnapshot record, bool tried) =>
        NoteUnrestored(NvidiaSessionSettings[record.SettingId].What, () => RetryAdmin(snapshot, () => !snapshot.NvidiaSettings.Contains(record)), key: record, tried: tried);

    /// <summary>The one entry for the scheduler values (its key is what lets a later restore settle it).</summary>
    private void NoteSchedulerRecord(PerformanceProfileSessionSnapshot snapshot, bool tried) =>
        NoteUnrestored(SchedulerSettingsWhat, () => RetryAdmin(snapshot, () => !RegistryOnRecord(snapshot)), key: SchedulerEntryKey, tried: tried);

    private static readonly object SchedulerEntryKey = new();

    /// <summary>
    /// Restore Previous for what needs administrator rights. The registry values and Resizable BAR go back in one step, so
    /// one prompt however many of them are noted: the first to be retried in a pass does it, and the rest only look.
    /// </summary>
    private bool RetryAdmin(PerformanceProfileSessionSnapshot snapshot, Func<bool> isBack)
    {
        if (_adminPutBackPass != _retryPass)
        {
            _adminPutBackPass = _retryPass;
            PutBackAdmin(snapshot);
        }
        return isBack();
    }

    private int _retryPass;
    private int _adminPutBackPass = -1;

    private void PutBackAdmin(PerformanceProfileSessionSnapshot snapshot)
    {
        // What it can't put back is noted by the retry that called it, which keeps its own entry for that.
        _quietNotes = true;
        try { RestoreAdminTweaks(snapshot); }
        finally { _quietNotes = false; }
        lock (_lock) FinishSnapshot(snapshot);
    }

    private bool RetryDefender(PerformanceProfileSessionSnapshot snapshot, PerGameProfileSnapshot perGame)
    {
        if (perGame.DefenderExclusionPath is not { } path || !_backend.RemoveDefenderExclusion(path)) return false;
        lock (_lock)
        {
            perGame.DefenderExclusionCaptured = false;
            snapshot.PerGameSnapshots.Remove(perGame);
            FinishSnapshot(snapshot);
        }
        return true;
    }

    /// <summary>
    /// The snapshot on disk after something on record was put back: gone when nothing is left and no game is running, otherwise
    /// as it now is. Not this run's snapshot (a later restore replaced it): nothing on disk to update. Call with the lock held.
    /// </summary>
    private void FinishSnapshot(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _snapshot)) return;
        if (_activeSessionKeys.Count == 0 && IsEmpty(snapshot))
        {
            _snapshot = null;
            _store.DeleteProfileSessionSnapshot();
        }
        else
        {
            _store.SaveProfileSessionSnapshot(snapshot);
        }
    }
}
