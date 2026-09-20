using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Session semantics of <see cref="PerformanceProfileService"/> against a recording fake backend:
/// first-wins for machine-wide tweaks, per-game restore, duplicate Begin, untracked End, deferred
/// elevated restore at Windows shutdown, and validation on crash recovery.
/// </summary>
public class PerformanceProfileServiceTests : IDisposable
{
    private sealed class FakeStore : IProfileSnapshotStore
    {
        public PerformanceProfileSessionSnapshot? OnDisk;
        public int Saves, Deletes;
        public PerformanceProfileSessionSnapshot? LoadProfileSessionSnapshot() => OnDisk;
        public void SaveProfileSessionSnapshot(PerformanceProfileSessionSnapshot snapshot) { OnDisk = snapshot; Saves++; }
        public void DeleteProfileSessionSnapshot() { OnDisk = null; Deletes++; }
    }

    private sealed class FakeBackend : ISystemTweakBackend
    {
        public bool IsElevated { get; set; }
        public string ActiveScheme = "381b4222-f694-41f0-9685-ff5bb260df2e";
        public readonly Dictionary<string, object?> Hklm = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> GpuPrefs = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Defender = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Log = new();

        public string? GetActivePowerSchemeGuid() => ActiveScheme;
        public bool ActivateUltimatePowerPlan() { ActiveScheme = "ultimate"; Log.Add("power:ultimate"); return true; }
        public void SetActivePowerScheme(string schemeGuid) { ActiveScheme = schemeGuid; Log.Add("power:" + schemeGuid); }

        public int? ReadHklmDword(string subKey, string valueName) => Hklm.GetValueOrDefault(subKey + "|" + valueName) as int?;
        public string? ReadHklmString(string subKey, string valueName) => Hklm.GetValueOrDefault(subKey + "|" + valueName) as string;
        /// <summary>Models a declined UAC prompt: the write fails and nothing changes.</summary>
        public bool RefuseHklmWrites;
        public bool WriteHklmDword(string subKey, string valueName, int value)
        {
            if (RefuseHklmWrites) { Log.Add($"hklm:{valueName}=<refused>"); return false; }
            Hklm[subKey + "|" + valueName] = value; Log.Add($"hklm:{valueName}={value}"); return true;
        }
        public bool WriteHklmString(string subKey, string valueName, string value) { Hklm[subKey + "|" + valueName] = value; Log.Add($"hklm:{valueName}={value}"); return true; }
        public bool DeleteHklmValue(string subKey, string valueName)
        {
            if (RefuseHklmWrites) { Log.Add($"hklm:{valueName}=<refused>"); return false; }
            Hklm.Remove(subKey + "|" + valueName); Log.Add($"hklm:{valueName}=<deleted>"); return true;
        }

        public readonly List<HdrControlService.DisplayColorState> HdrDisplays = new();
        public List<HdrControlService.DisplayColorState> GetHdrDisplayStates() => new(HdrDisplays);
        public bool SetDisplayHdrEnabled(HdrControlService.LUID adapterId, uint targetId, bool enable)
        {
            Log.Add($"hdr:{targetId}={(enable ? "on" : "off")}");
            return true;
        }

        public string? GetGpuPreference(string exePath) => GpuPrefs.GetValueOrDefault(exePath);
        public void SetGpuPreference(string exePath, string value) { GpuPrefs[exePath] = value; Log.Add($"gpu:{Path.GetFileName(exePath)}={value}"); }
        public void DeleteGpuPreference(string exePath) { GpuPrefs.Remove(exePath); Log.Add($"gpu:{Path.GetFileName(exePath)}=<deleted>"); }

        public bool AddDefenderExclusion(string exePath)
        {
            if (!Defender.Add(exePath)) return false;
            Log.Add("defender:+" + Path.GetFileName(exePath));
            return true;
        }
        public bool RemoveDefenderExclusion(string exePath) { Defender.Remove(exePath); Log.Add("defender:-" + Path.GetFileName(exePath)); return true; }

        public void SetProcessPriority(Process process, ProcessPriorityClass priority) => Log.Add("priority:" + priority);

