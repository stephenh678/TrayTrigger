using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Applies a per-game "Performance Profile" (Optimized/Aggressive) for the duration of a single
/// gaming session and restores the exact prior system state afterward - as opposed to
/// <see cref="SystemTweaksService"/>, which owns the permanent, always-on System &amp; Performance
/// tweaks. Reuses SystemTweaksService's low-level registry/power-plan helpers, but owns its own
/// capture/apply/restore orchestration (see the Performance Profiles migration notes).
///
/// Two kinds of tweak, two lifetimes:
/// - Machine-wide singletons (Power Plan, System Responsiveness, MMCSS Scheduling Category): only
///   the first tracked session captures/applies them ("first wins" - see <see cref="BeginGameSession"/>),
///   and only the last tracked session ending restores them.
/// - Per-executable tweaks (GPU Preference, Defender Exclusion): independent per game, applied and
///   restored on that specific game's own session regardless of other sessions still running.
///
/// The pre-profile snapshot is persisted to disk so an abnormal exit (crash/kill) can still be
/// recovered on the next startup via <see cref="RecoverFromCrashIfNeeded"/>.
/// </summary>
public class PerformanceProfileService
{
    private const string SystemResponsivenessPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GpuPreferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private readonly StorageService _storageService;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _activeSessionKeys = new();
    private PerformanceProfileSessionSnapshot? _snapshot;

    public PerformanceProfileService(StorageService storageService)
    {
        _storageService = storageService;
    }

    /// <summary>Call once at startup, before anything else could touch these same registry values.</summary>
    public void RecoverFromCrashIfNeeded()
    {
        var snapshot = _storageService.LoadProfileSessionSnapshot();
        if (snapshot == null) return;

        LoggingService.Warn("PerformanceProfile", "Found a leftover profile session snapshot from a previous run (likely an abnormal exit) - restoring pre-profile system state.");
        RestoreGlobalTweaks(snapshot);
        foreach (var perGame in snapshot.PerGameSnapshots)
        {
            RestorePerGameTweaks(perGame);
        }
        _storageService.DeleteProfileSessionSnapshot();
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
    //                                          Above Normal priority). Never runs for Steam
    //                                          launches, where TrayTrigger has no handle.
    //   4. EndGameSession(gameId)              RESTORE. Per-game tweaks immediately; machine-wide
    //                                          tweaks once the last tracked session ends.
    //
    // ADDING A TWEAK: put it in ApplyPreLaunchTweaks unless it genuinely needs the Process object,
    // in which case it goes in ApplyProcessTweaks. Anything that must be undone needs a snapshot
    // field and a matching Restore* call - see RestoreGlobalTweaks / RestorePerGameTweaks.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// PRE-LAUNCH phase. Captures the snapshot and applies every tweak that doesn't need the game
    /// process. Machine-wide tweaks (Power Plan, System Responsiveness, MMCSS Scheduling) are only
    /// captured/applied by the first tracked session ("first wins") - a second concurrent game just
    /// joins it. Per-executable tweaks (GPU Preference, Defender Exclusion) apply independently
    /// every time, since they're keyed to that game's own exe path and don't conflict with another
    /// game's. If the launch subsequently fails, the launcher must call <see cref="EndGameSession"/>.
    /// </summary>
    public void BeginGameSession(GameEntry game)
    {
        if (game.PerformanceProfile == PerformanceProfileMode.Off) return;

        lock (_lock)
        {
            if (_activeSessionKeys.Contains(game.Id)) return;

            var settings = _storageService.LoadSettings();
            bool isFirstSession = _activeSessionKeys.Count == 0;

            _snapshot ??= new PerformanceProfileSessionSnapshot();

            // Persists the snapshot after each individual tweak below (not once at the end),
            // so a crash mid-sequence still leaves a recovery record for whatever was already
            // applied instead of leaving mutated system state with nothing on disk to undo it.
            bool appliedAnything = ApplyPreLaunchTweaks(game, settings, isFirstSession, _snapshot, _storageService);

            if (!appliedAnything)
            {
                LoggingService.Verbose("PerformanceProfile", $"'{game.Name}' requested {game.PerformanceProfile} but every applicable pre-launch tweak is disabled (or not resolvable for this launch type) - nothing to apply.");
                if (isFirstSession && _snapshot.PerGameSnapshots.Count == 0 && !_snapshot.PowerPlanCaptured
                    && !_snapshot.SystemResponsivenessCaptured && !_snapshot.SchedulingCategoryCaptured && !_snapshot.HdrCaptured)
                {
                    _snapshot = null;
                }
                return;
            }

            _activeSessionKeys.Add(game.Id);
            LoggingService.Info("PerformanceProfile", $"Applied {game.PerformanceProfile} profile for '{game.Name}' (pre-launch).");
        }
    }

    /// <summary>
    /// POST-START phase. Applies the tweaks that need the actual game process. These need no
    /// snapshot or restore - they die with the process - so this doesn't touch session tracking
    /// and is safe to call even when <see cref="BeginGameSession"/> applied nothing.
    /// </summary>
    public void OnGameProcessStarted(GameEntry game, Process process)
    {
        if (game.PerformanceProfile == PerformanceProfileMode.Off) return;

        var settings = _storageService.LoadSettings();
        ApplyProcessTweaks(game, settings, process);
    }

    /// <summary>All pre-launch tweaks, in application order. Returns true if anything was applied.
    /// Persists the snapshot to disk after each tweak that actually changed something, so a crash
    /// between two tweaks doesn't leave the earlier one's system change unrecoverable.</summary>
    private static bool ApplyPreLaunchTweaks(GameEntry game, AppSettings settings, bool isFirstSession, PerformanceProfileSessionSnapshot snapshot, StorageService storageService)
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
                storageService.SaveProfileSessionSnapshot(snapshot);
            }

