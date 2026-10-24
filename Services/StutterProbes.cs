using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// The reads Stutter Check makes on the live PC, kept apart from the check itself so the check is
/// a pure function. Each probe swallows its own failures and answers "unknown" rather than
/// throwing: a check that can't be made is left out, never shown as a problem.
/// </summary>
public static class StutterProbes
{
    /// <summary>Process names of apps that draw an in-game overlay, and how the card names them.</summary>
    private static readonly (string Process, string Name)[] OverlayApps =
    {
        ("Discord", "Discord"),
        ("NVIDIA Overlay", "NVIDIA App overlay"),
        ("RTSS", "RivaTuner (RTSS)"),
        ("Medal", "Medal"),
        ("Overwolf", "Overwolf"),
        ("GameBar", "Xbox Game Bar"),
    };

    public static IReadOnlyList<string> RunningOverlayApps()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcesses();
            var running = new HashSet<string>(processes.Select(SafeName), StringComparer.OrdinalIgnoreCase);
            return OverlayApps.Where(a => running.Contains(a.Process)).Select(a => a.Name).ToList();
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("StutterCheck", $"Couldn't list processes for the overlay check: {ex.Message}");
            return [];
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch (InvalidOperationException)
        {
            // The process exited between the listing and this read: it has no name and isn't an overlay app.
            return "";
        }
    }

    /// <summary>Chrome, Edge and Discord: installed, hardware acceleration on, running, and GPU memory held.</summary>
    public static IReadOnlyList<AppAccelerationState> AppAccelerationStates()
    {
        var result = new List<AppAccelerationState>();
        Dictionary<int, long> gpuByPid;
        try { gpuByPid = GpuTelemetryService.ReadProcessDedicatedMemory(); }
        catch (Exception ex)
        {
            LoggingService.Verbose("StutterCheck", $"Per-process GPU memory unavailable: {ex.Message}");
            gpuByPid = new Dictionary<int, long>();
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        result.Add(Chromium("Chrome", Path.Combine(local, "Google", "Chrome", "User Data", "Local State"), "chrome", gpuByPid));
        result.Add(Chromium("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data", "Local State"), "msedge", gpuByPid));
        result.Add(Chromium("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Local State"), "brave", gpuByPid));
        result.Add(Discord(Path.Combine(roaming, "discord", "settings.json"), gpuByPid));
        return result;
    }

    /// <summary>A Chromium browser's Local State: hardware_acceleration_mode.enabled is only written when someone turned it off.</summary>
    internal static AppAccelerationState Chromium(string name, string localStatePath, string processName, IReadOnlyDictionary<int, long> gpuByPid)
    {
        bool installed = File.Exists(localStatePath);
        bool accel = installed && ChromiumAccelerationOn(ReadAllTextOrNull(localStatePath));
        var (running, bytes) = RunningAndMemory(processName, gpuByPid);
        return new AppAccelerationState(name, installed, accel, running, bytes);
    }

    internal static bool ChromiumAccelerationOn(string? localStateJson)
    {
        if (string.IsNullOrEmpty(localStateJson)) return true;
        try
        {
            using var doc = JsonDocument.Parse(localStateJson);
            if (doc.RootElement.TryGetProperty("hardware_acceleration_mode", out var mode)
                && mode.ValueKind == JsonValueKind.Object
                && mode.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }
        catch (JsonException ex)
        {
            LoggingService.Swallowed("StutterCheck", ex, "reading a browser's Local State");
        }
        return true;
    }

    /// <summary>Discord's settings.json: enableHardwareAcceleration is absent until someone turns it off.</summary>
    internal static AppAccelerationState Discord(string settingsPath, IReadOnlyDictionary<int, long> gpuByPid)
    {
        bool installed = File.Exists(settingsPath);
        bool accel = installed && DiscordAccelerationOn(ReadAllTextOrNull(settingsPath));
        var (running, bytes) = RunningAndMemory("Discord", gpuByPid);
        return new AppAccelerationState("Discord", installed, accel, running, bytes);
    }

    internal static bool DiscordAccelerationOn(string? settingsJson)
    {
        if (string.IsNullOrEmpty(settingsJson)) return true;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty("enableHardwareAcceleration", out var v) && v.ValueKind == JsonValueKind.False)
                return false;
        }
        catch (JsonException ex)
        {
            LoggingService.Swallowed("StutterCheck", ex, "reading Discord's settings.json");
        }
        return true;
    }

    private static (bool Running, long Bytes) RunningAndMemory(string processName, IReadOnlyDictionary<int, long> gpuByPid)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            long bytes = 0;
            foreach (var p in procs)
            {
                if (gpuByPid.TryGetValue(p.Id, out long b)) bytes += b;
                p.Dispose();
            }
            return (procs.Length > 0, bytes);
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed("StutterCheck", ex, $"listing {processName} processes");
            return (false, 0);
        }
    }

    private static string? ReadAllTextOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex)
        {
            LoggingService.Swallowed("StutterCheck", ex, $"reading {Path.GetFileName(path)}");
            return null;
        }
    }

    /// <summary>
    /// Whether SearchIndexer is working now: its CPU over a short sample. Null when it isn't
    /// running (the service is off or idle enough to have exited), so the check is left out.
    /// </summary>
    public static (bool? Busy, double CpuPercent) SearchIndexerBusy(int sampleMs = 400)
    {
        Process[] procs = [];
        try
        {
            procs = Process.GetProcessesByName("SearchIndexer");
            if (procs.Length == 0) return (null, 0);
            var p = procs[0];
            var start = p.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            Thread.Sleep(sampleMs);
            p.Refresh();
            var used = p.TotalProcessorTime - start;
            sw.Stop();
            double percent = used.TotalMilliseconds / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
            return (percent >= 3, Math.Round(percent, 1));
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("StutterCheck", $"Couldn't read the indexer's CPU: {ex.Message}");
            return (null, 0);
        }
        finally
        {
            foreach (var q in procs) q.Dispose();
        }
    }

    /// <summary>
    /// How the paging file is configured: the PagingFiles setting under Session Manager's Memory
    /// Management key. Empty means none; "?:\pagefile.sys" or sizes of 0 0 mean Windows manages it;
    /// "C:\pagefile.sys 1024 2048" is a custom file with a minimum and maximum in MB.
    /// </summary>
    public static PagefileState Pagefile()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            return ParsePagefile(key?.GetValue("PagingFiles") as string[]);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("StutterCheck", $"Could not read the paging file setting: {ex.Message}");
            return new PagefileState(PagefileKind.Unknown);
        }
    }

    internal static PagefileState ParsePagefile(string[]? lines)
    {
        if (lines == null) return new PagefileState(PagefileKind.Unknown);
        var entries = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (entries.Count == 0) return new PagefileState(PagefileKind.None);

        long total = 0;
        foreach (var entry in entries)
        {
            var parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool sized = parts.Length >= 3 && long.TryParse(parts[^2], out long min) && long.TryParse(parts[^1], out long max) && (min > 0 || max > 0);
            if (!sized) return new PagefileState(PagefileKind.SystemManaged);
            total += long.Parse(parts[^1]);
        }
        return new PagefileState(PagefileKind.Custom, total);
    }

    /// <summary>The registry name of AMD's 3D V-Cache Performance Optimizer service.</summary>
    internal const string VCacheServiceName = "amd3dvcacheSvc";

    /// <summary>
    /// Whether AMD's V-Cache optimizer service is running. Asked only on a dual-CCD X3D CPU: on a
    /// single-CCD one the service is stopped by design, and elsewhere it isn't needed.
    /// </summary>
    public static VCacheOptimizerState VCacheOptimizer(CpuCoreLayout layout)
    {
        if (layout != CpuCoreLayout.VCacheMultiCcd) return VCacheOptimizerState.NotApplicable;
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {VCacheServiceName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (BoundedProcess.Run(psi, 5000) is not { Completed: true } result) return VCacheOptimizerState.Unknown;
            return ParseServiceQuery(result.Output, result.ExitCode);
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("StutterCheck", $"Couldn't ask about the V-Cache optimizer service: {ex.Message}");
            return VCacheOptimizerState.Unknown;
        }
    }

    /// <summary>
    /// Reads "sc query" for one service. The STATE line starts with a number whatever the Windows
    /// language (4 is running); exit code 1060 means the service isn't installed.
    /// </summary>
    internal static VCacheOptimizerState ParseServiceQuery(string? output, int exitCode)
    {
        if (exitCode == 1060) return VCacheOptimizerState.NotRunning;
        var match = Regex.Match(output ?? "", @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return VCacheOptimizerState.Unknown;
        return match.Groups[1].Value == "4" ? VCacheOptimizerState.Running : VCacheOptimizerState.NotRunning;
    }

    /// <summary>Core Ultra desktop and mobile parts, and the 14th-gen desktop K parts Intel added APO to. The "(TM)" is optional: the name shown in Hardware Specs has it removed.</summary>
    private static readonly Regex ApoCpu = new(@"Core(?:\(TM\))?\s+(?:Ultra|i[579]-14\d{3}K)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IntelApoState IntelApo(string cpuModelName)
    {
        if (string.IsNullOrWhiteSpace(cpuModelName) || !ApoCpu.IsMatch(cpuModelName)) return IntelApoState.NotNeeded;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (key == null) return IntelApoState.Unknown;
            return key.GetSubKeyNames().Any(n => n.StartsWith("AppUp.IntelApplicationOptimization", StringComparison.OrdinalIgnoreCase))
                ? IntelApoState.Present
                : IntelApoState.Missing;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("StutterCheck", $"Couldn't look for Intel APO: {ex.Message}");
            return IntelApoState.Unknown;
        }
    }

    /// <summary>Exposed for tests: whether a CPU name is one APO applies to.</summary>
    internal static bool IsApoCpu(string cpuModelName) => ApoCpu.IsMatch(cpuModelName ?? "");
}
