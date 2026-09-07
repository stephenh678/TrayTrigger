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

    /// <summary>
    /// Begins tracking a gaming session for this game. Machine-wide tweaks (Power Plan, System
    /// Responsiveness, MMCSS Scheduling) are only captured/applied by the first tracked session
    /// ("first wins") - a second concurrent game just joins it. Per-executable tweaks (GPU
    /// Preference, Defender Exclusion) apply independently every time, since they're keyed to
    /// that game's own exe path and don't conflict with another game's.
    /// </summary>
    /// <param name="process">The actual game process, when known (direct .exe launches only) -
    /// needed for the Above Normal priority tweak, which has no effect for Steam-launched games
    /// since TrayTrigger never holds a handle to the real game process in that case.</param>
    public void BeginGameSession(GameEntry game, Process? process = null)
    {
        if (game.PerformanceProfile == PerformanceProfileMode.Off) return;

        lock (_lock)
        {
            if (_activeSessionKeys.Contains(game.Id)) return;

            var settings = _storageService.LoadSettings();
            bool isFirstSession = _activeSessionKeys.Count == 0;
            bool appliedAnything = false;

            _snapshot ??= new PerformanceProfileSessionSnapshot();

            if (isFirstSession)
            {
                if (settings.OptimizedProfileTweaks.PowerPlanEnabled)
                {
                    ApplyPowerPlan(_snapshot);
                    appliedAnything = true;
                }

                if (game.PerformanceProfile == PerformanceProfileMode.Aggressive)
                {
                    if (settings.AggressiveProfileTweaks.SystemResponsivenessEnabled)
                    {
                        ApplySystemResponsiveness(_snapshot);
                        appliedAnything = true;
                    }

                    if (settings.AggressiveProfileTweaks.MmcssGamesPriorityEnabled)
                    {
                        ApplySchedulingCategory(_snapshot);
                        appliedAnything = true;
                    }
                }
            }

            string? resolvedExePath = ResolveRealExecutablePath(game);
            PerGameProfileSnapshot? perGame = null;

            if (settings.OptimizedProfileTweaks.GpuPreferenceEnabled && resolvedExePath != null)
            {
                perGame = new PerGameProfileSnapshot { GameId = game.Id };
                ApplyGpuPreference(perGame, resolvedExePath);
                appliedAnything = true;
            }

            if (game.PerformanceProfile == PerformanceProfileMode.Aggressive)
            {
                if (settings.AggressiveProfileTweaks.AboveNormalPriorityEnabled && process != null)
                {
                    ApplyAboveNormalPriority(process, game.Name);
                    appliedAnything = true;
                }

                if (settings.AggressiveProfileTweaks.DefenderExclusionEnabled && resolvedExePath != null)
                {
                    perGame ??= new PerGameProfileSnapshot { GameId = game.Id };
                    ApplyDefenderExclusion(perGame, resolvedExePath);
                    appliedAnything = true;
                }
            }

            if (perGame != null)
            {
                _snapshot.PerGameSnapshots.Add(perGame);
            }

            if (!appliedAnything)
            {
                LoggingService.Verbose("PerformanceProfile", $"'{game.Name}' requested {game.PerformanceProfile} but every applicable tweak is disabled (or not resolvable for this launch type) - nothing to apply.");
                if (isFirstSession && _snapshot.PerGameSnapshots.Count == 0 && !_snapshot.PowerPlanCaptured
                    && !_snapshot.SystemResponsivenessCaptured && !_snapshot.SchedulingCategoryCaptured)
                {
                    _snapshot = null;
                }
                return;
            }

            _storageService.SaveProfileSessionSnapshot(_snapshot);
            _activeSessionKeys.Add(game.Id);
            LoggingService.Info("PerformanceProfile", $"Applied {game.PerformanceProfile} profile for '{game.Name}'.");
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
    }

    private static void RestorePowerPlan(PerformanceProfileSessionSnapshot snapshot)
    {
        if (!snapshot.PowerPlanCaptured || string.IsNullOrWhiteSpace(snapshot.PreviousPowerSchemeGuid)) return;
        SystemTweaksService.RunPowercfg($"/setactive {snapshot.PreviousPowerSchemeGuid}");
    }

    private static void ApplySystemResponsiveness(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousSystemResponsiveness = ReadDword(SystemResponsivenessPath, "SystemResponsiveness");
        snapshot.SystemResponsivenessCaptured = true;

        // Microsoft's MMCSS docs: values below 10 are clamped back up to 20, so 10 is the
        // lowest reserve Windows actually honors.
        SystemTweaksService.SetHklmDword(SystemResponsivenessPath, "SystemResponsiveness", 10);
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
    }

    private static void ApplySchedulingCategory(PerformanceProfileSessionSnapshot snapshot)
    {
        snapshot.PreviousSchedulingCategory = ReadString(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category");
        snapshot.SchedulingCategoryCaptured = true;

        // SFIO Priority is intentionally not written - Microsoft's MMCSS docs state it "is not used".
        SystemTweaksService.SetHklmValuesBatch(
            (SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", "High", RegistryValueKind.String));
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
    }

    private static void RestoreGlobalTweaks(PerformanceProfileSessionSnapshot snapshot)
    {
        RestorePowerPlan(snapshot);
        RestoreSystemResponsiveness(snapshot);
        RestoreSchedulingCategory(snapshot);
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
