#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// <c>--test-dlss [executable-path-or-name]</c>: prints everything
/// <see cref="DlssProbeService"/> can observe, and changes nothing.
///
/// <para>This replaces the hand-run PowerShell spike in docs/dlss-plan.md with the code that will
/// later back the UI, so the numbers on screen and the numbers in a spike come from one
/// implementation. Read-only by construction - there is no write path to reach from here.</para>
/// </summary>
public partial class App
{
    private static void RunDlssDiagnostic(string? target)
    {
        Console.WriteLine("=== DLSS PROBE ===");

        string? exePath = ResolveProbeTarget(target);
        if (exePath == null)
        {
            // No game named: still worth running, because the driver-side half of the report -
            // the model store and the override mapping - does not depend on a game at all.
            PrintDriverStore();
            Console.WriteLine();
            Console.WriteLine("No executable given. Pass a path or an .exe name for the per-game half.");
            return;
        }

        Process? running = FindRunningProcess(exePath);
        Console.WriteLine($"Target        : {exePath}");
        Console.WriteLine($"Running       : {(running == null ? "no" : $"yes (pid {running.Id})")}");

        var r = DlssProbeService.Probe(exePath, running);

        Console.WriteLine($"GPU           : {r.GpuName ?? "not an NVIDIA machine"}");
        Console.WriteLine($"Driver        : {r.DriverVersion ?? "unknown"}");
        Console.WriteLine();

        PrintProfile(r);
        PrintSettings(r);
        PrintGlobalProfile(r);
        PrintShipped(r);
        PrintDriverStore();
        PrintLoaded(r);
        PrintRegistry(r);

        Console.WriteLine();
        Console.WriteLine("=== END (nothing was modified) ===");
        running?.Dispose();
    }

    private static void PrintProfile(DlssProbeService.ProbeResult r)
    {
        Console.WriteLine("-- Driver profile --");
        if (r.DrsError != null && r.Profile == null)
        {
            // The headline question for unlisted games: no profile means NVIDIA has never heard of
            // this executable, and applying an override would have to create one.
            Console.WriteLine($"  No application profile for {r.ExecutableName}: {r.DrsError}");
            Console.WriteLine("  -> NVIDIA has no entry for this game. A profile would have to be created.");
        }
        else if (r.Profile != null)
        {
            Console.WriteLine($"  Name        : {r.Profile.ProfileName}");
            Console.WriteLine($"  Predefined  : {(r.Profile.IsPredefined ? "yes (shipped by NVIDIA)" : "no (created by a user or tool)")}");
            Console.WriteLine($"  Applications: {r.Profile.ApplicationCount}   Settings: {r.Profile.SettingCount}");
            if (r.Profile.MatchedApplicationName != null)
                Console.WriteLine($"  Matched exe : {r.Profile.MatchedApplicationName}  ({r.Profile.MatchedFriendlyName})");
        }
        Console.WriteLine();
    }

    private static void PrintSettings(DlssProbeService.ProbeResult r)
    {
        Console.WriteLine("-- DLSS settings, as the driver reports them for this game --");
        if (r.SettingStates.Count == 0)
        {
            Console.WriteLine("  (not read - no profile)");
            Console.WriteLine();
            return;
        }
        foreach (var s in r.SettingStates)
        {
            if (s.Value == null)
            {
                Console.WriteLine($"  0x{s.Definition.Id:X8}  {s.Definition.Name,-38} Absent  ({s.Note})");
            }
            else
            {
                Console.WriteLine($"  0x{s.Definition.Id:X8}  {s.Definition.Name,-38} 0x{s.Value.CurrentValue:X8}  [{s.Value.OriginLabel}]");
            }
        }
        Console.WriteLine();
    }

    private static void PrintGlobalProfile(DlssProbeService.ProbeResult r)
    {
        Console.WriteLine("-- Global profile: DLSS settings applying to every game --");
        if (r.GlobalProfileDlssSettings.Count == 0)
        {
            Console.WriteLine("  (none - the Global profile carries no DLSS override)");
        }
        else
        {
            // On the development machine four of these were already set by another tool, days
            // before the spike, and nothing in any UI showed them. That is the confound the plan
            // records; surfacing it is the whole point of reading this layer.
            foreach (var s in r.GlobalProfileDlssSettings)
                Console.WriteLine($"  0x{s.SettingId:X8}  {s.Name,-38} 0x{s.CurrentValue:X8}  [{s.OriginLabel}]");
            Console.WriteLine("  -> A per-game 'use recommended' may be a no-op for this user.");
        }
        Console.WriteLine();
    }

    private static void PrintShipped(DlssProbeService.ProbeResult r)
    {
        Console.WriteLine("-- DLSS runtimes the game ships --");
        if (r.ShippedRuntimes.Count == 0)
        {
            Console.WriteLine("  (none found in the install folder)");
        }
        else
        {
            foreach (var s in r.ShippedRuntimes)
                Console.WriteLine($"  {s.Feature,-18} {s.FileVersion ?? "?",-12} {s.RelativePath}");
        }
        Console.WriteLine();
    }

    private static void PrintDriverStore()
    {
        Console.WriteLine("-- Runtimes the driver already holds --");
        var newest = NgxModelStore.NewestPerFeature();
        if (newest.Count == 0)
        {
            Console.WriteLine($"  (no model store at {NgxModelStore.DefaultRoot})");
            Console.WriteLine();
            return;
        }
        foreach (var kv in newest.OrderBy(k => k.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {kv.Key,-18} newest {kv.Value.Version,-10} {kv.Value.SizeBytes / 1024 / 1024} MB  {Path.GetFileName(kv.Value.FilePath)}");

        int total = NgxModelStore.Enumerate().Count;
        Console.WriteLine($"  {total} runtime file(s) in the store in total.");

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
            .Where(m => overrideIds.Contains(m.AppId) &&
                        (m.Section is "dlss" or "dlssd" or "dlssg"))
            .ToList();

        if (mapped.Count > 0)
        {
            Console.WriteLine("  Override app id -> version (from nvngx_config.txt):");
            foreach (var m in mapped)
                Console.WriteLine($"    [{m.Section}] {m.AppId} = {m.Version}");
        }
        Console.WriteLine();
    }

    private static void PrintLoaded(DlssProbeService.ProbeResult r)
    {
        Console.WriteLine("-- DLSS modules loaded right now --");
        if (r.LoadedRuntimes.Count == 0)
        {
            Console.WriteLine($"  {r.ModuleScanNote}");
        }
        else
        {
            foreach (var m in r.LoadedRuntimes)
            {
                string source = m.FromDriverStore ? "DRIVER STORE" : "game folder";
                Console.WriteLine($"  [{source}] {m.ModuleName}  {m.FileVersion ?? "?"}");
                Console.WriteLine($"      {m.Path}");
                if (m.ProductName != null) Console.WriteLine($"      product: {m.ProductName}");
            }
        }
        Console.WriteLine();
    }

    private static void PrintRegistry(DlssProbeService.ProbeResult r)
    {
        Console.WriteLine("-- NGX diagnostics registry (HKLM\\SOFTWARE\\NVIDIA Corporation\\Global\\NGXCore) --");
        foreach (var kv in r.NgxRegistry)
            Console.WriteLine($"  {kv.Key,-24} {(kv.Value == null ? "absent" : kv.Value.ToString())}");
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
