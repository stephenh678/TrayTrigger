using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>Where the crash-recovery snapshot lives. <see cref="StorageService"/> in production.</summary>
public interface IProfileSnapshotStore
{
    PerformanceProfileSessionSnapshot? LoadProfileSessionSnapshot();
    void SaveProfileSessionSnapshot(PerformanceProfileSessionSnapshot snapshot);
    void DeleteProfileSessionSnapshot();
}

/// <summary>
/// Applies a per-game "Performance Profile" (Optimized/Aggressive) for the duration of a single
/// gaming session and restores the exact prior system state afterward - as opposed to
/// <see cref="SystemTweaksService"/>, which owns the permanent, always-on System &amp; Performance
/// tweaks. All machine access goes through <see cref="ISystemTweakBackend"/>; this class owns only
/// the capture/apply/restore orchestration.
///
/// Two kinds of tweak, two lifetimes:
/// - Machine-wide singletons (Power Plan, HDR, System Responsiveness, MMCSS Scheduling Category):
///   only the first tracked session captures/applies them ("first wins" - see
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
    private readonly Lock _lock = new();
    private readonly HashSet<string> _activeSessionKeys = new();
    private PerformanceProfileSessionSnapshot? _snapshot;

    public PerformanceProfileService(StorageService storageService)
        : this(storageService, () => storageService.LoadSettings(), new WindowsTweakBackend())
    {
    }

    public PerformanceProfileService(IProfileSnapshotStore store, Func<AppSettings> settingsProvider, ISystemTweakBackend backend)
    {
        _store = store;
        _settingsProvider = settingsProvider;
        _backend = backend;
    }

    /// <summary>Game IDs with a profile currently applied - for UI "Playing" state and tests.</summary>
    public IReadOnlyCollection<string> ActiveSessionGameIds
    {
        get { lock (_lock) { return _activeSessionKeys.ToArray(); } }
    }

    /// <summary>Call once at startup, before anything else could touch these same registry values.</summary>
    public void RecoverFromCrashIfNeeded()
    {
        var snapshot = _store.LoadProfileSessionSnapshot();
        if (snapshot == null) return;

        LoggingService.Warn("PerformanceProfile", "Found a leftover profile session snapshot from a previous run (likely an abnormal exit) - restoring pre-profile system state.");

        // The file is user-writable and its contents end up in elevated reg/powercfg/PowerShell
        // invocations: drop anything that isn't a well-formed value before restoring.
        foreach (var problem in ProfileSnapshotValidator.Sanitize(snapshot))
        {
            LoggingService.Warn("PerformanceProfile", $"Ignoring invalid entry in the crash-recovery snapshot: {problem}");
        }

        RestoreGlobalTweaks(snapshot, skipElevated: false);
        foreach (var perGame in snapshot.PerGameSnapshots)
        {
            RestorePerGameTweaks(perGame);
        }
        _store.DeleteProfileSessionSnapshot();
    }

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
        if (game.PerformanceProfile == PerformanceProfileMode.Off) return false;

        lock (_lock)
        {
            if (_activeSessionKeys.Contains(game.Id)) return false;

            var settings = _settingsProvider();
            bool isFirstSession = _activeSessionKeys.Count == 0;

            _snapshot ??= new PerformanceProfileSessionSnapshot();

            // Persists the snapshot after each individual tweak below (not once at the end),
            // so a crash mid-sequence still leaves a recovery record for whatever was already
            // applied instead of leaving mutated system state with nothing on disk to undo it.
            bool appliedAnything = ApplyPreLaunchTweaks(game, settings, isFirstSession, _snapshot);

            if (!appliedAnything)
            {
                LoggingService.Verbose("PerformanceProfile", $"'{game.Name}' requested {game.PerformanceProfile} but every applicable pre-launch tweak is disabled (or not resolvable for this launch type) - nothing to apply.");
                if (isFirstSession && IsEmpty(_snapshot))
                {
                    _snapshot = null;
                }
                return false;
            }

            _activeSessionKeys.Add(game.Id);
            LoggingService.Info("PerformanceProfile", $"Applied {game.PerformanceProfile} profile for '{game.Name}' (pre-launch).");
            return true;
        }
    }

    private static bool IsEmpty(PerformanceProfileSessionSnapshot s) =>
        s.PerGameSnapshots.Count == 0 && !s.PowerPlanCaptured && !s.SystemResponsivenessCaptured
        && !s.SchedulingCategoryCaptured && !s.HdrCaptured && !s.ToastsCaptured && !s.TimerResolutionRequested;

    /// <summary>
    /// POST-START phase. Applies the tweaks that need the actual game process. These need no
    /// snapshot or restore - they die with the process - so this doesn't touch session tracking
    /// and is safe to call even when <see cref="BeginGameSession"/> applied nothing.
    /// </summary>
    public void OnGameProcessStarted(GameEntry game, Process process)
    {
        if (game.PerformanceProfile == PerformanceProfileMode.Off) return;

        var settings = _settingsProvider();
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
            if (settings.OptimizedProfileTweaks.PowerPlanEnabled)
            {
                ApplyPowerPlan(snapshot);
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (settings.OptimizedProfileTweaks.HdrEnabled && ApplyHdr(snapshot))
            {
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (aggressive && settings.AggressiveProfileTweaks.SystemResponsivenessEnabled)
            {
                ApplySystemResponsiveness(snapshot);
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
            }

            if (aggressive && settings.AggressiveProfileTweaks.MmcssGamesPriorityEnabled)
            {
                ApplySchedulingCategory(snapshot);
                applied = true;
                _store.SaveProfileSessionSnapshot(snapshot);
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
    public bool EndGameSession(string gameId)
    {
        lock (_lock)
        {
            if (!_activeSessionKeys.Remove(gameId)) return false;
            if (_snapshot == null) return true;

            var perGame = _snapshot.PerGameSnapshots.FirstOrDefault(p => p.GameId == gameId);
            if (perGame != null)
            {
                RestorePerGameTweaks(perGame);
                _snapshot.PerGameSnapshots.Remove(perGame);
            }

            if (_activeSessionKeys.Count == 0)
            {
                RestoreGlobalTweaks(_snapshot, skipElevated: false);
                _snapshot = null;
                _store.DeleteProfileSessionSnapshot();
                LoggingService.Info("PerformanceProfile", "All tracked game sessions ended; restored pre-profile system state.");
            }
            else
            {
                _store.SaveProfileSessionSnapshot(_snapshot);
            }
            return true;
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
        lock (_lock)
        {
            if (_snapshot == null) return;

            bool deferElevated = skipElevated && !_backend.IsElevated;

            RestoreGlobalTweaks(_snapshot, deferElevated);
            foreach (var perGame in _snapshot.PerGameSnapshots.ToList())
            {
                RestoreGpuPreference(perGame);
                if (!deferElevated)
                {
                    RestoreDefenderExclusion(perGame);
                }
                else if (!perGame.DefenderExclusionCaptured || perGame.DefenderExclusionWasPreExisting)
                {
                    _snapshot.PerGameSnapshots.Remove(perGame);
                }
            }

            bool anythingDeferred = deferElevated && (_snapshot.SystemResponsivenessCaptured || _snapshot.SchedulingCategoryCaptured || _snapshot.PerGameSnapshots.Count > 0);

            _activeSessionKeys.Clear();
            if (anythingDeferred)
            {
                _store.SaveProfileSessionSnapshot(_snapshot);
                LoggingService.Info("PerformanceProfile", "Restored the non-elevated parts of the active profile at shutdown; elevated tweaks will be restored on next start.");
            }
            else
            {
                _store.DeleteProfileSessionSnapshot();
                LoggingService.Info("PerformanceProfile", "Restored pre-profile system state on application exit.");
            }
            _snapshot = null;
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

    private void ApplyPowerPlan(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousPowerSchemeGuid = _backend.GetActivePowerSchemeGuid();
        snapshot.PowerPlanCaptured = true;

        if (!_backend.ActivateUltimatePowerPlan())
        {
            LoggingService.Warn("PerformanceProfile", "Could not create or locate the 'Ultimate Plan - TrayTrigger' power scheme.");
            return;
        }
        LoggingService.Verbose("PerformanceProfile", $"Power Plan: switched active scheme to 'Ultimate Plan - TrayTrigger' (was {snapshot.PreviousPowerSchemeGuid ?? "unknown"}).");
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
        _backend.SetActivePowerScheme(snapshot.PreviousPowerSchemeGuid);
        LoggingService.Verbose("PerformanceProfile", $"Power Plan: restored active scheme to {snapshot.PreviousPowerSchemeGuid}.");
    }

    private void ApplySystemResponsiveness(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousSystemResponsiveness = _backend.ReadHklmDword(SystemResponsivenessPath, "SystemResponsiveness");
        snapshot.SystemResponsivenessCaptured = true;

        // Microsoft's MMCSS docs: values below 10 are clamped back up to 20, so 10 is the
        // lowest reserve Windows actually honors.
        _backend.WriteHklmDword(SystemResponsivenessPath, "SystemResponsiveness", 10);
        LoggingService.Verbose("PerformanceProfile", $"System Responsiveness: set to 10 (was {snapshot.PreviousSystemResponsiveness?.ToString() ?? "unset"}).");
    }

    private void RestoreSystemResponsiveness(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.SystemResponsivenessCaptured) return;

        if (snapshot.PreviousSystemResponsiveness.HasValue)
        {
            _backend.WriteHklmDword(SystemResponsivenessPath, "SystemResponsiveness", snapshot.PreviousSystemResponsiveness.Value);
        }
        else
        {
            _backend.DeleteHklmValue(SystemResponsivenessPath, "SystemResponsiveness");
        }
        LoggingService.Verbose("PerformanceProfile", $"System Responsiveness: restored to {snapshot.PreviousSystemResponsiveness?.ToString() ?? "unset"}.");
    }

    private void ApplySchedulingCategory(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousSchedulingCategory = _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category");
        snapshot.SchedulingCategoryCaptured = true;

        // SFIO Priority is intentionally not written - Microsoft's MMCSS docs state it "is not used".
        _backend.WriteHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", "High");
        LoggingService.Verbose("PerformanceProfile", $"MMCSS Scheduling Category: set to 'High' (was '{snapshot.PreviousSchedulingCategory ?? "unset"}').");
    }

    private void RestoreSchedulingCategory(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.SchedulingCategoryCaptured) return;

        if (snapshot.PreviousSchedulingCategory != null)
        {
            if (!ProfileSnapshotValidator.IsValidSchedulingCategory(snapshot.PreviousSchedulingCategory))
            {
                LoggingService.Warn("PerformanceProfile", $"MMCSS Scheduling Category: not restoring - captured value '{snapshot.PreviousSchedulingCategory}' is not one Windows defines.");
                return;
            }
            _backend.WriteHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", snapshot.PreviousSchedulingCategory);
        }
        else
        {
            _backend.DeleteHklmValue(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category");
        }
        LoggingService.Verbose("PerformanceProfile", $"MMCSS Scheduling Category: restored to '{snapshot.PreviousSchedulingCategory ?? "unset"}'.");
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
            _backend.SetDefaultPlaybackMuted(snapshot.PreviousPlaybackMuted);
            LoggingService.Info("PerformanceProfile", $"Unmute audio: playback device restored to {(snapshot.PreviousPlaybackMuted ? "muted" : "unmuted")}.");
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

        if (skipElevated && !_backend.IsElevated)
        {
            return;
        }
        RestoreSystemResponsiveness(snapshot);
        snapshot.SystemResponsivenessCaptured = false;
        RestoreSchedulingCategory(snapshot);
        snapshot.SchedulingCategoryCaptured = false;
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
            var adapterId = new HdrControlService.LUID { LowPart = s.AdapterIdLowPart, HighPart = s.AdapterIdHighPart };
            bool ok = _backend.SetDisplayHdrEnabled(adapterId, s.TargetId, s.WasEnabled);

            // Name the mode the display goes back to, not just the HDR bit. "HDR state to Off"
            // read as though a display that had been in WCG was being dropped to plain SDR; it
            // is not - clearing HDR returns it to whichever non-HDR mode Auto Color Management
            // had it in.
            string restoredTo = s.WasEnabled ? "HDR" : s.WasWcg ? "WCG" : "SDR";
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
            bool alreadyExcluded = _backend.GetDefenderExclusionPaths().Contains(exePath, StringComparer.OrdinalIgnoreCase);
            snapshot.DefenderExclusionPath = exePath;
            snapshot.DefenderExclusionWasPreExisting = alreadyExcluded;
            snapshot.DefenderExclusionCaptured = true;

            if (!alreadyExcluded)
            {
                _backend.AddDefenderExclusion(exePath);
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

        try
        {
            _backend.RemoveDefenderExclusion(snapshot.DefenderExclusionPath);
            LoggingService.Verbose("PerformanceProfile", $"Defender Exclusion: removed '{snapshot.DefenderExclusionPath}'.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreDefenderExclusion failed: {ex.Message}");
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
