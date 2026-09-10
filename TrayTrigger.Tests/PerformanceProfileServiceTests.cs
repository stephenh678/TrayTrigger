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
        public bool WriteHklmDword(string subKey, string valueName, int value) { Hklm[subKey + "|" + valueName] = value; Log.Add($"hklm:{valueName}={value}"); return true; }
        public bool WriteHklmString(string subKey, string valueName, string value) { Hklm[subKey + "|" + valueName] = value; Log.Add($"hklm:{valueName}={value}"); return true; }
        public bool DeleteHklmValue(string subKey, string valueName) { Hklm.Remove(subKey + "|" + valueName); Log.Add($"hklm:{valueName}=<deleted>"); return true; }

        public List<HdrControlService.DisplayColorState> GetHdrDisplayStates() => new();
        public bool SetDisplayHdrEnabled(HdrControlService.LUID adapterId, uint targetId, bool enable) => true;

        public string? GetGpuPreference(string exePath) => GpuPrefs.GetValueOrDefault(exePath);
        public void SetGpuPreference(string exePath, string value) { GpuPrefs[exePath] = value; Log.Add($"gpu:{Path.GetFileName(exePath)}={value}"); }
        public void DeleteGpuPreference(string exePath) { GpuPrefs.Remove(exePath); Log.Add($"gpu:{Path.GetFileName(exePath)}=<deleted>"); }

        public HashSet<string> GetDefenderExclusionPaths() => new(Defender, StringComparer.OrdinalIgnoreCase);
        public bool AddDefenderExclusion(string exePath) { Defender.Add(exePath); Log.Add("defender:+" + Path.GetFileName(exePath)); return true; }
        public bool RemoveDefenderExclusion(string exePath) { Defender.Remove(exePath); Log.Add("defender:-" + Path.GetFileName(exePath)); return true; }

        public void SetProcessPriority(Process process, ProcessPriorityClass priority) => Log.Add("priority:" + priority);

        public bool TimerHeld;
        public uint RequestHighTimerResolution() { TimerHeld = true; Log.Add("timer:request"); return 5000; }
        public void ReleaseHighTimerResolution() { TimerHeld = false; Log.Add("timer:release"); }

        public int? Toasts;
        public int? GetToastsEnabled() => Toasts;
        public void SetToastsEnabled(int? value) { Toasts = value; Log.Add("toasts:" + (value?.ToString() ?? "unset")); }
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
}
