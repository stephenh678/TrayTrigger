using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What a Stutter Check row concluded, in the order the card lists them.</summary>
public enum StutterVerdict
{
    /// <summary>Something outside TrayTrigger worth a look: it can only point the way.</summary>
    NeedsLook,
    /// <summary>A TrayTrigger tweak is off; Fix it applies it.</summary>
    CanFix,
    /// <summary>Worth knowing, deliberately left alone (Core Isolation).</summary>
    Info,
    Fine,
}

/// <summary>What the row's button does, if it has one.</summary>
public enum StutterAction { None, HelpTopic, OpenSetting, ApplyTweak }

/// <summary>One check: what was looked at, what was found, and what to do about it.</summary>
public sealed record StutterCheckItem(
    string Id,
    string Title,
    string Detail,
    StutterVerdict Verdict,
    StutterAction Action = StutterAction.None,
    string ActionLabel = "",
    string ActionArg = "");

/// <summary>A browser or chat app that may hold video memory while a game runs.</summary>
public sealed record AppAccelerationState(string Name, bool Installed, bool HardwareAccelerationOn, bool IsRunning, long DedicatedGpuBytes);

public enum IntelApoState { NotNeeded, Present, Missing, Unknown }

/// <summary>Everything a run reads, gathered first so the check itself is a pure function (and testable).</summary>
public sealed class StutterCheckInputs
{
    public SystemHardwareReport? Hardware { get; init; }
    /// <summary>"C:", the drive Windows is on; its free space matters for the pagefile and shader caches.</summary>
    public string WindowsDriveLetter { get; init; } = "";
    public IReadOnlyList<SystemTweakItem> Tweaks { get; init; } = [];
    /// <summary>Whether the Optimized profile switches to the Ultimate plan while a game runs.</summary>
    public bool ProfileSwitchesPowerPlan { get; init; }
    /// <summary>Apps with an in-game overlay that are running now, by display name.</summary>
    public IReadOnlyList<string> OverlayApps { get; init; } = [];
    public IReadOnlyList<AppAccelerationState> Apps { get; init; } = [];
    /// <summary>Null when the indexer isn't running or couldn't be read.</summary>
    public bool? SearchIndexerBusy { get; init; }
    public double SearchIndexerCpuPercent { get; init; }
    public IntelApoState IntelApo { get; init; } = IntelApoState.Unknown;
}

/// <summary>The outcome of a run: the rows, how long it took, and the lines Copy Results produces.</summary>
public sealed class StutterCheckReport
{
    public StutterCheckReport(IReadOnlyList<StutterCheckItem> items, DateTime ranAt, TimeSpan took)
    {
        Items = items;
        RanAt = ranAt;
        Took = took;
    }

    public IReadOnlyList<StutterCheckItem> Items { get; }
    public DateTime RanAt { get; }
    public TimeSpan Took { get; }

    public int NeedsLookCount => Items.Count(i => i.Verdict == StutterVerdict.NeedsLook);
    public int CanFixCount => Items.Count(i => i.Verdict == StutterVerdict.CanFix);
    public int InfoCount => Items.Count(i => i.Verdict == StutterVerdict.Info);
    public int FineCount => Items.Count(i => i.Verdict == StutterVerdict.Fine);

    /// <summary>The rows the card shows: everything that isn't fine, worst first, in the order found.</summary>
    public IReadOnlyList<StutterCheckItem> Attention => Items.Where(i => i.Verdict != StutterVerdict.Fine).OrderBy(i => i.Verdict).ToList();
    public IReadOnlyList<StutterCheckItem> Fine => Items.Where(i => i.Verdict == StutterVerdict.Fine).ToList();

