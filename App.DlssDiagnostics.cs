#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// <c>--test-dlss [executable-path-or-name]</c>: prints everything
/// <see cref="DlssProbeService"/> can observe, and changes nothing.
///
/// <para>This replaces the hand-run PowerShell spike in docs/dlss-plan.md with the code that will
/// later back the UI, so the numbers on screen and the numbers in a spike come from one
/// implementation. Read-only by construction - there is no write path to reach from here.</para>
///
/// <para>The report goes to a file as well as the console. TrayTrigger is a WinExe, so a run
/// started from Explorer - which is how you start it <i>without</i> elevation, the one thing the
/// probe still needs to establish - has no console to attach to and would otherwise print into
/// the void.</para>
/// </summary>
public partial class App
{
    /// <summary>Where the report is always written, whatever the console situation.</summary>
    private static string DlssReportPath => Path.Combine(
        Path.GetDirectoryName(LoggingService.LogFilePath) ?? Path.GetTempPath(),
        "dlss-probe.txt");

    private static void RunDlssDiagnostic(string? target)
    {
        var sb = new StringBuilder();
        try
        {
            BuildDlssReport(sb, target);
        }
        catch (Exception ex)
        {
            // A probe that throws tells us nothing; a probe that records why it threw still does.
            sb.AppendLine().AppendLine($"!! Probe threw: {ex}");
        }

        string report = sb.ToString();
        Console.Write(report);

        try
        {
            string path = DlssReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            Console.WriteLine($"Report written to {path}");
            LoggingService.Info("Dlss", $"Probe report written to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write the report file: {ex.Message}");
            LoggingService.Warn("Dlss", $"Could not write the probe report: {ex.Message}");
        }
    }

