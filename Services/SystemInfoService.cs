using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public partial class SystemInfoService
{
    // =========================================================================
    // Win32 Native Structs & Imports
    // =========================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    private const int ENUM_CURRENT_SETTINGS = -1;

    private readonly Lock _cpuSampleLock = new();
    private long _lastIdleTime;
    private long _lastKernelTime;
    private long _lastUserTime;
    private bool _hasPrevTimes;

    public SystemInfoService()
    {
        // Initialize baseline CPU times
        if (GetSystemTimes(out _lastIdleTime, out _lastKernelTime, out _lastUserTime))
        {
            _hasPrevTimes = true;
        }
    }

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Gathers all system hardware metrics asynchronously on a background worker.
    /// </summary>
    public Task<SystemHardwareReport> GetFullHardwareReportAsync()
    {
        return Task.Run(() =>
        {
            var report = new SystemHardwareReport();
            try
            {
                report.Cpu = GetCpuInfo();
                report.Gpus = GetGpuInfoList();
                report.Ram = GetRamInfo();
                report.Displays = GetDisplays();
                report.Drives = GetDrives();
                report.Os = GetOsInfo();
                report.Power = GetPowerInfo();
                report.Network = GetNetworkInfo();
            }
            catch (Exception ex)
            {
                LoggingService.Error("SystemInfoService", "Error gathering system hardware report", ex);
            }
            return report;
        });
    }

    /// <summary>
    /// Samples live CPU load and RAM usage in less than a millisecond without disk or WMI overhead.
    /// </summary>
    public (int CpuUsagePercent, RamHardwareInfo Ram) GetQuickTelemetry()
    {
        int cpuUsage = SampleCpuUsage();
        var ram = GetRamUsage();
        return (cpuUsage, ram);
    }

    // =========================================================================
    // CPU Detection
    // =========================================================================

    private CpuHardwareInfo GetCpuInfo()
    {
        var cpu = new CpuHardwareInfo
        {
            LogicalProcessors = Environment.ProcessorCount,
            PhysicalCores = Environment.ProcessorCount,
            CurrentUsagePercent = SampleCpuUsage()
        };

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key != null)
            {
                var nameVal = key.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(nameVal))
                {
                    cpu.ModelName = CleanCpuName(nameVal);
                }

                var mhzVal = key.GetValue("~MHz");
                if (mhzVal is int mhz)
                {
                    cpu.MaxClockSpeedGhz = Math.Round(mhz / 1000.0, 2);
                }
            }

            // Estimate physical cores if hyperthreaded / SMT
            using var procKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
            if (procKey != null)
            {
                int count = procKey.SubKeyCount;
                if (count > 0)
                {
                    cpu.LogicalProcessors = count;
                    // On consumer desktop gaming CPUs, SMT typically doubles threads
                    cpu.PhysicalCores = Math.Max(1, count > 4 && count % 2 == 0 ? count / 2 : count);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Could not read CPU registry: {ex.Message}");
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["CurrentClockSpeed"] is uint currentMhz && currentMhz > 0)
                    {
                        cpu.CurrentClockSpeedGhz = Math.Round(currentMhz / 1000.0, 2);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Could not read CPU current clock via WMI: {ex.Message}");
        }

        return cpu;
    }

    private static string CleanCpuName(string raw)
    {
        return raw.Replace("(R)", "")
                  .Replace("(TM)", "")
                  .Replace("CPU", "")
                  .Replace("Processor", "")
                  .Trim();
    }

    private int SampleCpuUsage()
    {
        // GetQuickTelemetry() (UI polling timer) and GetFullHardwareReportAsync() (background
        // Task.Run) can both call this on the same service instance; guard the shared
        // _last*Time/_hasPrevTimes fields against concurrent read-modify-write.
        lock (_cpuSampleLock)
        {
            try
            {
                if (GetSystemTimes(out long idle, out long kernel, out long user))
                {
                    if (_hasPrevTimes)
                    {
                        long idleDiff = idle - _lastIdleTime;
                        long kernelDiff = kernel - _lastKernelTime;
                        long userDiff = user - _lastUserTime;
                        long totalDiff = kernelDiff + userDiff;

                        _lastIdleTime = idle;
                        _lastKernelTime = kernel;
                        _lastUserTime = user;

                        if (totalDiff > 0)
                        {
                            double busy = totalDiff - idleDiff;
                            return (int)Math.Clamp(Math.Round((busy / totalDiff) * 100.0), 0, 100);
                        }
                    }
                    else
                    {
                        _lastIdleTime = idle;
                        _lastKernelTime = kernel;
                        _lastUserTime = user;
                        _hasPrevTimes = true;
                    }
                }
            }
            catch
            {
                // Ignore telemetry sampling errors
            }

            return 0;
        }
    }

    // =========================================================================
    // GPU Detection
    // =========================================================================

    private List<GpuHardwareInfo> GetGpuInfoList()
    {
        var list = new List<GpuHardwareInfo>();

        try
        {
            const string videoClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var classKey = Registry.LocalMachine.OpenSubKey(videoClassKey);
            if (classKey != null)
            {
                foreach (var subName in classKey.GetSubKeyNames())
                {
                    if (subName.StartsWith("Properties", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var cardKey = classKey.OpenSubKey(subName);
                    if (cardKey == null) continue;

                    string? desc = cardKey.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(desc) || desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var gpu = new GpuHardwareInfo
                    {
                        ModelName = desc,
                        DriverVersion = cardKey.GetValue("DriverVersion") as string ?? "Unknown",
                        DriverDate = cardKey.GetValue("DriverDate") as string ?? "Unknown"
                    };

                    // Clean driver version for NVIDIA (e.g. 31.0.15.5186 -> 551.86)
                    if (gpu.DriverVersion.Contains('.'))
                    {
                        var parts = gpu.DriverVersion.Split('.');
                        if (parts.Length >= 4 && parts[^2].Length >= 1)
                        {
                            string lastTwo = parts[^2] + parts[^1];
                            if (lastTwo.Length >= 5)
                            {
                                gpu.DriverVersion = $"{lastTwo[^5..^2]}.{lastTwo[^2..]}";
                            }
                        }
                    }

                    // VRAM detection
                    object? vramBytes = cardKey.GetValue("HardwareInformation.qwMemorySize");
                    if (vramBytes == null)
                    {
                        vramBytes = cardKey.GetValue("HardwareInformation.MemorySize");
                    }

                    if (vramBytes is long lBytes && lBytes > 0)
                    {
                        gpu.VramGigabytes = Math.Round(lBytes / (1024.0 * 1024.0 * 1024.0), 1);
                    }
                    else if (vramBytes is int iBytes && iBytes > 0)
                    {
                        gpu.VramGigabytes = Math.Round((uint)iBytes / (1024.0 * 1024.0 * 1024.0), 1);
                    }

                    gpu.IsDedicated = !desc.Contains("Intel", StringComparison.OrdinalIgnoreCase) || desc.Contains("Arc", StringComparison.OrdinalIgnoreCase);

                    list.Add(gpu);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Failed to read GPU registry: {ex.Message}");
        }

        if (list.Count == 0)
        {
            list.Add(new GpuHardwareInfo { ModelName = "Primary Graphics Adapter", VramGigabytes = 0 });
        }

        // Order dedicated GPUs first
        return list.OrderByDescending(g => g.IsDedicated).ThenByDescending(g => g.VramGigabytes).ToList();
    }

    // =========================================================================
    // RAM Detection
    // =========================================================================

    private RamHardwareInfo GetRamInfo()
    {
        var ram = GetRamUsage();
        ApplyRamSpeedInfo(ram);
        return ram;
    }

    /// <summary>
    /// GlobalMemoryStatusEx only - sub-millisecond, no WMI. Safe to call on every UI poll tick.
    /// </summary>
    private RamHardwareInfo GetRamUsage()
    {
        var ram = new RamHardwareInfo();
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };

        if (GlobalMemoryStatusEx(ref memStatus))
        {
            ram.TotalGigabytes = Math.Round(memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
            ram.AvailableGigabytes = Math.Round(memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0), 1);
            ram.UsedGigabytes = Math.Max(0, Math.Round(ram.TotalGigabytes - ram.AvailableGigabytes, 1));
            ram.UsagePercent = (int)memStatus.dwMemoryLoad;
        }

        return ram;
    }

    /// <summary>
    /// WMI RAM speed lookup - tens to hundreds of ms. Only call from the full hardware report,
    /// never from the quick UI poll.
    /// </summary>
    private static void ApplyRamSpeedInfo(RamHardwareInfo ram)
    {
        try
        {
            // Speed = the module's rated/capable speed (e.g. 6000 for DDR5-6000); ConfiguredClockSpeed
            // is what it's actually running at right now - lower than Speed means XMP/EXPO isn't active
            // and the module is running at its JEDEC default instead of its rated speed.
            using var searcher = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    uint rated = obj["Speed"] as uint? ?? 0;
                    uint configured = obj["ConfiguredClockSpeed"] as uint? ?? 0;
                    if (configured > 0)
                    {
                        ram.SpeedMhz = (int)configured;
                        ram.RatedSpeedMhz = (int)rated;
                        ram.IsXmpActive = rated > 0 && configured >= rated;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Could not read RAM speed via WMI: {ex.Message}");
        }
    }

    // =========================================================================
    // Display & Refresh Rate Detection
    // =========================================================================

    private List<DisplayHardwareInfo> GetDisplays()
    {
        var list = new List<DisplayHardwareInfo>();

        try
        {
            // Primary display via EnumDisplaySettings
            var devMode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref devMode))
            {
                list.Add(new DisplayHardwareInfo
                {
                    DeviceName = "Primary Monitor",
                    Width = (int)devMode.dmPelsWidth,
                    Height = (int)devMode.dmPelsHeight,
                    RefreshRateHz = (int)devMode.dmDisplayFrequency,
                    IsPrimary = true
                });
            }

            // Check secondary displays
            for (int i = 1; i <= 4; i++)
            {
                string deviceName = $@"\\.\DISPLAY{i}";
                var extraMode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
                if (EnumDisplaySettingsW(deviceName, ENUM_CURRENT_SETTINGS, ref extraMode))
                {
                    // Avoid duplicating primary
                    if (list.Count > 0 && i == 1 && list[0].Width == (int)extraMode.dmPelsWidth && list[0].Height == (int)extraMode.dmPelsHeight && list[0].RefreshRateHz == (int)extraMode.dmDisplayFrequency)
                    {
                        continue;
                    }

                    list.Add(new DisplayHardwareInfo
                    {
                        DeviceName = $"Display {i}",
                        Width = (int)extraMode.dmPelsWidth,
                        Height = (int)extraMode.dmPelsHeight,
                        RefreshRateHz = (int)extraMode.dmDisplayFrequency,
                        IsPrimary = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Display query error: {ex.Message}");
        }

        if (list.Count == 0)
        {
            list.Add(new DisplayHardwareInfo { DeviceName = "Standard Display", Width = 1920, Height = 1080, RefreshRateHz = 60 });
        }

        return list;
    }

    // =========================================================================
    // Drive & Storage Detection
    // =========================================================================

    private List<DriveStorageInfo> GetDrives()
    {
        var list = new List<DriveStorageInfo>();
        var mediaTypeByLetter = GetDriveLetterMediaTypes();

        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .OrderBy(d => d.Name);

            foreach (var d in drives)
            {
                double totalGb = Math.Round(d.TotalSize / (1024.0 * 1024.0 * 1024.0), 1);
                double freeGb = Math.Round(d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                string letter = d.Name.TrimEnd('\\');

                list.Add(new DriveStorageInfo
                {
                    DriveLetter = letter,
                    VolumeLabel = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel,
                    MediaTypeDisplay = mediaTypeByLetter.GetValueOrDefault(letter, ""),
                    TotalGigabytes = totalGb,
                    FreeGigabytes = freeGb
                });
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Failed to read drives: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// Maps each drive letter (e.g. "C:") to a human-readable media type (NVMe SSD / SATA SSD /
    /// HDD / USB) by joining MSFT_PhysicalDisk (accurate MediaType/BusType, same source
    /// PowerShell's Get-PhysicalDisk uses) to Win32_DiskDrive's partition associator chain.
    /// Win32_DiskDrive.MediaType is NOT used to classify SSD vs HDD - on most systems it reports
    /// the generic string "Fixed hard disk media" for both, which is why an earlier version of
    /// this method misclassified SSDs as HDDs.
    /// </summary>
    private static Dictionary<string, string> GetDriveLetterMediaTypes()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // MSFT_PhysicalDisk.DeviceId is a string of the same disk number as
            // Win32_DiskDrive.Index (both "0", "1", ...) - use it to correlate the two.
            var displayByIndex = new Dictionary<string, string>();
            using (var physicalDiskSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"))
            {
                foreach (ManagementObject pd in physicalDiskSearcher.Get())
                {
                    using (pd)
                    {
                        string deviceId = pd["DeviceId"] as string ?? "";
                        ushort mediaType = pd["MediaType"] as ushort? ?? 0;   // 3 = HDD, 4 = SSD
                        ushort busType = pd["BusType"] as ushort? ?? 0;       // 17 = NVMe, 11 = SATA, 7 = USB

                        string display;
                        if (busType == 17) display = "NVMe SSD";
                        else if (busType == 7) display = "USB";
                        else if (mediaType == 4) display = "SATA SSD";
                        else if (mediaType == 3) display = "HDD";
                        else continue; // Unspecified - leave MediaTypeDisplay blank rather than guess.

                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            displayByIndex[deviceId] = display;
                        }
                    }
                }
            }

            if (displayByIndex.Count == 0) return result;

            using var diskSearcher = new ManagementObjectSearcher("SELECT DeviceID, Index FROM Win32_DiskDrive");
            foreach (ManagementObject disk in diskSearcher.Get())
            {
                using (disk)
                {
                    string index = disk["Index"]?.ToString() ?? "";
                    if (!displayByIndex.TryGetValue(index, out string? display)) continue;

                    foreach (ManagementBaseObject partition in disk.GetRelated("Win32_DiskPartition"))
                    {
                        using var part = (ManagementObject)partition;
                        foreach (ManagementBaseObject logicalDisk in part.GetRelated("Win32_LogicalDisk"))
                        {
                            using var logical = (ManagementObject)logicalDisk;
                            string? letter = logical["DeviceID"] as string;
                            if (!string.IsNullOrWhiteSpace(letter))
                            {
                                result[letter] = display;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Could not read drive media types via WMI: {ex.Message}");
        }

        return result;
    }

    // =========================================================================
    // Operating System Info
    // =========================================================================

    private OsEnvironmentInfo GetOsInfo()
    {
        var info = new OsEnvironmentInfo();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                info.WindowsEdition = key.GetValue("ProductName") as string ?? "Windows 11";
                info.DisplayVersion = key.GetValue("DisplayVersion") as string ?? "";
                info.BuildNumber = key.GetValue("CurrentBuild") as string ?? key.GetValue("CurrentBuildNumber") as string ?? "";

                // Modern Windows 11 branding adjustment if ProductName reports Windows 10 on Win11 builds
                if (int.TryParse(info.BuildNumber, out int build) && build >= 22000)
                {
                    info.WindowsEdition = info.WindowsEdition.Replace("Windows 10", "Windows 11");
                }
            }

            // Game Mode status
            using var gameBarKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            if (gameBarKey != null)
            {
                var val = gameBarKey.GetValue("AllowAutoGameMode");
                info.IsGameModeActive = val == null || (int)val != 0;
            }

            // Power plan
            info.ActivePowerPlan = GetActivePowerPlanName();

            // System uptime - no WMI needed, TickCount64 is milliseconds since boot.
            info.LastBootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Failed to read OS info: {ex.Message}");
        }

        try
        {
            using var boardSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject obj in boardSearcher.Get())
            {
                using (obj)
                {
                    info.MotherboardManufacturer = CleanBoardVendorName(obj["Manufacturer"] as string ?? "");
                    info.MotherboardModel = obj["Product"] as string ?? "";
                    break;
                }
            }

            using var biosSearcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (ManagementObject obj in biosSearcher.Get())
            {
                using (obj)
                {
                    info.BiosVersion = obj["SMBIOSBIOSVersion"] as string ?? "";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfoService", $"Could not read motherboard/BIOS info via WMI: {ex.Message}");
        }

        return info;
    }

    private static string CleanBoardVendorName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw.Replace("Micro-Star International Co., Ltd.", "MSI")
                   .Replace("ASUSTeK COMPUTER INC.", "ASUS")
                   .Replace("Gigabyte Technology Co., Ltd.", "Gigabyte")
                   .Trim();
    }

    private static string GetActivePowerPlanName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            if (key != null)
            {
                string? activeGuid = key.GetValue("ActivePowerScheme") as string;
                if (!string.IsNullOrWhiteSpace(activeGuid))
                {
                    if (activeGuid.Equals("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase))
                        return "High Performance";
                    if (activeGuid.Equals("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.OrdinalIgnoreCase))
                        return "Ultimate Performance";
                    if (activeGuid.Equals("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase))
                        return "Balanced";
                    if (activeGuid.Equals("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase))
                        return "Power Saver";

                    // The friendly name of a (custom) power scheme lives under a "FriendlyName"
                    // subkey's default value, not as a value directly on the GUID key.
                    using var nameKey = key.OpenSubKey($@"{activeGuid}\FriendlyName");
                    string? friendly = nameKey?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(friendly)) return friendly;
                }
            }
        }
        catch
        {
            // Default fallback
        }
        return "Balanced";
    }

    // =========================================================================
    // Battery / Power State
    // =========================================================================

    private PowerBatteryInfo GetPowerInfo()
    {
        var info = new PowerBatteryInfo();
        if (GetSystemPowerStatus(out var status))
        {
            info.HasBattery = status.BatteryFlag != 128 && status.BatteryLifePercent <= 100;
            info.IsPluggedIn = status.ACLineStatus != 0;
            info.BatteryPercent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
        }
        return info;
    }

    // =========================================================================
    // Network Telemetry
    // =========================================================================

    private NetworkTelemetryInfo GetNetworkInfo()
    {
        var info = new NetworkTelemetryInfo();

        try
        {
            var activeInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                      ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                      ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                      ni.GetIPProperties().GatewayAddresses.Count > 0);

            if (activeInterface != null)
            {
                info.AdapterName = activeInterface.Name;
                info.ConnectionType = activeInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet (LAN)";
            }

            // Rapid ping measurement
            using var ping = new Ping();
            var reply = ping.Send("8.8.8.8", 500);
            if (reply.Status == IPStatus.Success)
            {
                info.PingMs = (int)reply.RoundtripTime;
            }
        }
        catch
        {
            info.PingMs = -1;
        }

        return info;
    }
}
