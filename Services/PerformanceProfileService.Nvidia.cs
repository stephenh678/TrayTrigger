using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>The NVIDIA driver-profile settings of a profile session: frame cap and Resizable BAR, written to the Global profile and put back from the saved record.</summary>
public partial class PerformanceProfileService
{
    /// <summary>
    /// The session's NVIDIA settings, in one session and one save: a session loads the driver's
    /// whole database, well over 100 ms, and this runs before the game starts. Each is recorded in
    /// the snapshot on disk before the database is saved, so a crash during or after the save
    /// still leaves a record. True when anything was written.
    /// </summary>
    private bool ApplyNvidiaSettings(GameEntry game, PerformanceProfileSessionSnapshot snapshot, AppSettings settings)
    {
        bool maxPerformance = settings.OptimizedProfileTweaks.NvidiaMaxPerformanceEnabled;
        bool frameCap = settings.OptimizedProfileTweaks.FrameCapEnabled;
        if (!maxPerformance && !frameCap) return false;

        if (_drs.GetSettingName(NvidiaGlobalSetting.PresenceProbeId) == null)
        {
            LoggingService.Verbose("PerformanceProfile", "NVIDIA settings: no NVIDIA driver; nothing to do.");
            Skipped("NVIDIA tweaks, no NVIDIA driver");
            return false;
        }

        var wanted = new List<NvidiaWanted>(2);
        if (maxPerformance)
            wanted.Add(new(NvPowerModeSettingId, NvPowerModePreferMax, "NVIDIA power management", "Prefer maximum performance", _ => null));
        if (frameCap && FrameCapWanted(game) is { } cap) wanted.Add(cap);
        if (wanted.Count == 0) return false;

        var written = new List<(NvidiaSettingSnapshot Record, NvidiaWanted Want)>(wanted.Count);
        using (var session = _drs.OpenSession(out string? error))
        {
            var global = session?.GetGlobalProfile(out error);
            if (session == null || global == null)
            {
                LoggingService.Warn("PerformanceProfile", $"NVIDIA settings: could not open the driver's settings ({error}); left unchanged.");
                Skipped("NVIDIA tweaks, the driver's settings couldn't be opened");
                return false;
            }

            foreach (var want in wanted)
            {
                if (!ShouldWriteNvidia(session, global, want, out var reading)) continue;
                if (!session.SetSetting(global, want.SettingId, want.Value, out error))
                {
                    LoggingService.Warn("PerformanceProfile", $"{want.Label}: could not set {want.Describe}: {error}");
                    Skipped($"{want.Label} couldn't be set");
                    NvidiaWriteRefused(game, want, error);
                    continue;
                }
                written.Add((new NvidiaSettingSnapshot { SettingId = want.SettingId, Written = want.Value, Previous = NvidiaGlobalSetting.TokenFor(reading) }, want));
            }
            if (written.Count == 0) return false;

            foreach (var (record, _) in written) snapshot.NvidiaSettings.Add(record);
            _store.SaveProfileSessionSnapshot(snapshot);

            if (!session.Save(out error))
            {
                LoggingService.Warn("PerformanceProfile", $"NVIDIA settings: the driver refused to save them ({error}); nothing was changed.");
                foreach (var (record, _) in written) snapshot.NvidiaSettings.Remove(record);
                _store.SaveProfileSessionSnapshot(snapshot);
                Skipped("NVIDIA tweaks couldn't be saved by the driver");
                return false;
            }
            foreach (var (_, want) in written) Note($"{want.Label}: {want.Describe}");
        }

        // Read back through a fresh session, since sessions don't merge. A value that reads back
        // different keeps its record all the same: the save went through, and restore only acts
        // while the driver still holds what was written.
        using (var check = _drs.OpenSession(out string? checkError))
        {
            var global = check?.GetGlobalProfile(out checkError);
            foreach (var (record, want) in written)
            {
                uint? now = check != null && global != null ? check.GetSetting(global, record.SettingId, out checkError)?.Value : null;
                if (now == record.Written)
                    LoggingService.Info("PerformanceProfile", $"{want.Label}: {want.Describe} (was {record.Previous}).");
                else
                    LoggingService.Warn("PerformanceProfile", $"{want.Label}: saved, but the driver reads back {now?.ToString() ?? "nothing"}{(checkError != null ? $" ({checkError})" : "")}.");
            }
        }
        return true;
    }