    private static void BuildDlssReport(StringBuilder o, string? target)
    {
        o.AppendLine("=== DLSS PROBE ===");
        o.AppendLine($"When          : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // The headline fact for the open question in the plan. DRS lives in ProgramData, so reads
        // are expected to work either way - but "expected" is what the probe exists to replace.
        bool elevated = SystemTweaksService.IsElevated;
        o.AppendLine($"Elevated      : {(elevated ? "YES - says nothing about the unelevated case" : "NO")}");

        string? exePath = ResolveProbeTarget(target);
        if (exePath == null)
        {
            // No game named: the driver-side half does not depend on one.
            PrintDriverStore(o);
            o.AppendLine();
            o.AppendLine("No executable given. Pass a path or an .exe name for the per-game half.");
            return;
        }

        Process? running = FindRunningProcess(exePath);
        o.AppendLine($"Target        : {exePath}");
        o.AppendLine($"Running       : {(running == null ? "no" : $"yes (pid {running.Id})")}");

        var r = DlssProbeService.Probe(exePath, running);

        o.AppendLine($"GPU           : {r.GpuName ?? "not an NVIDIA machine"}");
        o.AppendLine($"Driver        : {r.DriverVersion ?? "unknown"}");
        o.AppendLine();

        PrintProfile(o, r);
        PrintSettings(o, r);
        PrintGlobalProfile(o, r);
        PrintShipped(o, r);
        PrintDriverStore(o);
        PrintLoaded(o, r);
        PrintRegistry(o, r);

        o.AppendLine();
        o.AppendLine(elevated
            ? "=== END (nothing was modified; run again unelevated to settle the read-elevation question) ==="
            : "=== END (nothing was modified; DRS reads confirmed working WITHOUT elevation) ===");
        running?.Dispose();
    }

    private static void PrintProfile(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- Driver profile --");
        if (r.DrsError != null && r.Profile == null)
        {
            // The headline question for unlisted games: no profile means NVIDIA has never heard of
            // this executable, and applying an override would have to create one.
            o.AppendLine($"  No application profile for {r.ExecutableName}: {r.DrsError}");
            o.AppendLine("  -> NVIDIA has no entry for this game. A profile would have to be created.");
        }
        else if (r.Profile != null)
        {
            o.AppendLine($"  Name        : {r.Profile.ProfileName}");
            o.AppendLine($"  Predefined  : {(r.Profile.IsPredefined ? "yes (shipped by NVIDIA)" : "no (created by a user or tool)")}");
            o.AppendLine($"  Applications: {r.Profile.ApplicationCount}   Settings: {r.Profile.SettingCount}");
            if (r.Profile.MatchedApplicationName != null)
                o.AppendLine($"  Matched exe : {r.Profile.MatchedApplicationName}  ({r.Profile.MatchedFriendlyName})");
        }
        o.AppendLine();
    }

    private static void PrintSettings(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- DLSS settings, as the driver reports them for this game --");
        if (r.SettingStates.Count == 0)
        {
            o.AppendLine("  (not read - no profile)");
            o.AppendLine();
            return;
        }
        foreach (var s in r.SettingStates)
        {
            if (s.Value == null)
                o.AppendLine($"  0x{s.Definition.Id:X8}  {s.Definition.Name,-38} Absent  ({s.Note})");
            else
                o.AppendLine($"  0x{s.Definition.Id:X8}  {s.Definition.Name,-38} 0x{s.Value.CurrentValue:X8}  [{s.Value.OriginLabel}]");
        }
        o.AppendLine();
    }

    private static void PrintGlobalProfile(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- Global profile: DLSS settings applying to every game --");
        if (r.GlobalProfileDlssSettings.Count == 0)
        {
            o.AppendLine("  (none - the Global profile carries no DLSS override)");
        }
        else
        {
            // On the development machine four of these were already set by another tool, days
            // before the spike, and nothing in any UI showed them. That is the confound the plan
            // records; surfacing it is the whole point of reading this layer.
            foreach (var s in r.GlobalProfileDlssSettings)
                o.AppendLine($"  0x{s.SettingId:X8}  {s.Name,-38} 0x{s.CurrentValue:X8}  [{s.OriginLabel}]");
            o.AppendLine("  -> A per-game 'use recommended' may be a no-op for this user.");
        }
        o.AppendLine();
    }

    private static void PrintShipped(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- DLSS runtimes the game ships --");
        if (r.ShippedRuntimes.Count == 0)
        {
            o.AppendLine("  (none found in the install folder)");
        }
        else
        {
            foreach (var s in r.ShippedRuntimes)
                o.AppendLine($"  {s.Feature,-18} {s.FileVersion ?? "?",-12} {s.RelativePath}");
        }
        o.AppendLine();
    }

    private static void PrintDriverStore(StringBuilder o)
    {
        o.AppendLine("-- Runtimes the driver already holds --");
        var newest = NgxModelStore.NewestPerFeature();
        if (newest.Count == 0)
        {
            o.AppendLine($"  (no model store at {NgxModelStore.DefaultRoot})");
            o.AppendLine();
            return;
        }
        foreach (var kv in newest.OrderBy(k => k.Key, StringComparer.Ordinal))
            o.AppendLine($"  {kv.Key,-18} newest {kv.Value.Version,-10} {kv.Value.SizeBytes / 1024 / 1024} MB  {Path.GetFileName(kv.Value.FilePath)}");

        int total = NgxModelStore.Enumerate().Count;
        o.AppendLine($"  {total} runtime file(s) in the store in total.");

        // nvngx_config.txt states outright which version each NGX app id maps to. The override
        // pseudo-app ids are the ones the sl_*_override_0 sections key on, so the versions listed
        // against them are what an override would load - a fact, not a "latest" promise.
        var config = NgxModelStore.ReadConfig();
        var overrideIds = config
            .Where(m => m.Section.EndsWith("_override_0", StringComparison.Ordinal))
            .Select(m => m.AppId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var mapped = config
            .Where(m => overrideIds.Contains(m.AppId) && m.Section is "dlss" or "dlssd" or "dlssg")
            .ToList();

        if (mapped.Count > 0)
        {
            o.AppendLine("  Override app id -> version (from nvngx_config.txt):");
            foreach (var m in mapped)
                o.AppendLine($"    [{m.Section}] {m.AppId} = {m.Version}");
        }
        o.AppendLine();
    }

    private static void PrintLoaded(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- DLSS modules loaded right now --");
        if (r.LoadedRuntimes.Count == 0)
        {
            o.AppendLine($"  {r.ModuleScanNote}");
        }
        else
        {
            foreach (var m in r.LoadedRuntimes)
            {
                string source = m.FromDriverStore ? "DRIVER STORE" : "game folder";
                o.AppendLine($"  [{source}] {m.ModuleName}  {m.FileVersion ?? "?"}");
                o.AppendLine($"      {m.Path}");
                if (m.ProductName != null) o.AppendLine($"      product: {m.ProductName}");
            }
        }
        o.AppendLine();
    }

    private static void PrintRegistry(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine(@"-- NGX diagnostics registry (HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore) --");
        foreach (var kv in r.NgxRegistry)
            o.AppendLine($"  {kv.Key,-24} {(kv.Value == null ? "absent" : kv.Value.ToString())}");
    }

    /// <summary>
    /// Accepts a full path, or a bare executable name matched against the library. A bare name
    /// that matches nothing is still probed, so an executable NVIDIA has never seen can be tested
    /// without importing it first.
    /// </summary>
    private static string? ResolveProbeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (target.Contains('\\') || target.Contains('/')) return target;

        var app = (App)Current;
        var match = app._mainViewModel?.Games
            .FirstOrDefault(g => !string.IsNullOrEmpty(g.Game.ExecutablePath) &&
                                 (g.Name.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                                  Path.GetFileName(g.Game.ExecutablePath).Contains(target, StringComparison.OrdinalIgnoreCase)));

        return match?.Game.ExecutablePath ?? target;
    }

    private static Process? FindRunningProcess(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var all = Process.GetProcessesByName(name);
            Process? best = all.FirstOrDefault();
            foreach (var p in all.Skip(1)) p.Dispose();
            return best;
        }
        catch { return null; }
    }
}
#endif
