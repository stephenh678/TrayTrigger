using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TrayTrigger.Models;

public class CpuHardwareInfo
{
    public string ModelName { get; set; } = "Unknown CPU";
    public int PhysicalCores { get; set; }
    public int LogicalProcessors { get; set; }
    public double MaxClockSpeedGhz { get; set; }
    public double CurrentClockSpeedGhz { get; set; }
    public int CurrentUsagePercent { get; set; }
    public string ClockSpeedDisplay =>
        CurrentClockSpeedGhz > 0
            ? $"{CurrentClockSpeedGhz:0.00} GHz Current, {MaxClockSpeedGhz:0.00} GHz Max"
            : $"{MaxClockSpeedGhz:0.00} GHz Max Clock";
}

public enum GpuVendor { Other, Nvidia, Amd, Intel }

public class GpuHardwareInfo
{
    public string ModelName { get; set; } = "Unknown GPU";
    public double VramGigabytes { get; set; }
    /// <summary>The version the vendor uses: 617.14 for NVIDIA, the Adrenalin release for AMD,
    /// Windows' own driver version otherwise.</summary>
    public string DriverVersion { get; set; } = "Unknown";
    /// <summary>Windows' four-part driver version (32.0.16.1714), whatever the vendor calls it.</summary>
    public string WindowsDriverVersion { get; set; } = "";
    public string DriverDate { get; set; } = "Unknown";
    /// <summary>The driver's date, parsed from <see cref="DriverDate"/>; null when it can't be read.</summary>
    public DateTime? DriverDateValue { get; set; }
    public bool IsDedicated { get; set; } = true;
    public GpuVendor Vendor { get; set; }
    /// <summary>The adapter's LUID as DXGI reports it, packed into one value: what Windows' GPU
    /// performance counters name each adapter by. 0 when it couldn't be matched.</summary>
    public long AdapterLuid { get; set; }
    /// <summary>The display-class registry subkey ("0000") the GPU was read from; matches its device.</summary>
    public string DriverKey { get; set; } = "";

    // The PCIe link, from Windows' device properties; 0 when it couldn't be read (a built-in GPU,
    // or a laptop's discrete GPU powered down). Generation is Windows' link-speed value: 1 = PCIe
    // 1.0 (2.5 GT/s) up to 5 = PCIe 5.0 (32 GT/s) and 6 = PCIe 6.0.
    public int PcieCurrentWidth { get; set; }
    public int PcieMaxWidth { get; set; }
    public int PcieCurrentGeneration { get; set; }
    public int PcieMaxGeneration { get; set; }

    public bool HasPcieLink => PcieCurrentWidth > 0 && PcieMaxWidth > 0;

    /// <summary>
    /// Fewer lanes than the card supports: x8 for an x16 card means the second slot, or a slot
    /// sharing lanes with an M.2 drive. Only the width counts - the speed drops whenever the GPU
    /// idles, so a low speed at the moment of reading means nothing.
    /// </summary>
    public bool IsPcieNarrowed => HasPcieLink && PcieCurrentWidth < PcieMaxWidth;

    /// <summary>"PCIe 5.0 x16": the link as it runs now, with what the card supports when a slower
    /// slot holds it back - informational, as a generation down costs games little.</summary>
    public string PcieDisplay
    {
        get
        {
            if (!HasPcieLink) return "";
            int generation = PcieCurrentGeneration > 0 ? PcieCurrentGeneration : PcieMaxGeneration;
            string link = generation > 0 ? $"PCIe {generation}.0 x{PcieCurrentWidth}" : $"PCIe x{PcieCurrentWidth}";
            return PcieMaxGeneration > generation ? $"{link} (card supports PCIe {PcieMaxGeneration}.0)" : link;
        }
    }

    /// <summary>The GPU's biggest memory window (BAR), in bytes; 0 when it couldn't be read.</summary>
    public long LargestBarBytes { get; set; }

