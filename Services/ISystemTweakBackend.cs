using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// The machine-touching half of <see cref="PerformanceProfileService"/>: every registry write,
/// powercfg call, HDR toggle, and Defender cmdlet goes through here. The default
/// <see cref="WindowsTweakBackend"/> delegates to the static helpers in
/// <see cref="SystemTweaksService"/>/<see cref="HdrControlService"/>; tests substitute a fake so the
/// session bookkeeping (first-wins, per-game restore, crash recovery) can be exercised without
/// touching the real system.
/// </summary>
public interface ISystemTweakBackend
{
    /// <summary>True when TrayTrigger itself runs elevated, so HKLM writes need no UAC prompt.</summary>
    bool IsElevated { get; }

    string? GetActivePowerSchemeGuid();
    /// <summary>Finds or creates the "Ultimate Plan - TrayTrigger" scheme, applies its tweaks and
    /// makes it active. Returns false if the scheme could not be created or located.</summary>
    bool ActivateUltimatePowerPlan();
    void SetActivePowerScheme(string schemeGuid);

    int? ReadHklmDword(string subKey, string valueName);
    string? ReadHklmString(string subKey, string valueName);
    bool WriteHklmDword(string subKey, string valueName, int value);
    bool WriteHklmString(string subKey, string valueName, string value);
    bool DeleteHklmValue(string subKey, string valueName);

    List<HdrControlService.DisplayColorState> GetHdrDisplayStates();
    bool SetDisplayHdrEnabled(HdrControlService.LUID adapterId, uint targetId, bool enable);

    string? GetGpuPreference(string exePath);
    void SetGpuPreference(string exePath, string value);
    void DeleteGpuPreference(string exePath);

    HashSet<string> GetDefenderExclusionPaths();
    bool AddDefenderExclusion(string exePath);
    bool RemoveDefenderExclusion(string exePath);

    void SetProcessPriority(Process process, ProcessPriorityClass priority);

    /// <summary>Holds a 0.5 ms system timer request from this process until released. Returns the resolution actually granted (100 ns units), or 0 on failure.</summary>
    uint RequestHighTimerResolution();
    void ReleaseHighTimerResolution();

    /// <summary>The notification centre's global toast switch (NOC_GLOBAL_SETTING_TOASTS_ENABLED): null = value absent = enabled.</summary>
    int? GetToastsEnabled();
    void SetToastsEnabled(int? value);
}

/// <summary>Production <see cref="ISystemTweakBackend"/> - thin pass-throughs, no logic.</summary>
public sealed class WindowsTweakBackend : ISystemTweakBackend
{
    private const string GpuPreferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public bool IsElevated => SystemTweaksService.IsElevated;

    public string? GetActivePowerSchemeGuid() => SystemTweaksService.GetActivePowerSchemeGuid();

    public bool ActivateUltimatePowerPlan()
    {
        string? schemeGuid = SystemTweaksService.FindExistingUltimatePlanGuid() ?? SystemTweaksService.CreateUltimateTrayTriggerPlan();
        if (string.IsNullOrWhiteSpace(schemeGuid)) return false;
        SystemTweaksService.ApplyUltimatePlanTweaks(schemeGuid);
        SystemTweaksService.RunPowercfg($"/setactive {schemeGuid}");
        return true;
    }

    public void SetActivePowerScheme(string schemeGuid) => SystemTweaksService.RunPowercfg($"/setactive {schemeGuid}");

    public int? ReadHklmDword(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) is int i ? i : null;
        }
        catch { return null; }
    }

    public string? ReadHklmString(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    public bool WriteHklmDword(string subKey, string valueName, int value) => SystemTweaksService.SetHklmDword(subKey, valueName, value);

    public bool WriteHklmString(string subKey, string valueName, string value) =>
        SystemTweaksService.SetHklmValuesBatch((subKey, valueName, value, RegistryValueKind.String));

    public bool DeleteHklmValue(string subKey, string valueName) => SystemTweaksService.DeleteHklmValue(subKey, valueName);

    public List<HdrControlService.DisplayColorState> GetHdrDisplayStates() => HdrControlService.GetDisplayStates();

    public bool SetDisplayHdrEnabled(HdrControlService.LUID adapterId, uint targetId, bool enable) =>
        HdrControlService.SetDisplayHdrEnabled(adapterId, targetId, enable);

    public string? GetGpuPreference(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesPath);
        return key?.GetValue(exePath) as string;
    }

    public void SetGpuPreference(string exePath, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesPath);
        key?.SetValue(exePath, value, RegistryValueKind.String);
    }

    public void DeleteGpuPreference(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(GpuPreferencesPath);
        key?.DeleteValue(exePath, throwOnMissingValue: false);
    }

    public HashSet<string> GetDefenderExclusionPaths()
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

    public bool AddDefenderExclusion(string exePath) =>
        RunElevatedPowerShell($"Add-MpPreference -ExclusionPath '{EscapeForPowerShellSingleQuoted(exePath)}'");

    public bool RemoveDefenderExclusion(string exePath) =>
        RunElevatedPowerShell($"Remove-MpPreference -ExclusionPath '{EscapeForPowerShellSingleQuoted(exePath)}'");

    public void SetProcessPriority(Process process, ProcessPriorityClass priority) => process.PriorityClass = priority;

    // NtSetTimerResolution is the documented-by-usage kernel call behind timeBeginPeriod; it takes
    // 100 ns units, so 5000 = 0.5 ms (the finest most hardware supports). The request is scoped to
    // this process and released explicitly - Windows also drops it if TrayTrigger exits.
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution, bool setResolution, out uint currentResolution);

    private const uint HalfMillisecond = 5000;

    public uint RequestHighTimerResolution()
    {
        try
        {
            int status = NtSetTimerResolution(HalfMillisecond, true, out uint current);
            return status == 0 ? current : 0;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"NtSetTimerResolution failed: {ex.Message}");
            return 0;
        }
    }

    public void ReleaseHighTimerResolution()
    {
        try { NtSetTimerResolution(HalfMillisecond, false, out _); } catch { }
    }

    private const string NotificationSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ToastsEnabledValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    public int? GetToastsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NotificationSettingsPath);
            return key?.GetValue(ToastsEnabledValue) is int i ? i : null;
        }
        catch { return null; }
    }

    public void SetToastsEnabled(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(NotificationSettingsPath);
        if (key == null) return;
        if (value.HasValue) key.SetValue(ToastsEnabledValue, value.Value, RegistryValueKind.DWord);
        else key.DeleteValue(ToastsEnabledValue, throwOnMissingValue: false);
    }

    private static string EscapeForPowerShellSingleQuoted(string value) => value.Replace("'", "''");

    /// <summary>
    /// Modern Windows (Tamper Protection) blocks direct registry writes to Defender's exclusion
    /// list, so exclusions go through the supported Add-/Remove-MpPreference cmdlets, elevated via
    /// UAC when TrayTrigger itself isn't. The command text is built only from single-quote-escaped
    /// paths, never raw data.
    /// </summary>
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
}
