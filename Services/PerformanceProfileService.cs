using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Where the crash-recovery snapshot lives. <see cref="StorageService"/> in production.</summary>
public interface IProfileSnapshotStore
{
    PerformanceProfileSessionSnapshot? LoadProfileSessionSnapshot();
    void SaveProfileSessionSnapshot(PerformanceProfileSessionSnapshot snapshot);
    void DeleteProfileSessionSnapshot();

    /// <summary>After <see cref="LoadProfileSessionSnapshot"/> returned null for a damaged snapshot,
    /// once: where a copy of it was kept, or "" when the copy failed. Null when there was none.</summary>
    string? TakeUnreadableProfileSessionSnapshot() => null;
}

/// <summary>
/// Applies a per-game "Performance Profile" (Optimized/Aggressive) for the duration of a single
/// gaming session and restores the exact prior system state afterward - as opposed to
/// <see cref="SystemTweaksService"/>, which owns the permanent, always-on System &amp; Performance
/// tweaks. All machine access goes through <see cref="ISystemTweakBackend"/>; this class owns only
/// the capture/apply/restore orchestration.
///
/// Two kinds of tweak, two lifetimes:
/// - Machine-wide singletons (Power Plan, HDR, System Responsiveness, MMCSS Scheduling Category,
///   the NVIDIA Global-profile settings): only the first tracked session captures/applies them
///   ("first wins" - see
///   <see cref="BeginGameSession"/>), and only the last tracked session ending restores them.
/// - Per-executable tweaks (GPU Preference, Defender Exclusion): independent per game, applied and
///   restored on that specific game's own session regardless of other sessions still running.
///
/// The pre-profile snapshot is persisted to disk so an abnormal exit (crash/kill) can still be
/// recovered on the next startup via <see cref="RecoverFromCrashIfNeeded"/>. That file is
/// user-writable, so everything read back from it is validated by
/// <see cref="ProfileSnapshotValidator"/> before it is fed to any elevated operation.
/// </summary>
public class PerformanceProfileService
{
    internal const string SystemResponsivenessPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    /// <summary>
    /// Windows' own values, put back when what was there before isn't known. Never a delete: MMCSS
    /// reads SystemProfile when it starts, and without SystemResponsiveness it doesn't start at all
    /// ("The server is currently disabled"), which takes webcams and audio in some apps down with it.
    /// </summary>
    internal const int WindowsDefaultSystemResponsiveness = 20;
    internal const string WindowsDefaultSchedulingCategory = "Medium";

    /// <summary>What the Aggressive profile writes.</summary>
    private const int AggressiveSystemResponsiveness = 10;
    private const string AggressiveSchedulingCategory = "High";

    private readonly IProfileSnapshotStore _store;
    private readonly Func<AppSettings> _settingsProvider;

    /// <summary>
    /// Test hook: whatever the settings provider currently hands back. The --test-live-settings
    /// harness asserts this is reference-equal to the ViewModel's own AppSettings, because the
    /// convenience constructor's provider instead re-read and re-decrypted settings.json on every
    /// call - twice per game launch, on the launch hot path - and returned a detached copy, so an
    /// in-memory toggle that had not been flushed yet was silently ignored.
    /// </summary>
    internal AppSettings CurrentSettingsForTests => _settingsProvider();
    private readonly ISystemTweakBackend _backend;
    private readonly IDrsBackend _drs;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _activeSessionKeys = new();
    private PerformanceProfileSessionSnapshot? _snapshot;

    /// <summary>Who each session is for, for the Activity &amp; History line its restore writes.</summary>
    private readonly Dictionary<string, (string Name, PerformanceProfileMode Mode, DateTime StartedUtc)> _sessionInfo = new(StringComparer.Ordinal);

    /// <summary>
    /// What a restore couldn't put back, and how to try again. Filled while a restore runs and kept
    /// until a retry succeeds, as the Critical NEEDS ATTENTION item in Activity &amp; History. Only
    /// in memory: a restore that fails at exit is a history entry, and what crash recovery owns is
    /// still in the snapshot. Key is the snapshot record behind an entry, when it has one: an NVIDIA
    /// setting is tried again without being asked, and its entry goes once its record has.
    /// </summary>
    private readonly List<(string What, Func<bool> Retry, object? Key)> _unrestored = new();

    /// <summary>The Critical entries this run recorded for what's in <see cref="_unrestored"/>: the
    /// ones Restore Previous marks FIXED when everything is back - never an earlier run's, which it
    /// didn't retry.</summary>
    private readonly List<string> _unrestoredEntryIds = new();

    /// <summary>The Activity &amp; History key for "something wasn't put back": the state and the
    /// entries that announce it share it, so they count once.</summary>
    public const string UnrestoredActivityKey = "profile.unrestored";

    /// <summary>What isn't back the way it was, for the state's text and tests.</summary>
    public IReadOnlyList<string> UnrestoredItems
    {
        get { lock (_lock) return _unrestored.Select(u => u.What).ToList(); }
    }

    private void NoteUnrestored(string what, Func<bool> retry, object? key = null)
    {
        lock (_lock) _unrestored.Add((what, retry, key));
        LoggingService.Warn("PerformanceProfile", $"Could not put back {what}.");
    }

    /// <summary>For view-model tests that need a service but never run a session. No NVIDIA access.</summary>
    public PerformanceProfileService(StorageService storageService)
        : this(storageService, () => storageService.LoadSettings(), new WindowsTweakBackend())
    {
    }

    /// <param name="drs">The NVIDIA driver settings, for the Optimized profile's NVIDIA tweaks. Left
    /// out, there is no NVIDIA driver as far as this service knows (<see cref="NoDrsBackend"/>), so a
    /// test session can't write to the real driver of the PC running it. App passes the real one.</param>
    public PerformanceProfileService(IProfileSnapshotStore store, Func<AppSettings> settingsProvider, ISystemTweakBackend backend, IDrsBackend? drs = null)
    {
        _store = store;
        _settingsProvider = settingsProvider;
        _backend = backend;
        _drs = drs ?? NoDrsBackend.Instance;
    }

