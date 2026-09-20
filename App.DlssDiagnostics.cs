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
/// <para>There were eight of these flags while the feature was being built - apply, undo,
/// round-trip, write-check, launch. They existed to answer questions that are now answered, and
/// each was a second implementation of something the product already does. Only the read-only
/// report survives, because it is the one worth having when someone reports that DLSS is not
/// doing what they expect.</para>
///
/// <para>The report goes to a file as well as the console: TrayTrigger is a WinExe, so a run
/// started from anywhere but a console has nowhere to print.</para>
/// </summary>
public partial class App
{
    private static void RunDlssDiagnostic(string? target)
    {
        var sb = new StringBuilder();
        try
        {
            BuildDlssReport(sb, target);
        }
        catch (Exception ex)
        {
            // A probe that throws tells us nothing; one that records why it threw still does.
            sb.AppendLine().AppendLine($"!! Probe threw: {ex}");
        }

        string report = sb.ToString();
        Console.Write(report);

        try
        {
            string path = Path.Combine(
                Path.GetDirectoryName(LoggingService.LogFilePath) ?? Path.GetTempPath(), "dlss-probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            Console.WriteLine($"Report written to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write the report file: {ex.Message}");
        }
    }

    private static void BuildDlssReport(StringBuilder o, string? target)
    {
        o.AppendLine("=== DLSS PROBE (read-only) ===");
        o.AppendLine($"When          : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        o.AppendLine($"Elevated      : {SystemTweaksService.IsElevated}");

        string? launched = ResolveProbeTarget(target);
        if (launched == null)
        {
            // The driver-side half does not depend on a game.
            PrintDriverStore(o);
            o.AppendLine();
            o.AppendLine("No executable given. Pass a path or an .exe name for the per-game half.");
            return;
        }

        // Driver profiles key on the executable that renders, which for a launcher-based game is
        // not the one TrayTrigger launches.
        string exePath = DlssProbeService.ResolveRenderingExecutable(launched);
        o.AppendLine($"Launched      : {launched}");
        if (exePath != launched) o.AppendLine($"Renders       : {exePath}");

        using Process? running = FindRunningProcess(exePath);
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
        o.AppendLine("=== END (nothing was modified) ===");
    }

    private static void PrintProfile(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- Driver profile --");
        if (r.Profile == null)
        {
            // Not a failure: NVIDIA has no entry for this executable, and applying would create one.
            o.AppendLine($"  None for {r.ExecutableName}: {r.DrsError}");
        }
        else
        {
            o.AppendLine($"  Name        : {r.Profile.ProfileName}");
            o.AppendLine($"  Predefined  : {(r.Profile.IsPredefined ? "yes (shipped by NVIDIA)" : "no (created by a user or tool)")}");
            o.AppendLine($"  Applications: {r.Profile.ApplicationCount}   Settings: {r.Profile.SettingCount}");
        }
        o.AppendLine();
    }

    private static void PrintSettings(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- DLSS settings, as the driver reports them for this game --");
        if (r.SettingStates.Count == 0) o.AppendLine("  (not read - no profile)");
        foreach (var s in r.SettingStates)
        {
            o.AppendLine(s.Value == null
                ? $"  0x{s.Definition.Id:X8}  {s.Definition.Name,-38} absent"
                : $"  0x{s.Definition.Id:X8}  {s.Definition.Name,-38} 0x{s.Value.CurrentValue:X8}  [{s.Value.OriginLabel}]");
        }
        o.AppendLine();
    }

    private static void PrintGlobalProfile(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- Global profile: DLSS settings applying to every game --");
        if (r.GlobalProfileDlssSettings.Count == 0)
        {
            o.AppendLine("  (none)");
        }
        else
        {
            // Easy to forget about, and they make a per-game override look like a no-op.
            foreach (var s in r.GlobalProfileDlssSettings)
                o.AppendLine($"  0x{s.SettingId:X8}  {s.Name,-38} 0x{s.CurrentValue:X8}  [{s.OriginLabel}]");
        }
        o.AppendLine();
    }

    private static void PrintShipped(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- DLSS runtimes the game ships --");
        if (r.ShippedRuntimes.Count == 0) o.AppendLine("  (none found)");
        foreach (var s in r.ShippedRuntimes)
            o.AppendLine($"  {s.Feature,-18} {s.FileVersion ?? "?",-12} {s.RelativePath}");
        o.AppendLine();
    }

    private static void PrintDriverStore(StringBuilder o)
    {
        o.AppendLine("-- Runtimes the driver already holds --");
        var newest = NgxModelStore.NewestPerFeature();
        if (newest.Count == 0)
        {
            o.AppendLine($"  (no model store at {NgxModelStore.DefaultRoot})");
        }
        else
        {
            foreach (var kv in newest.OrderBy(k => k.Key, StringComparer.Ordinal))
                o.AppendLine($"  {kv.Key,-18} newest {kv.Value.Version,-10} {kv.Value.SizeBytes / 1024 / 1024} MB");
        }
        o.AppendLine();
    }

    private static void PrintLoaded(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine("-- DLSS modules loaded right now --");
        if (r.LoadedRuntimes.Count == 0) o.AppendLine($"  {r.ModuleScanNote}");

        string gameDir = Path.GetDirectoryName(r.ExecutablePath) ?? string.Empty;
        foreach (var m in r.LoadedRuntimes)
        {
            o.AppendLine($"  [{SourceLabel(m, gameDir)}] {m.ModuleName}");
            o.AppendLine($"      {m.Path}");
        }
        o.AppendLine();
    }

    private static void PrintRegistry(StringBuilder o, DlssProbeService.ProbeResult r)
    {
        o.AppendLine(@"-- NGX diagnostics registry (HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore) --");
        foreach (var kv in r.NgxRegistry)
            o.AppendLine($"  {kv.Key,-24} {kv.Value?.ToString() ?? "absent"}");
    }

    /// <summary>
    /// Where a loaded module came from, in three kinds rather than two. "Not the NGX store" does
    /// not mean "the game folder": NVIDIA's own loader lives under Windows' DriverStore, which an
    /// earlier version of this report labelled as the game's.
    /// </summary>
    private static string SourceLabel(DlssProbeService.LoadedRuntime module, string gameDirectory) =>
        module.FromDriverStore ? "DRIVER STORE"
        : !string.IsNullOrEmpty(gameDirectory) && module.Path.StartsWith(gameDirectory, StringComparison.OrdinalIgnoreCase) ? "game folder"
        : "elsewhere";

    /// <summary>
    /// Accepts a full path, or a bare name matched against the library. A bare name that matches
    /// nothing is still probed, so an executable NVIDIA has never seen can be tested without
    /// importing it first.
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
