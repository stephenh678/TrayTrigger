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

    /// <summary>Adds the exclusion unless it already exists. True only when this call added it.</summary>
    bool AddDefenderExclusion(string exePath);
    bool RemoveDefenderExclusion(string exePath);

    void SetProcessPriority(Process process, ProcessPriorityClass priority);

    /// <summary>Holds a 0.5 ms system timer request from this process until released. Returns the resolution actually granted (100 ns units), or 0 on failure.</summary>
    uint RequestHighTimerResolution();
    void ReleaseHighTimerResolution();

    /// <summary>The notification centre's global toast switch (NOC_GLOBAL_SETTING_TOASTS_ENABLED): null = value absent = enabled.</summary>
    int? GetToastsEnabled();
    void SetToastsEnabled(int? value);

    /// <summary>The default playback device's mute flag; null when the machine has no playback device.</summary>
    bool? GetDefaultPlaybackMuted();
    /// <summary>Sets the default playback device's mute flag. False when there was nothing to set.</summary>
    bool SetDefaultPlaybackMuted(bool muted);
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
        catch (Exception ex) { LoggingService.Swallowed("SystemTweaks", ex, "reading a registry number"); return null; }
    }

    public string? ReadHklmString(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) { LoggingService.Swallowed("SystemTweaks", ex, "reading a registry string"); return null; }
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

    /// <summary>
    /// Returns true only when this call added the exclusion. The presence check runs inside the
    /// elevated command because an unelevated process cannot read Defender's exclusion list (it
    /// answers "N/A: Must be an administrator to view exclusions"), so an exclusion the user
    /// already had would otherwise look new and be removed on restore.
    /// </summary>
    public bool AddDefenderExclusion(string exePath)
    {
        string path = ElevatedPowerShell.QuoteLiteral(exePath);
        // -ErrorAction Stop: Add-MpPreference reports most failures as non-terminating errors, which
        // would otherwise leave exit code 0 and record an exclusion that was never added.
        return RunElevatedPowerShell($"if (@((Get-MpPreference).ExclusionPath) -contains {path}) {{ exit 3 }}; Add-MpPreference -ExclusionPath {path} -ErrorAction Stop");
    }

    public bool RemoveDefenderExclusion(string exePath) =>
        RunElevatedPowerShell($"Remove-MpPreference -ExclusionPath {ElevatedPowerShell.QuoteLiteral(exePath)} -ErrorAction Stop");

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
        try { NtSetTimerResolution(HalfMillisecond, false, out _); } catch (Exception ex) { LoggingService.Swallowed("SystemTweaks", ex, "releasing the timer resolution request"); }
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
        catch (Exception ex) { LoggingService.Swallowed("SystemTweaks", ex, "reading the notifications setting"); return null; }
    }

    public void SetToastsEnabled(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(NotificationSettingsPath);
        if (key == null) return;
        if (value.HasValue) key.SetValue(ToastsEnabledValue, value.Value, RegistryValueKind.DWord);
        else key.DeleteValue(ToastsEnabledValue, throwOnMissingValue: false);
    }

    public bool? GetDefaultPlaybackMuted() => AudioEndpointService.GetDefaultRenderMuted();

    public bool SetDefaultPlaybackMuted(bool muted) => AudioEndpointService.SetDefaultRenderMuted(muted);

    /// <summary>
    /// Modern Windows (Tamper Protection) blocks direct registry writes to Defender's exclusion
    /// list, so exclusions go through the supported Add-/Remove-MpPreference cmdlets, elevated via
    /// UAC when TrayTrigger itself isn't. Paths reach the command only through
    /// <see cref="ElevatedPowerShell.QuoteLiteral"/>, never raw.
    /// </summary>
    private static bool RunElevatedPowerShell(string script) =>
        ElevatedPowerShell.Run(script, TimeSpan.FromMinutes(2), "PerformanceProfile");
}