    /// <summary>PREFERRED_PSTATE_ID in NVIDIA's NvApiDriverSettings.h: "Power management mode".</summary>
    internal const uint NvPowerModeSettingId = 0x1057EB71;
    /// <summary>PREFERRED_PSTATE_PREFER_MAX: "Prefer maximum performance".</summary>
    internal const uint NvPowerModePreferMax = 1;
    /// <summary>FRL_FPS_ID: "Max Frame Rate" in the Control Panel, 0 for off.</summary>
    internal const uint NvFrameCapSettingId = 0x10835002;
    /// <summary>The highest cap the driver accepts (FRL_FPS_MAX).</summary>
    internal const uint NvFrameCapMax = 1023;
    /// <summary>The slowest primary display the frame cap is set for: a 60 Hz one, which Windows calls 59 when it runs at 59.94.</summary>
    internal const int MinFrameCapRefreshHz = 59;
    /// <summary>"rBAR - Feature", the switch NVIDIA sets to 1 on the games it approves. Hidden: the driver doesn't name it.</summary>
    internal const uint NvResizableBarSettingId = 0x000F00BA;

    /// <summary>
    /// Every NVIDIA setting a session may write, what restore calls it, and the only values a
    /// session writes - which is all a crash-recovery record read back from disk may claim.
    /// </summary>
    internal static readonly IReadOnlyDictionary<uint, (string What, Func<uint, bool> IsValidWritten)> NvidiaSessionSettings =
        new Dictionary<uint, (string, Func<uint, bool>)>
        {
            [NvPowerModeSettingId] = ("the NVIDIA power management mode", v => v == NvPowerModePreferMax),
            [NvFrameCapSettingId] = ("the NVIDIA frame rate limit", v => v is >= 1 and <= NvFrameCapMax),
            [NvResizableBarSettingId] = ("the NVIDIA Resizable BAR setting", v => v == 1),
        };

    /// <summary>
    /// The cap for a refresh rate: refresh - refresh^2 / 3600, rounded down. The formula NVIDIA's
    /// Reflex uses for its own automatic cap with G-SYNC: 59 at 60 Hz, 138 at 144, 157 at 165, 224
    /// at 240, 324 at 360. Far enough under the refresh rate that frame-time variation doesn't
    /// push frames past it and out of the variable refresh range.
    /// </summary>
    internal static uint FrameCapFor(int refreshHz) =>
        (uint)Math.Floor(refreshHz - refreshHz * (double)refreshHz / 3600.0);

    /// <summary>Game IDs with a profile currently applied - for UI "Playing" state and tests.</summary>
    public IReadOnlyCollection<string> ActiveSessionGameIds
    {
        get { lock (_lock) { return _activeSessionKeys.ToArray(); } }
    }

    public const string UnreadableSnapshotActivityKey = "profile.snapshot.unreadable";

    /// <summary>
    /// The record of what a profile changed was there but damaged: nothing can be put back from it,
    /// and treating it as "nothing to restore" would leave the PC changed with nobody told. Critical,
    /// with what to check by hand. Removed once reported, so it's said once; a copy is kept.
    /// </summary>
    private void ReportUnreadableSnapshot()
    {
        if (_store.TakeUnreadableProfileSessionSnapshot() is not { } copy) return;
        bool kept = copy.Length > 0;
        // Removed only once a copy is safe; without one it stays, and is reported again next start.
        if (kept) _store.DeleteProfileSessionSnapshot();
        LoggingService.Error("PerformanceProfile", "The crash-recovery snapshot is damaged; nothing was restored from it."
                                                   + (kept ? $" A copy was kept at '{copy}'." : " It could not be copied, so it was left in place."));
        ActivityService.Add(ActivityLevel.Critical,
            "Couldn't read what a Performance Profile changed, so it may not all have been put back",
            detail: "TrayTrigger closed during a game and its record of the changes was damaged. Check the power plan "
                    + "(Control Panel > Power Options), HDR, and any Defender exclusion for the game."
                    + (kept ? $" A copy of the record was kept at {copy}." : string.Empty),
            groupKey: UnreadableSnapshotActivityKey);
    }

    /// <summary>Call once at startup, before anything else could touch these same registry values.</summary>
    public void RecoverFromCrashIfNeeded()
    {
        var snapshot = _store.LoadProfileSessionSnapshot();
        if (snapshot == null)
        {
            ReportUnreadableSnapshot();
            return;
        }

        LoggingService.Warn("PerformanceProfile", "Found a leftover profile session snapshot from a previous run (likely an abnormal exit) - restoring pre-profile system state.");

        // The file is user-writable and its contents end up in elevated reg/powercfg/PowerShell
        // invocations: drop anything that isn't a well-formed value before restoring.
        foreach (var problem in ProfileSnapshotValidator.Sanitize(snapshot))
        {
            LoggingService.Warn("PerformanceProfile", $"Ignoring invalid entry in the crash-recovery snapshot: {problem}");
        }

        int before;
        lock (_lock) before = _unrestored.Count;
        RestoreGlobalTweaks(snapshot, skipElevated: false);
        foreach (var perGame in snapshot.PerGameSnapshots)
        {
            RestorePerGameTweaks(perGame);
        }
        if (snapshot.NvidiaSettings.Count > 0)
        {
            // An NVIDIA setting that couldn't be put back stays on record (see RestoreNvidiaSettings).
            // Adopted as this run's snapshot, so a game started now records into it rather than
            // writing a new file over it.
            snapshot.PerGameSnapshots.Clear();
            lock (_lock) _snapshot ??= snapshot;
            _store.SaveProfileSessionSnapshot(snapshot);
        }
        else
        {
            _store.DeleteProfileSessionSnapshot();
        }

        List<string> failed;
        lock (_lock) failed = _unrestored.Skip(before).Select(u => u.What).ToList();
        if (failed.Count > 0)
        {
            ReportUnrestored(failed, gameName: null);
        }
        else
        {
            ActivityService.Add(ActivityLevel.Change,
                "Put back the settings a Performance Profile had changed when TrayTrigger last stopped",
                detail: "TrayTrigger or Windows closed while a game was running, so the profile was put back at this start.");
        }
    }

    /// <summary>
    /// Tells Activity &amp; History what a restore couldn't put back: a Critical entry now, and the
    /// NEEDS ATTENTION item, with Restore Previous to try again, until a retry succeeds.
    /// </summary>
    private void ReportUnrestored(IReadOnlyList<string> justFailed, string? gameName)
    {
        string what = JoinList(justFailed);
        // An NVIDIA setting isn't in Windows Settings, and Resizable BAR isn't in NVIDIA's own apps either.
        bool nvidiaOnly = justFailed.All(w => NvidiaSessionSettings.Values.Any(s => s.What == w));
        var entry = ActivityService.Add(ActivityLevel.Critical,
            gameName == null ? $"Couldn't put back {what}" : $"{gameName}: couldn't put back {what}",
            subject: gameName,
            detail: nvidiaOnly
                ? "Restore Previous on this page tries again. So does TrayTrigger, when the next game ends and the next time it starts."
                : $"Restore Previous on this page tries again. Windows Settings can also change {ItOrThem(justFailed)} back by hand.",
            groupKey: UnrestoredActivityKey);
        if (entry != null) lock (_lock) _unrestoredEntryIds.Add(entry.Id);
        PublishUnrestoredState();
    }