        public bool TimerHeld;
        public uint RequestHighTimerResolution() { TimerHeld = true; Log.Add("timer:request"); return 5000; }
        public void ReleaseHighTimerResolution() { TimerHeld = false; Log.Add("timer:release"); }

        public int? Toasts;
        public int? GetToastsEnabled() => Toasts;
        public void SetToastsEnabled(int? value) { Toasts = value; Log.Add("toasts:" + (value?.ToString() ?? "unset")); }

        /// <summary>null models a machine with no playback device at all.</summary>
        public bool? PlaybackMuted;
        public bool? GetDefaultPlaybackMuted() => PlaybackMuted;
        public bool SetDefaultPlaybackMuted(bool muted)
        {
            if (PlaybackMuted == null) return false;
            PlaybackMuted = muted;
            Log.Add("mute:" + muted);
            return true;
        }
    }

    private readonly string _dir;
    private readonly string _exeA;
    private readonly string _exeB;
    private readonly FakeStore _store = new();
    private readonly FakeBackend _backend = new();
    private readonly AppSettings _settings = new();
    private readonly PerformanceProfileService _service;

    public PerformanceProfileServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TrayTriggerProfileTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _exeA = Path.Combine(_dir, "a.exe");
        _exeB = Path.Combine(_dir, "b.exe");
        File.WriteAllText(_exeA, "");
        File.WriteAllText(_exeB, "");
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = true;
        _service = new PerformanceProfileService(_store, () => _settings, _backend);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private GameEntry Game(string id, string exe, PerformanceProfileMode mode) =>
        new() { Id = id, Name = id, ExecutablePath = exe, PerformanceProfile = mode };