    /// <summary>"14 checks: 3 need a look, 2 TrayTrigger can fix, 1 to know about, 8 fine".</summary>
    public string Summary
    {
        get
        {
            if (Items.Count == 0) return "Nothing could be checked on this PC.";
            var parts = new List<string>();
            if (NeedsLookCount > 0) parts.Add(NeedsLookCount == 1 ? "1 needs a look" : $"{NeedsLookCount} need a look");
            if (CanFixCount > 0) parts.Add($"{CanFixCount} TrayTrigger can fix");
            if (InfoCount > 0) parts.Add($"{InfoCount} to know about");
            parts.Add(FineCount == Items.Count ? "all fine" : $"{FineCount} fine");
            return $"{Items.Count} checks: {string.Join(", ", parts)}";
        }
    }

    /// <summary>Copy Results and the diagnostic report: one line per check, the ones needing attention first.</summary>
    public IReadOnlyList<string> ToLines()
    {
        var lines = new List<string> { $"Stutter Check, {RanAt:yyyy-MM-dd HH:mm}: {Summary}" };
        foreach (var item in Attention.Concat(Fine))
        {
            string tag = item.Verdict switch
            {
                StutterVerdict.NeedsLook => "NEEDS A LOOK",
                StutterVerdict.CanFix => "TRAYTRIGGER CAN FIX",
                StutterVerdict.Info => "INFO",
                _ => "FINE",
            };
            lines.Add($"[{tag}] {item.Title}" + (item.Detail.Length > 0 && item.Verdict != StutterVerdict.Fine ? $" - {item.Detail}" : ""));
        }
        return lines;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var line in ToLines()) sb.AppendLine(line);
        sb.Append("TrayTrigger ").Append(UpdateService.CurrentVersionDisplay);
        return sb.ToString();
    }
}

/// <summary>
/// Stutter Check: the things every "my game stutters" thread tells you to go and check, checked
/// in one go. It only reports. Fix it applies one of the System page's own tweaks, which can be put
/// back; Here's how opens the help page for an app TrayTrigger doesn't own. Nothing is killed or
/// edited in another app.
///
/// <para>Each check is skipped, not failed, when the PC gives nothing to judge (no dedicated GPU
/// for Resizable BAR, HAGS unsupported, the indexer not running), so the count is what was
/// actually checked here.</para>
/// </summary>
public static class StutterCheckService
{
    public const string HelpTopic = "tweaks/stutter_check";

    public static StutterCheckReport Run(StutterCheckInputs inputs, DateTime? now = null, TimeSpan? took = null)
    {
        var items = new List<StutterCheckItem>();
        Overlays(inputs, items);
        AppAcceleration(inputs, items);
        Displays(inputs, items);
        Memory(inputs, items);
        Drives(inputs, items);
        DriveSpace(inputs, items);
        Pcie(inputs, items);
        ResizableBar(inputs, items);
        Tweak(inputs, items, "nv_shader_cache", "NVIDIA shader cache is unlimited", "NVIDIA shader cache is capped",
            "A capped cache throws away the oldest compiled shaders, so a game you come back to recompiles them as you play: the most-cited cause of stutter in the fix threads. Fix it applies the NVIDIA Shader Cache tweak below.");
        Tweak(inputs, items, "dx_shader_cache", "Windows keeps the DirectX shader cache", "Windows cleanups delete the DirectX shader cache",
            "Storage Sense and Disk Cleanup can wipe every game's compiled shaders, and the next launch compiles them again mid-game. Fix it applies the Keep the DirectX Shader Cache tweak below.");
        Tweak(inputs, items, "hags", "Hardware-Accelerated GPU Scheduling is on", "Hardware-Accelerated GPU Scheduling is off",
            "Lets the GPU manage its own command queue, which steadies frame pacing on CPU-limited PCs and is needed for DLSS Frame Generation. Fix it applies the HAGS tweak below; it asks for administrator permission and needs a restart, and Restore Previous puts it back.");
        Tweak(inputs, items, "game_dvr", "Game Bar background recording is off", "Game Bar is recording in the background",
            "\"Record what happened\" keeps the GPU encoder busy and writes to your SSD the whole time you play. Fix it applies the Game Bar Captures tweak below.");
        Tweak(inputs, items, "game_mode", "Windows Game Mode is on", "Windows Game Mode is off",
            "Game Mode keeps Windows Update and driver installs from starting while a full-screen game runs. Microsoft recommends it on; some threads blame it for hitching in particular games, so if one still stutters, try it the other way for that game. Fix it applies the Game Mode tweak below.");
        PowerPlan(inputs, items);
        CoreIsolation(inputs, items);
        SearchIndexer(inputs, items);
        IntelApo(inputs, items);
        return new StutterCheckReport(items, now ?? DateTime.Now, took ?? TimeSpan.Zero);
    }