    private void PublishUnrestoredState()
    {
        List<string> all;
        lock (_lock) all = _unrestored.Select(u => u.What).Distinct().ToList();
        if (all.Count == 0)
        {
            ActivityService.Current?.ClearState(UnrestoredActivityKey);
            return;
        }
        string it = ItOrThem(all);
        bool plural = it == "them";
        ActivityService.Current?.SetState(new AttentionItem(
            UnrestoredActivityKey,
            $"{Capitalize(JoinList(all))} {(plural ? "weren't" : "wasn't")} put back",
            $"A Performance Profile changed {it} for a game and couldn't change {it} back afterwards.",
            "Restore Previous",
            () => _ = System.Threading.Tasks.Task.Run(RetryUnrestored),
            IsCritical: true));
    }

    /// <summary>
    /// Restore Previous on the NEEDS ATTENTION item: tries each thing that wasn't put back again.
    /// Off the UI thread - a Windows setting can need an administrator prompt. Returns true when
    /// everything is back.
    /// </summary>
    public bool RetryUnrestored()
    {
        List<(string What, Func<bool> Retry, object? Key)> pending;
        lock (_lock)
        {
            pending = _unrestored.ToList();
            _unrestored.Clear();
        }
        var fixedNow = new List<string>();
        foreach (var item in pending)
        {
            bool ok;
            try { ok = item.Retry(); }
            catch (Exception ex) { LoggingService.Warn("PerformanceProfile", $"Retrying {item.What} failed: {ex.Message}"); ok = false; }
            if (ok) fixedNow.Add(item.What);
            else lock (_lock) _unrestored.Add(item);
        }
        ReportPutBack(fixedNow);
        lock (_lock) return _unrestored.Count == 0;
    }

    /// <summary>
    /// Tells Activity &amp; History that something a restore couldn't put back is back now, by
    /// Restore Previous or by a later restore getting to it.
    /// </summary>
    private void ReportPutBack(IReadOnlyList<string> fixedNow)
    {
        int stillLeft;
        List<string> reported = new();
        lock (_lock)
        {
            stillLeft = _unrestored.Count;
            if (stillLeft == 0)
            {
                reported = _unrestoredEntryIds.ToList();
                _unrestoredEntryIds.Clear();
            }
        }
        if (fixedNow.Count > 0 && stillLeft == 0)
        {
            // Everything is back: the Critical entries that reported it get FIXED rather than a row of their own.
            ActivityService.Current?.MarkFixed(reported);
        }
        else if (fixedNow.Count > 0)
        {
            // Some of it: say what, since the entries can't be marked FIXED yet.
            ActivityService.Add(ActivityLevel.Change, $"Put back {JoinList(fixedNow)}", groupKey: $"{UnrestoredActivityKey}.fixed");
        }
        PublishUnrestoredState();
    }

    /// <summary>
    /// Drops what was noted for an NVIDIA setting whose record is no longer pending: a later restore
    /// put it back, or found it changed by someone else, so there is nothing left to offer Restore
    /// Previous for. Returns what was dropped. Call with the lock held, and only after the newly
    /// failed have been read off the end of the list.
    /// </summary>
    private List<string> TakeSettledNvidiaEntries(List<NvidiaSettingSnapshot> stillPending)
    {
        bool Settled((string What, Func<bool> Retry, object? Key) entry) =>
            entry.Key is NvidiaSettingSnapshot record && !stillPending.Contains(record);

        var settled = _unrestored.Where(Settled).Select(u => u.What).Distinct().ToList();
        if (settled.Count > 0) _unrestored.RemoveAll(Settled);
        return settled;
    }

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"them" for several things, or one plural on its own ("settings", "notifications"); "it" otherwise.</summary>
    private static string ItOrThem(IReadOnlyList<string> items) => items.Count > 1 || items[0].EndsWith('s') ? "them" : "it";

    // ------------------------------------------------------------------------------------------
    // SESSION LIFECYCLE - the launcher calls these in this exact order:
    //
    //   1. BeginGameSession(game)              PRE-LAUNCH. Runs before the game process exists.
    //                                          Everything that can be applied here, is: the game
    //                                          then starts up already on the fast power plan, with
    //                                          its GPU preference and Defender exclusion in place
    //                                          before it loads a single shader.
    //   2. (launcher runs the user's pre-launch script, then starts the game)
    //   3. OnGameProcessStarted(game, process) POST-START. Only for tweaks that need a live
    //                                          handle to the game process (currently just
    //                                          Above Normal priority). Runs for any launch path
    //                                          that eventually finds a real process.
    //   4. EndGameSession(gameId)              RESTORE. Per-game tweaks immediately; machine-wide
    //                                          tweaks once the last tracked session ends.
    //
    // ADDING A TWEAK: put it in ApplyPreLaunchTweaks unless it genuinely needs the Process object,
    // in which case it goes in ApplyProcessTweaks. Anything that must be undone needs a snapshot
    // field and a matching Restore* call - see RestoreGlobalTweaks / RestorePerGameTweaks.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// PRE-LAUNCH phase. Captures the snapshot and applies every tweak that doesn't need the game
    /// process. Machine-wide tweaks are only captured/applied by the first tracked session ("first
    /// wins") - a second concurrent game just joins it. Per-executable tweaks apply independently
    /// every time, since they're keyed to that game's own exe path. If the launch subsequently
    /// fails, the launcher must call <see cref="EndGameSession"/>. Returns true if this call
    /// started tracking a session (i.e. something was applied that needs restoring later).
    /// </summary>
    public bool BeginGameSession(GameEntry game)
    {
        if (game.PerformanceProfile == PerformanceProfileMode.Off)
        {
            LoggingService.Verbose("PerformanceProfile", $"'{game.Name}': profile is Off, nothing applied.");
            return false;
        }

        lock (_lock)
        {
            if (_activeSessionKeys.Contains(game.Id))
            {
                LoggingService.Verbose("PerformanceProfile", $"'{game.Name}': its profile is already applied for a session in progress, not applied again.");
                return false;
            }

            var settings = _settingsProvider();
            bool isFirstSession = _activeSessionKeys.Count == 0;

            _snapshot ??= new PerformanceProfileSessionSnapshot();

            // Persists the snapshot after each individual tweak below (not once at the end),
            // so a crash mid-sequence still leaves a recovery record for whatever was already
            // applied instead of leaving mutated system state with nothing on disk to undo it.
            bool appliedAnything = ApplyPreLaunchTweaks(game, settings, isFirstSession, _snapshot);

            // A later game that adds no per-exe tweaks of its own still relies on the machine-wide
            // ones the first session applied, so it joins the session: otherwise the first game
            // ending restores them while this one is still running.
            if (!appliedAnything && isFirstSession)
            {
                LoggingService.Verbose("PerformanceProfile", $"'{game.Name}' requested {game.PerformanceProfile} but every applicable pre-launch tweak is disabled (or not resolvable for this launch type) - nothing to apply.");
                if (IsEmpty(_snapshot))
                {
                    _snapshot = null;
                }
                return false;
            }

            _activeSessionKeys.Add(game.Id);
            _sessionInfo[game.Id] = (game.Name, game.PerformanceProfile, DateTime.UtcNow);
            LoggingService.Info("PerformanceProfile", $"Applied {game.PerformanceProfile} profile for '{game.Name}' (pre-launch).");
            return true;
        }
    }

