using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The GPU facts the registry walk in <see cref="SystemInfoService.GetGpuInfoList"/> can't state
/// plainly: who made it, whether it's built into the CPU, the version its maker uses for the
/// driver, and the adapter LUID Windows' GPU performance counters name it by.
/// </summary>
public partial class SystemInfoService
{
    internal static GpuVendor VendorFromName(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Nvidia
        : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Amd
        : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? GpuVendor.Intel
        : GpuVendor.Other;

    private static GpuVendor VendorFromPciId(uint vendorId) => vendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        _ => GpuVendor.Other
    };

    // Intel's discrete cards are Arc A- and B-series (A770, B580). "Intel Arc Graphics" and the
    // Arc 130V / 140V are the GPUs inside Core Ultra processors.
    private static readonly Regex IntelDiscreteArc = new(@"Arc(\(TM\))?\s+[AB]\d{3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Built into the processor rather than a card of its own. Intel's are, apart from Arc A/B
    /// cards. AMD's are named "Radeon Graphics" / "Radeon 780M Graphics" / "Radeon Vega 8
    /// Graphics" - only the cards carry RX or Pro. Counting AMD's as dedicated, as before, put a
    /// Ryzen's built-in GPU on a level with the graphics card beside it.
    /// </summary>
    internal static bool IsIntegratedGpu(GpuVendor vendor, string name) => vendor switch
    {
        GpuVendor.Intel => !IntelDiscreteArc.IsMatch(name),
        GpuVendor.Amd => name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
                         && !name.Contains("RX", StringComparison.Ordinal)
                         && !name.Contains("Pro ", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>
    /// The driver version as its maker names it. NVIDIA's is the last five digits of Windows'
    /// version (32.0.16.1714 is 617.14). AMD's Adrenalin release (24.9.1) is in its own registry
    /// value; without it, and for everyone else, Windows' version is the one they use too.
    /// </summary>
    internal static string VendorDriverVersion(GpuVendor vendor, string windowsVersion, string? radeonSoftwareVersion)
    {
        if (vendor == GpuVendor.Nvidia)
        {
            var parts = windowsVersion.Split('.');
            if (parts.Length >= 4)
            {
                string digits = parts[^2] + parts[^1];
                if (digits.Length >= 5 && digits.All(char.IsDigit))
                    return $"{digits[^5..^2]}.{digits[^2..]}";
            }
        }
        if (vendor == GpuVendor.Amd && !string.IsNullOrWhiteSpace(radeonSoftwareVersion))
            return radeonSoftwareVersion.Trim();
        return string.IsNullOrWhiteSpace(windowsVersion) ? "Unknown" : windowsVersion;
    }

    /// <summary>The display-class key's DriverDate, written "9-17-2026" (month first).</summary>
    internal static DateTime? ParseDriverDate(string? value) =>
        DateTime.TryParseExact(value?.Trim(), new[] { "M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>
    /// Gives each GPU from the registry its DXGI adapter LUID, matched by name, and settles its
    /// vendor from the PCI vendor ID where the name was ambiguous. Best effort: a GPU left without
    /// a LUID just has no live load reading.
    /// </summary>
    private static void MatchDxgiAdapters(List<GpuHardwareInfo> gpus)
    {
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null)
                return;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0 && adapter != null; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var desc) != 0 || (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                            continue;

                        foreach (var gpu in gpus)
                        {
                            if (gpu.AdapterLuid != 0 || !string.Equals(gpu.ModelName.Trim(), desc.Description?.Trim(), StringComparison.OrdinalIgnoreCase))
                                continue;
                            gpu.AdapterLuid = ((long)desc.AdapterLuidHigh << 32) | desc.AdapterLuidLow;
                            if (gpu.Vendor == GpuVendor.Other)
                                gpu.Vendor = VendorFromPciId(desc.VendorId);
                            break;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            // Only the live GPU load needs the LUID; everything else on the card is already read.
            LoggingService.Warn("SystemInfo", $"Could not match GPUs to DXGI adapters: {ex.Message}");
        }
    }

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    // Only the methods called are typed; the rest hold their vtable slots in declaration order.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 adapterOut);
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);
}
