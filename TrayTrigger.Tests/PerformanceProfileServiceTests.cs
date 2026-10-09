using System.Diagnostics;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.Tests.Fakes;

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
        /// <summary>Models a snapshot file that was there but couldn't be read.</summary>
        public string? UnreadableCopy;
        public string? TakeUnreadableProfileSessionSnapshot() { var c = UnreadableCopy; UnreadableCopy = null; return c; }
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
        /// <summary>Models a power plan Windows won't switch back to (deleted, or held by a policy).</summary>
        public bool RefusePowerScheme;
        public void SetActivePowerScheme(string schemeGuid) { if (RefusePowerScheme) return; ActiveScheme = schemeGuid; Log.Add("power:" + schemeGuid); }

        public int? ReadHklmDword(string subKey, string valueName) => Hklm.GetValueOrDefault(subKey + "|" + valueName) as int?;
        public string? ReadHklmString(string subKey, string valueName) => Hklm.GetValueOrDefault(subKey + "|" + valueName) as string;
        public bool WriteHklmDword(string subKey, string valueName, int value) { Hklm[subKey + "|" + valueName] = value; Log.Add($"hklm:{valueName}={value}"); return true; }
        public bool WriteHklmString(string subKey, string valueName, string value) { Hklm[subKey + "|" + valueName] = value; Log.Add($"hklm:{valueName}={value}"); return true; }
        public bool DeleteHklmValue(string subKey, string valueName) { Hklm.Remove(subKey + "|" + valueName); Log.Add($"hklm:{valueName}=<deleted>"); return true; }

        /// <summary>
        /// One elevated step, however many values it carries. Counted, because the count is what the
        /// user experiences: in production each of these is one administrator prompt. The individual
        /// changes are still routed through the calls above, so what the registry ends up holding is
        /// asserted the same way as before.
        /// </summary>
        public int ElevatedBatches;
        /// <summary>Every change sent in an elevated step, so a test can build the .reg file production would import.</summary>
        public readonly List<SystemTweaksService.RegFileEntry> Applied = new();
        public bool ApplyHklmChanges(IReadOnlyList<SystemTweaksService.RegFileEntry> entries)
        {
            if (entries.Count == 0) return true;
            ElevatedBatches++;
            Applied.AddRange(entries);
            foreach (var e in entries)
            {
                if (e.Delete) DeleteHklmValue(e.SubKey, e.ValueName);
                else if (e.Value is int i) WriteHklmDword(e.SubKey, e.ValueName, i);
                else WriteHklmString(e.SubKey, e.ValueName, (string)e.Value!);
            }
            return true;
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

        // Defender runs through elevated PowerShell, not the registry, so it cannot join the batch
        // above - in production it is its own administrator prompt. Counted here for the same
        // reason: ElevatedBatches is meant to be what the user is asked, not what we happened to
        // route through one code path.
        public bool AddDefenderExclusion(string exePath)
        {
            if (!Defender.Add(exePath)) return false;
            ElevatedBatches++;
            Log.Add("defender:+" + Path.GetFileName(exePath));
            return true;
        }
        public bool RemoveDefenderExclusion(string exePath) { if (Defender.Remove(exePath)) ElevatedBatches++; Log.Add("defender:-" + Path.GetFileName(exePath)); return true; }

        public void SetProcessPriority(Process process, ProcessPriorityClass priority) => Log.Add("priority:" + priority);

        public bool ExemptFromPowerThrottling(Process process) { Log.Add("throttling:exempt"); return true; }

        public int? PrimaryRefreshHz = 144;
        public int? GetPrimaryRefreshHz() => PrimaryRefreshHz;

        public bool? ResizableBarOn = true;
        public bool? IsResizableBarEnabled() => ResizableBarOn;

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

    /// <summary>
    /// A power plan Windows won't switch back to is noticed, not assumed restored: it's what the
    /// Critical NEEDS ATTENTION item in Activity &amp; History reports, and Restore Previous retries it.
    /// </summary>
    [Fact]
    public void Restore_ThatDoesNotTakeEffect_IsRemembered_UntilARetrySucceeds()
    {
        var game = Game("g", _exeA, PerformanceProfileMode.Optimized);
        Assert.True(_service.BeginGameSession(game));
        _backend.RefusePowerScheme = true;

        Assert.True(_service.EndGameSession("g"));

        Assert.Equal("ultimate", _backend.ActiveScheme);
        Assert.Equal(["the power plan"], _service.UnrestoredItems);

        // Still refused: kept for the next try.
        Assert.False(_service.RetryUnrestored());
        Assert.Equal(["the power plan"], _service.UnrestoredItems);

        _backend.RefusePowerScheme = false;
        Assert.True(_service.RetryUnrestored());
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", _backend.ActiveScheme);
        Assert.Empty(_service.UnrestoredItems);
    }

    /// <summary>A restore that worked leaves nothing behind to report.</summary>
    [Fact]
    public void Restore_ThatWorks_LeavesNothingUnrestored()
    {
        Assert.True(_service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive)));
        Assert.True(_service.EndGameSession("g"));
        Assert.Empty(_service.UnrestoredItems);
    }

    [Fact]
    public void Off_AppliesNothing()
    {
        Assert.False(_service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Off)));
        Assert.Empty(_backend.Log);
        Assert.Null(_store.OnDisk);
        Assert.Empty(_service.ActiveSessionGameIds);
    }

    /// <summary>The Profile line of the Played row: what was changed, what was skipped and why, and that it was put back.</summary>
    [Fact]
    public void Optimized_SaysWhatItChanged_AndThatItPutItBack()
    {
        var game = Game("profile-notes", _exeA, PerformanceProfileMode.Optimized);
        LaunchRecord.Begin(game.Id);

        Assert.True(_service.BeginGameSession(game));
        Assert.Equal(["Ultimate Performance power plan", "High performance GPU for the game's program"],
            LaunchRecord.Peek(game.Id, LaunchRecord.Profile).Where(n => !n.Contains("NVIDIA") && !n.Contains("HDR")));

        Assert.True(_service.EndGameSession("profile-notes"));
        var record = LaunchRecord.Take(game.Id);
        var profile = Assert.Single(record, r => r.Section == LaunchRecord.Profile).Notes;
        Assert.Equal("put back: everything", profile[^1]);
    }

    [Fact]
    public void Off_SaysNothingChanged()
    {
        var game = Game("off-notes", _exeA, PerformanceProfileMode.Off);
        LaunchRecord.Begin(game.Id);

        Assert.False(_service.BeginGameSession(game));

        Assert.Equal(["nothing changed"], LaunchRecord.Peek(game.Id, LaunchRecord.Profile));
        LaunchRecord.Take(game.Id);
    }

    [Fact]
    public void ARestoreThatFails_IsSaidOnTheProfileLine_BesideTheCriticalRow()
    {
        var game = Game("fail-notes", _exeA, PerformanceProfileMode.Optimized);
        LaunchRecord.Begin(game.Id);
        Assert.True(_service.BeginGameSession(game));
        _backend.RefusePowerScheme = true;

        Assert.True(_service.EndGameSession("fail-notes"));

        var profile = Assert.Single(LaunchRecord.Take(game.Id), r => r.Section == LaunchRecord.Profile).Notes;
        Assert.Equal("put back: everything except the power plan, which couldn't be", profile[^1]);
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
        // Was absent before -> Windows' default, never deleted.
        Assert.Equal("Medium", _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
        Assert.DoesNotContain(_exeA, _backend.Defender);
    }

    /// <summary>
    /// A restore that deleted SystemResponsiveness stopped MMCSS starting at the next boot, which took
    /// a webcam down with it. Whatever was captured, the .reg file imported on the way out writes both
    /// values and deletes neither.
    /// </summary>
    [Fact]
    public void Aggressive_PreviousValuesUnknown_RestoresWindowsDefaults_AndDeletesNothing()
    {
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = false;
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        _service.EndGameSession("g");

        Assert.Equal(20, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.Equal("Medium", _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
        string reg = SystemTweaksService.BuildRegFileContent(_backend.Applied);
        Assert.Contains("\"SystemResponsiveness\"=dword:00000014", reg);
        Assert.Contains("\"Scheduling Category\"=\"Medium\"", reg);
        Assert.DoesNotContain("\"SystemResponsiveness\"=-", reg);
        Assert.DoesNotContain("\"Scheduling Category\"=-", reg);
    }

    /// <summary>
    /// Already 10 and "High" before the game means an earlier session never restored them. Putting
    /// those back would leave the tweak on for good: Windows' defaults go back instead.
    /// </summary>
    [Fact]
    public void Aggressive_ValuesAlreadyOurs_RestoreWindowsDefaults()
    {
        _backend.Hklm[PerformanceProfileService.SystemResponsivenessPath + "|SystemResponsiveness"] = 10;
        _backend.Hklm[SystemTweaksService.MmcssGamesTaskPath + "|Scheduling Category"] = "High";

        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        _service.EndGameSession("g");

        Assert.Equal(20, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.Equal("Medium", _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
    }

    /// <summary>A category no Windows version writes isn't put back; Windows' own goes back instead.</summary>
    [Fact]
    public void Aggressive_PreviousCategoryNotOneWindowsDefines_RestoresMedium()
    {
        _backend.Hklm[SystemTweaksService.MmcssGamesTaskPath + "|Scheduling Category"] = "Ultra";
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        _service.EndGameSession("g");

        Assert.Equal("Medium", _backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
    }

    /// <summary>
    /// The two machine-wide HKLM tweaks go in one elevated step, and so does putting them back.
    /// Each elevated step is an administrator prompt, and written one at a time this profile asked
    /// four times for a single game session - twice on the way in, twice on the way out.
    /// </summary>
    [Fact]
    public void Aggressive_AsksForElevationOnceOnTheWayIn_AndOnceOnTheWayOut()
    {
        // Defender off: it is a separate elevated call and has its own test below.
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = false;
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        Assert.Equal(1, _backend.ElevatedBatches);

        _service.EndGameSession("g");
        Assert.Equal(2, _backend.ElevatedBatches);
    }

    /// <summary>A second game while the first is running is not a second prompt: these apply once.</summary>
    [Fact]
    public void Aggressive_ASecondGame_DoesNotAskAgain()
    {
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = false;
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        _service.BeginGameSession(Game("h", _exeB, PerformanceProfileMode.Aggressive));

        Assert.Equal(1, _backend.ElevatedBatches);
    }

    /// <summary>
    /// Optimized touches nothing under HKLM - power plan, GPU preference, HDR, notifications and
    /// audio are all either per-user or an API call - so it must never raise a prompt at all.
    /// </summary>
    [Fact]
    public void Optimized_NeverAsksForElevation()
    {
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = false;
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized));
        _service.EndGameSession("g");

        Assert.Equal(0, _backend.ElevatedBatches);
    }

    /// <summary>
    /// With only one of the two tweaks on there is still exactly one prompt, and it carries only
    /// that tweak - the batch must not write a value the user has switched off.
    /// </summary>
    [Fact]
    public void Aggressive_WithOneTweakOff_StillAsksOnce_AndLeavesTheOtherValueAlone()
    {
        _settings.AggressiveProfileTweaks.MmcssGamesPriorityEnabled = false;
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = false;

        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));

        Assert.Equal(1, _backend.ElevatedBatches);
        Assert.Equal(10, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.Null(_backend.ReadHklmString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category"));
    }

    /// <summary>
    /// Both off means nothing to write, so nothing to ask for - an empty batch must not reach the
    /// backend and raise a prompt that changes nothing.
    /// </summary>
    [Fact]
    public void Aggressive_WithBothHklmTweaksOff_NeverAsks()
    {
        _settings.AggressiveProfileTweaks.SystemResponsivenessEnabled = false;
        _settings.AggressiveProfileTweaks.MmcssGamesPriorityEnabled = false;
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = false;

        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        _service.EndGameSession("g");

        Assert.Equal(0, _backend.ElevatedBatches);
    }

    /// <summary>
    /// Defender Exclusion is the one tweak the batch cannot absorb: it is an elevated PowerShell
    /// call, not a registry write, so turning it on costs a second prompt each way. Held so the
    /// cost is visible rather than discovered, and so it fails if someone later folds the two into
    /// one elevated step without meaning to.
    /// </summary>
    [Fact]
    public void Aggressive_WithDefenderExclusion_CostsASecondPromptEachWay()
    {
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = true;

        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        Assert.Equal(2, _backend.ElevatedBatches);

        _service.EndGameSession("g");
        Assert.Equal(4, _backend.ElevatedBatches);
    }

    /// <summary>
    /// And it is per game, not per session: the machine-wide tweaks are applied once however many
    /// games run, but a second game with Defender Exclusion on is a second prompt. That is the one
    /// place prompt fatigue can still come from, and batching cannot fix it.
    /// </summary>
    [Fact]
    public void Aggressive_DefenderExclusion_AsksAgainForEachGame()
    {
        _settings.AggressiveProfileTweaks.DefenderExclusionEnabled = true;

        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        int afterFirst = _backend.ElevatedBatches;
        _service.BeginGameSession(Game("h", _exeB, PerformanceProfileMode.Aggressive));

        Assert.Equal(afterFirst + 1, _backend.ElevatedBatches);
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

        // Next start: recovery finishes the job. It was absent before, so Windows' default goes back.
        var next = new PerformanceProfileService(_store, () => _settings, _backend);
        next.RecoverFromCrashIfNeeded();
        Assert.Equal(20, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
        Assert.DoesNotContain(_exeA, _backend.Defender);
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void ShutdownWhenElevated_RestoresEverythingEvenWithSkipElevated()
    {
        _backend.IsElevated = true;
        _service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive));
        _service.RestoreActiveSessionOnShutdown(skipElevated: true);
        Assert.Equal(20, _backend.ReadHklmDword(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness"));
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

    /// <summary>A game's own HDR choice beats the profile's switch, both ways.</summary>
    [Fact]
    public void Hdr_GameOff_BeatsProfileOn()
    {
        _settings.OptimizedProfileTweaks.HdrEnabled = true;
        _backend.HdrDisplays.Add(Display(4355, enabled: false));
        var game = Game("a", _exeA, PerformanceProfileMode.Optimized);
        game.Hdr = HdrMode.Off;

        _service.BeginGameSession(game);
        _service.EndGameSession("a");

        Assert.DoesNotContain(_backend.Log, l => l.StartsWith("hdr:"));
        Assert.Contains("power:ultimate", _backend.Log);
    }

    [Fact]
    public void Hdr_GameOn_BeatsProfileOff()
    {
        _settings.OptimizedProfileTweaks.HdrEnabled = false;
        _backend.HdrDisplays.Add(Display(4355, enabled: false));
        var game = Game("a", _exeA, PerformanceProfileMode.Optimized);
        game.Hdr = HdrMode.On;

        _service.BeginGameSession(game);
        Assert.Contains("hdr:4355=on", _backend.Log);
        _service.EndGameSession("a");
        Assert.Contains("hdr:4355=off", _backend.Log);
    }

    /// <summary>
    /// Profile Off with HDR On is a session of exactly one thing: HDR goes on and comes back, the
    /// power plan and everything else are never touched, and the Played line reports no profile.
    /// </summary>
    [Fact]
    public void Hdr_GameOn_WithProfileOff_IsAnHdrOnlySession()
    {
        _settings.OptimizedProfileTweaks.HdrEnabled = false;
        _backend.HdrDisplays.Add(Display(4355, enabled: false));
        var game = Game("a", _exeA, PerformanceProfileMode.Off);
        game.Hdr = HdrMode.On;

        Assert.True(_service.BeginGameSession(game));
        Assert.Equal(new[] { "hdr:4355=on" }, _backend.Log.ToArray());
        Assert.True(_store.OnDisk!.HdrCaptured);

        Assert.True(_service.EndGameSession("a", out var putBack));
        Assert.Null(putBack);
        Assert.Equal(new[] { "hdr:4355=on", "hdr:4355=off" }, _backend.Log.ToArray());
        Assert.Null(_store.OnDisk);
    }

    /// <summary>Profile Off and no HDR choice is still nothing at all.</summary>
    [Fact]
    public void Hdr_ProfileOff_WithoutAChoice_AppliesNothing()
    {
        _settings.OptimizedProfileTweaks.HdrEnabled = true;
        _backend.HdrDisplays.Add(Display(4355, enabled: false));

        Assert.False(_service.BeginGameSession(Game("a", _exeA, PerformanceProfileMode.Off)));
        Assert.Empty(_backend.Log);
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
    /// <summary>The game's "Played" line in Activity &amp; History names the profile only when
    /// everything was put back; what wasn't is reported on its own, as Critical.</summary>
    [Fact]
    public void EndGameSession_ReportsTheModePutBack_OnlyWhenEverythingWas()
    {
        Assert.True(_service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.True(_service.EndGameSession("g", out var putBack));
        Assert.Equal(PerformanceProfileMode.Optimized, putBack);

        Assert.True(_service.BeginGameSession(Game("h", _exeA, PerformanceProfileMode.Optimized)));
        _backend.RefusePowerScheme = true;
        Assert.True(_service.EndGameSession("h", out var notPutBack));
        Assert.Null(notPutBack);
    }
    /// <summary>A damaged crash-recovery record is reported (Critical) and removed once a copy is
    /// safe, so it's said once; without a copy it stays, rather than being lost.</summary>
    [Fact]
    public void UnreadableSnapshot_IsRemovedOnceReported_OnlyWhenACopyWasKept()
    {
        _store.UnreadableCopy = "profile-session.json.corrupt-1";
        _service.RecoverFromCrashIfNeeded();
        Assert.Equal(1, _store.Deletes);

        _service.RecoverFromCrashIfNeeded();
        Assert.Equal(1, _store.Deletes);

        _store.UnreadableCopy = string.Empty;
        _service.RecoverFromCrashIfNeeded();
        Assert.Equal(1, _store.Deletes);
    }

    // ---- Optimized: NVIDIA settings on the Global profile, and power throttling ----------------

    private const uint PowerMode = PerformanceProfileService.NvPowerModeSettingId;
    private const uint FrameCap = PerformanceProfileService.NvFrameCapSettingId;

    private PerformanceProfileService WithDriver(FakeDrsBackend driver) => new(_store, () => _settings, _backend, driver);

    [Fact]
    public void NvidiaMaxPerformance_IsSetForTheSession_AndRemovedAfter()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.Equal((PerformanceProfileService.NvPowerModePreferMax, false), driver.Global.Settings[PowerMode]);
        Assert.Contains(_store.OnDisk!.NvidiaSettings, r => r.SettingId == PowerMode);

        Assert.True(service.EndGameSession("g"));
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.Empty(service.UnrestoredItems);
    }

    [Fact]
    public void NvidiaMaxPerformance_PutsBackAModeTheUserChose()
    {
        var driver = new FakeDrsBackend();
        driver.Global.Settings[PowerMode] = (5, false);   // Optimal power, set by the user
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.True(service.EndGameSession("g"));

        Assert.Equal((5u, false), driver.Global.Settings[PowerMode]);
    }

    [Fact]
    public void NvidiaMaxPerformance_AlreadyOn_ChangesAndRecordsNothing()
    {
        var driver = new FakeDrsBackend();
        driver.Global.Settings[PowerMode] = (PerformanceProfileService.NvPowerModePreferMax, false);
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.DoesNotContain(_store.OnDisk!.NvidiaSettings, r => r.SettingId == PowerMode);
        Assert.Equal(0, driver.SaveCount);
    }

    [Fact]
    public void NvidiaMaxPerformance_SwitchedOff_IsNotApplied()
    {
        var driver = new FakeDrsBackend();
        _settings.OptimizedProfileTweaks.NvidiaMaxPerformanceEnabled = false;

        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
    }

    /// <summary>The service's default: no NVIDIA driver, so a test can't touch the real one.</summary>
    [Fact]
    public void NoNvidiaDriver_AppliesNoNvidiaSetting()
    {
        Assert.True(_service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.DoesNotContain(_store.OnDisk!.NvidiaSettings, r => r.SettingId == PowerMode);
    }

    [Fact]
    public void ChangedDuringTheSession_IsLeftAlone()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);
        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        driver.Global.Settings[PowerMode] = (0, false);   // the user picked Normal in the NVIDIA App mid-game

        Assert.True(service.EndGameSession("g"));
        Assert.Equal((0u, false), driver.Global.Settings[PowerMode]);
        Assert.Empty(service.UnrestoredItems);
    }

    [Fact]
    public void NvidiaRestoreThatFails_IsRemembered_UntilARetrySucceeds()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);
        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));

        driver.SaveError = "refused";
        Assert.True(service.EndGameSession("g"));
        Assert.Equal(["the NVIDIA power management mode"], service.UnrestoredItems);

        driver.SaveError = null;
        Assert.True(service.RetryUnrestored());
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
    }

    [Fact]
    public void NvidiaSettings_AreRecoveredAfterACrash()
    {
        var driver = new FakeDrsBackend();
        _settings.OptimizedProfileTweaks.FrameCapEnabled = true;
        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.True(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.True(driver.Global.Settings.ContainsKey(FrameCap));

        // TrayTrigger dies mid-game; the next start finds the snapshot.
        WithDriver(driver).RecoverFromCrashIfNeeded();

        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.False(driver.Global.Settings.ContainsKey(FrameCap));
        Assert.Null(_store.OnDisk);
    }

    [Fact]
    public void FrameCap_IsOffUnlessTurnedOn()
    {
        var driver = new FakeDrsBackend();
        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.False(driver.Global.Settings.ContainsKey(FrameCap));
    }

    [Fact]
    public void FrameCap_FollowsThePrimaryDisplay_AndIsRemovedAfter()
    {
        var driver = new FakeDrsBackend();
        _settings.OptimizedProfileTweaks.FrameCapEnabled = true;
        _backend.PrimaryRefreshHz = 144;
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.Equal((138u, false), driver.Global.Settings[FrameCap]);

        Assert.True(service.EndGameSession("g"));
        Assert.False(driver.Global.Settings.ContainsKey(FrameCap));
    }

    /// <summary>A Max Frame Rate the user set - for power, heat, or a game that misbehaves uncapped - is theirs.</summary>
    [Fact]
    public void FrameCap_LeavesTheUsersOwnCapAlone()
    {
        var driver = new FakeDrsBackend();
        driver.Global.Settings[FrameCap] = (120, false);
        _settings.OptimizedProfileTweaks.FrameCapEnabled = true;
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.Equal((120u, false), driver.Global.Settings[FrameCap]);
        Assert.DoesNotContain(_store.OnDisk!.NvidiaSettings, r => r.SettingId == FrameCap);

        Assert.True(service.EndGameSession("g"));
        Assert.Equal((120u, false), driver.Global.Settings[FrameCap]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(50)]
    [InlineData(58)]
    public void FrameCap_NeedsAReadableRefreshRateOfAtLeast59(int? refresh)
    {
        var driver = new FakeDrsBackend();
        _settings.OptimizedProfileTweaks.FrameCapEnabled = true;
        _backend.PrimaryRefreshHz = refresh;

        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.False(driver.Global.Settings.ContainsKey(FrameCap));
    }

    /// <summary>A 60 Hz screen running at 59.94 Hz, as many TVs do, is 59 to Windows: still capped.</summary>
    [Fact]
    public void FrameCap_IsSetForA5994HzDisplay_WhichWindowsReportsAs59()
    {
        var driver = new FakeDrsBackend();
        _settings.OptimizedProfileTweaks.FrameCapEnabled = true;
        _backend.PrimaryRefreshHz = 59;

        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.Equal((58u, false), driver.Global.Settings[FrameCap]);
    }

    [Theory]
    [InlineData(60, 59u)]
    [InlineData(144, 138u)]
    [InlineData(165, 157u)]
    [InlineData(240, 224u)]
    [InlineData(360, 324u)]
    [InlineData(480, 416u)]
    public void FrameCapFor_IsReflexsFormula(int refresh, uint cap) =>
        Assert.Equal(cap, PerformanceProfileService.FrameCapFor(refresh));

    /// <summary>The snapshot is user-writable: a cap the driver could never hold is not compared against.</summary>
    [Fact]
    public void Sanitize_DropsAnImpossibleFrameCap()
    {
        var snapshot = new PerformanceProfileSessionSnapshot();
        snapshot.NvidiaSettings.Add(new NvidiaSettingSnapshot { SettingId = FrameCap, Written = 5000 });
        snapshot.NvidiaSettings.Add(new NvidiaSettingSnapshot { SettingId = 0x12345678, Written = 1 });   // not one TrayTrigger writes
        snapshot.NvidiaSettings.Add(new NvidiaSettingSnapshot { SettingId = PowerMode, Written = 1, Previous = "user:5" });
        var problems = ProfileSnapshotValidator.Sanitize(snapshot);
        Assert.Equal(2, problems.Count);
        Assert.Equal(PowerMode, Assert.Single(snapshot.NvidiaSettings).SettingId);
    }

    // ---- Aggressive: Resizable BAR for the games NVIDIA hasn't decided on -------------------------

    private const uint Rebar = PerformanceProfileService.NvResizableBarSettingId;

    [Fact]
    public void ResizableBar_IsOnForAnAggressiveSession_AndRemovedAfter()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive)));
        Assert.Equal((1u, false), driver.Global.Settings[Rebar]);

        Assert.True(service.EndGameSession("g"));
        Assert.False(driver.Global.Settings.ContainsKey(Rebar));
    }

    [Fact]
    public void ResizableBar_IsNotPartOfOptimized()
    {
        var driver = new FakeDrsBackend();
        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.False(driver.Global.Settings.ContainsKey(Rebar));
    }

    /// <summary>With Resizable BAR off in the BIOS the driver has nothing to use; an unknown reading still tries.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public void ResizableBar_FollowsTheBios(bool? biosOn, bool expectWritten)
    {
        var driver = new FakeDrsBackend();
        _backend.ResizableBarOn = biosOn;

        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive)));
        Assert.Equal(expectWritten, driver.Global.Settings.ContainsKey(Rebar));
    }

    /// <summary>Set on the Global profile by the user or another tool: their choice for every game, left alone.</summary>
    [Fact]
    public void ResizableBar_LeavesAGlobalChoiceAlone()
    {
        var driver = new FakeDrsBackend();
        driver.Global.Settings[Rebar] = (0, false);
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive)));
        Assert.Equal((0u, false), driver.Global.Settings[Rebar]);
        Assert.True(service.EndGameSession("g"));
        Assert.Equal((0u, false), driver.Global.Settings[Rebar]);
    }

    /// <summary>
    /// The driver won't name its Resizable BAR settings, so a driver is recognised by a setting it
    /// does name - and one without even that is no NVIDIA driver at all.
    /// </summary>
    [Fact]
    public void ResizableBar_HiddenSetting_IsStillWritten_WhileTheDriverIsThere()
    {
        var driver = new FakeDrsBackend();
        driver.UnknownSettingIds.Add(Rebar);
        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive)));
        Assert.True(driver.Global.Settings.ContainsKey(Rebar));

        var noDriver = new FakeDrsBackend();
        noDriver.UnknownSettingIds.UnionWith([Rebar, PowerMode]);
        WithDriver(noDriver).BeginGameSession(Game("h", _exeB, PerformanceProfileMode.Aggressive));
        Assert.False(noDriver.Global.Settings.ContainsKey(Rebar));
    }

    /// <summary>
    /// A session loads the driver's whole database, well over 100 ms, on the way into a game: all
    /// three settings go on with one save and come off with one.
    /// </summary>
    [Fact]
    public void NvidiaSettings_GoOnWithOneSave_AndComeOffWithOne()
    {
        var driver = new FakeDrsBackend();
        _settings.OptimizedProfileTweaks.FrameCapEnabled = true;
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Aggressive)));
        Assert.Equal([PowerMode, FrameCap, Rebar], _store.OnDisk!.NvidiaSettings.Select(r => r.SettingId));
        Assert.Equal(1, driver.SaveCount);

        Assert.True(service.EndGameSession("g"));
        Assert.Empty(driver.Global.Settings);
        Assert.Equal(2, driver.SaveCount);
    }

    /// <summary>
    /// The save went through but the value reads back different: the record stays, since the
    /// value may be in place after all, and restore only acts while the driver holds it.
    /// </summary>
    [Fact]
    public void NvidiaSetting_ThatReadsBackDifferent_KeepsItsRecord()
    {
        var driver = new FakeDrsBackend();
        driver.AfterSave = () => driver.Global.Settings[PowerMode] = (0, false);
        var service = WithDriver(driver);

        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.Contains(_store.OnDisk!.NvidiaSettings, r => r.SettingId == PowerMode);

        driver.AfterSave = null;
        Assert.True(service.EndGameSession("g"));
        Assert.Equal((0u, false), driver.Global.Settings[PowerMode]);   // not ours, so left alone
        Assert.Empty(service.UnrestoredItems);
    }

    [Fact]
    public void NvidiaSettings_TheDriverRefusesToSave_AreNotRecorded()
    {
        var driver = new FakeDrsBackend { SaveError = "NVAPI_ERROR (-1)" };
        _settings.OptimizedProfileTweaks.PowerPlanEnabled = false;
        _settings.OptimizedProfileTweaks.GpuPreferenceEnabled = false;

        Assert.False(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.True(_store.OnDisk == null || _store.OnDisk.NvidiaSettings.Count == 0);
    }

    /// <summary>
    /// An NVIDIA setting that couldn't be put back when the last game ended stays on record, and
    /// the next session's end puts it back - a hidden setting can't be changed back by hand.
    /// </summary>
    [Fact]
    public void NvidiaSetting_ThatCouldNotBePutBack_IsTriedAgainAtTheNextSessionsEnd()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);
        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));

        driver.SaveError = "refused";
        Assert.True(service.EndGameSession("g"));
        Assert.True(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.Contains(_store.OnDisk!.NvidiaSettings, r => r.SettingId == PowerMode);

        driver.SaveError = null;
        Assert.True(service.BeginGameSession(Game("h", _exeB, PerformanceProfileMode.Optimized)));
        Assert.True(service.EndGameSession("h"));
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.Null(_store.OnDisk);
    }

    /// <summary>Put back by a later session's end: there is nothing left to offer Restore Previous for.</summary>
    [Fact]
    public void NvidiaSetting_PutBackAtTheNextSessionsEnd_IsNoLongerUnrestored()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);
        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));

        driver.SaveError = "refused";
        Assert.True(service.EndGameSession("g"));
        Assert.Equal(["the NVIDIA power management mode"], service.UnrestoredItems);

        driver.SaveError = null;
        Assert.True(service.BeginGameSession(Game("h", _exeB, PerformanceProfileMode.Optimized)));
        Assert.True(service.EndGameSession("h"));
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.Empty(service.UnrestoredItems);
    }

    /// <summary>
    /// Once Restore Previous has put a setting back, its record goes too. Kept, the same value
    /// chosen by the user later would be taken for TrayTrigger's own and removed.
    /// </summary>
    [Fact]
    public void NvidiaSetting_PutBackByARetry_LeavesNoRecordBehind()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);
        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));

        driver.SaveError = "refused";
        Assert.True(service.EndGameSession("g"));
        driver.SaveError = null;
        Assert.True(service.RetryUnrestored());
        Assert.Null(_store.OnDisk);

        // Prefer maximum performance, picked in the NVIDIA App this time.
        driver.Global.Settings[PowerMode] = (PerformanceProfileService.NvPowerModePreferMax, false);
        Assert.True(service.BeginGameSession(Game("h", _exeB, PerformanceProfileMode.Optimized)));
        Assert.True(service.EndGameSession("h"));
        Assert.Equal((PerformanceProfileService.NvPowerModePreferMax, false), driver.Global.Settings[PowerMode]);
    }

    /// <summary>Not put back as TrayTrigger closed: kept on disk for the next start to finish.</summary>
    [Fact]
    public void NvidiaSetting_ThatCouldNotBePutBackAtExit_IsFinishedAtTheNextStart()
    {
        var driver = new FakeDrsBackend();
        var service = WithDriver(driver);
        Assert.True(service.BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));

        driver.SaveError = "refused";
        service.RestoreActiveSessionOnShutdown(skipElevated: true);
        Assert.NotNull(_store.OnDisk);

        driver.SaveError = null;
        WithDriver(driver).RecoverFromCrashIfNeeded();
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
        Assert.Null(_store.OnDisk);
    }

    /// <summary>Recovery that fails again keeps the record and adopts it, so a game started now doesn't write over it.</summary>
    [Fact]
    public void NvidiaSetting_ThatRecoveryCannotPutBack_IsKeptForTheNextSession()
    {
        var driver = new FakeDrsBackend();
        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));   // then TrayTrigger dies

        driver.SaveError = "refused";
        var next = WithDriver(driver);
        next.RecoverFromCrashIfNeeded();
        Assert.Contains(_store.OnDisk!.NvidiaSettings, r => r.SettingId == PowerMode);

        driver.SaveError = null;
        Assert.True(next.BeginGameSession(Game("h", _exeB, PerformanceProfileMode.Optimized)));
        Assert.True(next.EndGameSession("h"));
        Assert.False(driver.Global.Settings.ContainsKey(PowerMode));
    }

    /// <summary>No NVIDIA driver any more (another graphics card): nothing holds the values, so the records go.</summary>
    [Fact]
    public void NvidiaRecords_AreDropped_WhenTheDriverHasGone()
    {
        var driver = new FakeDrsBackend();
        Assert.True(WithDriver(driver).BeginGameSession(Game("g", _exeA, PerformanceProfileMode.Optimized)));   // then TrayTrigger dies

        var gone = new FakeDrsBackend();
        gone.UnknownSettingIds.Add(PowerMode);
        var next = WithDriver(gone);
        next.RecoverFromCrashIfNeeded();

        Assert.Null(_store.OnDisk);
        Assert.Empty(next.UnrestoredItems);
    }

    [Fact]
    public void PowerThrottlingExemption_IsAppliedToTheGameProcess()
    {
        using var process = Process.GetCurrentProcess();
        _service.OnGameProcessStarted(Game("g", _exeA, PerformanceProfileMode.Optimized), process);
        Assert.Contains("throttling:exempt", _backend.Log);
    }

    [Fact]
    public void PowerThrottlingExemption_NotForOff_NorWhenSwitchedOff()
    {
        using var process = Process.GetCurrentProcess();
        _service.OnGameProcessStarted(Game("g", _exeA, PerformanceProfileMode.Off), process);
        _settings.OptimizedProfileTweaks.PowerThrottlingExemptEnabled = false;
        _service.OnGameProcessStarted(Game("h", _exeA, PerformanceProfileMode.Aggressive), process);
        Assert.DoesNotContain("throttling:exempt", _backend.Log);
    }
}