    private static bool IsEmpty(PerformanceProfileSessionSnapshot s) =>
        s.PerGameSnapshots.Count == 0 && !s.PowerPlanCaptured && !s.SystemResponsivenessCaptured
        && !s.SchedulingCategoryCaptured && !s.HdrCaptured && !s.ToastsCaptured && !s.TimerResolutionRequested
        && s.NvidiaSettings.Count == 0;

    /// <summary>
    /// POST-START phase. Applies the tweaks that need the actual game process. These need no
    /// snapshot or restore - they die with the process - so this doesn't touch session tracking
    /// and is safe to call even when <see cref="BeginGameSession"/> applied nothing.
    /// </summary>
    public void OnGameProcessStarted(GameEntry game, Process process)
    {
        if (game.PerformanceProfile == PerformanceProfileMode.Off) return;

        var settings = _settingsProvider();
        if (settings.OptimizedProfileTweaks.PowerThrottlingExemptEnabled)
        {
            ApplyPowerThrottlingExemption(process, game.Name);
        }

        bool aggressive = game.PerformanceProfile == PerformanceProfileMode.Aggressive;
        if (aggressive && settings.AggressiveProfileTweaks.AboveNormalPriorityEnabled)
        {
            ApplyAboveNormalPriority(process, game.Name);
        }
    }

