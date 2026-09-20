#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TrayTrigger.Models;
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

    /// <summary>
    /// <c>--test-dlss-writecheck &lt;exe&gt;</c>: proves the write interop works without changing
    /// anything. It sets a value in the session, reads it back, then disposes the session
    /// <b>without calling Save</b> - NVAPI stages writes in memory, so nothing reaches the driver's
    /// database. This separates "the interop is wrong" from "the driver refused", which a failed
    /// real apply cannot.
    /// </summary>
    private static void RunDlssWriteCheck(string? target)
    {
        var o = new StringBuilder();
        o.AppendLine("=== DLSS WRITE CHECK (nothing is saved) ===");
        o.AppendLine($"Elevated      : {SystemTweaksService.IsElevated}");

        string? exePath = ResolveProbeTarget(target);
        if (exePath == null) { Console.Write(o.Append("Pass an executable.\n")); return; }

        string exeName = Path.GetFileName(exePath);
        o.AppendLine($"Target        : {exeName}");

        using var session = NvApi.Session.TryOpen(out string? error);
        if (session == null)
        {
            Console.Write(o.AppendLine($"Session       : FAILED - {error}"));
            return;
        }

        var profile = session.FindProfileForExecutable(exeName, out IntPtr handle, out string? findError);
        if (profile == null)
        {
            o.AppendLine($"Profile       : none ({findError}) - cannot test a write without one.");
            Console.Write(o);
            return;
        }
        o.AppendLine($"Profile       : {profile.ProfileName}");

        // The SR preset letter: a DLSS setting, so a value here is meaningful rather than random,
        // and it is one the recipe writes anyway.
        const uint settingId = 0x10E41DF3;
        var before = session.GetSetting(handle, settingId, out _);
        o.AppendLine($"Before        : {(before == null ? "absent" : $"0x{before.CurrentValue:X8} [{before.OriginLabel}]")}");

        const uint probeValue = 0x0000000B;
        bool set = session.SetSetting(handle, settingId, probeValue, out string? setError);
        o.AppendLine($"SetSetting    : {(set ? "OK" : $"FAILED - {setError}")}");

        if (set)
        {
            var after = session.GetSetting(handle, settingId, out _);
            bool roundTripped = after != null && after.CurrentValue == probeValue;
            o.AppendLine($"Read back     : {(after == null ? "absent" : $"0x{after.CurrentValue:X8}")} - {(roundTripped ? "round-tripped" : "DID NOT round-trip")}");
        }

        o.AppendLine();
        o.AppendLine("Session disposed without Save: the driver database is untouched.");
        o.AppendLine("Run --test-dlss afterwards to confirm the value is unchanged on disk.");
        Console.Write(o);
    }

    /// <summary>
    /// <c>--test-dlss-roundtrip &lt;exe&gt;</c>: the first thing that actually calls
    /// <c>NvAPI_DRS_SaveSettings</c>. Reads the settings, applies, reads again, undoes, reads a
    /// third time, and compares the first and last readings.
    ///
    /// <para>Self-reversing, and the ownership records are written to a file <i>before</i> the
    /// undo, so a crash between the two still leaves enough to put the machine back by hand.</para>
    /// </summary>
    private static void RunDlssRoundTrip(string? target)
    {
        var o = new StringBuilder();
        o.AppendLine("=== DLSS ROUND TRIP (this one really writes) ===");
        o.AppendLine($"When          : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        o.AppendLine($"Elevated      : {SystemTweaksService.IsElevated}");

        string? exePath = ResolveProbeTarget(target);
        if (exePath == null) { WriteReport(o.AppendLine("Pass an executable.").ToString(), "dlss-roundtrip.txt"); return; }
        // Apply targets the renderer, not the launched executable. Reading the launcher's profile
        // here would compare a profile nothing ever wrote to - and report success whatever
        // happened. The first version of this harness did exactly that.
        string renderer = DlssProbeService.ResolveRenderingExecutable(exePath);
        o.AppendLine($"Launched      : {exePath}");
        o.AppendLine($"Renders       : {renderer}{(renderer == exePath ? "" : "   <- the profile that is written")}");
        exePath = renderer;
        o.AppendLine();

        // Which ids this driver actually knows. A setting nobody has set and an id the driver will
        // refuse to write both read as "absent" through a profile; only this tells them apart.
        o.AppendLine("-- Does the driver recognise these setting ids? --");
        foreach (var def in DlssProbeService.Settings)
        {
            string? name = NvApi.GetSettingName(def.Id);
            o.AppendLine($"  0x{def.Id:X8}  {(name == null ? "NOT KNOWN TO THIS DRIVER" : $"known as \"{name}\"")}");
        }
        o.AppendLine();

        var service = new DlssOverrideService();
        var before = ReadSettings(exePath);
        Dump(o, "BEFORE", before);

        var applied = service.Apply(exePath, "Round trip test");
        o.AppendLine($"Apply         : {(applied.Succeeded ? "OK" : $"FAILED - {applied.Error}")}");
        // Every outcome that is not a clean Applied, not just the write-back ones. Printing only
        // write-back failures hid a setting the driver had refused outright.
        foreach (var d in applied.Details.Where(d => d.Outcome != DlssSettingOutcome.Applied))
            o.AppendLine($"  0x{d.SettingId:X8}  {d.Outcome}: {d.Error}");

        if (!applied.Succeeded)
        {
            // Nothing was saved, so there is nothing to undo and nothing to compare.
            o.AppendLine();
            o.AppendLine("Nothing was written. The database is untouched.");
            WriteReport(o.ToString(), "dlss-roundtrip.txt");
            return;
        }

        // Written before the undo on purpose: if this process dies now, this file is the only
        // record of what to put back.
        string recordPath = Path.Combine(Path.GetDirectoryName(LoggingService.LogFilePath) ?? Path.GetTempPath(), "dlss-roundtrip-records.json");
        try
        {
            File.WriteAllText(recordPath, System.Text.Json.JsonSerializer.Serialize(applied.Records,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            o.AppendLine($"Records saved : {recordPath}");
        }
        catch (Exception ex) { o.AppendLine($"Records saved : FAILED - {ex.Message}"); }

        o.AppendLine();
        Dump(o, "AFTER APPLY", ReadSettings(exePath));

        var undone = service.Undo(applied.Records);
        o.AppendLine($"Undo          : {(undone.Succeeded ? "OK" : $"FAILED - {undone.Error}")}");
        if (undone.HadForeignChanges) o.AppendLine("  some settings were left alone (changed elsewhere)");

        o.AppendLine();
        var after = ReadSettings(exePath);
        Dump(o, "AFTER UNDO", after);

        o.AppendLine(Matches(before, after)
            ? "RESULT: every setting is back exactly as it started."
            : "RESULT: *** the settings did NOT return to their original state - see above ***");

        WriteReport(o.ToString(), "dlss-roundtrip.txt");
    }

    /// <summary>Where a stand-alone apply parks its ownership records so a later undo can find them.</summary>
    private static string DlssRecordsPath => Path.Combine(
        Path.GetDirectoryName(LoggingService.LogFilePath) ?? Path.GetTempPath(), "dlss-roundtrip-records.json");

    /// <summary>
    /// <c>--test-dlss-apply &lt;exe&gt;</c> / <c>--test-dlss-observe &lt;exe&gt;</c> /
    /// <c>--test-dlss-undo</c>: the round trip split across three runs, so a real game can be
    /// played in between. This is the only way to exercise the observation path against a game
    /// that is actually rendering.
    ///
    /// <para>Apply writes its records to disk before returning, so undo works from a later process
    /// - and so an interrupted test is still reversible.</para>
    /// </summary>
    private static void RunDlssApply(string? target)
    {
        var o = new StringBuilder();
        o.AppendLine("=== DLSS APPLY (leaves the settings in place) ===");
        o.AppendLine($"Elevated      : {SystemTweaksService.IsElevated}");

        string? exePath = ResolveProbeTarget(target);
        if (exePath == null) { WriteReport(o.AppendLine("Pass an executable.").ToString(), "dlss-apply.txt"); return; }

        string renderer = DlssProbeService.ResolveRenderingExecutable(exePath);
        o.AppendLine($"Renders       : {renderer}");

        var result = new DlssOverrideService().Apply(exePath, "DLSS end-to-end test");
        o.AppendLine($"Apply         : {(result.Succeeded ? "OK" : $"FAILED - {result.Error}")}");
        foreach (var d in result.Details.Where(d => d.Outcome != DlssSettingOutcome.Applied))
            o.AppendLine($"  0x{d.SettingId:X8}  {d.Outcome}: {d.Error}");

        if (result.Succeeded)
        {
            try
            {
                File.WriteAllText(DlssRecordsPath, System.Text.Json.JsonSerializer.Serialize(result.Records,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                o.AppendLine($"Records       : {DlssRecordsPath}");
            }
            catch (Exception ex) { o.AppendLine($"Records       : FAILED to save - {ex.Message}"); }

            o.AppendLine();
            Dump(o, "NOW", ReadSettings(renderer));
            o.AppendLine("Play the game, then run --test-dlss-observe, then --test-dlss-undo.");
        }

        WriteReport(o.ToString(), "dlss-apply.txt");
    }

    private static void RunDlssObserve(string? target)
    {
        var o = new StringBuilder();
        o.AppendLine("=== DLSS OBSERVE (read-only) ===");
        o.AppendLine($"When          : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        string? exePath = ResolveProbeTarget(target);
        if (exePath == null) { WriteReport(o.AppendLine("Pass an executable.").ToString(), "dlss-observe.txt"); return; }

        string renderer = DlssProbeService.ResolveRenderingExecutable(exePath);
        o.AppendLine($"Renders       : {renderer}");

        using var process = FindRunningProcess(renderer);
        o.AppendLine($"Running       : {(process == null ? "NO - start the game first" : $"yes (pid {process.Id})")}");
        o.AppendLine();

        var probe = DlssProbeService.Probe(renderer, process);
        o.AppendLine("-- Modules seen --");
        if (probe.LoadedRuntimes.Count == 0) o.AppendLine($"  {probe.ModuleScanNote}");
        string gameDir = Path.GetDirectoryName(renderer) ?? string.Empty;
        foreach (var m in probe.LoadedRuntimes)
            o.AppendLine($"  [{SourceLabel(m, gameDir)}] {m.ModuleName}  {m.Path}");
        o.AppendLine();

        var observations = DlssVerificationService.Interpret(
            probe.LoadedRuntimes, probe.ModuleScanNote, probe.DriverVersion, DateTime.UtcNow,
            probe.ShippedRuntimes);

        o.AppendLine("-- What the card would say --");
        foreach (var obs in observations)
            o.AppendLine($"  {obs.Feature}: {DlssVerificationService.Describe(obs)}");

        WriteReport(o.ToString(), "dlss-observe.txt");
    }

    private static void RunDlssUndo()
    {
        var o = new StringBuilder();
        o.AppendLine("=== DLSS UNDO ===");
        try
        {
            var records = System.Text.Json.JsonSerializer.Deserialize<List<DlssSettingRecord>>(File.ReadAllText(DlssRecordsPath))
                          ?? new List<DlssSettingRecord>();
            o.AppendLine($"Records       : {records.Count} from {DlssRecordsPath}");

            var result = new DlssOverrideService().Undo(records);
            o.AppendLine($"Undo          : {(result.Succeeded ? "OK" : $"FAILED - {result.Error}")}");
            foreach (var d in result.Details)
                o.AppendLine($"  0x{d.SettingId:X8}  {d.Outcome}");

            if (records.Count > 0)
            {
                o.AppendLine();
                Dump(o, "AFTER UNDO", ReadSettings(records[0].ApplicationName));
            }
        }
        catch (Exception ex) { o.AppendLine($"FAILED: {ex.Message}"); }

        WriteReport(o.ToString(), "dlss-undo.txt");
    }

    /// <summary>
    /// <c>--test-dlss-launch &lt;name&gt;</c>: applies the override to a real library entry and
    /// launches it through the normal launch path, then stays running so the in-session poller
    /// can do its work. The only way to exercise the automatic observation - every other harness
    /// here bypasses the launcher.
    ///
    /// <para><c>--test-dlss-launch-undo &lt;name&gt;</c> reverses it from the game's own records
    /// and clears them.</para>
    /// </summary>
    private void RunDlssLaunch(string? target)
    {
        var o = new StringBuilder();
        o.AppendLine("=== DLSS LAUNCH (applies, then launches for real) ===");
        o.AppendLine($"Elevated      : {SystemTweaksService.IsElevated}");

        var card = FindLibraryCard(target);
        if (card == null)
        {
            WriteReport(o.AppendLine($"No library entry matches '{target}'.").ToString(), "dlss-launch.txt");
            ExitApplication();
            return;
        }

        var game = card.Game;
        o.AppendLine($"Game          : {game.Name}");
        o.AppendLine($"Launches      : {game.ExecutablePath}");
        o.AppendLine($"Renders       : {DlssProbeService.ResolveRenderingExecutable(game.ExecutablePath)}");

        var result = new DlssOverrideService().Apply(game.ExecutablePath, game.Name);
        o.AppendLine($"Apply         : {(result.Succeeded ? "OK" : $"FAILED - {result.Error}")}");
        if (!result.Succeeded)
        {
            WriteReport(o.ToString(), "dlss-launch.txt");
            ExitApplication();
            return;
        }

        // Onto the real entry, so the launcher's poller sees a game it is managing - which is the
        // whole point of this harness.
        game.DlssSettings = result.Records.ToList();
        game.DlssObservations.Clear();
        game.DlssConflicted = false;
        _mainViewModel!.Library.SaveGamesOnly();
        o.AppendLine($"Records       : {game.DlssSettings.Count} stored on the library entry");
        o.AppendLine();
        o.AppendLine("Launching. TrayTrigger stays running; the poller checks every 20s for ~5 min.");
        o.AppendLine("Watch debug.log for [Dlss] lines, then run --test-dlss-launch-undo.");
        WriteReport(o.ToString(), "dlss-launch.txt");

        _mainViewModel.Library.LaunchGame(card);
    }

    private void RunDlssLaunchUndo(string? target)
    {
        var o = new StringBuilder();
        o.AppendLine("=== DLSS LAUNCH UNDO ===");

        var card = FindLibraryCard(target);
        if (card == null)
        {
            WriteReport(o.AppendLine($"No library entry matches '{target}'.").ToString(), "dlss-launch-undo.txt");
            return;
        }

        var game = card.Game;
        o.AppendLine($"Game          : {game.Name}");
        o.AppendLine($"Records       : {game.DlssSettings.Count}");

        foreach (var obs in game.DlssObservations)
            o.AppendLine($"  observed {obs.Feature}: {DlssVerificationService.Describe(obs)}");

        var result = new DlssOverrideService().Undo(game.DlssSettings);
        o.AppendLine($"Undo          : {(result.Succeeded ? "OK" : $"FAILED - {result.Error}")}");
        foreach (var d in result.Details) o.AppendLine($"  0x{d.SettingId:X8}  {d.Outcome}");

        game.DlssSettings = result.Records.ToList();
        game.DlssObservations.Clear();
        game.DlssConflicted = false;
        _mainViewModel!.Library.SaveGamesOnly();

        o.AppendLine();
        Dump(o, "AFTER UNDO", ReadSettings(DlssProbeService.ResolveRenderingExecutable(game.ExecutablePath)));
        WriteReport(o.ToString(), "dlss-launch-undo.txt");
    }

    private ViewModels.GameCardViewModel? FindLibraryCard(string? target) =>
        string.IsNullOrWhiteSpace(target)
            ? null
            : _mainViewModel?.Games.FirstOrDefault(g => g.Name.Contains(target, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where a loaded module came from, in three kinds rather than two.
    ///
    /// <para>"Not the NGX store" does not mean "the game folder": Cyberpunk loads NVIDIA's own
    /// <c>_nvngx.dll</c> loader out of <c>C:\Windows\System32\DriverStore</c>, which an earlier
    /// version of this report labelled as the game's. Nothing depended on that label, but a report
    /// that states something false is how the last two defects in this feature stayed hidden.</para>
    /// </summary>
    private static string SourceLabel(DlssProbeService.LoadedRuntime module, string gameDirectory) =>
        module.FromDriverStore ? "DRIVER STORE"
        : !string.IsNullOrEmpty(gameDirectory) && module.Path.StartsWith(gameDirectory, StringComparison.OrdinalIgnoreCase) ? "game folder"
        : "elsewhere";

    /// <summary>
    /// Prints a report and writes it next to the log. TrayTrigger is a WinExe, so a run started
    /// from Explorer - or at a reduced trust level - has no console to attach to and would
    /// otherwise print into the void.
    /// </summary>
    private static void WriteReport(string report, string fileName)
    {
        Console.Write(report);
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(LoggingService.LogFilePath) ?? Path.GetTempPath(), fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            Console.WriteLine($"Report written to {path}");
            LoggingService.Info("Dlss", $"Report written to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not write the report file: {ex.Message}");
            LoggingService.Warn("Dlss", $"Could not write the report: {ex.Message}");
        }
    }

    private static Dictionary<uint, string> ReadSettings(string exePath)
    {
        var map = new Dictionary<uint, string>();
        var r = DlssProbeService.Probe(exePath);
        foreach (var s in r.SettingStates)
            map[s.Definition.Id] = s.Value == null ? "absent" : $"0x{s.Value.CurrentValue:X8} [{s.Value.OriginLabel}]";
        return map;
    }

    private static void Dump(StringBuilder o, string label, Dictionary<uint, string> settings)
    {
        o.AppendLine($"-- {label} --");
        foreach (var def in DlssProbeService.Settings)
            o.AppendLine($"  0x{def.Id:X8}  {def.Name,-38} {(settings.TryGetValue(def.Id, out string? v) ? v : "not read")}");
        o.AppendLine();
    }

    private static bool Matches(Dictionary<uint, string> a, Dictionary<uint, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out string? v) && v == kv.Value);

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
            string gameDir = Path.GetDirectoryName(r.ExecutablePath) ?? string.Empty;
            foreach (var m in r.LoadedRuntimes)
            {
                string source = SourceLabel(m, gameDir);
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
