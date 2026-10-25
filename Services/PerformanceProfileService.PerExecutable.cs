using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>The tweaks made for one game's program, applied and put back on that game's own session: power throttling, GPU preference, priority and the Defender exclusion.</summary>
public partial class PerformanceProfileService
{
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
            {
                LoggingService.Verbose("PerformanceProfile", $"'{gameName}' is exempt from Windows power throttling.");
                NoteGame("exempt from Windows power throttling");
            }
            else
            {
                LoggingService.Info("PerformanceProfile", $"Windows refused to exempt '{gameName}' from power throttling (it may be running as administrator).");
                NoteGame("Windows refused the power throttling exemption (the game may run as administrator)");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Exempting '{gameName}' from power throttling failed: {ex.Message}");
            NoteGame("the power throttling exemption couldn't be set");
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
            // Only the GpuPreference token changes: Windows keeps the game's other per-app settings in the
            // same string (AppStatus, and AutoHDREnable with the game's Auto HDR intensity), and writing
            // the whole value would drop them for the length of the session.
            _backend.SetGpuPreference(exePath, DirectXGlobalSettings.SetToken(snapshot.PreviousGpuPreferenceValue, "GpuPreference", "2"));
            LoggingService.Verbose("PerformanceProfile", $"GPU Preference: set 'High performance' for '{exePath}' (was '{snapshot.PreviousGpuPreferenceValue ?? "unset"}').");
            Note("High performance GPU for the game's program");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyGpuPreference failed: {ex.Message}");
            Skipped("GPU preference couldn't be set");
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
            LoggingService.Verbose("PerformanceProfile", $"Graphics entry: restored for '{snapshot.GpuPreferenceExecutablePath}' to '{snapshot.PreviousGpuPreferenceValue ?? "unset"}'.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"RestoreGpuPreference failed: {ex.Message}");
            string path = snapshot.GpuPreferenceExecutablePath;
            string? previous = snapshot.PreviousGpuPreferenceValue;
            NoteUnrestored($"the graphics settings for {Path.GetFileName(path)}", () =>
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
            NoteGame("Above Normal priority");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"Failed to raise process priority for '{gameName}': {ex.Message}");
            // Access denied is what an elevated game gives a normal TrayTrigger.
            NoteGame(ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException
                ? "Above Normal priority couldn't be set (the game may run as administrator)"
                : "Above Normal priority couldn't be set");
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
                Note("Defender exclusion for the game's program");
            }
            else
            {
                LoggingService.Verbose("PerformanceProfile", $"Defender Exclusion: '{exePath}' was already excluded; leaving as-is.");
                Skipped("Defender exclusion, already there");
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("PerformanceProfile", $"ApplyDefenderExclusion failed: {ex.Message}");
            Skipped("Defender exclusion couldn't be added");
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