    [Fact]
    public void Off_AppliesNothing()
    {
        Assert.False(_service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Off)));
        Assert.Empty(_backend.Log);
        Assert.Null(_store.OnDisk);
        Assert.Empty(_service.ActiveSessionGameIds);
    }

    [Fact]
    public void Optimized_AppliesPowerPlanAndGpuPreference_ThenRestores()
    {
        var game = Game("g", _exeA, PerformanceProfileMode.Optimized);

        Assert.True(_service.BeginGameSession(game));
        Assert.Equal("ultimate", _backend.ActiveScheme);
        Assert.Equal("GpuPreference=2;", _backend.GpuPrefs[_exeA]);
        Assert.NotNull(_store.OnDisk);
        Assert.Contains("g", _service.ActiveSessionGameIds);

        Assert.True(_service.EndGameSession("g"));
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", _backend.ActiveScheme);
        Assert.False(_backend.GpuPrefs.ContainsKey(_exeA));
        Assert.Null(_store.OnDisk);
        Assert.Empty(_service.ActiveSessionGameIds);
    }

    [Fact]
    public void Aggressive_AppliesMachineWideTweaks_AndRestoresPriorValues()
    {
        _backend.Hklm[PerformanceProfileService.SystemResponsivenessPath + "|SystemResponsiveness"] = 20;
        var game = Game("g", _exeA, PerformanceProfileMode.Aggressive);

        _service.BeginGameSession(game);
        Assert.Equal(10, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.Equal("High", _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
        Assert.Contains(_exeA, _backend.Defender);

        _service.EndGameSession("g");
        Assert.Equal(20, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        // Was absent before -> deleted, not written back as some default.
        Assert.Null(_backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
        Assert.DoesNotContain(_exeA, _backend.Defender);
    }

    [Fact]
    public void PreExistingDefenderExclusion_IsLeftAlone()
    {
        _backend.Defender.Add(_exeA);
        var game = Game("g", _exeA, PerformanceProfileMode.Aggressive);
        _service.BeginGameSession(game);
        _service.EndGameSession("g");
        Assert.Contains(_exeA, _backend.Defender);
        Assert.DoesNotContain("defender:-a.exe", _backend.Log);
    }

    [Fact]
    public void SecondGameWithNoPerExeTweaks_KeepsMachineWideTweaksUntilItEnds()
    {
        Assert.True(_service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized)));
        Assert.True(_service.BeginGameSession(Game("b", "steam://rungameid/10", PerformanceProfileMode.Optimized)));

        _service.EndGameSession("a");
        Assert.Equal("ultimate", _backend.ActiveScheme);

        _service.EndGameSession("b");
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", _backend.ActiveScheme);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void TwoSessions_FirstWinsMachineWide_LastRestores_PerGameIndependent()
    {
        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        int powerApplies = _backend.Log.Count(l => l == "power:ultimate");
        _service.BeginGameSession(Game("b", _exeB, PerformanceProfileMode.Optimized));

        Assert.Equal(powerApplies, _backend.Log.Count(l => l == "power:ultimate")); // not re-applied
        Assert.True(_backend.GpuPrefs.ContainsKey(_exeA));
        Assert.True(_backend.GpuPrefs.ContainsKey(_exeB));

        _service.EndGameSession("a");
        Assert.False(_backend.GpuPrefs.ContainsKey(_exeA));  // a's own tweak restored now
        Assert.True(_backend.GpuPrefs.ContainsKey(_exeB));
        Assert.Equal("ultimate", _backend.ActiveScheme);       // machine-wide stays until b ends
        Assert.NotNull(_store.OnDisk);

        _service.EndGameSession("b");
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", _backend.ActiveScheme);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void DuplicateBegin_IsNoOp_AndUntrackedEnd_IsFalse()
    {
        var game = Game("g", _exeA, PerformanceProfileMode.Optimized);
        Assert.True(_service.BeginGameSession(game));
        int logCount = _backend.Log.Count;
        Assert.False(_service.BeginGameSession(game));
        Assert.Equal(logCount, _backend.Log.Count);

        Assert.False(_service.EndGameSession("never-started"));
        Assert.True(_service.EndGameSession("g"));
        Assert.False(_service.EndGameSession("g"));
    }

    [Fact]
    public void SteamUrlPath_SkipsPerExeTweaks_ButStillAppliesMachineWide()
    {
        var game = Game("g", "steam://rungameid/440", PerformanceProfileMode.Optimized);
        Assert.True(_service.BeginGameSession(game));
        Assert.Empty(_backend.GpuPrefs);
        Assert.Equal("ultimate", _backend.ActiveScheme);
        _service.EndGameSession("g");
    }

    [Fact]
    public void ShutdownWithSkipElevated_RestoresNonElevated_DefersHklmAndDefender()
    {
        _backend.IsElevated = false;
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));

        _service.RestoreActiveSessionOnShutdown(skipElevated: true);

        // Power plan and HKCU GPU preference need no UAC and are restored right away...
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", _backend.ActiveScheme);
        Assert.False(_backend.GpuPrefs.ContainsKey(_exeA));
        // ...the HKLM values and the Defender exclusion are not touched...
        Assert.Equal(10, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.Contains(_exeA, _backend.Defender);
        // ...and the snapshot stays on disk with exactly those still captured, for the next start.
        Assert.NotNull(_store.OnDisk);
        Assert.False(_store.OnDisk!.PowerPlanCaptured);
        Assert.True(_store.OnDisk.SystemResponsivenessCaptured);
        Assert.True(_store.OnDisk.SchedulingCategoryCaptured);
        Assert.Single(_store.OnDisk.PerGameSnapshots);
        Assert.False(_store.OnDisk.PerGameSnapshots[0].GpuPreferenceCaptured);
        Assert.True(_store.OnDisk.PerGameSnapshots[0].DefenderExclusionCaptured);
        Assert.Empty(_service.ActiveSessionGameIds);

        // Next start: recovery finishes the job.
        var next = new PerformanceProfileService(_store, () => _settings, _backend);
        next.RecoverFromCrashIfNeeded();
        Assert.Null(_backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.DoesNotContain(_exeA, _backend.Defender);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void ShutdownWhenElevated_RestoresEverythingEvenWithSkipElevated()
    {
        _backend.IsElevated = true;
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        _service.RestoreActiveSessionOnShutdown(skipElevated: true);
        Assert.Null(_backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.DoesNotContain(_exeA, _backend.Defender);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void Aggressive_HoldsTimerResolution_UntilLastSessionEnds()
    {
        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Aggressive));
        Assert.True(_backend.TimerHeld);
        int requests = _backend.Log.Count(l => l == "timer:request");

        _service.BeginGameSession(Game("b", _exeB, PerformanceProfileMode.Aggressive));
        Assert.Equal(requests, _backend.Log.Count(l => l == "timer:request")); // first wins

        _service.EndGameSession("a");
        Assert.True(_backend.TimerHeld);                                        // b still running
        _service.EndGameSession("b");
        Assert.False(_backend.TimerHeld);
    }

    [Fact]
    public void Optimized_DoesNotRequestTimer_WhenTierIsOptimized()
    {
        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        Assert.False(_backend.TimerHeld);
        _service.EndGameSession("a");
    }

    [Fact]
    public void DoNotDisturb_TurnsToastsOff_AndRestoresPriorValue()
    {
        _settings.OptimizedProfileTweaks.DoNotDisturbEnabled = true;
        _backend.Toasts = null; // Windows default: value absent = on

        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        Assert.Equal(0, _backend.Toasts);
        Assert.True(_store.OnDisk!.ToastsCaptured);
        Assert.Null(_store.OnDisk.PreviousToastsEnabled);

        _service.EndGameSession("a");
        Assert.Null(_backend.Toasts); // restored to "absent", not forced to 1
    }

    [Fact]
    public void DoNotDisturb_UserAlreadyOff_StaysOff()
    {
        _settings.OptimizedProfileTweaks.DoNotDisturbEnabled = true;
        _backend.Toasts = 0;
        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        _service.EndGameSession("a");
        Assert.Equal(0, _backend.Toasts);
    }

    private static HdrControlService.DisplayColorState Display(uint targetId, bool enabled, bool wcg = false) =>
        new(new HdrControlService.LUID { LowPart = 1, HighPart = 0 }, targetId, Supported: true, Enabled: enabled, IsWcg: wcg);

    /// <summary>
    /// Writing the HDR bit makes Windows re-negotiate the display mode, which blanks most monitors
    /// for a second or two. A display that was already in HDR was never changed, so re-asserting it
    /// on exit bought nothing and cost a blank screen after every single game.
    /// </summary>
    [Fact]
    public void Hdr_DisplayAlreadyOn_IsNeverTouched()
    {
        _settings.OptimizedProfileTweaks.HdrEnabled = true;
        _backend.HdrDisplays.Add(Display(4355, enabled: true));

        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        _service.EndGameSession("a");

        Assert.DoesNotContain(_backend.Log, l => l.StartsWith("hdr:"));
    }

    [Fact]
    public void Hdr_DisplayOff_IsEnabledThenPutBack()
    {
        _settings.OptimizedProfileTweaks.HdrEnabled = true;
        _backend.HdrDisplays.Add(Display(4355, enabled: false));

        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        Assert.Contains("hdr:4355=on", _backend.Log);

        _service.EndGameSession("a");
        Assert.Contains("hdr:4355=off", _backend.Log);
    }

    /// <summary>
    /// A mixed setup must not be all-or-nothing: the off display is driven, the on one is left be.
    /// </summary>
    [Fact]
    public void Hdr_MixedDisplays_OnlyTheOffOneIsDriven()
    {
        _settings.OptimizedProfileTweaks.HdrEnabled = true;
        _backend.HdrDisplays.Add(Display(4353, enabled: true));
        _backend.HdrDisplays.Add(Display(4355, enabled: false));

        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        _service.EndGameSession("a");

        Assert.Equal(new[] { "hdr:4355=on", "hdr:4355=off" }, _backend.Log.Where(l => l.StartsWith("hdr:")).ToArray());
    }

    [Fact]
    public void UnmuteAudio_MutedDevice_IsUnmutedThenRemuted()
    {
        _settings.OptimizedProfileTweaks.UnmuteAudioEnabled = true;
        _backend.PlaybackMuted = true;

        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        Assert.False(_backend.PlaybackMuted);
        Assert.True(_store.OnDisk!.PlaybackMuteCaptured);
        Assert.True(_store.OnDisk.PreviousPlaybackMuted);

        _service.EndGameSession("a");
        Assert.True(_backend.PlaybackMuted);
    }

    /// <summary>
    /// The usual case. Nothing is written and nothing is captured, so the exit restore has nothing
    /// to put back - a device the user unmuted mid-session is left alone.
    /// </summary>
    [Fact]
    public void UnmuteAudio_DeviceAlreadyUnmuted_TouchesNothing()
    {
        _settings.OptimizedProfileTweaks.UnmuteAudioEnabled = true;
        _backend.PlaybackMuted = false;

        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        Assert.DoesNotContain(_backend.Log, l => l.StartsWith("mute:"));
        Assert.False(_store.OnDisk!.PlaybackMuteCaptured);

        _backend.PlaybackMuted = true; // user muted while playing
        _service.EndGameSession("a");
        Assert.True(_backend.PlaybackMuted);
    }

    [Fact]
    public void UnmuteAudio_NoPlaybackDevice_IsNotAnError()
    {
        _settings.OptimizedProfileTweaks.UnmuteAudioEnabled = true;
        _backend.PlaybackMuted = null;

        Assert.True(_service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized)));
        Assert.False(_store.OnDisk!.PlaybackMuteCaptured);
        _service.EndGameSession("a");
    }

    /// <summary>Opt-in: picking Optimized on its own must not start touching the speakers.</summary>
    [Fact]
    public void UnmuteAudio_Disabled_LeavesTheDeviceMuted()
    {
        _backend.PlaybackMuted = true;
        _service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Optimized));
        Assert.True(_backend.PlaybackMuted);
        Assert.DoesNotContain(_backend.Log, l => l.StartsWith("mute:"));
        _service.EndGameSession("a");
    }

    [Fact]
    public void CrashRecovery_RestoresPlaybackMute()
    {
        _backend.PlaybackMuted = false;
        _store.OnDisk = new PerformanceProfileSessionSnapshot
        {
            PlaybackMuteCaptured = true,
            PreviousPlaybackMuted = true
        };
        _service.RecoverFromCrashIfNeeded();
        Assert.True(_backend.PlaybackMuted);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void CrashRecovery_RestoresToasts_AndIgnoresStaleTimerFlag()
    {
        _store.OnDisk = new PerformanceProfileSessionSnapshot
        {
            ToastsCaptured = true,
            PreviousToastsEnabled = 1,
            TimerResolutionRequested = true
        };
        _service.RecoverFromCrashIfNeeded();
        Assert.Equal(1, _backend.Toasts);
        Assert.DoesNotContain("timer:release", _backend.Log);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void CrashRecovery_RestoresRecordedValues()
    {
        _store.OnDisk = new PerformanceProfileSessionSnapshot
        {
            PowerPlanCaptured = true,
            PreviousPowerSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            SystemResponsivenessCaptured = true,
            PreviousSystemResponsiveness = 20,
            SchedulingCategoryCaptured = true,
            PreviousSchedulingCategory = "Medium",
            PerGameSnapshots = { new PerGameProfileSnapshot { GameId = "x", GpuPreferenceCaptured = true, GpuPreferenceExecutablePath = _exeA, PreviousGpuPreferenceValue = "GpuPreference=1;" } }
        };

        _service.RecoverFromCrashIfNeeded();

        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", _backend.ActiveScheme);
        Assert.Equal(20, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.Equal("Medium", _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
        Assert.Equal("GpuPreference=1;", _backend.GpuPrefs[_exeA]);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void CrashRecovery_NeverWritesTamperedValues()
    {
        _store.OnDisk = new PerformanceProfileSessionSnapshot
        {
            PowerPlanCaptured = true,
            PreviousPowerSchemeGuid = "not-a-guid /delete",
            SchedulingCategoryCaptured = true,
            PreviousSchedulingCategory = "\" & calc & \"",
            PerGameSnapshots = { new PerGameProfileSnapshot { GameId = "x", DefenderExclusionCaptured = true, DefenderExclusionPath = @"\\attacker\share\x.exe" } }
        };

        _service.RecoverFromCrashIfNeeded();

        Assert.DoesNotContain(_backend.Log, l => l.StartsWith("power:"));
        Assert.DoesNotContain(_backend.Log, l => l.StartsWith("hklm:"));
        Assert.DoesNotContain(_backend.Log, l => l.StartsWith("defender:"));
        Assert.Null(_store.OnDisk); // the poisoned file is still consumed so it can't be replayed
    }

    // --- The DLSS on-screen overlay (step 8) ------------------------------------------------

    private const string NgxKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore|ShowDlssIndicator";

    private GameEntry OverlayGame(string id, string exe, PerformanceProfileMode mode = PerformanceProfileMode.Off)
    {
        var game = Game(id, exe, mode);
        game.DlssShowOverlay = true;
        return game;
    }

    [Fact]
    public void Overlay_TurnsOnEvenWithNoPerformanceProfile()
    {
        // It is not a profile tweak: a user can want the indicator on a game with no profile at
        // all, and the session must start anyway.
        Assert.True(_service.BeginGameSession(OverlayGame("g", _exeA)));

        Assert.Equal(0x400, _backend.Hklm[NgxKey]);
    }

    [Fact]
    public void Overlay_IsRemovedOnSessionEnd_BecauseItWasAbsentBefore()
    {
        // Absent before means restoring is a delete. Writing 0 would leave a value NVIDIA never had.
        _service.BeginGameSession(OverlayGame("g", _exeA));
        _service.EndGameSession("g");

        Assert.False(_backend.Hklm.ContainsKey(NgxKey));
    }

    [Fact]
    public void Overlay_PutsBackAValueTheUserAlreadyHad()
    {
        _backend.Hklm[NgxKey] = 1;

        _service.BeginGameSession(OverlayGame("g", _exeA));
        Assert.Equal(0x400, _backend.Hklm[NgxKey]);

        _service.EndGameSession("g");
        Assert.Equal(1, _backend.Hklm[NgxKey]);
    }

    [Fact]
    public void Overlay_StaysOnWhileASecondGameThatWantedItIsStillRunning()
    {
        _service.BeginGameSession(OverlayGame("a", _exeA));
        _service.BeginGameSession(OverlayGame("b", _exeB));

        _service.EndGameSession("a");
        Assert.Equal(0x400, _backend.Hklm[NgxKey]);

        _service.EndGameSession("b");
        Assert.False(_backend.Hklm.ContainsKey(NgxKey));
    }

    [Fact]
    public void Overlay_EndsWithTheGameThatWantedIt_NotWithTheLastSessionOfAnyKind()
    {
        // The whole justification for the overlay is that it is scoped to one game. A second game
        // running alongside must not keep it on, or inherit it.
        _service.BeginGameSession(OverlayGame("overlay", _exeA));
        _service.BeginGameSession(Game("plain", _exeB, PerformanceProfileMode.Optimized));

        _service.EndGameSession("overlay");

        Assert.False(_backend.Hklm.ContainsKey(NgxKey));
        Assert.Contains("plain", _service.ActiveSessionGameIds);
    }

    [Fact]
    public void Overlay_CapturesOnlyOnce_SoASecondGameCannotRecordOurOwnValueAsThePrevious()
    {
        // If the second Begin captured, it would record 0x400 - TrayTrigger's own write - as the
        // value to restore, and the indicator would be left on forever.
        _service.BeginGameSession(OverlayGame("a", _exeA));
        _service.BeginGameSession(OverlayGame("b", _exeB));
        _service.EndGameSession("a");
        _service.EndGameSession("b");

        Assert.False(_backend.Hklm.ContainsKey(NgxKey));
    }

    [Fact]
    public void Overlay_SurvivesACrash_AndIsRestoredOnTheNextStart()
    {
        _service.BeginGameSession(OverlayGame("g", _exeA));
        var leftBehind = _store.OnDisk;
        Assert.NotNull(leftBehind);
        Assert.True(leftBehind!.DlssOverlayCaptured);

        // A new service instance, as after a crash, finds the snapshot and puts things back.
        var recovered = new PerformanceProfileService(_store, () => _settings, _backend);
        recovered.RecoverFromCrashIfNeeded();

        Assert.False(_backend.Hklm.ContainsKey(NgxKey));
    }

    [Fact]
    public void Overlay_WhenTheRegistryWriteIsRefused_RecordsNothingToRestore()
    {
        // The user declined the UAC prompt. Nothing was written, so nothing is owed - a capture
        // here would later delete a value TrayTrigger never set.
        _backend.RefuseHklmWrites = true;

        bool started = _service.BeginGameSession(OverlayGame("g", _exeA));

        Assert.False(started);                                  // nothing was applied
        Assert.False(_backend.Hklm.ContainsKey(NgxKey));        // nothing was written
        Assert.False(_store.OnDisk?.DlssOverlayCaptured ?? false); // and nothing is owed
    }

    [Fact]
    public void AnOverlayOnlyGame_DoesNotCountAsTheFirstProfileSession()
    {
        // It applies none of the machine-wide tweaks, so the profile game that follows it is the
        // first session for those and must still get them.
        _service.BeginGameSession(OverlayGame("overlay", _exeA));
        _service.BeginGameSession(Game("plain", _exeB, PerformanceProfileMode.Optimized));

        Assert.Equal("ultimate", _backend.ActiveScheme);
    }

    [Fact]
    public void WhenTheLastProfileGameEnds_ItsTweaksGoBack_EvenWithAnOverlayOnlyGameStillRunning()
    {
        // Otherwise the next profile game is a first session again and captures TrayTrigger's own
        // power plan as the one to restore.
        _service.BeginGameSession(OverlayGame("overlay", _exeA));
        _service.BeginGameSession(Game("plain", _exeB, PerformanceProfileMode.Optimized));

        _service.EndGameSession("plain");

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", _backend.ActiveScheme);
        Assert.Equal(0x400, _backend.Hklm[NgxKey]);   // the overlay game is still playing

        _service.BeginGameSession(Game("again", _exeB, PerformanceProfileMode.Optimized));
        _service.EndGameSession("again");
        _service.EndGameSession("overlay");

        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", _backend.ActiveScheme);
        Assert.False(_backend.Hklm.ContainsKey(NgxKey));
    }

    [Fact]
    public void Overlay_DeferredAtShutdown_StaysOnDiskForTheNextStart()
    {
        // Not elevated at sign-out: the HKLM restore is skipped, so the snapshot is the only thing
        // that can turn the indicator off again.
        _backend.IsElevated = false;
        _service.BeginGameSession(OverlayGame("g", _exeA));

        _service.RestoreActiveSessionOnShutdown(skipElevated: true);

        Assert.True(_store.OnDisk?.DlssOverlayCaptured);

        var next = new PerformanceProfileService(_store, () => _settings, _backend);
        next.RecoverFromCrashIfNeeded();
        Assert.False(_backend.Hklm.ContainsKey(NgxKey));
    }

    [Fact]
    public void Overlay_WhenTheRestoreIsRefused_KeepsTheRealPreviousValue()
    {
        _backend.Hklm[NgxKey] = 1;
        _service.BeginGameSession(OverlayGame("a", _exeA));

        _backend.RefuseHklmWrites = true;       // the UAC prompt at session end is declined
        _service.EndGameSession("a");
        Assert.True(_store.OnDisk?.DlssOverlayCaptured);

        // The next overlay game must not capture TrayTrigger's own 0x400 as the previous value.
        _backend.RefuseHklmWrites = false;
        _service.BeginGameSession(OverlayGame("b", _exeB));
        _service.EndGameSession("b");

        Assert.Equal(1, _backend.Hklm[NgxKey]);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void AGameWithNeitherAProfileNorTheOverlay_StillAppliesNothing()
    {
        Assert.False(_service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Off)));
        Assert.Empty(_backend.Log);
    }
}