    /// <summary>A GPU's biggest window without Resizable BAR: the PCI default the CPU sees VRAM through.</summary>
    internal const long LegacyBarBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Whether the card can do Resizable BAR at all, going by its name: a GeForce RTX 30 series or
    /// later, a Radeon RX 5000 series or later, or an Intel Arc. For an older card the 256 MB
    /// window is all there will ever be, and no BIOS setting changes that. A name this doesn't
    /// recognise counts as "can't", so the worst a new naming scheme does is leave a warning out.
    /// </summary>
    internal bool SupportsResizableBar => Vendor switch
    {
        // "Quadro RTX 4000" is a 20-series card under a number that reads as a later one.
        GpuVendor.Nvidia => !ModelName.Contains("Quadro", StringComparison.OrdinalIgnoreCase) && SeriesNumber("RTX") >= 3000,
        GpuVendor.Amd => SeriesNumber("RX") >= 5000,
        GpuVendor.Intel => ModelName.Contains("Arc", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>The four digits after a family name: 5080 for "GeForce RTX 5080", 0 for "RX 580" or "RTX A4000".</summary>
    private int SeriesNumber(string family)
    {
        var match = Regex.Match(ModelName, $@"\b{family}\s*(\d{{4}})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Whether Resizable BAR (AMD's Smart Access Memory) is on: the CPU reaches the card's whole
    /// memory at once rather than through a 256 MB window. Null when it can't be told - a built-in
    /// GPU, which shares system memory, a card with no more memory than the window, or no reading -
    /// and for a 256 MB window on a card that can't do Resizable BAR, where "off" would send
    /// someone to the BIOS for nothing.
    /// </summary>
    public bool? ResizableBarEnabled =>
        !IsDedicated || LargestBarBytes <= 0 || (VramGigabytes > 0 && VramGigabytes <= 0.25) ? null
        : LargestBarBytes > LegacyBarBytes ? true
        : SupportsResizableBar ? false
        : null;

    public string ResizableBarWarning => ResizableBarEnabled == false
        ? "Resizable BAR is off, so the CPU reaches this card's memory through a 256 MB window. Turning on Above 4G Decoding and Re-Size BAR Support in the BIOS"
          + (Vendor == GpuVendor.Amd ? " (AMD calls it Smart Access Memory)" : "")
          + " is worth a few percent on average, and far more in some games. It needs Windows to start in UEFI mode, with CSM off."
        : "";

    public string PcieWarning => IsPcieNarrowed
        ? $"Running on x{PcieCurrentWidth} of the x{PcieMaxWidth} lanes this card supports. It may be in a second slot, or in one that shares its lanes with an M.2 drive; the motherboard manual says which slot gives x{PcieMaxWidth}."
        : "";
}

public class RamHardwareInfo
{
    public double TotalGigabytes { get; set; }
    public double UsedGigabytes { get; set; }
    public double AvailableGigabytes { get; set; }
    public int UsagePercent { get; set; }
    public int SpeedMhz { get; set; }
    public int RatedSpeedMhz { get; set; }
    public bool IsXmpActive { get; set; }
    public string SpeedDisplay
    {
        get
        {
            if (SpeedMhz <= 0) return "";
            string profileNote = RatedSpeedMhz > SpeedMhz
                ? $" (XMP/EXPO Inactive - Rated {RatedSpeedMhz} MHz)"
                : (IsXmpActive ? " (XMP/EXPO Active)" : "");
            return $"{SpeedMhz} MHz{profileNote}";
        }
    }
    public bool HasSpeedInfo => SpeedMhz > 0;
}

public class DisplayHardwareInfo
{
    public string DeviceName { get; set; } = "Primary Display";
    /// <summary>The monitor's own name from its EDID ("LG ULTRAGEAR"); empty for a laptop panel
    /// or a display that doesn't report one.</summary>
    public string MonitorName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRateHz { get; set; }
    /// <summary>The highest refresh rate this display offers at its current resolution.</summary>
    public int MaxRefreshRateHz { get; set; }
    public bool IsPrimary { get; set; } = true;
    public bool HdrSupported { get; set; }
    public bool HdrEnabled { get; set; }

    /// <summary>The monitor's name when it has one, otherwise "Primary display" / "Display 2".</summary>
    public string Title => string.IsNullOrWhiteSpace(MonitorName) ? DeviceName : MonitorName;

    /// <summary>
    /// Running below what the screen can do at this resolution: a 144 Hz monitor left at 60 Hz after
    /// a driver update or a new cable is the most common reason a fast screen feels slow. A gap of a
    /// couple of hertz is the 59.94 / 60 or 143.9 / 144 rounding Windows does, not a setting.
    /// </summary>
    public bool IsBelowMaxRefresh => RefreshRateHz > 1 && MaxRefreshRateHz - RefreshRateHz >= 3;

    public string RefreshWarning => IsBelowMaxRefresh
        ? $"Set to {RefreshRateHz} Hz, but this screen offers up to {MaxRefreshRateHz} Hz at {Width} × {Height}. Change it in Windows Settings > Display > Advanced display."
        : "";

    /// <summary>"HDR on" / "HDR off", or empty for a screen with no HDR mode.</summary>
    public string HdrDisplay => !HdrSupported ? "" : HdrEnabled ? "HDR on" : "HDR off";
    public bool HasHdr => HdrSupported;
    /// <summary>"1920 × 1080 @ 144Hz". EnumDisplaySettings reports 0 or 1 for "the default refresh
    /// rate" (remote-desktop and virtual displays), and then the rate is left off.</summary>
    public string ResolutionDisplay =>
        RefreshRateHz > 1 ? $"{Width} × {Height} @ {RefreshRateHz}Hz" : $"{Width} × {Height}";

    /// <summary>"3440 × 1440 · 120 Hz (max 240 Hz)": the rate it runs at and the fastest it offers,
    /// always both, so a screen below its best shows at a glance.</summary>
    public string ModeDisplay =>
        RefreshRateHz <= 1 ? $"{Width} × {Height}"
        : MaxRefreshRateHz > 1 ? $"{Width} × {Height} · {RefreshRateHz} Hz (max {MaxRefreshRateHz} Hz)"
        : $"{Width} × {Height} · {RefreshRateHz} Hz";
}

public class DriveStorageInfo
{
    public string DriveLetter { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string MediaTypeDisplay { get; set; } = "";
    public double TotalGigabytes { get; set; }
    public double FreeGigabytes { get; set; }
    public double UsedGigabytes => Math.Max(0, TotalGigabytes - FreeGigabytes);
    public int UsagePercent => TotalGigabytes > 0 ? (int)Math.Round((UsedGigabytes / TotalGigabytes) * 100) : 0;
    /// <summary>Above 90% used: the drive's bar turns the warning colour (UX-14d).</summary>
    public bool IsNearlyFull => TotalGigabytes > 0 && UsedGigabytes / TotalGigabytes > 0.9;
    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"Local Disk ({DriveLetter})"
        : $"{VolumeLabel} ({DriveLetter})";
    public bool HasMediaTypeInfo => !string.IsNullOrWhiteSpace(MediaTypeDisplay);

    /// <summary>How many games in the library are installed on this drive; -1 when not counted.</summary>
    public int GameCount { get; set; } = -1;
    public bool HasGameCount => GameCount > 0;
    public string GameCountDisplay => GameCount == 1 ? "1 game" : $"{GameCount} games";

    /// <summary>Games on a hard drive load noticeably slower than on an SSD.</summary>
    public bool HasGamesOnHdd => GameCount > 0 && MediaTypeDisplay == "HDD";
    public string HddGamesNote => GameCount == 1
        ? "1 game here is on a hard drive; it loads faster from an SSD."
        : $"{GameCount} games here are on a hard drive; they load faster from an SSD.";
}

public class OsEnvironmentInfo
{
    public string WindowsEdition { get; set; } = "Windows";
    public string DisplayVersion { get; set; } = "";
    public string BuildNumber { get; set; } = "";
    public string ActivePowerPlan { get; set; } = "Balanced";
    public string MotherboardManufacturer { get; set; } = "";
    public string MotherboardModel { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public DateTime? LastBootTime { get; set; }
    /// <summary>"Windows 11 Pro 24H2 (Build 26100)". DisplayVersion doesn't exist before Windows 10
    /// 20H2 and both values are empty when the CurrentVersion key can't be read, so each part is
    /// only included when it has a value.</summary>
    public string FullOsTitle
    {
        get
        {
            string title = WindowsEdition.Trim();
            if (!string.IsNullOrWhiteSpace(DisplayVersion)) title += $" {DisplayVersion.Trim()}";
            if (!string.IsNullOrWhiteSpace(BuildNumber)) title += $" (Build {BuildNumber.Trim()})";
            return title;
        }
    }
    public string MotherboardDisplay
    {
        get
        {
            string board = $"{MotherboardManufacturer} {MotherboardModel}".Trim();
            if (string.IsNullOrWhiteSpace(board)) return "";
            return string.IsNullOrWhiteSpace(BiosVersion) ? board : $"{board} (BIOS {BiosVersion})";
        }
    }
    public string UptimeDisplay
    {
        get
        {
            if (LastBootTime == null) return "";
            var uptime = DateTime.Now - LastBootTime.Value;
            if (uptime.TotalDays >= 1)
                return $"Up {(int)uptime.TotalDays}d {uptime.Hours}h (since {LastBootTime.Value:MMM d, h:mm tt})";
            if (uptime.TotalHours >= 1)
                return $"Up {(int)uptime.TotalHours}h {uptime.Minutes}m (since {LastBootTime.Value:h:mm tt})";
            return $"Up {uptime.Minutes}m (since {LastBootTime.Value:h:mm tt})";
        }
    }
    public bool HasMotherboardInfo => !string.IsNullOrWhiteSpace(MotherboardDisplay);
    public bool HasUptimeInfo => LastBootTime != null;
}

public class PowerBatteryInfo
{
    public bool HasBattery { get; set; }
    public bool IsPluggedIn { get; set; } = true;
    public int BatteryPercent { get; set; } = 100;
    public string StatusText => !HasBattery
        ? "Desktop PC (AC Power)"
        : (IsPluggedIn ? $"Plugged In ({BatteryPercent}%)" : $"On Battery ({BatteryPercent}%)");
}

public class NetworkTelemetryInfo
{
    public string AdapterName { get; set; } = "Ethernet";
    public string ConnectionType { get; set; } = "Wired";
    /// <summary>Round trip to 8.8.8.8 in milliseconds, or -1 when the ping failed or timed out.
    /// The report is measured before it reaches the view, so there is no "not yet measured" state.</summary>
    public int PingMs { get; set; } = -1;
    public string PingDisplay => PingMs >= 0 ? $"{PingMs} ms" : "Unavailable";
}

public class SystemHardwareReport
{
    public CpuHardwareInfo Cpu { get; set; } = new();
    public List<GpuHardwareInfo> Gpus { get; set; } = new();
    public RamHardwareInfo Ram { get; set; } = new();
    public List<DisplayHardwareInfo> Displays { get; set; } = new();
    public List<DriveStorageInfo> Drives { get; set; } = new();
    public OsEnvironmentInfo Os { get; set; } = new();
    public PowerBatteryInfo Power { get; set; } = new();
    public NetworkTelemetryInfo Network { get; set; } = new();
    public DateTime CapturedAt { get; set; } = DateTime.Now;
}