            if (settings.OptimizedProfileTweaks.HdrEnabled && ApplyHdr(snapshot))
            {
                applied = true;
                storageService.SaveProfileSessionSnapshot(snapshot);
            }

            if (aggressive && settings.AggressiveProfileTweaks.SystemResponsivenessEnabled)
            {
                ApplySystemResponsiveness(snapshot);
                applied = true;
                storageService.SaveProfileSessionSnapshot(snapshot);
            }

            if (aggressive && settings.AggressiveProfileTweaks.MmcssGamesPriorityEnabled)
            {
                ApplySchedulingCategory(snapshot);
                applied = true;
                storageService.SaveProfileSessionSnapshot(snapshot);
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
            storageService.SaveProfileSessionSnapshot(snapshot);
        }

        return applied;
    }

    /// <summary>All post-start tweaks (those that need the live process).</summary>
    private static void ApplyProcessTweaks(GameEntry game, AppSettings settings, Process process)
    {
        bool aggressive = game.PerformanceProfile == PerformanceProfileMode.Aggressive;

        if (aggressive && settings.AggressiveProfileTweaks.AboveNormalPriorityEnabled)
        {
            ApplyAboveNormalPriority(process, game.Name);
        }
    }

    /// <summary>
    /// Ends tracking for this game's session. That game's own per-executable tweaks are restored
    /// immediately; machine-wide tweaks are only restored once every tracked session has ended.
    /// </summary>
    public void EndGameSession(string gameId)
    {
        lock (_lock)
        {
            if (!_activeSessionKeys.Remove(gameId)) return;
            if (_snapshot == null) return;

            var perGame = _snapshot.PerGameSnapshots.FirstOrDefault(p => p.GameId == gameId);
            if (perGame != null)
            {
                RestorePerGameTweaks(perGame);
                _snapshot.PerGameSnapshots.Remove(perGame);
            }

            if (_activeSessionKeys.Count == 0)
            {
                RestoreGlobalTweaks(_snapshot);
                _snapshot = null;
                _storageService.DeleteProfileSessionSnapshot();
                LoggingService.Info("PerformanceProfile", "All tracked game sessions ended; restored pre-profile system state.");
            }
            else
            {
                _storageService.SaveProfileSessionSnapshot(_snapshot);
            }
        }
    }

    /// <summary>Called from App.ExitApplication so a temporary profile never outlives TrayTrigger.</summary>
    public void RestoreActiveSessionOnShutdown()
    {
        lock (_lock)
        {
            if (_snapshot == null) return;

            RestoreGlobalTweaks(_snapshot);
            foreach (var perGame in _snapshot.PerGameSnapshots)
            {
                RestorePerGameTweaks(perGame);
            }

            _snapshot = null;
            _activeSessionKeys.Clear();
            _storageService.DeleteProfileSessionSnapshot();
            LoggingService.Info("PerformanceProfile", "Restored pre-profile system state on application exit.");
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

    private static void ApplyPowerPlan(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousPowerSchemeGuid = SystemTweaksService.GetActivePowerSchemeGuid();
        snapshot.PowerPlanCaptured = true;

        string? schemeGuid = SystemTweaksService.FindExistingUltimatePlanGuid() ?? SystemTweaksService.CreateUltimateTrayTriggerPlan();
        if (string.IsNullOrWhiteSpace(schemeGuid))
        {
            LoggingService.Warn("PerformanceProfile", "Could not create or locate the 'Ultimate Plan - TrayTrigger' power scheme.");
            return;
        }

        SystemTweaksService.ApplyUltimatePlanTweaks(schemeGuid);
        SystemTweaksService.RunPowercfg($"/setactive {schemeGuid}");
        LoggingService.Verbose("PerformanceProfile", $"Power Plan: switched active scheme to 'Ultimate Plan - TrayTrigger' (was {snapshot.PreviousPowerSchemeGuid ?? "unknown"}).");
    }

    private static void RestorePowerPlan(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.PowerPlanCaptured || string.IsNullOrWhiteSpace(snapshot.PreviousPowerSchemeGuid)) return;
        SystemTweaksService.RunPowercfg($"/setactive {snapshot.PreviousPowerSchemeGuid}");
        LoggingService.Verbose("PerformanceProfile", $"Power Plan: restored active scheme to {snapshot.PreviousPowerSchemeGuid}.");
    }

    private static void ApplySystemResponsiveness(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousSystemResponsiveness = ReadDword(SystemResponsivenessPath, "SystemResponsiveness");
        snapshot.SystemResponsivenessCaptured = true;

        // Microsoft's MMCSS docs: values below 10 are clamped back up to 20, so 10 is the
        // lowest reserve Windows actually honors.
        SystemTweaksService.SetHklmDword(SystemResponsivenessPath, "SystemResponsiveness", 10);
        LoggingService.Verbose("PerformanceProfile", $"System Responsiveness: set to 10 (was {snapshot.PreviousSystemResponsiveness?.ToString() ?? "unset"}).");
    }

    private static void RestoreSystemResponsiveness(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.SystemResponsivenessCaptured) return;

        if (snapshot.PreviousSystemResponsiveness.HasValue)
        {
            SystemTweaksService.SetHklmDword(SystemResponsivenessPath, "SystemResponsiveness", snapshot.PreviousSystemResponsiveness.Value);
        }
        else
        {
            SystemTweaksService.DeleteHklmValue(SystemResponsivenessPath, "SystemResponsiveness");
        }
        LoggingService.Verbose("PerformanceProfile", $"System Responsiveness: restored to {snapshot.PreviousSystemResponsiveness?.ToString() ?? "unset"}.");
    }

    private static void ApplySchedulingCategory(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousSchedulingCategory = ReadString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category");
        snapshot.SchedulingCategoryCaptured = true;

        // SFIO Priority is intentionally not written - Microsoft's MMCSS docs state it "is not used".
        SystemTweaksService.SetHklmValuesBatch(
            (SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", "High", RegistryValueKind.String));
        LoggingService.Verbose("PerformanceProfile", $"MMCSS Scheduling Category: set to 'High' (was '{snapshot.PreviousSchedulingCategory ?? "unset"}').");
    }

    private static void RestoreSchedulingCategory(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.SchedulingCategoryCaptured) return;

        if (snapshot.PreviousSchedulingCategory != null)
        {
            SystemTweaksService.SetHklmValuesBatch(
                (SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", snapshot.PreviousSchedulingCategory, RegistryValueKind.String));
        }
        else
        {
            SystemTweaksService.DeleteHklmValue(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category");
        }
        LoggingService.Verbose("PerformanceProfile", $"MMCSS Scheduling Category: restored to '{snapshot.PreviousSchedulingCategory ?? "unset"}'.");
    }

    private static void RestoreGlobalTweaks(PerformanceProfileSessionSnapshot snapshot)
    {
        RestorePowerPlan(snapshot);
        RestoreHdr(snapshot);
        RestoreSystemResponsiveness(snapshot);
        RestoreSchedulingCategory(snapshot);
    }

    /// <summary>
    /// Turns on Windows' native HDR mode via the CCD advanced-color API (see
    /// <see cref="HdrControlService"/>) on every display that reports HDR support. Displays already
    /// in HDR are left alone but still recorded, so restore doesn't force them off either. Returns
    /// false (and captures nothing) if there's no HDR-capable display, or every capable display is
    /// currently in Auto Color Management's WCG mode - those are left untouched entirely, since the
    /// on/off-only HDR API used to restore afterward would drop them to plain SDR instead of back
    /// to WCG (see RestoreHdr).
    /// </summary>
    private static bool ApplyHdr(PerformanceProfileSessionSnapshot snapshot)
    {
        var states = HdrControlService.GetDisplayStates().Where(s => s.Supported).ToList();
        if (states.Count == 0)
        {
            LoggingService.Verbose("PerformanceProfile", "No HDR-capable display detected; skipping Enable HDR.");
            return false;
        }

        var touchable = states.Where(s => !s.IsWcg).ToList();
        if (touchable.Count == 0)
        {
            LoggingService.Verbose("PerformanceProfile", "Every HDR-capable display is in WCG mode; leaving as-is.");
            return false;
        }

        snapshot.PreviousHdrStates = touchable.Select(s => new HdrDisplaySnapshot
        {
            AdapterIdLowPart = s.AdapterId.LowPart,
            AdapterIdHighPart = s.AdapterId.HighPart,
            TargetId = s.TargetId,
            WasEnabled = s.Enabled
        }).ToList();
        snapshot.HdrCaptured = true;

        LoggingService.Info("PerformanceProfile", $"Enable HDR: found {touchable.Count} HDR-capable display(s) ({touchable.Count(s => s.Enabled)} already on, {states.Count - touchable.Count} left alone in WCG mode).");

        foreach (var s in touchable.Where(s => !s.Enabled))
        {
            if (HdrControlService.SetDisplayHdrEnabled(s.AdapterId, s.TargetId, true))
            {
                LoggingService.Info("PerformanceProfile", $"Enabled HDR on display target {s.TargetId}.");
            }
            else
            {
                LoggingService.Warn("PerformanceProfile", $"Failed to enable HDR on display target {s.TargetId}.");
            }
        }

        return true;
    }

    private static void RestoreHdr(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.HdrCaptured) return;

        foreach (var s in snapshot.PreviousHdrStates)
        {
            var adapterId = new HdrControlService.LUID { LowPart = s.AdapterIdLowPart, HighPart = s.AdapterIdHighPart };
            bool ok = HdrControlService.SetDisplayHdrEnabled(adapterId, s.TargetId, s.WasEnabled);
            LoggingService.Info("PerformanceProfile", ok
                ? $"Restored display target {s.TargetId} HDR state to {(s.WasEnabled ? "On" : "Off")}."
                : $"Failed to restore display target {s.TargetId} HDR state.");
        }
    }

    /// <summary>
    /// Windows' Settings &gt; System &gt; Display &gt; Graphics "GPU preference" feature, stored
    /// per-executable. Real, Microsoft-backed mechanism (this is the same registry location the
    /// Settings UI itself writes to), no elevation needed since it's HKCU.
    /// </summary>
    private static void ApplyGpuPreference(PerGameProfileSnapshot snapshot, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesPath);
            snapshot.GpuPreferenceExecutablePath = exePath;
            snapshot.PreviousGpuPreferenceValue = key?.GetValue(exePath) as string;
            snapshot.GpuPreferenceCaptured = true;
            key?.SetValue(exePath, "GpuPreference=2;", RegistryValueKind.String);
            LoggingService.Verbose("PerformanceProfile", $"GPU Preference: set 'High performance' for '{exePath}' (was '{snapshot.PreviousGpuPreferenceValue ?? "unset"}').");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyGpuPreference failed: {ex.Message}");
        }
    }

    private static void RestoreGpuPreference(PerGameProfileSnapshot snapshot)
    {
        if (!snapshot.GpuPreferenceCaptured || snapshot.GpuPreferenceExecutablePath == null) return;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesPath);
            if (snapshot.PreviousGpuPreferenceValue != null)
            {
                key?.SetValue(snapshot.GpuPreferenceExecutablePath, snapshot.PreviousGpuPreferenceValue, RegistryValueKind.String);
            }
            else
            {
                key?.DeleteValue(snapshot.GpuPreferenceExecutablePath, throwOnMissingValue: false);
            }
            LoggingService.Verbose("PerformanceProfile", $"GPU Preference: restored for '{snapshot.GpuPreferenceExecutablePath}' to '{snapshot.PreviousGpuPreferenceValue ?? "unset"}'.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreGpuPreference failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Raises the launched process to Above Normal priority - Microsoft's own documented
    /// scheduling classes via <see cref="Process.PriorityClass"/> (a thin wrapper over
    /// SetPriorityClass). Deliberately stops at Above Normal, not High: community benchmarking
    /// consistently shows High priority risks starving audio/input threads for a negligible
    /// additional gain over Above Normal. No restore needed - priority dies with the process.
    /// </summary>
    private static void ApplyAboveNormalPriority(Process process, string gameName)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
            LoggingService.Verbose("PerformanceProfile", $"Set '{gameName}' process priority to Above Normal.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Failed to raise process priority for '{gameName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Adds a Microsoft Defender real-time-protection exclusion for the game's executable.
    /// Modern Windows (Tamper Protection) blocks direct registry writes to Defender's exclusion
    /// list, so this goes through the supported Add-MpPreference/Remove-MpPreference cmdlets
    /// instead. Only removes the exclusion on restore if this session is the one that added it -
    /// an exclusion the user (or another tool) already had is left untouched.
    /// </summary>
    private static void ApplyDefenderExclusion(PerGameProfileSnapshot snapshot, string exePath)
    {
        try
        {
            bool alreadyExcluded = GetDefenderExclusionPaths().Contains(exePath, StringComparer.OrdinalIgnoreCase);
            snapshot.DefenderExclusionPath = exePath;
            snapshot.DefenderExclusionWasPreExisting = alreadyExcluded;
            snapshot.DefenderExclusionCaptured = true;

            if (!alreadyExcluded)
            {
                RunElevatedPowerShell($"Add-MpPreference -ExclusionPath '{EscapeForPowerShellSingleQuoted(exePath)}'");
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

    private static void RestorePerGameTweaks(PerGameProfileSnapshot snapshot)
    {
        RestoreGpuPreference(snapshot);
        RestoreDefenderExclusion(snapshot);
    }

    private static void RestoreDefenderExclusion(PerGameProfileSnapshot snapshot)
    {
        if (!snapshot.DefenderExclusionCaptured || snapshot.DefenderExclusionPath == null) return;
        if (snapshot.DefenderExclusionWasPreExisting) return;

        try
        {
            RunElevatedPowerShell($"Remove-MpPreference -ExclusionPath '{EscapeForPowerShellSingleQuoted(snapshot.DefenderExclusionPath)}'");
            LoggingService.Verbose("PerformanceProfile", $"Defender Exclusion: removed '{snapshot.DefenderExclusionPath}'.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreDefenderExclusion failed: {ex.Message}");
        }
    }

    private static string EscapeForPowerShellSingleQuoted(string value) => value.Replace("'", "''");

    private static HashSet<string> GetDefenderExclusionPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("(Get-MpPreference).ExclusionPath");

            using var proc = Process.Start(psi);
            if (proc == null) return result;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);

            foreach (var line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    result.Add(trimmed);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Failed to read Defender exclusions: {ex.Message}");
        }
        return result;
    }

    private static bool RunElevatedPowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            if (SystemTweaksService.IsElevated)
            {
                psi.UseShellExecute = false;
            }
            else
            {
                psi.UseShellExecute = true;
                psi.Verb = "runas";
            }

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            proc.WaitForExit(120000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Elevated PowerShell command failed: {ex.Message}");
            return false;
        }
    }

    private static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            var val = key?.GetValue(valueName);
            return val is int i ? i : null;
        }
        catch { return null; }
    }

    private static string? ReadString(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }
}