    /// <summary>
    /// Reads what the Global profile holds for a setting the session is about to write, and when there is nothing to write says why, in the log and
    /// the launch record: it can't be read (a failed read is not "absent": recording it as such would make undo delete a value the user had chosen),
    /// it already has the value, or it is the user's own choice. True when the write should go ahead.
    /// </summary>
    private bool ShouldWriteNvidia(IDrsSession session, DrsProfileHandle global, NvidiaWanted want, out DrsSettingReading? reading)
    {
        reading = session.GetSetting(global, want.SettingId, out string? error);
        if (error != null)
        {
            LoggingService.Warn("PerformanceProfile", $"{want.Label}: could not read the driver's setting ({error}); left unchanged.");
            Skipped($"{want.Label}, the driver's setting couldn't be read");
            return false;
        }
        if (reading?.Value == want.Value)
        {
            LoggingService.Verbose("PerformanceProfile", $"{want.Label}: already {want.Describe}; nothing to do.");
            Skipped($"{want.Label} already {want.Describe}");
            return false;
        }
        if (want.LeaveAlone(reading) is string reason)
        {
            LoggingService.Verbose("PerformanceProfile", $"{want.Label}: {reason}; left as it is.");
            Skipped($"{want.Label}, {reason}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Works out whether Resizable BAR is to be set for this session, and if so writes what was there to the crash-recovery
    /// snapshot (before anything is changed) and adds the change to <paramref name="ops"/> for the elevated step. Reading
    /// needs no administrator rights. Null when there is nothing to do.
    /// </summary>
    private NvidiaSettingSnapshot? PlanResizableBar(PerformanceProfileSessionSnapshot snapshot, AppSettings settings, bool aggressive, List<NvidiaAdminOp> ops)
    {
        if (!aggressive || !settings.AggressiveProfileTweaks.ResizableBarEnabled) return null;

        if (_drs.GetSettingName(NvidiaGlobalSetting.PresenceProbeId) == null)
        {
            LoggingService.Verbose("PerformanceProfile", "Resizable BAR: no NVIDIA driver; nothing to do.");
            Skipped("Resizable BAR, no NVIDIA driver");
            return null;
        }
        if (ResizableBarWanted() is not { } want) return null;

        using var session = _drs.OpenSession(out string? error);
        var global = session?.GetGlobalProfile(out error);
        if (session == null || global == null)
        {
            LoggingService.Warn("PerformanceProfile", $"{want.Label}: could not open the driver's settings ({error}); left unchanged.");
            Skipped($"{want.Label}, the driver's settings couldn't be opened");
            return null;
        }

        if (!ShouldWriteNvidia(session, global, want, out var reading)) return null;

        var record = new NvidiaSettingSnapshot { SettingId = want.SettingId, Written = want.Value, Previous = NvidiaGlobalSetting.TokenFor(reading) };
        snapshot.NvidiaSettings.Add(record);
        _store.SaveProfileSessionSnapshot(snapshot);
        ops.Add(new NvidiaAdminOp(want.SettingId, NvidiaAdminAction.Set, want.Value));
        return record;
    }

    /// <summary>
    /// After the elevated step: says it was set, or, when it was not, takes the record out unless the driver holds the value
    /// after all (then it still has to be put back) and tells Activity &amp; History why, in plain words.
    /// </summary>
    private void FinishResizableBar(GameEntry game, PerformanceProfileSessionSnapshot snapshot, NvidiaSettingSnapshot record, ElevatedResult result)
    {
        const string label = "Resizable BAR";
        if (result.NvidiaOk)
        {
            LoggingService.Info("PerformanceProfile", $"{label}: on for games NVIDIA hasn't decided on (was {record.Previous}).");
            Note($"{label}: on for games NVIDIA hasn't decided on");
            return;
        }

        // Held, or not known not to be: an elevated step that was still running when the wait ended may make its change after all, and
        // then there has to be a record to put it back from.
        bool held = result.Unfinished || ReadGlobalValue(record.SettingId) == record.Written;
        if (!held)
        {
            snapshot.NvidiaSettings.Remove(record);
            _store.SaveProfileSessionSnapshot(snapshot);
        }
        LoggingService.Warn("PerformanceProfile", $"{label}: the elevated step did not set it ({result.Problem}); {(held ? "the driver holds it all the same" : "left as it was")}.");
        Skipped($"{label} couldn't be set");
        ActivityService.Add(ActivityLevel.Problem, $"{game.Name}: {label} couldn't be set", subject: game.Name,
            detail: $"{result.Problem ?? "The change was not made."} {(held ? "It may be set all the same; it is put back when the game ends." : "It was left as it was.")} The game started as usual.",
            groupKey: $"nvidia-set|{record.SettingId}");
    }

    /// <summary>
    /// What the Global profile holds for a setting, read straight from a session: <see cref="NvidiaGlobalSetting.Read"/> first asks the
    /// driver to name the setting, which it won't for a hidden one such as Resizable BAR. Null when absent or unreadable.
    /// </summary>
    private uint? ReadGlobalValue(uint settingId)
    {
        using var session = _drs.OpenSession(out _);
        var global = session?.GetGlobalProfile(out _);
        return session != null && global != null ? session.GetSetting(global, settingId, out _)?.Value : null;
    }

    /// <summary>NVAPI_INVALID_USER_PRIVILEGE: the driver wants administrator rights for this change.</summary>
    internal const string NvidiaNeedsAdminCode = "-137";

    /// <summary>
    /// A write the driver refused is said in Activity &amp; History, not only in the log: the tweak's switch stays ENABLED, and
    /// without this nothing on screen says it did not take effect.
    /// </summary>
    private static void NvidiaWriteRefused(GameEntry game, NvidiaWanted want, string? error)
    {
        bool needsAdmin = error?.Contains(NvidiaNeedsAdminCode, StringComparison.Ordinal) == true;
        ActivityService.Add(ActivityLevel.Problem, $"{game.Name}: {want.Label} couldn't be set",
            subject: game.Name,
            detail: needsAdmin
                ? $"The NVIDIA driver only lets this setting be changed with administrator rights, and TrayTrigger isn't running as administrator, so it was left as it was. The game started as usual."
                : $"The NVIDIA driver refused it ({error ?? "no reason given"}), so it was left as it was. The game started as usual.",
            groupKey: $"nvidia-set|{want.SettingId}");
    }

    private NvidiaWanted? FrameCapWanted(GameEntry game)
    {
        // The game's own display setting is applied after this, so the rate it will give the main display is asked of it first:
        // a cap worked out for the desktop's 60 Hz would hold a game set to 144 Hz at 59 fps.
        int? expected = _backend.GetPlannedPrimaryRefreshHz(game) ?? _backend.GetPrimaryRefreshHz();
        // 59, not 60: Windows reports a 59.94 Hz mode, which many TVs and monitors run at, as 59.
        if (expected is not int refresh || refresh < MinFrameCapRefreshHz)
        {
            LoggingService.Verbose("PerformanceProfile", $"Frame cap: the primary display's refresh rate couldn't be read, or is under {MinFrameCapRefreshHz} Hz; no cap set.");
            Skipped($"frame cap, the primary display's refresh rate couldn't be read or is under {MinFrameCapRefreshHz} Hz");
            return null;
        }
        uint cap = Math.Min(FrameCapFor(refresh), NvFrameCapMax);
        // A cap someone chose - for power, heat or a game that misbehaves uncapped - is theirs.
        return new(NvFrameCapSettingId, cap, "Frame cap", $"{cap} fps for the primary display's {refresh} Hz",
            reading => reading?.Value is uint existing && existing != 0 ? $"a Max Frame Rate of {existing} fps is already set" : null);
    }

    /// <summary>
    /// "rBAR - Feature" on the Global profile. A game's own profile outranks the Global one, so
    /// the games NVIDIA tested keep NVIDIA's answer - approved ones stay on, rejected ones
    /// (Final Fantasy XVI, Delta Force, Hogwarts Legacy...) stay off - and only the games NVIDIA
    /// never decided on get it. Pointless with Resizable BAR off in the BIOS, so skipped then.
    /// </summary>
    private NvidiaWanted? ResizableBarWanted()
    {
        if (_backend.IsResizableBarEnabled() == false)
        {
            LoggingService.Verbose("PerformanceProfile", "Resizable BAR: off in the BIOS, so there is nothing for the driver to use; not set.");
            Skipped("Resizable BAR, off in the BIOS");
            return null;
        }
        // Set on the Global profile by the user or another tool, either way: their choice for every game.
        return new(NvResizableBarSettingId, 1, "Resizable BAR", "on for games NVIDIA hasn't decided on",
            reading => reading?.Origin == DlssSettingOrigin.UserSet ? $"already set to {reading.Value} on the Global profile" : null);
    }

    /// <summary>
    /// Puts back what the session wrote, in one session and one save. Whatever couldn't be put back
    /// stays in the snapshot, for the next session's end, TrayTrigger's exit or the next start to
    /// try again - a hidden setting like Resizable BAR can't be changed back by hand without
    /// Profile Inspector - and is offered as Restore Previous in Activity &amp; History meanwhile.
    /// </summary>
    private void RestoreNvidiaSettings(PerformanceProfileSessionSnapshot snapshot)
    {
        // The ones the driver lets an administrator change stay on the record: they go back in the elevated step (RestoreGlobalTweaks).
        var admin = snapshot.NvidiaSettings.Where(r => IsAdminNvidia(r.SettingId)).ToList();
        var own = snapshot.NvidiaSettings.Where(r => !IsAdminNvidia(r.SettingId)).ToList();
        if (own.Count == 0) return;
        var failed = PutBackNvidiaSettings(own);
        snapshot.NvidiaSettings = failed.Concat(admin).ToList();
        foreach (var record in failed)
        {
            NoteUnrestored(NvidiaSessionSettings[record.SettingId].What, () => RetryNvidiaSetting(snapshot, record), key: record);
        }
    }

    /// <summary>
    /// Puts back the settings the driver only lets an administrator change, in the same elevated step as
    /// <paramref name="registry"/> (so one prompt for both). Reading what the driver holds needs no administrator rights: a
    /// value someone changed since is left alone, as for the other settings. Returns the records that couldn't be put back,
    /// and whether the registry changes went in.
    /// </summary>
    private (List<NvidiaSettingSnapshot> Failed, bool RegistryOk) PutBackAdminNvidia(IReadOnlyList<NvidiaSettingSnapshot> records, IReadOnlyList<SystemTweaksService.RegFileEntry> registry)
    {
        var failed = new List<NvidiaSettingSnapshot>();
        var planned = new List<NvidiaSettingSnapshot>();
        var ops = new List<NvidiaAdminOp>();

        var known = records.Where(r => IsAdminNvidia(r.SettingId)).ToList();
        if (known.Count > 0 && _drs.GetSettingName(NvidiaGlobalSetting.PresenceProbeId) == null)
        {
            LoggingService.Info("PerformanceProfile", $"No NVIDIA driver any more, so {known.Count} NVIDIA setting(s) recorded by an earlier session have nothing to be put back on.");
            known.Clear();
        }

        if (known.Count > 0)
        {
            using var session = _drs.OpenSession(out string? error);
            var global = session?.GetGlobalProfile(out error);
            if (session == null || global == null)
            {
                LoggingService.Warn("PerformanceProfile", $"Could not open the NVIDIA driver's settings to put them back ({error}).");
                failed.AddRange(known);
            }
            else
            {
                for (int i = known.Count - 1; i >= 0; i--)
                {
                    var record = known[i];
                    var now = session.GetSetting(global, record.SettingId, out error);
                    if (error != null)
                    {
                        LoggingService.Warn("PerformanceProfile", $"Restoring {NvidiaSessionSettings[record.SettingId].What} failed: {error}");
                        failed.Add(record);
                        continue;
                    }
                    if (now?.Value != record.Written)
                    {
                        LoggingService.Info("NvidiaSettings", $"Global setting 0x{record.SettingId:X8} was changed outside TrayTrigger since it was set to {record.Written}; left as it is.");
                        continue;
                    }
                    var op = PutBackOp(record);
                    if (!op.IsAllowed)
                    {
                        LoggingService.Warn("PerformanceProfile", $"Restoring {NvidiaSessionSettings[record.SettingId].What} failed: the value to put back ({record.Previous}) is not one TrayTrigger writes.");
                        failed.Add(record);
                        continue;
                    }
                    ops.Add(op);
                    planned.Add(record);
                }
            }
        }

        bool registryOk = true;
        if (ops.Count > 0 || registry.Count > 0)
        {
            var result = _backend.ApplyAdminChanges(registry, ops);
            registryOk = result.RegistryOk;
            if (!result.NvidiaOk)
            {
                LoggingService.Warn("PerformanceProfile", $"The elevated step did not put the NVIDIA setting back ({result.Problem}).");
                failed.AddRange(planned);
            }
            else
            {
                foreach (var record in planned)
                    LoggingService.Info("PerformanceProfile", $"Restored {NvidiaSessionSettings[record.SettingId].What} (to {record.Previous ?? NvidiaGlobalSetting.AbsentToken}).");
            }
        }
        return (known.Where(failed.Contains).ToList(), registryOk);
    }

    /// <summary>What puts a setting back the way <see cref="NvidiaSettingSnapshot.Previous"/> says it was.</summary>
    private static NvidiaAdminOp PutBackOp(NvidiaSettingSnapshot record) => NvidiaGlobalSetting.ParseToken(record.Previous) switch
    {
        (DlssSettingOrigin.UserSet, uint value) => new NvidiaAdminOp(record.SettingId, NvidiaAdminAction.Set, value),
        (DlssSettingOrigin.Predefined, _) => new NvidiaAdminOp(record.SettingId, NvidiaAdminAction.RestoreDefault),
        _ => new NvidiaAdminOp(record.SettingId, NvidiaAdminAction.Delete),
    };

    private const string SchedulerSettingsWhat = "Windows' multimedia scheduler settings";

    /// <summary>
    /// Restore Previous for one NVIDIA setting. Once it's back its record goes too, from the
    /// snapshot in memory and on disk: left there, the same value chosen by the user later would
    /// read as TrayTrigger's own, and be removed at the next game's end.
    /// </summary>
    private bool RetryNvidiaSetting(PerformanceProfileSessionSnapshot snapshot, NvidiaSettingSnapshot record)
    {
        if (PutBackNvidiaSettings([record]).Count > 0) return false;

        lock (_lock)
        {
            // A record a later restore already settled: nothing on disk to update.
            if (snapshot.NvidiaSettings.Remove(record)) FinishSnapshot(snapshot);
        }
        return true;
    }

    /// <summary>
    /// The records that couldn't be put back, in the order given. The rest are back, or were
    /// changed since by someone else and left alone.
    /// </summary>
    private List<NvidiaSettingSnapshot> PutBackNvidiaSettings(IReadOnlyList<NvidiaSettingSnapshot> records)
    {
        var known = records.Where(r => NvidiaSessionSettings.ContainsKey(r.SettingId)).ToList();
        if (known.Count == 0) return new List<NvidiaSettingSnapshot>();

        if (_drs.GetSettingName(NvidiaGlobalSetting.PresenceProbeId) == null)
        {
            // The NVIDIA driver has gone since (another graphics card, or uninstalled): there is
            // nothing left holding these values, and keeping them would report a failure every start.
            LoggingService.Info("PerformanceProfile", $"No NVIDIA driver any more, so {known.Count} NVIDIA setting(s) recorded by an earlier session have nothing to be put back on.");
            return new List<NvidiaSettingSnapshot>();
        }

        using var session = _drs.OpenSession(out string? error);
        var global = session?.GetGlobalProfile(out error);
        if (session == null || global == null)
        {
            LoggingService.Warn("PerformanceProfile", $"Could not open the NVIDIA driver's settings to put them back ({error}).");
            return known;
        }

        var failed = new HashSet<NvidiaSettingSnapshot>();
        var changed = new List<NvidiaSettingSnapshot>();
        // Last written, first put back.
        for (int i = known.Count - 1; i >= 0; i--)
        {
            var record = known[i];
            switch (NvidiaGlobalSetting.RestoreIn(session, global, record.SettingId, record.Written, record.Previous, out error))
            {
                case NvidiaGlobalSetting.RestoreResult.PutBack:
                    changed.Add(record);
                    break;
                case NvidiaGlobalSetting.RestoreResult.LeftAlone:
                    break;
                default:
                    LoggingService.Warn("PerformanceProfile", $"Restoring {NvidiaSessionSettings[record.SettingId].What} failed: {error}");
                    failed.Add(record);
                    break;
            }
        }

        if (changed.Count > 0)
        {
            if (session.Save(out error))
            {
                foreach (var record in changed)
                    LoggingService.Verbose("PerformanceProfile", $"Restored {NvidiaSessionSettings[record.SettingId].What} (to {record.Previous ?? NvidiaGlobalSetting.AbsentToken}).");
            }
            else
            {
                LoggingService.Warn("PerformanceProfile", $"The NVIDIA driver refused to save the settings being put back ({error}).");
                failed.UnionWith(changed);
            }
        }
        return known.Where(failed.Contains).ToList();
    }
}