    private static void Overlays(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var apps = inputs.OverlayApps;
        if (apps.Count >= 2)
        {
            items.Add(new("overlays", $"{Count(apps.Count, "overlay app")} running: {JoinNames(apps)}",
                "Each one hooks into the game to draw over it, and two or more at once is the most common cause of stutter in the fix threads. Keep the one you use and turn the rest off in their own settings.",
                StutterVerdict.NeedsLook, StutterAction.HelpTopic, "Here's how", "tweaks/stutter_overlays"));
        }
        else
        {
            items.Add(new("overlays", apps.Count == 1 ? $"One overlay app running: {apps[0]}" : "No overlay apps running",
                apps.Count == 1 ? "One overlay is normal; it's stacking them that hurts." : "", StutterVerdict.Fine));
        }
    }

    private static void AppAcceleration(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var holding = inputs.Apps.Where(a => a.Installed && a.HardwareAccelerationOn && (a.IsRunning || a.DedicatedGpuBytes > 0)).ToList();
        if (holding.Count == 0)
        {
            items.Add(new("app_accel", "No browser or chat app holding video memory", "", StutterVerdict.Fine));
            return;
        }
        long bytes = holding.Sum(a => a.DedicatedGpuBytes);
        string memory = bytes >= 64L * 1024 * 1024 ? $" They hold {bytes / (1024.0 * 1024 * 1024):0.0} GB of video memory right now." : "";
        items.Add(new("app_accel", $"{JoinNames(holding.Select(a => a.Name).ToList())} {(holding.Count == 1 ? "uses" : "use")} GPU hardware acceleration",
            $"A browser or Discord with hardware acceleration on keeps video memory while a game runs, which on an 8 or 12 GB card is the difference between textures fitting and not.{memory} Turn it off in each app's settings, or run them on the built-in GPU.",
            StutterVerdict.NeedsLook, StutterAction.HelpTopic, "Here's how", "tweaks/stutter_hw_accel"));
    }

    private static void Displays(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var displays = inputs.Hardware?.Displays;
        if (displays == null || displays.Count == 0) return;
        var slow = displays.Where(d => d.IsBelowMaxRefresh).ToList();
        if (slow.Count == 0)
        {
            items.Add(new("refresh_rate", displays.Count == 1 ? $"{displays[0].Title} is at its fastest refresh rate" : "Every display is at its fastest refresh rate", "", StutterVerdict.Fine));
            return;
        }
        var d = slow[0];
        items.Add(new("refresh_rate", $"{d.Title} is running at {d.RefreshRateHz} Hz, offers {d.MaxRefreshRateHz} Hz",
            "A driver update or a new cable can leave a monitor at a lower rate than it offers. Windows lets you set it under Display > Advanced display." + (slow.Count > 1 ? $" {slow.Count - 1} more display(s) are below their best too." : ""),
            StutterVerdict.NeedsLook, StutterAction.OpenSetting, "Open Display Settings", "ms-settings:display-advanced"));
    }

    private static void Memory(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var ram = inputs.Hardware?.Ram;
        if (ram == null || ram.SpeedMhz <= 0) return;
        if (ram.RatedSpeedMhz > ram.SpeedMhz)
        {
            items.Add(new("xmp", $"Memory runs at {ram.SpeedMhz} MHz, rated for {ram.RatedSpeedMhz} MHz",
                "XMP or EXPO is off, so the RAM runs at its fallback speed. It's the most overlooked cause of poor 1% lows on a Ryzen. Turn the profile on in the BIOS; it's one setting and the memory is sold to run that way.",
                StutterVerdict.NeedsLook, StutterAction.HelpTopic, "Here's how", "tweaks/stutter_xmp"));
        }
        else
        {
            items.Add(new("xmp", $"Memory at its rated {ram.SpeedMhz} MHz" + (ram.IsXmpActive ? " (XMP/EXPO on)" : ""), "", StutterVerdict.Fine));
        }
    }

