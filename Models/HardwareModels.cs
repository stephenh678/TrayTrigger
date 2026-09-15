using System;
using System.Collections.Generic;

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
            ? $"{CurrentClockSpeedGhz:0.00} GHz Current, {MaxClockSpeedGhz:0.0} GHz Max"
            : $"{MaxClockSpeedGhz:0.0} GHz Max Clock";
}

public class GpuHardwareInfo
{
    public string ModelName { get; set; } = "Unknown GPU";
    public double VramGigabytes { get; set; }
    public string DriverVersion { get; set; } = "Unknown";
    public string DriverDate { get; set; } = "Unknown";
    public bool IsDedicated { get; set; } = true;
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
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRateHz { get; set; }
    public bool IsPrimary { get; set; } = true;
    /// <summary>"1920 × 1080 @ 144Hz". EnumDisplaySettings reports 0 or 1 for "the default refresh
    /// rate" (remote-desktop and virtual displays), and then the rate is left off.</summary>
    public string ResolutionDisplay =>
        RefreshRateHz > 1 ? $"{Width} × {Height} @ {RefreshRateHz}Hz" : $"{Width} × {Height}";
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
    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"Local Disk ({DriveLetter})"
        : $"{VolumeLabel} ({DriveLetter})";
    public bool HasMediaTypeInfo => !string.IsNullOrWhiteSpace(MediaTypeDisplay);
}

public class OsEnvironmentInfo
{
    public string WindowsEdition { get; set; } = "Windows";
    public string DisplayVersion { get; set; } = "";
    public string BuildNumber { get; set; } = "";
    public string ActivePowerPlan { get; set; } = "Balanced";
    public bool IsGameModeActive { get; set; } = true;
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