    private bool ApplyPreLaunchTweaks(GameEntry game, AppSettings settings, bool isFirstSession, PerformanceProfileSessionSnapshot snapshot)
    {
        bool applied = false;
        bool aggressive = game.PerformanceProfile == PerformanceProfileMode.Aggressive;

        // --- Machine-wide (first session only) ---
        if (isFirstSession)
        {
            if (settings.OptimizedProfileTweaks.PowerPlanEnabled && ApplyPowerPlan(snapshot))
            {
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (settings.OptimizedProfileTweaks.HdrEnabled && ApplyHdr(snapshot))
            {
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            // The two HKLM tweaks are captured first and written together, so one administrator
            // prompt covers both. The snapshot is saved before the write for the same reason it
            // always was: whatever happens next, what was there is already on disk.
            var hklmChanges = new List<SystemTweaksService.RegFileEntry>(2);

            if (aggressive && settings.AggressiveProfileTweaks.SystemResponsivenessEnabled)
            {
                hklmChanges.Add(CaptureSystemResponsiveness(snapshot));
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (aggressive && settings.AggressiveProfileTweaks.MmcssGamesPriorityEnabled)
            {
                hklmChanges.Add(CaptureSchedulingCategory(snapshot));
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (hklmChanges.Count > 0)
            {
                _backend.ApplyHklmChanges(hklmChanges);
            }

            if (settings.OptimizedProfileTweaks.DoNotDisturbEnabled)
            {
                ApplyDoNotDisturb(snapshot);
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (settings.OptimizedProfileTweaks.UnmuteAudioEnabled)
            {
                ApplyUnmuteAudio(snapshot);
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (ApplyNvidiaSettings(snapshot, settings, aggressive))
            {
                applied = true;
            }

            if (aggressive && settings.AggressiveProfileTweaks.TimerResolutionEnabled)
            {
                ApplyTimerResolution(snapshot);
                applied = true;
                // Not persisted on purpose: the request dies with this process, so there is
                // nothing for crash recovery to undo.
            }
        }

        // --- Per-executable (every session; needs a real file path) ---
        string? resolvedExePath = ResolveRealExecutablePath(game);
        PerGameProfileSnapshot? perGame = null;

        if (settings.OptimizedProfileTweaks.GpuPreferenceEnabled && resolvedExePath != null)
        {
            perGame = new PerGameProfileSnapshot { GameId = game.Id };
            ApplyGpuPreference(perGame, resolvedExePath);
            applied = true;
        }

        if (aggressive && settings.AggressiveProfileTweaks.DefenderExclusionEnabled && resolvedExePath != null)
        {
            perGame ??= new PerGameProfileSnapshot { GameId = game.Id };
            ApplyDefenderExclusion(perGame, resolvedExePath);
            applied = true;
        }

        if (perGame != null)
        {
            snapshot.PerGameSnapshots.Add(perGame);
            _store.SaveProfileSessionSnapshot(snapshot);
        }

        return applied;
    }

    /// <summary>
    /// Ends tracking for this game's session. That game's own per-executable tweaks are restored
    /// immediately; machine-wide tweaks are only restored once every tracked session has ended.
    /// Returns true if a tracked session was actually ended by this call.
    /// </summary>
    public bool EndGameSession(string gameId) => EndGameSession(gameId, out _);

    /// <param name="putBack">The profile's mode when it was applied and everything was put back,
    /// for the game's "Played" line in Activity &amp; History; null otherwise. What couldn't be put
    /// back is reported here, as Critical.</param>
    public bool EndGameSession(string gameId, out PerformanceProfileMode? putBack)
    {
        putBack = null;
        (string Name, PerformanceProfileMode Mode, DateTime StartedUtc) info;
        List<string> failed;
        List<string> settled;
        lock (_lock)
        {
            if (!_activeSessionKeys.Remove(gameId)) return false;
            _sessionInfo.Remove(gameId, out info);
            if (_snapshot == null) return true;
            int before = _unrestored.Count;
            var snapshot = _snapshot;

            var perGame = _snapshot.PerGameSnapshots.FirstOrDefault(p => p.GameId == gameId);
            if (perGame != null)
            {
                RestorePerGameTweaks(perGame);
                _snapshot.PerGameSnapshots.Remove(perGame);
            }

            if (_activeSessionKeys.Count == 0)
            {
                RestoreGlobalTweaks(_snapshot, skipElevated: false);
                if (_snapshot.NvidiaSettings.Count > 0)
                {
                    // An NVIDIA setting that couldn't be put back stays on record (see RestoreNvidiaSettings).
                    _store.SaveProfileSessionSnapshot(_snapshot);
                }
                else
                {
                    _snapshot = null;
                    _store.DeleteProfileSessionSnapshot();
                }
                LoggingService.Info("PerformanceProfile", "All tracked game sessions ended; restored pre-profile system state.");
            }
            else
            {
                _store.SaveProfileSessionSnapshot(_snapshot);
            }
            failed = _unrestored.Skip(before).Select(u => u.What).ToList();
            // An NVIDIA setting an earlier session couldn't put back, and this one's end did.
            settled = TakeSettledNvidiaEntries(snapshot.NvidiaSettings);
        }

        // Outside the lock: recording raises events the UI listens to.
        string name = info.Name ?? "A game";
        if (settled.Count > 0)
        {
            ReportPutBack(settled);
        }
        if (failed.Count > 0)
        {
            ReportUnrestored(failed, name);
        }
        else
        {
            putBack = info.Mode;
        }
        return true;
    }

    /// <summary>
    /// Called from App.ExitApplication so a temporary profile never outlives TrayTrigger. Also
    /// used for Windows shutdown/sign-out via <c>Application.SessionEnding</c> with
    /// <paramref name="skipElevated"/> true: there, anything that would raise a UAC prompt
    /// (HKLM writes and Defender cmdlets while not elevated) is left in the on-disk snapshot for
    /// <see cref="RecoverFromCrashIfNeeded"/> to finish on the next start, so shutdown is never
    /// blocked behind a prompt nobody can answer. Everything that needs no elevation (power plan,
    /// HDR, HKCU GPU preference) is restored right away either way.
    /// </summary>
    public void RestoreActiveSessionOnShutdown(bool skipElevated = false)
    {
        List<string> failed;
        List<string> nextStart;
        lock (_lock)
        {
            if (_snapshot == null) return;
            int before = _unrestored.Count;
            nextStart = RestoreActiveSessionOnShutdownLocked(_snapshot, skipElevated);
            failed = _unrestored.Skip(before).Select(u => u.What).Where(w => !nextStart.Contains(w)).ToList();
            _sessionInfo.Clear();
        }
        if (nextStart.Count > 0)
        {
            ActivityService.Add(ActivityLevel.Problem, $"Couldn't put back {JoinList(nextStart)} when TrayTrigger closed",
                detail: "TrayTrigger will try again the next time it starts.",
                groupKey: UnrestoredActivityKey);
        }
        // An entry for next time: nothing is left to retry it once TrayTrigger has gone.
        if (failed.Count > 0)
        {
            // Not "play the game again": a new session would take the changed state as the one to put back.
            ActivityService.Add(ActivityLevel.Critical, $"Couldn't put back {JoinList(failed)} when TrayTrigger closed",
                detail: $"TrayTrigger can't try again once it has closed. Change {ItOrThem(failed)} back by hand in Windows Settings; Show in log has the details.",
                groupKey: UnrestoredActivityKey);
        }
    }

    /// <returns>What couldn't be put back and stays in the snapshot for the next start to finish.</returns>
    private List<string> RestoreActiveSessionOnShutdownLocked(PerformanceProfileSessionSnapshot snapshot, bool skipElevated)
    {
        {
            bool deferElevated = skipElevated && !_backend.IsElevated;

            RestoreGlobalTweaks(snapshot, deferElevated);
            foreach (var perGame in snapshot.PerGameSnapshots.ToList())
            {
                RestoreGpuPreference(perGame);
                if (!deferElevated)
                {
                    RestoreDefenderExclusion(perGame);
                }
                else if (!perGame.DefenderExclusionCaptured || perGame.DefenderExclusionWasPreExisting)
                {
                    snapshot.PerGameSnapshots.Remove(perGame);
                }
            }

            // NVIDIA settings that couldn't be put back are kept whatever the reason for closing:
            // nothing else will ever try them again (see RestoreNvidiaSettings).
            var nextStart = snapshot.NvidiaSettings.Select(r => NvidiaSessionSettings[r.SettingId].What).ToList();
            bool anythingDeferred = (deferElevated && (snapshot.SystemResponsivenessCaptured || snapshot.SchedulingCategoryCaptured || snapshot.PerGameSnapshots.Count > 0))
                                    || nextStart.Count > 0;

            _activeSessionKeys.Clear();
            if (anythingDeferred)
            {
                _store.SaveProfileSessionSnapshot(snapshot);
                LoggingService.Info("PerformanceProfile", "Restored the non-elevated parts of the active profile at shutdown; elevated tweaks will be restored on next start.");
            }
            else
            {
                _store.DeleteProfileSessionSnapshot();
                LoggingService.Info("PerformanceProfile", "Restored pre-profile system state on application exit.");
            }
            _snapshot = null;
            return nextStart;
        }
    }

    /// <summary>
    /// Only a real, existing local file path can be used for the per-executable tweaks - a Steam
    /// game's ExecutablePath is a "steam://rungameid/&lt;id&gt;" URL, not a path on disk, so GPU
    /// Preference and the Defender exclusion silently have nothing to key themselves to for those
    /// launches rather than guessing at a path.
    /// </summary>
    private static string? ResolveRealExecutablePath(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath)) return null;
        if (ProcessLauncherService.IsNonFileProtocolUrl(game.ExecutablePath)) return null;
        return File.Exists(game.ExecutablePath) ? game.ExecutablePath : null;
    }

    /// <summary>True when the previous scheme was captured, and so has to be restored later.</summary>
    private bool ApplyPowerPlan(PerformanceProfileSessionSnapshot snapshot)
    {
        string? previous = _backend.GetActivePowerSchemeGuid();
        if (previous == null)
        {
            // Nothing to put back afterwards, so switching now would leave the Ultimate plan on for good.
            LoggingService.Warn("PerformanceProfile", "Power Plan: could not read the active scheme, so it was left unchanged.");
            return false;
        }
        snapshot.PreviousPowerSchemeGuid = previous;
        snapshot.PowerPlanCaptured = true;

        if (!_backend.ActivateUltimatePowerPlan())
        {
            LoggingService.Warn("PerformanceProfile", "Could not create or locate the 'Ultimate Plan - TrayTrigger' power scheme.");
            return true;
        }
        LoggingService.Verbose("PerformanceProfile", $"Power Plan: switched active scheme to 'Ultimate Plan - TrayTrigger' (was {snapshot.PreviousPowerSchemeGuid ?? "unknown"}).");
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
        return new SystemTweaksService.RegFileEntry(
            SystemResponsivenessPath, "SystemResponsiveness", AggressiveSystemResponsiveness, RegistryValueKind.DWord, Delete: false);
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

    /// <summary>Captures and returns the change; see <see cref="CaptureSystemResponsiveness"/>.</summary>
    private SystemTweaksService.RegFileEntry CaptureSchedulingCategory(PerformanceProfileSessionSnapshot snapshot)
    {
        string? current = _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category");
        // Already ours before the game: left over, as for System Responsiveness above.
        bool leftOver = string.Equals(current, AggressiveSchedulingCategory, StringComparison.OrdinalIgnoreCase);
        snapshot.PreviousSchedulingCategory = leftOver ? null : current;
        snapshot.SchedulingCategoryCaptured = true;

        LoggingService.Verbose("PerformanceProfile", leftOver
            ? $"MMCSS Scheduling Category: setting to '{AggressiveSchedulingCategory}' (already '{current}', TrayTrigger's own value left from an earlier game; Windows' default '{WindowsDefaultSchedulingCategory}' goes back afterwards)."
            : $"MMCSS Scheduling Category: setting to '{AggressiveSchedulingCategory}' (was '{current ?? "unset"}').");

        // SFIO Priority is intentionally not written - Microsoft's MMCSS docs state it "is not used".
        return new SystemTweaksService.RegFileEntry(
            SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", AggressiveSchedulingCategory, RegistryValueKind.String, Delete: false);
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
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyDoNotDisturb failed: {ex.Message}");
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
                return;
            }
            if (muted == false)
            {
                LoggingService.Verbose("PerformanceProfile", "Unmute audio: playback device is already unmuted.");
                return;
            }

            if (_backend.SetDefaultPlaybackMuted(false))
            {
                snapshot.PlaybackMuteCaptured = true;
                snapshot.PreviousPlaybackMuted = true;
                LoggingService.Info("PerformanceProfile", "Unmute audio: default playback device unmuted for this session.");
            }
            else
            {
                LoggingService.Warn("PerformanceProfile", "Unmute audio: the default playback device refused the change.");
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
        // Both together, as they were applied: one administrator prompt on the way out too.
        var hklmChanges = new List<SystemTweaksService.RegFileEntry>(2);
        if (TryRestoreSystemResponsiveness(snapshot, out var responsiveness)) hklmChanges.Add(responsiveness);
        if (TryRestoreSchedulingCategory(snapshot, out var scheduling)) hklmChanges.Add(scheduling);

        // Cleared whether or not there was anything to write: a capture we have decided not to put
        // back must not be retried by crash recovery on the next start.
        snapshot.SystemResponsivenessCaptured = false;
        snapshot.SchedulingCategoryCaptured = false;

        if (hklmChanges.Count > 0 && !_backend.ApplyHklmChanges(hklmChanges))
        {
            var entries = hklmChanges.ToList();
            NoteUnrestored("Windows' multimedia scheduler settings", () => _backend.ApplyHklmChanges(entries));
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
    /// The session's NVIDIA settings, in one session and one save: a session loads the driver's
    /// whole database, well over 100 ms, and this runs before the game starts. Each is recorded in
    /// the snapshot on disk before the database is saved, so a crash during or after the save
    /// still leaves a record. True when anything was written.
    /// </summary>
    private bool ApplyNvidiaSettings(PerformanceProfileSessionSnapshot snapshot, AppSettings settings, bool aggressive)
    {
        bool maxPerformance = settings.OptimizedProfileTweaks.NvidiaMaxPerformanceEnabled;
        bool frameCap = settings.OptimizedProfileTweaks.FrameCapEnabled;
        bool resizableBar = aggressive && settings.AggressiveProfileTweaks.ResizableBarEnabled;
        if (!maxPerformance && !frameCap && !resizableBar) return false;

        if (_drs.GetSettingName(NvidiaGlobalSetting.PresenceProbeId) == null)
        {
            LoggingService.Verbose("PerformanceProfile", "NVIDIA settings: no NVIDIA driver; nothing to do.");
            return false;
        }

        var wanted = new List<NvidiaWanted>(3);
        if (maxPerformance)
            wanted.Add(new(NvPowerModeSettingId, NvPowerModePreferMax, "NVIDIA power management", "Prefer maximum performance", _ => null));
        if (frameCap && FrameCapWanted() is { } cap) wanted.Add(cap);
        if (resizableBar && ResizableBarWanted() is { } rebar) wanted.Add(rebar);
        if (wanted.Count == 0) return false;

        var written = new List<(NvidiaSettingSnapshot Record, NvidiaWanted Want)>(wanted.Count);
        using (var session = _drs.OpenSession(out string? error))
        {
            var global = session?.GetGlobalProfile(out error);
            if (session == null || global == null)
            {
                LoggingService.Warn("PerformanceProfile", $"NVIDIA settings: could not open the driver's settings ({error}); left unchanged.");
                return false;
            }

            foreach (var want in wanted)
            {
                var reading = session.GetSetting(global, want.SettingId, out error);
                if (error != null)
                {
                    // A failed read is not "absent": recording it as such would make undo delete a
                    // value the user had chosen.
                    LoggingService.Warn("PerformanceProfile", $"{want.Label}: could not read the driver's setting ({error}); left unchanged.");
                    continue;
                }
                if (reading?.Value == want.Value)
                {
                    LoggingService.Verbose("PerformanceProfile", $"{want.Label}: already {want.Describe}; nothing to do.");
                    continue;
                }
                if (want.LeaveAlone(reading) is string reason)
                {
                    LoggingService.Verbose("PerformanceProfile", $"{want.Label}: {reason}; left as it is.");
                    continue;
                }
                if (!session.SetSetting(global, want.SettingId, want.Value, out error))
                {
                    LoggingService.Warn("PerformanceProfile", $"{want.Label}: could not set {want.Describe}: {error}");
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
                return false;
            }
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

    private NvidiaWanted? FrameCapWanted()
    {
        // 59, not 60: Windows reports a 59.94 Hz mode, which many TVs and monitors run at, as 59.
        if (_backend.GetPrimaryRefreshHz() is not int refresh || refresh < MinFrameCapRefreshHz)
        {
            LoggingService.Verbose("PerformanceProfile", $"Frame cap: the primary display's refresh rate couldn't be read, or is under {MinFrameCapRefreshHz} Hz; no cap set.");
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
        if (snapshot.NvidiaSettings.Count == 0) return;
        var failed = PutBackNvidiaSettings(snapshot.NvidiaSettings);
        snapshot.NvidiaSettings = failed;
        foreach (var record in failed)
        {
            NoteUnrestored(NvidiaSessionSettings[record.SettingId].What, () => RetryNvidiaSetting(snapshot, record), key: record);
        }
    }

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
            // Not this run's snapshot any more (or a record a later restore already settled): nothing on disk to update.
            if (!snapshot.NvidiaSettings.Remove(record) || !ReferenceEquals(snapshot, _snapshot)) return true;

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

    /// <summary>
    /// Keeps Windows from throttling the game: EcoQoS lowers a process's clocks and, on a hybrid
    /// CPU, moves it to the efficiency cores once Windows decides it's in the background - a game
    /// on a second monitor while you type in Discord, or alt-tabbed. Windows 11 also ignores a
    /// hidden window's timer requests. No restore: it dies with the process.
    /// </summary>
    private void ApplyPowerThrottlingExemption(Process process, string gameName)
    {
        try
        {
            if (_backend.ExemptFromPowerThrottling(process))
                LoggingService.Verbose("PerformanceProfile", $"'{gameName}' is exempt from Windows power throttling.");
            else
                LoggingService.Info("PerformanceProfile", $"Windows refused to exempt '{gameName}' from power throttling (it may be running as administrator).");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Exempting '{gameName}' from power throttling failed: {ex.Message}");
        }
    }

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

        foreach (var s in states.Where(s => !s.Enabled))
        {
            if (_backend.SetDisplayHdrEnabled(s.AdapterId, s.TargetId, true))
            {
                LoggingService.Info("PerformanceProfile", $"Enabled HDR on display target {s.TargetId}{(s.IsWcg ? " (was in WCG mode)" : "")}.");
            }
            else
            {
                LoggingService.Warn("PerformanceProfile", $"Failed to enable HDR on display target {s.TargetId}.");
            }
        }

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

    /// <summary>
    /// Windows' Settings &gt; System &gt; Display &gt; Graphics "GPU preference" feature, stored
    /// per-executable. Real, Microsoft-backed mechanism (this is the same registry location the
    /// Settings UI itself writes to), no elevation needed since it's HKCU.
    /// </summary>
    private void ApplyGpuPreference(PerGameProfileSnapshot snapshot, string exePath)
    {
        try
        {
            snapshot.GpuPreferenceExecutablePath = exePath;
            snapshot.PreviousGpuPreferenceValue = _backend.GetGpuPreference(exePath);
            snapshot.GpuPreferenceCaptured = true;
            _backend.SetGpuPreference(exePath, "GpuPreference=2;");
            LoggingService.Verbose("PerformanceProfile", $"GPU Preference: set 'High performance' for '{exePath}' (was '{snapshot.PreviousGpuPreferenceValue ?? "unset"}').");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyGpuPreference failed: {ex.Message}");
        }
    }

    private void RestoreGpuPreference(PerGameProfileSnapshot snapshot)
    {
        if (!snapshot.GpuPreferenceCaptured || snapshot.GpuPreferenceExecutablePath == null) return;

        try
        {
            if (snapshot.PreviousGpuPreferenceValue != null)
            {
                _backend.SetGpuPreference(snapshot.GpuPreferenceExecutablePath, snapshot.PreviousGpuPreferenceValue);
            }
            else
            {
                _backend.DeleteGpuPreference(snapshot.GpuPreferenceExecutablePath);
            }
            LoggingService.Verbose("PerformanceProfile", $"GPU Preference: restored for '{snapshot.GpuPreferenceExecutablePath}' to '{snapshot.PreviousGpuPreferenceValue ?? "unset"}'.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreGpuPreference failed: {ex.Message}");
            string path = snapshot.GpuPreferenceExecutablePath;
            string? previous = snapshot.PreviousGpuPreferenceValue;
            NoteUnrestored($"the GPU preference for {Path.GetFileName(path)}", () =>
            {
                if (previous != null) _backend.SetGpuPreference(path, previous);
                else _backend.DeleteGpuPreference(path);
                return true;
            });
        }
        finally
        {
            snapshot.GpuPreferenceCaptured = false;
        }
    }

    /// <summary>
    /// Raises the launched process to Above Normal priority - Microsoft's own documented
    /// scheduling classes via <see cref="Process.PriorityClass"/> (a thin wrapper over
    /// SetPriorityClass). Deliberately stops at Above Normal, not High: community benchmarking
    /// consistently shows High priority risks starving audio/input threads for a negligible
    /// additional gain over Above Normal. No restore needed - priority dies with the process.
    /// </summary>
    private void ApplyAboveNormalPriority(Process process, string gameName)
    {
        try
        {
            _backend.SetProcessPriority(process, ProcessPriorityClass.AboveNormal);
            LoggingService.Verbose("PerformanceProfile", $"Set '{gameName}' process priority to Above Normal.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Failed to raise process priority for '{gameName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Adds a Microsoft Defender real-time-protection exclusion for the game's executable. Only
    /// removes the exclusion on restore if this session is the one that added it - an exclusion
    /// the user (or another tool) already had is left untouched.
    /// </summary>
    private void ApplyDefenderExclusion(PerGameProfileSnapshot snapshot, string exePath)
    {
        try
        {
            // Only an exclusion this session actually added is removed on restore. The add checks
            // for an existing one itself, elevated: an unelevated process can't read the list.
            bool added = _backend.AddDefenderExclusion(exePath);
            snapshot.DefenderExclusionPath = exePath;
            snapshot.DefenderExclusionWasPreExisting = !added;
            snapshot.DefenderExclusionCaptured = true;

            if (added)
            {
                LoggingService.Verbose("PerformanceProfile", $"Defender Exclusion: added '{exePath}'.");
            }
            else
            {
                LoggingService.Verbose("PerformanceProfile", $"Defender Exclusion: '{exePath}' was already excluded; leaving as-is.");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyDefenderExclusion failed: {ex.Message}");
        }
    }

    private void RestorePerGameTweaks(PerGameProfileSnapshot snapshot)
    {
        RestoreGpuPreference(snapshot);
        RestoreDefenderExclusion(snapshot);
    }

    private void RestoreDefenderExclusion(PerGameProfileSnapshot snapshot)
    {
        if (!snapshot.DefenderExclusionCaptured || snapshot.DefenderExclusionPath == null) return;
        snapshot.DefenderExclusionCaptured = false;
        if (snapshot.DefenderExclusionWasPreExisting) return;

        string path = snapshot.DefenderExclusionPath;
        bool removed;
        try
        {
            removed = _backend.RemoveDefenderExclusion(path);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreDefenderExclusion failed: {ex.Message}");
            removed = false;
        }
        if (removed)
        {
            LoggingService.Verbose("PerformanceProfile", $"Defender Exclusion: removed '{path}'.");
        }
        else
        {
            NoteUnrestored($"the Defender exclusion for {Path.GetFileName(path)}", () => _backend.RemoveDefenderExclusion(path));
        }
    }
}

/// <summary>
/// Validates a <see cref="PerformanceProfileSessionSnapshot"/> read back from disk. The file is
/// plain JSON under the user's AppData, so anything running as the user can edit it - and its
/// values are fed into elevated reg.exe/powercfg/PowerShell invocations on the next start. Each
/// check here restricts a field to the only shapes Windows itself ever produces.
/// </summary>
public static class ProfileSnapshotValidator
{
    /// <summary>The only values Microsoft documents for an MMCSS task's "Scheduling Category".</summary>
    private static readonly string[] SchedulingCategories = ["High", "Medium", "Low"];

    public static bool IsValidSchemeGuid(string? value) => Guid.TryParseExact(value, "D", out _);

    public static bool IsValidSchedulingCategory(string? value) =>
        value != null && Array.Exists(SchedulingCategories, c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows accepts 0-100 (percent of CPU reserved for non-multimedia work).</summary>
    public static bool IsValidSystemResponsiveness(int value) => value is >= 0 and <= 100;

    /// <summary>"GpuPreference=N;" is the only shape the Graphics Settings page writes.</summary>
    public static bool IsValidGpuPreferenceValue(string? value) =>
        value != null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^GpuPreference=\d;$");

    /// <summary>A rooted local path with no control characters - the exe path tweaks are keyed to.</summary>
    public static bool IsValidLocalExePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Any(char.IsControl)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        try
        {
            return Path.IsPathRooted(path) && Path.IsPathFullyQualified(path);
        }
        catch
        {
            // A path that cannot be parsed is not a usable one.
            return false;
        }
    }

    /// <summary>
    /// Removes every field that fails validation (clearing its Captured flag so nothing is
    /// restored from it) and returns a description of each removal for the log.
    /// </summary>
    public static List<string> Sanitize(PerformanceProfileSessionSnapshot snapshot)
    {
        var problems = new List<string>();

        if (snapshot.PowerPlanCaptured && !IsValidSchemeGuid(snapshot.PreviousPowerSchemeGuid))
        {
            problems.Add($"power scheme id '{snapshot.PreviousPowerSchemeGuid}' is not a GUID");
            snapshot.PowerPlanCaptured = false;
            snapshot.PreviousPowerSchemeGuid = null;
        }

        if (snapshot.SystemResponsivenessCaptured && snapshot.PreviousSystemResponsiveness is int sr && !IsValidSystemResponsiveness(sr))
        {
            problems.Add($"SystemResponsiveness {sr} is outside 0-100");
            snapshot.SystemResponsivenessCaptured = false;
            snapshot.PreviousSystemResponsiveness = null;
        }

        if (snapshot.SchedulingCategoryCaptured && snapshot.PreviousSchedulingCategory != null && !IsValidSchedulingCategory(snapshot.PreviousSchedulingCategory))
        {
            problems.Add($"Scheduling Category '{snapshot.PreviousSchedulingCategory}' is not High/Medium/Low");
            snapshot.SchedulingCategoryCaptured = false;
            snapshot.PreviousSchedulingCategory = null;
        }

        if (snapshot.ToastsCaptured && snapshot.PreviousToastsEnabled is int toasts && toasts is not (0 or 1))
        {
            problems.Add($"toast switch value {toasts} is not 0/1");
            snapshot.PreviousToastsEnabled = null;
        }

        // A timer request never survives the process that made it - nothing to recover.
        snapshot.TimerResolutionRequested = false;

        // Only settings a session writes, with values a session writes: the written value is what
        // restore compares against. The previous values are tokens, which read as "absent" when malformed.
        snapshot.NvidiaSettings ??= new List<NvidiaSettingSnapshot>();
        foreach (var record in snapshot.NvidiaSettings.ToList())
        {
            if (record == null
                || !PerformanceProfileService.NvidiaSessionSettings.TryGetValue(record.SettingId, out var known)
                || !known.IsValidWritten(record.Written))
            {
                problems.Add(record == null ? "an empty NVIDIA setting record" : $"NVIDIA setting 0x{record.SettingId:X8} = {record.Written} is not one TrayTrigger writes");
                snapshot.NvidiaSettings.Remove(record!);
            }
        }

        snapshot.PreviousHdrStates ??= new List<HdrDisplaySnapshot>();
        snapshot.PerGameSnapshots ??= new List<PerGameProfileSnapshot>();

        foreach (var perGame in snapshot.PerGameSnapshots)
        {
            if (perGame.GpuPreferenceCaptured)
            {
                if (!IsValidLocalExePath(perGame.GpuPreferenceExecutablePath))
                {
                    problems.Add($"GPU preference path '{perGame.GpuPreferenceExecutablePath}' is not a local file path");
                    perGame.GpuPreferenceCaptured = false;
                }
                else if (perGame.PreviousGpuPreferenceValue != null && !IsValidGpuPreferenceValue(perGame.PreviousGpuPreferenceValue))
                {
                    problems.Add($"GPU preference value '{perGame.PreviousGpuPreferenceValue}' is malformed");
                    perGame.GpuPreferenceCaptured = false;
                }
            }

            if (perGame.DefenderExclusionCaptured && !IsValidLocalExePath(perGame.DefenderExclusionPath))
            {
                problems.Add($"Defender exclusion path '{perGame.DefenderExclusionPath}' is not a local file path");
                perGame.DefenderExclusionCaptured = false;
            }
        }

        return problems;
    }
}
