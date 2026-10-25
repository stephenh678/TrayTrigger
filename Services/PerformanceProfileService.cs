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
public partial class PerformanceProfileService
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

    /// <summary>Set while a retry runs: what it still can't put back stays noted by the entry being retried, not by a second one.</summary>
    [ThreadStatic] private static bool _quietNotes;

    /// <param name="tried">False for what was left on record without asking for the administrator rights it needs.</param>
    private void NoteUnrestored(string what, Func<bool> retry, object? key = null, bool tried = true)
    {
        if (tried) LoggingService.Warn("PerformanceProfile", $"Could not put back {what}.");
        else LoggingService.Info("PerformanceProfile", $"Left {what} to be put back when TrayTrigger is allowed to ask for administrator rights.");
        if (_quietNotes) return;
        lock (_lock) _unrestored.Add((what, retry, key));
    }

    /// <summary>
    /// The snapshot adopted at start with what needs administrator rights still waiting on it, until a session applies an ADMIN tweak of
    /// its own. While it is the current one, what it holds is the user's to put back (the prompt at start and exit, or Restore Previous):
    /// a session that has no ADMIN tweaks of its own doesn't raise a prompt when it ends.
    /// </summary>
    private PerformanceProfileSessionSnapshot? _leftoverSnapshot;

    private bool IsLeftoverOnly(PerformanceProfileSessionSnapshot snapshot) => ReferenceEquals(snapshot, _leftoverSnapshot);

    /// <summary>
    /// The game whose launch is being worked on, so each tweak can say on its Played row what it did
    /// or why not (<see cref="LaunchRecord"/>). Set for the length of a Begin or End call, under the lock.
    /// </summary>
    private string? _noteGameId;
    private void Note(string text) { if (_noteGameId != null) LaunchRecord.Note(_noteGameId, LaunchRecord.Profile, text); }
    private void NoteGame(string text) { if (_noteGameId != null) LaunchRecord.Note(_noteGameId, LaunchRecord.Game, text); }
    /// <summary>A tweak that wasn't applied, and why: the row's Skipped line.</summary>
    private void Skipped(string text) => Note(LaunchRecord.SkippedPrefix + text);

    /// <summary>What was put back when the session ended, or couldn't be: the row's Put back line.</summary>
    private static void PutBack(string gameId, string text) => LaunchRecord.Note(gameId, LaunchRecord.Profile, LaunchRecord.PutBackPrefix + text);

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
    /// A setting the driver only lets an administrator change (Resizable BAR): it is made in the elevated step, under the same
    /// administrator prompt as the registry changes, and not in the session the others share.
    /// </summary>
    private static bool IsAdminNvidia(uint settingId) => NvidiaAdminOp.Allowed.ContainsKey(settingId);

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
        // What needs administrator rights waits for the user to say so (the prompt at start, or Restore Previous): a
        // prompt nobody expects, over a window that isn't even showing yet, is the one thing here that's worse than
        // leaving a harmless setting changed a little longer. Already elevated, nothing is waited for.
        bool elevated = _backend.IsElevated;
        RestoreGlobalTweaks(snapshot, skipElevated: !elevated);
        foreach (var perGame in snapshot.PerGameSnapshots.ToList())
        {
            RestoreGpuPreference(perGame);
            if (elevated) RestoreDefenderExclusion(perGame);
            else if (!perGame.DefenderExclusionCaptured || perGame.DefenderExclusionWasPreExisting) snapshot.PerGameSnapshots.Remove(perGame);
        }
        List<string> waiting = new();
        if (!IsEmpty(snapshot))
        {
            // What couldn't be put back stays on record (see RestoreNvidiaSettings). Adopted as this run's snapshot, so a
            // game started now records into it rather than writing a new file over it.
            if (elevated) snapshot.PerGameSnapshots.Clear();
            lock (_lock) _snapshot ??= snapshot;
            _store.SaveProfileSessionSnapshot(snapshot);
            if (!elevated)
            {
                waiting = NoteAdminLeftovers(snapshot);
                if (waiting.Count > 0) lock (_lock) _leftoverSnapshot = snapshot;
            }
        }
        else
        {
            _store.DeleteProfileSessionSnapshot();
        }

        List<string> failed;
        lock (_lock) failed = _unrestored.Skip(before).Select(u => u.What).Where(w => !waiting.Contains(w)).ToList();
        if (waiting.Count > 0)
        {
            ReportWaiting(waiting);
        }
        if (failed.Count > 0)
        {
            ReportUnrestored(failed, gameName: null);
        }
        else if (waiting.Count == 0)
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

    /// <summary>
    /// What a profile left changed because putting it back needs the user's permission, which TrayTrigger asks for
    /// itself (the prompt at start and exit) and Restore Previous does at once. Critical like a failed restore: it is
    /// still not back.
    /// </summary>
    private void ReportWaiting(IReadOnlyList<string> waiting)
    {
        string what = JoinList(waiting);
        var entry = ActivityService.Add(ActivityLevel.Critical,
            $"{Capitalize(what)} {(ItOrThem(waiting) == "them" ? "are" : "is")} still changed from a game",
            detail: $"Putting {ItOrThem(waiting)} back needs administrator rights, and TrayTrigger only asks when you say so. Restore Previous on this page does it.",
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
            _retryPass++;
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
    /// Drops what was noted for something whose record is no longer pending (an NVIDIA setting, the
    /// scheduler values, a Defender exclusion): a later restore put it back, or found it changed by
    /// someone else, so there is nothing left to offer Restore Previous for. Returns what was dropped.
    /// Call with the lock held, and only after the newly failed have been read off the end of the list.
    /// </summary>
    private List<string> TakeSettledEntries(PerformanceProfileSessionSnapshot snapshot)
    {
        bool Settled((string What, Func<bool> Retry, object? Key) entry) => entry.Key switch
        {
            NvidiaSettingSnapshot record => !snapshot.NvidiaSettings.Contains(record),
            PerGameProfileSnapshot perGame => !perGame.DefenderExclusionCaptured,
            _ when ReferenceEquals(entry.Key, SchedulerEntryKey) => !RegistryOnRecord(snapshot),
            _ => false,
        };

        var settled = _unrestored.Where(Settled).Select(u => u.What).Distinct().ToList();
        if (settled.Count > 0) _unrestored.RemoveAll(Settled);
        return settled;
    }

    internal static string JoinList(IReadOnlyList<string> items) => items.Count switch
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
        if (game.PerformanceProfile == PerformanceProfileMode.Off && game.Hdr != HdrMode.On && game.AutoHdr == HdrMode.ProfileDefault)
        {
            LoggingService.Verbose("PerformanceProfile", $"'{game.Name}': profile is Off, nothing applied.");
            LaunchRecord.Note(game.Id, LaunchRecord.Profile, "nothing changed");
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

            _noteGameId = game.Id;
            bool appliedAnything;
            try
            {
                // Persists the snapshot after each individual tweak below (not once at the end),
                // so a crash mid-sequence still leaves a recovery record for whatever was already
                // applied instead of leaving mutated system state with nothing on disk to undo it.
                appliedAnything = ApplyPreLaunchTweaks(game, settings, isFirstSession, _snapshot);

                // A later game that adds no per-exe tweaks of its own still relies on the machine-wide
                // ones the first session applied, so it joins the session: otherwise the first game
                // ending restores them while this one is still running.
                if (!appliedAnything && isFirstSession)
                {
                    LoggingService.Verbose("PerformanceProfile", game.PerformanceProfile == PerformanceProfileMode.Off
                        ? $"'{game.Name}' asked for HDR with its profile Off, but there was nothing to turn on - nothing to apply."
                        : $"'{game.Name}' requested {game.PerformanceProfile} but every applicable pre-launch tweak is disabled (or not resolvable for this launch type) - nothing to apply.");
                    Skipped("every tweak is off, or doesn't apply to this launch");
                    if (IsEmpty(_snapshot))
                    {
                        _snapshot = null;
                    }
                    return false;
                }
            }
            finally
            {
                _noteGameId = null;
            }

            _activeSessionKeys.Add(game.Id);
            _sessionInfo[game.Id] = (game.Name, game.PerformanceProfile, DateTime.UtcNow);
            LoggingService.Info("PerformanceProfile", $"Applied {game.PerformanceProfile} profile for '{game.Name}' (pre-launch).");
            return true;
        }
    }

    private static bool IsEmpty(PerformanceProfileSessionSnapshot s) =>
        s.PerGameSnapshots.Count == 0 && !s.PowerPlanCaptured && !s.SystemResponsivenessCaptured
        && !s.SchedulingCategoryCaptured && !s.HdrCaptured && !s.ToastsCaptured && !s.PlaybackMuteCaptured && !s.TimerResolutionRequested
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
        lock (_lock)
        {
            _noteGameId = game.Id;
            try
            {
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
            finally
            {
                _noteGameId = null;
            }
        }
    }

    /// <summary>
    /// Whether this game's session turns HDR on: the game's own choice when it has one, otherwise
    /// the Optimized profile's switch. A game on profile Off with HDR On gets HDR and nothing else.
    /// </summary>
    internal static bool WantsHdr(GameEntry game, AppSettings settings) => game.Hdr switch
    {
        HdrMode.On => true,
        HdrMode.Off => false,
        _ => game.PerformanceProfile != PerformanceProfileMode.Off && settings.OptimizedProfileTweaks.HdrEnabled,
    };

    /// <summary>
    /// What this game's session writes for Auto HDR: true for on, false for off, null to leave the
    /// game's own Windows setting alone. The game's choice wins; otherwise the Optimized profile's
    /// Enable Auto HDR switch, and a game on profile Off follows nothing.
    /// </summary>
    internal static bool? AutoHdrTarget(GameEntry game, AppSettings settings) => game.AutoHdr switch
    {
        HdrMode.On => true,
        HdrMode.Off => false,
        _ => game.PerformanceProfile != PerformanceProfileMode.Off && settings.OptimizedProfileTweaks.AutoHdrEnabled ? true : null,
    };

    /// <summary>The per-app Auto HDR number Windows stores for a game: odd is on. Its other bits vary by Windows build, so only the lowest one changes.</summary>
    internal static int AutoHdrValue(string? existingEntry, bool on)
    {
        int value = int.TryParse(DirectXGlobalSettings.GetToken(existingEntry, DirectXGlobalSettings.AutoHdr), out int current) && current > 1
            ? current
            : 2096;
        return on ? value | 1 : value & ~1;
    }

    /// <summary>
    /// Writes Auto HDR on or off into the game's own graphics entry (the same one GPU preference
    /// uses), keeping every other setting in it. The entry is captured once per game, so one restore
    /// puts the whole thing back.
    /// </summary>
    private bool ApplyAutoHdr(GameEntry game, AppSettings settings, PerGameProfileSnapshot perGame, string exePath)
    {
        bool? target = AutoHdrTarget(game, settings);
        if (target == null) return false;
        if (!_backend.SupportsAutoHdr)
        {
            Skipped("Auto HDR, Windows 11 only");
            return false;
        }
        // AppStatus=0 is how Windows keeps a game it has on its default graphics settings: nothing
        // written into that entry is applied, so say so and leave it alone.
        if (DirectXGlobalSettings.GetToken(_backend.GetGpuPreference(exePath), "AppStatus") == "0")
        {
            Skipped("Auto HDR, Windows has this game on its default graphics settings");
            return false;
        }
        try
        {
            if (!perGame.GpuPreferenceCaptured)
            {
                perGame.GpuPreferenceExecutablePath = exePath;
                perGame.PreviousGpuPreferenceValue = _backend.GetGpuPreference(exePath);
                perGame.GpuPreferenceCaptured = true;
            }
            string? current = _backend.GetGpuPreference(exePath);
            string updated = DirectXGlobalSettings.SetToken(current, DirectXGlobalSettings.AutoHdr, AutoHdrValue(current, target.Value).ToString());
            if (DirectXGlobalSettings.GetToken(updated, "AppStatus") == null) updated = DirectXGlobalSettings.SetToken(updated, "AppStatus", "1");
            _backend.SetGpuPreference(exePath, updated);
            LoggingService.Verbose("PerformanceProfile", $"Auto HDR: set {(target.Value ? "on" : "off")} for '{exePath}' (was '{perGame.PreviousGpuPreferenceValue ?? "unset"}').");
            if (game.AutoHdr == HdrMode.ProfileDefault) Note(target.Value ? "Auto HDR on" : "Auto HDR off");
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyAutoHdr failed: {ex.Message}");
            Skipped("Auto HDR couldn't be set");
            return false;
        }
    }

    /// <summary>The Auto HDR step for a session that has no per-game snapshot yet. Adds the snapshot when it applied.</summary>
    private bool ApplyAutoHdrStep(GameEntry game, AppSettings settings, PerformanceProfileSessionSnapshot snapshot, string? exePath)
    {
        if (AutoHdrTarget(game, settings) == null) return false;
        if (exePath == null)
        {
            Skipped("Auto HDR, the game's program isn't known for this launch type");
            return false;
        }
        var perGame = new PerGameProfileSnapshot { GameId = game.Id };
        if (!ApplyAutoHdr(game, settings, perGame, exePath)) return false;
        snapshot.PerGameSnapshots.Add(perGame);
        _store.SaveProfileSessionSnapshot(snapshot);
        return true;
    }

    private bool ApplyPreLaunchTweaks(GameEntry game, AppSettings settings, bool isFirstSession, PerformanceProfileSessionSnapshot snapshot)
    {
        bool applied = false;
        // Something changed: remember it, and keep what was there on disk before the next step.
        void Applied() { applied = true; _store.SaveProfileSessionSnapshot(snapshot); }
        bool aggressive = game.PerformanceProfile == PerformanceProfileMode.Aggressive;
        // Profile Off, HDR On: HDR is the whole session. Nothing below the HDR step applies.
        bool hdrOnly = game.PerformanceProfile == PerformanceProfileMode.Off;

        switch (game.Hdr)
        {
            case HdrMode.On: NoteGame("HDR on for this game"); break;
            case HdrMode.Off: NoteGame("HDR left off for this game"); break;
        }
        switch (game.AutoHdr)
        {
            case HdrMode.On: NoteGame("Auto HDR on for this game"); break;
            case HdrMode.Off: NoteGame("Auto HDR off for this game"); break;
        }

        // HDR is machine-wide and belongs to the first session; a later game can't add it.
        if (hdrOnly && !isFirstSession)
        {
            if (WantsHdr(game, settings))
            {
                LoggingService.Verbose("PerformanceProfile", $"'{game.Name}' asked for HDR with its profile Off, but another game's session already decided HDR - nothing to apply.");
                Skipped("HDR, as another game's session already decided it");
            }
            // Auto HDR is per game, so it does not wait for the first session.
            return ApplyAutoHdrStep(game, settings, snapshot, ResolveRealExecutablePath(game));
        }

        if (!isFirstSession) Skipped("the machine-wide tweaks, already on for another game's session");

        // --- Machine-wide (first session only) ---
        if (isFirstSession)
        {
            if (!hdrOnly && settings.OptimizedProfileTweaks.PowerPlanEnabled && ApplyPowerPlan(snapshot)) Applied();

            if (WantsHdr(game, settings) && ApplyHdr(snapshot)) Applied();

            if (hdrOnly)
            {
                return ApplyAutoHdrStep(game, settings, snapshot, ResolveRealExecutablePath(game)) || applied;
            }

            // The two HKLM tweaks are captured first and written together, so one administrator
            // prompt covers both. The snapshot is saved before the write for the same reason it
            // always was: whatever happens next, what was there is already on disk.
            var hklmChanges = new List<SystemTweaksService.RegFileEntry>(2);
            // Captured by an earlier put-back still waiting, not by this launch: those stay whatever happens below.
            bool responsivenessWasCaptured = snapshot.SystemResponsivenessCaptured;
            bool schedulingWasCaptured = snapshot.SchedulingCategoryCaptured;

            if (aggressive && settings.AggressiveProfileTweaks.SystemResponsivenessEnabled)
            {
                hklmChanges.Add(CaptureSystemResponsiveness(snapshot));
                Applied();
            }

            if (aggressive && settings.AggressiveProfileTweaks.MmcssGamesPriorityEnabled)
            {
                hklmChanges.Add(CaptureSchedulingCategory(snapshot));
                Applied();
            }

            // Resizable BAR is the one NVIDIA setting here that the driver only lets an administrator change. It goes in the same
            // elevated step as the registry changes, so the player still answers one prompt.
            var adminNvidia = new List<NvidiaAdminOp>(1);
            var resizableBar = PlanResizableBar(snapshot, settings, aggressive, adminNvidia);
            if (resizableBar != null) applied = true;

            if (hklmChanges.Count > 0 || adminNvidia.Count > 0)
            {
                // This session has ADMIN tweaks of its own, so its end asks for permission anyway, and puts back what was left too.
                _leftoverSnapshot = null;
                var result = _backend.ApplyAdminChanges(hklmChanges, adminNvidia);
                if (hklmChanges.Count > 0)
                {
                    if (result.RegistryOk) Note("Windows' multimedia scheduler settings");
                    else
                    {
                        Skipped("Windows' multimedia scheduler settings couldn't be set");
                        // Not written, so nothing to put back: without this the game's end asks for permission
                        // again to "restore" values that never changed. A helper still running may yet write them.
                        if (!result.Unfinished) ForgetUnwrittenSchedulerCaptures(snapshot, responsivenessWasCaptured, schedulingWasCaptured);
                    }
                }
                if (resizableBar != null) FinishResizableBar(game, snapshot, resizableBar, result);
            }

            if (settings.OptimizedProfileTweaks.DoNotDisturbEnabled)
            {
                ApplyDoNotDisturb(snapshot);
                Applied();
            }

            if (settings.OptimizedProfileTweaks.UnmuteAudioEnabled)
            {
                ApplyUnmuteAudio(snapshot);
                Applied();
            }

            if (ApplyNvidiaSettings(game, snapshot, settings))
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
        else if (settings.OptimizedProfileTweaks.GpuPreferenceEnabled)
        {
            Skipped("GPU preference, the game's program isn't known for this launch type");
        }

        if (AutoHdrTarget(game, settings) != null)
        {
            if (resolvedExePath == null) Skipped("Auto HDR, the game's program isn't known for this launch type");
            else
            {
                perGame ??= new PerGameProfileSnapshot { GameId = game.Id };
                if (ApplyAutoHdr(game, settings, perGame, resolvedExePath)) applied = true;
            }
        }

        if (aggressive && settings.AggressiveProfileTweaks.DefenderExclusionEnabled && resolvedExePath != null)
        {
            perGame ??= new PerGameProfileSnapshot { GameId = game.Id };
            _leftoverSnapshot = null;
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
            if (_snapshot == null)
            {
                PutBack(gameId, "nothing to put back");
                return true;
            }
            int before = _unrestored.Count;
            var snapshot = _snapshot;

            // Nothing of its own that needs administrator rights, with some left waiting from before: that is the user's to put back.
            bool leftoverOnly = IsLeftoverOnly(_snapshot);

            // Every record for the game: one adopted at start and the session's own are both about it.
            foreach (var perGame in _snapshot.PerGameSnapshots.Where(p => p.GameId == gameId).ToList())
            {
                if (leftoverOnly)
                {
                    RestoreGpuPreference(perGame);
                    if (DefenderOnRecord(perGame)) continue;
                }
                else
                {
                    RestorePerGameTweaks(perGame);
                }
                _snapshot.PerGameSnapshots.Remove(perGame);
            }

            if (_activeSessionKeys.Count == 0)
            {
                RestoreGlobalTweaks(_snapshot, skipElevated: leftoverOnly);
                if (!IsEmpty(_snapshot))
                {
                    // An NVIDIA setting or the scheduler values that couldn't be put back stay on record (see RestoreNvidiaSettings).
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
            settled = TakeSettledEntries(snapshot);

            PutBack(gameId, failed.Count > 0
                ? $"everything except {JoinList(failed)}, which couldn't be"
                : _activeSessionKeys.Count == 0 ? "everything" : "the game's own tweaks; the machine-wide ones stay for the game still running");
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
        else if (info.Mode != PerformanceProfileMode.Off)
        {
            putBack = info.Mode;
        }
        return true;
    }

    /// <summary>What still has to be put back, for the prompt before TrayTrigger closes or after it starts.</summary>
    /// <param name="GameRunning">A game is running, so closing puts back its changes, and those that need administrator rights ask for it.</param>
    /// <param name="Items">What would need the user's say-so (a game running), or what an earlier put-back left (none running).</param>
    public sealed record PutBackCheck(bool GameRunning, IReadOnlyList<string> Items);

    public PutBackCheck CheckPutBacks()
    {
        lock (_lock)
        {
            if (_activeSessionKeys.Count > 0 && _snapshot != null && !IsLeftoverOnly(_snapshot))
                return new PutBackCheck(true, _backend.IsElevated ? [] : AdminPutBacks(_snapshot));
            return new PutBackCheck(false, _unrestored.Select(u => u.What).Distinct().ToList());
        }
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
            ActivityService.Add(ActivityLevel.Problem, $"{Capitalize(JoinList(nextStart))} {(ItOrThem(nextStart) == "them" ? "were" : "was")} still changed when TrayTrigger closed",
                detail: $"TrayTrigger offers to put {ItOrThem(nextStart)} back the next time it starts.",
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
            // No game of its own that needs administrator rights: what is left on record is what an earlier put-back couldn't do, and the
            // next start asks again. Asking here too would be a second prompt for the same thing, straight after one that was just
            // declined or failed.
            bool deferElevated = (skipElevated || _activeSessionKeys.Count == 0 || IsLeftoverOnly(snapshot)) && !_backend.IsElevated;

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
            bool registryLeft = snapshot.SystemResponsivenessCaptured || snapshot.SchedulingCategoryCaptured;
            if (registryLeft && !deferElevated) nextStart.Add("Windows' multimedia scheduler settings");
            bool anythingDeferred = (deferElevated && (registryLeft || snapshot.PerGameSnapshots.Count > 0))
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

    /// <summary>
    /// The Graphics Settings page writes "Name=value;" tokens: GpuPreference=N; on its own, or with
    /// AutoHDREnable, AppStatus and others that Windows keeps in the same string.
    /// </summary>
    public static bool IsValidGpuPreferenceValue(string? value) =>
        value != null && System.Text.RegularExpressions.Regex.IsMatch(value, @"^(?:[A-Za-z][A-Za-z0-9]{0,63}=[0-9A-Za-z]{1,16};){1,16}$");

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
