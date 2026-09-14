using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The "Copy Diagnostic Info" report: everything a bug report needs, as Markdown ready to paste
/// into a GitHub issue. Built from the app's own state (settings, library, sessions, tweaks) plus
/// a machine section read from WMI and the registry. Every probe is wrapped so a failure prints
/// "unavailable" for that line rather than losing the report. API keys are reported as present
/// or absent only; their values never leave the machine.
/// </summary>
public static class DiagnosticReportService
{
    /// <summary>One launcher row: the integration switch in Settings, and whether the client is installed (null when TrayTrigger has no way to tell).</summary>
    public readonly record struct LauncherStatus(string Name, bool Enabled, bool? Installed);

    /// <summary>One tracked game session.</summary>
    public readonly record struct SessionStatus(string GameName, string Route, bool GameStarted);

    public sealed class Inputs
    {
        public required AppSettings Settings { get; init; }
        public required IReadOnlyList<GameEntry> Games { get; init; }
        public IReadOnlyList<LauncherStatus> Launchers { get; init; } = [];
        public IReadOnlyList<SessionStatus> Sessions { get; init; } = [];
        /// <summary>Names of the System-page tweaks currently applied, or null when the state could not be read.</summary>
        public IReadOnlyList<string>? AppliedTweaks { get; init; }
        public string? TrayPromotionStatus { get; init; }
        public string DataDirectory { get; init; } = string.Empty;
        public string CacheDirectory { get; init; } = string.Empty;
        public string LogPath { get; init; } = string.Empty;
        public string AppVersion { get; init; } = string.Empty;
        public bool StartedMinimized { get; init; }
    }

    /// <summary>How many recent warning/error lines the report quotes from the log.</summary>
    public const int LogTailLines = 15;