    private static void Drives(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var drives = inputs.Hardware?.Drives;
        if (drives == null || drives.All(d => d.GameCount < 0)) return;
        var hdd = drives.Where(d => d.HasGamesOnHdd).ToList();
        if (hdd.Count == 0)
        {
            items.Add(new("hdd_games", "Every library game is on an SSD", "", StutterVerdict.Fine));
            return;
        }
        int games = hdd.Sum(d => d.GameCount);
        items.Add(new("hdd_games", $"{Count(games, "library game")} on a hard drive ({JoinNames(hdd.Select(d => d.DriveLetter).ToList())})",
            "A game on a hard drive streams its textures and world slower than the engine expects, which shows as hitches while you move. Moving it to an SSD is the fix; Steam and the other launchers can move an install without re-downloading it.",
            StutterVerdict.NeedsLook));
    }

    /// <summary>Under 10 % or 20 GB free on a drive that holds games or Windows: shader caches, the pagefile and patches all need room.</summary>
    private static void DriveSpace(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var drives = inputs.Hardware?.Drives.Where(d => d.TotalGigabytes > 0).ToList();
        if (drives == null || drives.Count == 0) return;
        var matters = drives.Where(d => d.GameCount > 0 || d.DriveLetter.Equals(inputs.WindowsDriveLetter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matters.Count == 0) return;
        var full = matters.Where(d => d.FreeGigabytes < 20 || d.FreeGigabytes / d.TotalGigabytes < 0.10).ToList();
        if (full.Count == 0)
        {
            items.Add(new("drive_space", matters.Count == 1 ? $"{matters[0].DriveLetter} has room to spare" : "Every game and Windows drive has room to spare", "", StutterVerdict.Fine));
            return;
        }
        var d = full[0];
        string what = d.DriveLetter.Equals(inputs.WindowsDriveLetter, StringComparison.OrdinalIgnoreCase) ? "the Windows drive" : "a game drive";
        items.Add(new("drive_space", $"{d.DriveLetter} is nearly full: {d.FreeGigabytes:0} GB of {d.TotalGigabytes:0} GB free",
            $"That's {what}. Shader caches, the pagefile and game patches all write there, and a drive this full makes an SSD slower and leaves Windows juggling. Free up 10 % or so: Settings > System > Storage shows what's taking the space." + (full.Count > 1 ? $" {full.Count - 1} more drive(s) are nearly full too." : ""),
            StutterVerdict.NeedsLook, StutterAction.OpenSetting, "Open Storage Settings", "ms-settings:storagesense"));
    }

    private static void Pcie(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var gpus = inputs.Hardware?.Gpus.Where(g => g.IsDedicated && g.HasPcieLink).ToList();
        if (gpus == null || gpus.Count == 0) return;
        var narrow = gpus.FirstOrDefault(g => g.IsPcieNarrowed);
        if (narrow == null)
        {
            items.Add(new("pcie_lanes", $"{gpus[0].ModelName} has all the PCIe lanes it supports", "", StutterVerdict.Fine));
            return;
        }
        items.Add(new("pcie_lanes", $"{narrow.ModelName} is on x{narrow.PcieCurrentWidth} of its x{narrow.PcieMaxWidth} lanes", narrow.PcieWarning, StutterVerdict.NeedsLook));
    }

    private static void ResizableBar(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var gpus = inputs.Hardware?.Gpus.Where(g => g.ResizableBarEnabled != null).ToList();
        if (gpus == null || gpus.Count == 0) return;
        var off = gpus.FirstOrDefault(g => g.ResizableBarEnabled == false);
        if (off == null)
        {
            items.Add(new("rebar", "Resizable BAR is on", "", StutterVerdict.Fine));
            return;
        }
        items.Add(new("rebar", $"Resizable BAR is off for {off.ModelName}", off.ResizableBarWarning, StutterVerdict.NeedsLook));
    }

    private static void Tweak(StutterCheckInputs inputs, List<StutterCheckItem> items, string tweakId, string fineTitle, string offTitle, string detail)
    {
        var tweak = inputs.Tweaks.FirstOrDefault(t => t.Id == tweakId);
        if (tweak == null || !tweak.IsAvailable) return;
        items.Add(tweak.IsOptimal
            ? new(tweakId, fineTitle, "", StutterVerdict.Fine)
            : new(tweakId, offTitle, detail, StutterVerdict.CanFix, StutterAction.ApplyTweak, "Fix it", tweakId));
    }

    private static void PowerPlan(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var tweak = inputs.Tweaks.FirstOrDefault(t => t.Id == "power_plan");
        if (tweak == null || !tweak.IsAvailable) return;
        if (tweak.IsOptimal)
            items.Add(new("power_plan", "The Ultimate power plan is active", "", StutterVerdict.Fine));
        else if (inputs.ProfileSwitchesPowerPlan)
            items.Add(new("power_plan", "The power plan switches to Ultimate while a game runs", "", StutterVerdict.Fine));
        else
            items.Add(new("power_plan", "The power plan is left as Windows has it",
                "Neither the always-on tweak nor the Optimized profile changes the power plan, so the CPU can drop its clocks between busy moments and hitch when the action picks up. Turn Power Plan on under Performance Profiles for games only, or apply the always-on tweak below.",
                StutterVerdict.Info));
    }

    private static void CoreIsolation(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        var tweak = inputs.Tweaks.FirstOrDefault(t => t.Id == "core_isolation");
        if (tweak == null || !tweak.IsAvailable) return;
        items.Add(tweak.IsOptimal
            ? new("core_isolation", "Core Isolation (Memory Integrity) is on",
                "Costs roughly 3 to 8 % in some games, but it is a real security boundary and Riot Vanguard wants it on. TrayTrigger never changes it; weigh it yourself in Windows Security.",
                StutterVerdict.Info, StutterAction.OpenSetting, "Open Core Isolation", "windowsdefender://coreisolation")
            : new("core_isolation", "Core Isolation (Memory Integrity) is off", "", StutterVerdict.Fine));
    }

    private static void SearchIndexer(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        if (inputs.SearchIndexerBusy == null) return;
        items.Add(inputs.SearchIndexerBusy == true
            ? new("search_indexer", $"Windows Search is indexing right now ({inputs.SearchIndexerCpuPercent:0} % CPU)",
                "The indexer reads and writes the drive while it catalogues files, and a game loading at the same time stutters. It settles on its own, or you can pause it for a day in Windows' indexing options.",
                StutterVerdict.NeedsLook, StutterAction.HelpTopic, "Here's how", "tweaks/stutter_search")
            : new("search_indexer", "Windows Search is idle", "", StutterVerdict.Fine));
    }

    private static void IntelApo(StutterCheckInputs inputs, List<StutterCheckItem> items)
    {
        switch (inputs.IntelApo)
        {
            case IntelApoState.NotNeeded:
                items.Add(new("intel_apo", "Intel Application Optimization isn't needed on this CPU", "", StutterVerdict.Fine));
                break;
            case IntelApoState.Present:
                items.Add(new("intel_apo", "Intel Application Optimization is installed", "", StutterVerdict.Fine));
                break;
            case IntelApoState.Missing:
                items.Add(new("intel_apo", "Intel Application Optimization isn't installed",
                    "This CPU supports Intel's APO, which steers a game's threads to the right cores and is worth double-digit gains in the games Intel has profiled. It's a free app from the Microsoft Store that needs the Intel Dynamic Tuning driver.",
                    StutterVerdict.NeedsLook, StutterAction.HelpTopic, "Here's how", "tweaks/stutter_apo"));
                break;
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>"A", "A and B", "A, B and C": the list form the System page's text uses too.</summary>
    internal static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
