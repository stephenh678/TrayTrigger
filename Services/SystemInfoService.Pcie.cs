using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The PCIe link each GPU runs on, from the standard device properties Windows keeps for every PCI
/// Express device (DEVPKEY_PciDevice_*) - the same values Device Manager's Details tab lists. No
/// vendor tools and no administrator rights.
///
/// <para>Only the width is worth a warning. The speed drops to PCIe 1.0 whenever the GPU idles, to
/// save power, so a low current speed at the moment of reading means nothing. A narrower link than
/// the card supports does: x8 for an x16 card is a card in the second slot, or a slot sharing its
/// lanes with an M.2 drive.</para>
/// </summary>
public partial class SystemInfoService
{
    /// <summary>Fills in each GPU's PCIe link, matched to its display-class registry key. Best effort:
    /// a built-in GPU, or one powered down (a laptop's discrete GPU at idle), reports none.</summary>
    private static void ReadPcieLinks(List<GpuHardwareInfo> gpus)
    {
        try
        {
            ForEachDisplayDevice((IntPtr devices, ref SP_DEVINFO_DATA data) =>
            {
                // "{4d36e968-e325-11ce-bfc1-08002be10318}\0000": the registry key GetGpuInfoList read.
                string? driverKey = ReadStringProperty(devices, ref data, SPDRP_DRIVER);
                if (driverKey == null) return;
                string subKey = driverKey[(driverKey.LastIndexOf('\\') + 1)..];

                foreach (var gpu in gpus)
                {
                    if (!string.Equals(gpu.DriverKey, subKey, StringComparison.OrdinalIgnoreCase)) continue;
                    gpu.PcieCurrentWidth = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_CurrentLinkWidth);
                    gpu.PcieMaxWidth = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_MaxLinkWidth);
                    gpu.PcieCurrentGeneration = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_CurrentLinkSpeed);
                    gpu.PcieMaxGeneration = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_MaxLinkSpeed);
                    gpu.LargestBarBytes = ReadLargestMemoryWindow(data.DevInst);
                    string rebar = gpu.ResizableBarEnabled switch { true => "on", false => "off", null => "unknown" };
                    LoggingService.Verbose("SystemInfo", $"PCIe link for '{gpu.ModelName}': width x{gpu.PcieCurrentWidth} of x{gpu.PcieMaxWidth}, generation {gpu.PcieCurrentGeneration} of {gpu.PcieMaxGeneration}; largest memory window {gpu.LargestBarBytes / (1024 * 1024)} MB, Resizable BAR {rebar}.");
                }
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfo", $"Could not read the GPUs' PCIe links: {ex.Message}");
        }
    }

    private delegate void DisplayDeviceVisitor(IntPtr devices, ref SP_DEVINFO_DATA data);

    /// <summary>Each present display-class device, with the set it belongs to for property reads. Throws what SetupAPI throws; callers log it.</summary>
    private static void ForEachDisplayDevice(DisplayDeviceVisitor visit)
    {
        var displayClass = GUID_DEVCLASS_DISPLAY;
        IntPtr devices = SetupDiGetClassDevsW(ref displayClass, null, IntPtr.Zero, DIGCF_PRESENT);
        if (devices == IntPtr.Zero || devices == INVALID_HANDLE_VALUE) return;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(devices, i, ref data); i++)
                visit(devices, ref data);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devices);
        }
    }

    private static bool? _nvidiaResizableBarCached;
    private static bool _nvidiaResizableBarRead;

    /// <summary>
    /// Whether an NVIDIA graphics card has a memory window bigger than the 256 MB one every card
    /// gets without Resizable BAR: true as soon as one does, false when every NVIDIA window read is
    /// 256 MB or less, null when there's no NVIDIA card or nothing could be read. NVIDIA's only, as
    /// it gates NVIDIA's own Resizable BAR setting - another adapter's large window says nothing
    /// about the NVIDIA card. Read once - it can only change with a restart - and without the WMI
    /// queries of the full hardware report, since a game launch waits on it.
    /// </summary>
    public static bool? NvidiaResizableBarEnabled()
    {
        if (_nvidiaResizableBarRead) return _nvidiaResizableBarCached;
        long largest = 0;
        try
        {
            ForEachDisplayDevice((IntPtr _, ref SP_DEVINFO_DATA data) =>
            {
                if (IsNvidiaDevice(data.DevInst)) largest = Math.Max(largest, ReadLargestMemoryWindow(data.DevInst));
            });
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfo", $"Could not read the NVIDIA card's memory windows: {ex.Message}");
            return null;
        }
        _nvidiaResizableBarCached = largest <= 0 ? null : largest > GpuHardwareInfo.LegacyBarBytes;
        _nvidiaResizableBarRead = true;
        return _nvidiaResizableBarCached;
    }

    /// <summary>PCI vendor 10DE in the device's instance ID ("PCI\VEN_10DE&amp;DEV_2C02&amp;...").</summary>
    private static bool IsNvidiaDevice(uint devInst)
    {
        var buffer = new char[MaxDeviceIdLength];
        return CM_Get_Device_IDW(devInst, buffer, buffer.Length, 0) == CR_SUCCESS
               && new string(buffer).Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The biggest memory window (BAR) Windows gave the device, in bytes, or 0 when none could be
    /// read. With Resizable BAR off a GPU's biggest window is 256 MB; on, it spans the card's whole
    /// memory. Read from the resources the device was actually allocated, where a window above
    /// 4 GB is a large-memory resource (ResType_MemLarge). WMI's Win32_DeviceMemoryAddress leaves
    /// those out, so on a PC with Resizable BAR on it shows only the small windows.
    /// </summary>
    private static long ReadLargestMemoryWindow(uint devInst)
    {
        long largest = 0;
        if (CM_Get_First_Log_Conf(out IntPtr logConf, devInst, ALLOC_LOG_CONF) != CR_SUCCESS) return 0;
        try
        {
            IntPtr current = logConf;
            while (CM_Get_Next_Res_Des(out IntPtr next, current, ResType_All, out uint resourceType, 0) == CR_SUCCESS)
            {
                if (current != logConf) CM_Free_Res_Des_Handle(current);
                current = next;
                if (resourceType != ResType_Mem && resourceType != ResType_MemLarge) continue;
                if (CM_Get_Res_Des_Data_Size(out uint size, current, 0) != CR_SUCCESS || size < 24) continue;

                // MEM_DES and MEMLARGE_DES both open with Count and Type, then the 64-bit
                // allocated base and end at offsets 8 and 16.
                var buffer = new byte[size];
                if (CM_Get_Res_Des_Data(current, buffer, size, 0) != CR_SUCCESS) continue;
                ulong start = BitConverter.ToUInt64(buffer, 8), end = BitConverter.ToUInt64(buffer, 16);
                if (end > start) largest = Math.Max(largest, (long)Math.Min(end - start + 1, (ulong)long.MaxValue));
            }
            if (current != logConf) CM_Free_Res_Des_Handle(current);
        }
        finally
        {
            CM_Free_Log_Conf_Handle(logConf);
        }
        return largest;
    }

    private static string? ReadStringProperty(IntPtr devices, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[512];
        if (!SetupDiGetDeviceRegistryPropertyW(devices, ref data, property, out _, buffer, (uint)buffer.Length, out uint required) || required < 2)
            return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)required).TrimEnd('\0');
    }

    private static int ReadUInt32Property(IntPtr devices, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        var buffer = new byte[4];
        return SetupDiGetDevicePropertyW(devices, ref data, ref key, out uint type, buffer, 4, out _, 0) && type == DEVPROP_TYPE_UINT32
            ? (int)BitConverter.ToUInt32(buffer, 0)
            : 0;
    }

    private static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly Guid PciDevicePropertySet = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");
    // devpkey.h: 3 BaseClass, 4 SubClass, 5 ProgIf, 6-8 payload / read-request sizes, then these.
    private static readonly DEVPROPKEY DEVPKEY_PciDevice_CurrentLinkSpeed = new() { fmtid = PciDevicePropertySet, pid = 9 };
    private static readonly DEVPROPKEY DEVPKEY_PciDevice_CurrentLinkWidth = new() { fmtid = PciDevicePropertySet, pid = 10 };
    private static readonly DEVPROPKEY DEVPKEY_PciDevice_MaxLinkSpeed = new() { fmtid = PciDevicePropertySet, pid = 11 };
    private static readonly DEVPROPKEY DEVPKEY_PciDevice_MaxLinkWidth = new() { fmtid = PciDevicePropertySet, pid = 12 };

    private const uint DIGCF_PRESENT = 0x2;
    private const uint ALLOC_LOG_CONF = 0x2;
    private const uint ResType_All = 0x0;
    private const uint ResType_Mem = 0x1;
    private const uint ResType_MemLarge = 0x7;
    private const int CR_SUCCESS = 0;
    private const int MaxDeviceIdLength = 200;   // MAX_DEVICE_ID_LEN
    private const uint SPDRP_DRIVER = 0x9;
    private const uint DEVPROP_TYPE_UINT32 = 0x7;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_First_Log_Conf(out IntPtr logConf, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Next_Res_Des(out IntPtr resDes, IntPtr current, uint forResource, out uint resourceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Res_Des_Data_Size(out uint size, IntPtr resDes, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Res_Des_Data(IntPtr resDes, byte[] buffer, uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Free_Res_Des_Handle(IntPtr resDes);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Free_Log_Conf_Handle(IntPtr logConf);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, int bufferLen, uint flags);
}