    /// <summary>Builds the full report. <paramref name="machineSection"/> defaults to the live probe; tests pass a fixed string.</summary>
    public static string Build(Inputs inputs, Func<string>? machineSection = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### TrayTrigger Diagnostic Report");
        sb.AppendLine($"_Generated {DateTime.Now:yyyy-MM-dd HH:mm} local_");
        sb.AppendLine();

        AppendApp(sb, inputs);
        sb.AppendLine();
        sb.AppendLine("**System**");
        sb.AppendLine(Safe(machineSection ?? MachineSection, "- System details: unavailable"));
        sb.AppendLine();
        AppendLaunchers(sb, inputs.Launchers);
        sb.AppendLine();
        AppendLibrary(sb, inputs);
        sb.AppendLine();
        AppendSettings(sb, inputs);
        sb.AppendLine();
        AppendState(sb, inputs);
        sb.AppendLine();
        AppendStorage(sb, inputs);
        sb.AppendLine();
        AppendLogTail(sb, inputs.LogPath);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    // ------------------------------------------------------------------ sections

    private static void AppendApp(StringBuilder sb, Inputs inputs)
    {
        sb.AppendLine("**App**");
        sb.AppendLine($"- Version: {inputs.AppVersion}");
        sb.AppendLine(Safe(() =>
        {
            string exe = Environment.ProcessPath ?? "unknown";
            return $"- Install: {InstallKind(exe)} (`{exe}`)";
        }, "- Install: unavailable"));
        sb.AppendLine(Safe(() => $"- Elevated: {(IsElevated() ? "yes" : "no")}", "- Elevated: unavailable"));
        sb.AppendLine(Safe(() =>
        {
            var started = Process.GetCurrentProcess().StartTime;
            var up = DateTime.Now - started;
            return $"- Running for: {FormatDuration(up)}{(inputs.StartedMinimized ? " (started minimized)" : "")}";
        }, "- Running for: unavailable"));
        sb.AppendLine($"- Runtime: .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    }

    private static void AppendLaunchers(StringBuilder sb, IReadOnlyList<LauncherStatus> launchers)
    {
        sb.AppendLine("**Launchers**");
        if (launchers.Count == 0)
        {
            sb.AppendLine("- (none reported)");
            return;
        }
        sb.AppendLine("| Launcher | Integration | Client installed |");
        sb.AppendLine("|---|---|---|");
        foreach (var l in launchers)
        {
            string installed = l.Installed switch { true => "yes", false => "no", null => "n/a" };
            sb.AppendLine($"| {l.Name} | {(l.Enabled ? "on" : "off")} | {installed} |");
        }
    }

    private static void AppendLibrary(StringBuilder sb, Inputs inputs)
    {
        var games = inputs.Games;
        sb.AppendLine("**Library**");
        sb.AppendLine($"- Games: {games.Count}");

        var byPlatform = games
            .GroupBy(PlatformOf)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}");
        if (games.Count > 0) sb.AppendLine($"- By platform: {string.Join(", ", byPlatform)}");

        int hidden = games.Count(g => g.IsHidden);
        int hotkeys = games.Count(g => !string.IsNullOrWhiteSpace(g.Hotkey));
        int scripts = games.Count(g => g.HasScripts);
        int missing = games.Count(g => IsExeMissing(g));
        sb.AppendLine($"- Hidden {hidden}, with hotkey {hotkeys}, with scripts {scripts}, missing exe {missing}");
        sb.AppendLine($"- Scan locations: {inputs.Settings.ScanLocations.Count} ({inputs.Settings.ScanLocations.Count(l => l.IsEnabled)} enabled); ignored entries: {inputs.Settings.IgnoredGamePaths.Count}");
    }

    private static void AppendSettings(StringBuilder sb, Inputs inputs)
    {
        var s = inputs.Settings;
        sb.AppendLine("**Settings**");
        sb.AppendLine($"- Scripts: {OnOff(s.EnableGameScripts)}; default scripts: {OnOff(s.ScriptDefaults.Enabled)}" +
                      (s.ScriptDefaults.Enabled ? $" (pre-launch {(string.IsNullOrWhiteSpace(s.ScriptDefaults.PreLaunchScriptPath) ? "none" : "set")}, post-exit {(string.IsNullOrWhiteSpace(s.ScriptDefaults.PostExitScriptPath) ? "none" : "set")})" : ""));
        sb.AppendLine($"- Start with Windows: {OnOff(s.StartWithWindows)}; start minimized: {OnOff(s.StartMinimizedToTray)}; minimize on launch: {OnOff(s.MinimizeOnGameLaunch)}; keep launchers minimized: {OnOff(s.KeepLaunchersMinimized)}; launch popup: {OnOff(s.ShowLaunchPopup)} (every launch {OnOff(s.ShowLaunchPopupOnEveryLaunch)})");
        sb.AppendLine($"- Always show in tray: {OnOff(s.AlwaysShowTrayIcon)}" +
                      (string.IsNullOrWhiteSpace(inputs.TrayPromotionStatus) ? "" : $" - last result: {inputs.TrayPromotionStatus}"));
        sb.AppendLine($"- Tray menu: left-click opens menu {OnOff(s.TrayLeftClickOpensMenu)}, icons {OnOff(s.ShowTrayMenuIcons)}, compact {OnOff(s.CompactTrayMenu)}, grouped {OnOff(s.GroupTrayMenuByCategory)}");
        sb.AppendLine($"- Tools: {OnOff(s.EnableTools)}; in tray {OnOff(s.ShowToolsInTray)} (sort {s.ToolsTraySortOption}); page view {s.ToolsViewMode}, sort {s.ToolsSortOption}");
        sb.AppendLine($"- Scan on startup: {OnOff(s.AutoScanForGamesOnStartup)}; online title search: {OnOff(s.SearchOfficialTitleOnline)}; poster art: {OnOff(s.UseVerticalPosterArt)}");
        sb.AppendLine($"- SteamGridDB: {OnOff(s.UseSteamGridDbArt)}, key {Presence(s.SteamGridDbApiKey)}; RAWG: {OnOff(s.UseRawgMetadata)}, key {Presence(s.RawgApiKey)}");
        sb.AppendLine($"- Restore point before presets: {OnOff(s.CreateRestorePointBeforeTweaks)}; verbose logging: {OnOff(s.VerboseLoggingEnabled)}; beta updates: {OnOff(s.IncludePrereleaseUpdates)}");
        sb.AppendLine($"- Show/hide hotkey: {(string.IsNullOrWhiteSpace(s.GlobalManageHotkey) ? "none" : s.GlobalManageHotkey)}");
    }

    private static void AppendState(StringBuilder sb, Inputs inputs)
    {
        sb.AppendLine("**Right now**");
        if (inputs.Sessions.Count == 0)
        {
            sb.AppendLine("- No game session is being tracked.");
        }
        else
        {
            foreach (var session in inputs.Sessions)
            {
                sb.AppendLine($"- {session.GameName}: {(session.GameStarted ? "running" : "starting")} via {session.Route}");
            }
        }

        if (inputs.AppliedTweaks == null)
        {
            sb.AppendLine("- Applied system tweaks: unavailable");
        }
        else if (inputs.AppliedTweaks.Count == 0)
        {
            sb.AppendLine("- Applied system tweaks: none");
        }
        else
        {
            sb.AppendLine($"- Applied system tweaks ({inputs.AppliedTweaks.Count}): {string.Join(", ", inputs.AppliedTweaks)}");
        }
    }

    private static void AppendStorage(StringBuilder sb, Inputs inputs)
    {
        sb.AppendLine("**Storage**");
        sb.AppendLine(Safe(() => $"- Data: `{inputs.DataDirectory}` ({FormatBytes(DirectorySize(inputs.DataDirectory, recurse: false))} of json)", "- Data: unavailable"));
        sb.AppendLine(Safe(() => $"- Cache: `{inputs.CacheDirectory}` ({FormatBytes(DirectorySize(inputs.CacheDirectory, recurse: true))})", "- Cache: unavailable"));
        sb.AppendLine(Safe(() =>
        {
            var info = new FileInfo(inputs.LogPath);
            return info.Exists ? $"- Log: `{inputs.LogPath}` ({FormatBytes(info.Length)})" : $"- Log: `{inputs.LogPath}` (not created yet)";
        }, "- Log: unavailable"));
    }

    private static void AppendLogTail(StringBuilder sb, string logPath)
    {
        sb.AppendLine($"**Recent warnings and errors** (last {LogTailLines})");
        var lines = Safe(() => RecentProblems(logPath, LogTailLines), null);
        if (lines == null)
        {
            sb.AppendLine("- Log could not be read.");
            return;
        }
        if (lines.Count == 0)
        {
            sb.AppendLine("- None in the current log.");
            return;
        }
        sb.AppendLine("```");
        foreach (var line in lines) sb.AppendLine(line);
        sb.AppendLine("```");
    }

    // ------------------------------------------------------------------ machine probe

    /// <summary>OS, CPU, memory, GPU, display and locale, each line independently guarded.</summary>
    public static string MachineSection()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Safe(WindowsLine, "- Windows: unavailable"));
        sb.AppendLine(Safe(CpuLine, "- CPU: unavailable"));
        sb.AppendLine(Safe(MemoryLine, "- RAM: unavailable"));
        foreach (var gpu in Safe(GpuLines, ["- GPU: unavailable"]))
        {
            sb.AppendLine(gpu);
        }
        sb.AppendLine(Safe(DisplayLine, "- Display: unavailable"));
        sb.AppendLine(Safe(LocaleLine, "- Locale: unavailable"));
        return sb.ToString().TrimEnd();
    }

