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
        IntPtr devices = IntPtr.Zero;
        try
        {
            var displayClass = GUID_DEVCLASS_DISPLAY;
            devices = SetupDiGetClassDevsW(ref displayClass, null, IntPtr.Zero, DIGCF_PRESENT);
            if (devices == INVALID_HANDLE_VALUE) return;

            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(devices, i, ref data); i++)
            {
                // "{4d36e968-e325-11ce-bfc1-08002be10318}\0000": the registry key GetGpuInfoList read.
                string? driverKey = ReadStringProperty(devices, ref data, SPDRP_DRIVER);
                if (driverKey == null) continue;
                string subKey = driverKey[(driverKey.LastIndexOf('\\') + 1)..];

                foreach (var gpu in gpus)
                {
                    if (!string.Equals(gpu.DriverKey, subKey, StringComparison.OrdinalIgnoreCase)) continue;
                    gpu.PcieCurrentWidth = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_CurrentLinkWidth);
                    gpu.PcieMaxWidth = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_MaxLinkWidth);
                    gpu.PcieCurrentGeneration = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_CurrentLinkSpeed);
                    gpu.PcieMaxGeneration = ReadUInt32Property(devices, ref data, DEVPKEY_PciDevice_MaxLinkSpeed);
                    LoggingService.Verbose("SystemInfo", $"PCIe link for '{gpu.ModelName}': width x{gpu.PcieCurrentWidth} of x{gpu.PcieMaxWidth}, generation {gpu.PcieCurrentGeneration} of {gpu.PcieMaxGeneration}.");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfo", $"Could not read the GPUs' PCIe links: {ex.Message}");
        }
        finally
        {
            if (devices != IntPtr.Zero && devices != INVALID_HANDLE_VALUE)
                SetupDiDestroyDeviceInfoList(devices);
        }
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
}
