#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// <c>--test-dlss [executable-path-or-name]</c>: prints everything that can be observed about DLSS
/// for one game, and changes nothing. The one thing worth having when someone reports that DLSS
/// is not doing what they expect.
///
/// <para>It reads well beyond what the card does - the Global profile, the NGX registry, the
/// modules a running game has loaded - and does that reading here, so none of it is in a release
/// build.</para>
///
/// <para>The report goes to a file as well as the console: TrayTrigger is a WinExe, so a run
/// started from anywhere but a console has nowhere to print.</para>
/// </summary>
public partial class App
{
    private const string NgxCoreKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";

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

        var (launched, installDir) = ResolveProbeTarget(target);
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
        string exePath = DlssProbeService.ResolveRenderingExecutable(launched, installDir);
        o.AppendLine($"Launched      : {launched}");
        if (exePath != launched) o.AppendLine($"Renders       : {exePath}");

        using Process? running = FindRunningProcess(exePath);
        o.AppendLine($"Running       : {(running == null ? "no" : $"yes (pid {running.Id})")}");

        var gpu = new SystemInfoService().GetGpuInfoList()
            .FirstOrDefault(g => g.ModelName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        o.AppendLine($"GPU           : {gpu?.ModelName ?? "not an NVIDIA machine"}");
        o.AppendLine($"Driver        : {gpu?.DriverVersion ?? "unknown"}");
        o.AppendLine();

        PrintDriverDatabase(o, exePath);
        PrintShipped(o, exePath);
        PrintDriverStore(o);
        PrintLoaded(o, exePath, running);
        PrintRegistry(o);

        o.AppendLine();
        o.AppendLine("=== END (nothing was modified) ===");
    }

    /// <summary>The game's profile, its DLSS settings, and the Global profile's - one session.</summary>
    private static void PrintDriverDatabase(StringBuilder o, string exePath)
    {
        string exeName = Path.GetFileName(exePath);

        using var session = NvApi.Session.TryOpen(out string? openError);
        if (session == null)
        {
            o.AppendLine($"-- Driver settings database: unavailable ({openError}) --");
            o.AppendLine();
            return;
        }

        o.AppendLine("-- Driver profile --");
        var profile = session.FindProfileForExecutable(exePath, out IntPtr handle, out string? findError)
            ?? session.FindProfileForExecutable(exeName, out handle, out findError);
        if (profile == null)
        {
            // Not a failure: NVIDIA has no entry for this executable, and applying would create one.
            o.AppendLine($"  None for {exeName}: {findError}");
        }
        else
        {
            o.AppendLine($"  Name        : {profile.ProfileName}");
            o.AppendLine($"  Predefined  : {(profile.IsPredefined ? "yes (shipped by NVIDIA)" : "no (created by a user or tool)")}");
            o.AppendLine($"  Applications: {profile.ApplicationCount}   Settings: {profile.SettingCount}");
        }
        o.AppendLine();

        o.AppendLine("-- DLSS settings, as the driver reports them for this game --");
        if (profile == null)
        {
            o.AppendLine("  (not read - no profile)");
        }
        else
        {
            foreach (var def in DlssProbeService.Settings)
            {
                var value = session.GetSetting(handle, def.Id, out _);
                o.AppendLine(value == null
                    ? $"  0x{def.Id:X8}  {def.Name,-38} absent"
                    : $"  0x{def.Id:X8}  {def.Name,-38} 0x{value.CurrentValue:X8}  [{NvApi.OriginLabel(value)}]");
            }
        }
        o.AppendLine();

        // Easy to forget about, and they make a per-game override look like a no-op: a value here
        // applies to every game that has no override of its own.
        o.AppendLine("-- Global profile: DLSS settings applying to every game --");
        var known = DlssProbeService.Settings.Select(d => d.Id).ToHashSet();
        var global = session.GetGlobalProfile(out IntPtr globalHandle, out _) == null
            ? new List<NvApi.DrsSettingValue>()
            : session.EnumSettings(globalHandle, out _).Where(v => known.Contains(v.SettingId)).ToList();
        if (global.Count == 0) o.AppendLine("  (none)");
        foreach (var v in global)
            o.AppendLine($"  0x{v.SettingId:X8}  {v.Name,-38} 0x{v.CurrentValue:X8}  [{NvApi.OriginLabel(v)}]");
        o.AppendLine();
    }

    private static void PrintShipped(StringBuilder o, string exePath)
    {
        o.AppendLine("-- DLSS runtimes the game ships --");
        var shipped = DlssProbeService.FindShippedRuntimes(DlssProbeService.DlssSearchRoot(exePath) ?? string.Empty);
        if (shipped.Count == 0) o.AppendLine("  (none found)");
        foreach (var s in shipped)
            o.AppendLine($"  {s.Feature,-18} {s.FileVersion ?? "?",-12} {s.RelativePath}");
        o.AppendLine();
    }

    private static void PrintDriverStore(StringBuilder o)
    {
        o.AppendLine("-- Runtimes the driver already holds --");
        var newest = NgxModelStore.Enumerate()
            .GroupBy(r => r.Feature, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.EncodedVersion).First())
            .OrderBy(r => r.Feature, StringComparer.Ordinal)
            .ToList();
        if (newest.Count == 0) o.AppendLine($"  (no model store at {NgxModelStore.DefaultRoot})");
        foreach (var r in newest)
            o.AppendLine($"  {r.Feature,-18} newest {r.Version,-10} {r.SizeBytes / 1024 / 1024} MB");
        o.AppendLine();
    }

    private static void PrintLoaded(StringBuilder o, string exePath, Process? running)
    {
        o.AppendLine("-- DLSS modules loaded right now --");
        var loaded = DlssProbeService.ScanLoadedModules(running, out string? note);
        if (loaded.Count == 0) o.AppendLine($"  {note}");

        string gameDir = Path.GetDirectoryName(exePath) ?? string.Empty;
        foreach (var m in loaded)
        {
            o.AppendLine($"  [{SourceLabel(m, gameDir)}] {m.ModuleName}");
            o.AppendLine($"      {m.Path}");
        }
        o.AppendLine();
    }

    private static void PrintRegistry(StringBuilder o)
    {
        o.AppendLine($@"-- NGX diagnostics registry (HKLM\{NgxCoreKey}) --");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(NgxCoreKey);
            foreach (string name in new[] { "ShowDlssIndicator", "LogLevel", "EnableLogPathOverride", "LogPath" })
                o.AppendLine($"  {name,-24} {key?.GetValue(name)?.ToString() ?? "absent"}");
        }
        catch (Exception ex)
        {
            o.AppendLine($"  (could not be read: {ex.Message})");
        }
    }

    /// <summary>
    /// Where a loaded module came from, in three kinds rather than two. "Not the NGX store" does
    /// not mean "the game folder": NVIDIA's own loader lives under Windows' DriverStore.
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
    private static (string? Path, string? InstallDirectory) ResolveProbeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return (null, null);
        if (target.Contains('\\') || target.Contains('/')) return (target, null);

        var app = (App)Current;
        var match = app._mainViewModel?.Games
            .FirstOrDefault(g => !string.IsNullOrEmpty(g.Game.ExecutablePath) &&
                                 (g.Name.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                                  Path.GetFileName(g.Game.ExecutablePath).Contains(target, StringComparison.OrdinalIgnoreCase)));

        // The install folder too: a Steam entry's path is a link, and that folder is all there is.
        return (match?.Game.ExecutablePath ?? target, match?.Game.WorkingDirectory);
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
        catch { /* a diagnostics dump reports what it can */ return null; }
    }
}
#endif