    private static string WindowsLine()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = key?.GetValue("ProductName") as string ?? "Windows";
        string display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
        string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
        int ubr = key?.GetValue("UBR") is int u ? u : 0;
        // ProductName still says "Windows 10" on Windows 11; the build number is the truth.
        if (int.TryParse(build, out int b) && b >= 22000 && product.Contains("Windows 10")) product = product.Replace("Windows 10", "Windows 11");
        return $"- Windows: {product} {display} (build {build}.{ubr}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})";
    }

    private static string CpuLine()
    {
        string name = "unknown";
        int cores = 0, logical = Environment.ProcessorCount;
        using (var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
        {
            foreach (var o in searcher.Get())
            {
                name = (o["Name"] as string)?.Trim() ?? name;
                cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                logical = Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? logical);
                break;
            }
        }
        var topology = CpuTopologyService.GetTopology();
        string hybrid = topology.IsHybrid ? $", hybrid ({topology.PerformanceCoreCount} performance cores)" : "";
        return $"- CPU: {name} ({cores} cores / {logical} threads{hybrid})";
    }

    private static string MemoryLine()
    {
        using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
        foreach (var o in searcher.Get())
        {
            ulong total = Convert.ToUInt64(o["TotalPhysicalMemory"] ?? 0UL);
            return $"- RAM: {total / (1024.0 * 1024 * 1024):0.#} GB";
        }
        return "- RAM: unavailable";
    }

    private static List<string> GpuLines()
    {
        var lines = new List<string>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
        foreach (var o in searcher.Get())
        {
            string name = (o["Name"] as string)?.Trim() ?? "unknown";
            string driver = (o["DriverVersion"] as string) ?? "";
            lines.Add($"- GPU: {name}{(driver.Length > 0 ? $" (driver {driver})" : "")}");
        }
        if (lines.Count == 0) lines.Add("- GPU: none reported");
        return lines;
    }

    private static string DisplayLine()
    {
        double w = System.Windows.SystemParameters.PrimaryScreenWidth;
        double h = System.Windows.SystemParameters.PrimaryScreenHeight;
        double vw = System.Windows.SystemParameters.VirtualScreenWidth;
        double vh = System.Windows.SystemParameters.VirtualScreenHeight;
        string scale = "";
        var main = System.Windows.Application.Current?.MainWindow;
        if (main != null)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(main);
            scale = $", {dpi.DpiScaleX * 100:0}% scale";
        }
        return $"- Display: primary {w:0}×{h:0} (device-independent px{scale}); virtual desktop {vw:0}×{vh:0}";
    }

    private static string LocaleLine()
        => $"- Locale: {CultureInfo.CurrentCulture.Name} (UI {CultureInfo.CurrentUICulture.Name}), time zone {TimeZoneInfo.Local.Id}";

    // ------------------------------------------------------------------ helpers

    /// <summary>The last <paramref name="count"/> WARN/ERROR lines of the log, oldest first.</summary>
    public static List<string> RecentProblems(string logPath, int count)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath)) return result;

        // Read shared: the app is writing to this file while we read it.
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var ring = new LinkedList<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("[WARN", StringComparison.Ordinal) && !line.Contains("[ERROR", StringComparison.Ordinal)) continue;
            ring.AddLast(line.Length > 300 ? line[..300] + "…" : line);
            if (ring.Count > count) ring.RemoveFirst();
        }
        result.AddRange(ring);
        return result;
    }

    internal static string InstallKind(string exePath)
    {
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (exePath.StartsWith(programs, StringComparison.OrdinalIgnoreCase)) return "installed (per-user)";
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if ((programFiles.Length > 0 && exePath.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)) ||
            (programFilesX86.Length > 0 && exePath.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase)))
            return "installed (all users)";
        return "portable";
    }

    internal static string PlatformOf(GameEntry g) =>
        g.IsSteamGame ? "Steam"
        : g.IsGogGame ? "GOG"
        : g.IsEaGame ? "EA"
        : g.IsEpicGame ? "Epic"
        : g.IsUbisoftGame ? "Ubisoft"
        : g.IsXboxGame ? "Xbox"
        : g.IsBattleNetGame ? "Battle.net"
        : "Local";

    private static bool IsExeMissing(GameEntry g)
    {
        if (g.IsSteamGame || g.IsXboxGame || g.IsBattleNetGame) return false;
        if (string.IsNullOrWhiteSpace(g.ExecutablePath) || ProcessLauncherService.IsNonFileProtocolUrl(g.ExecutablePath)) return false;
        return !File.Exists(g.ExecutablePath);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static long DirectorySize(string dir, bool recurse)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(dir, recurse ? "*" : "*.json", option).Sum(f => new FileInfo(f).Length);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalMinutes}m";

    private static string OnOff(bool value) => value ? "on" : "off";
    private static string Presence(string? value) => string.IsNullOrWhiteSpace(value) ? "absent" : "present";

    private static T Safe<T>(Func<T> probe, T fallback)
    {
        try { return probe(); }
        catch (Exception ex)
        {
            LoggingService.Verbose("DiagnosticReport", $"Probe failed: {ex.Message}");
            return fallback;
        }
    }
}
