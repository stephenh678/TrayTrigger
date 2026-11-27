using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// This PC's parts for Game Details' requirements card (<see cref="PcSummary"/>): read once per run on a
/// background thread, from the same registry keys as the System page plus three small reads of its own -
/// the memory installed, the DirectX level of the graphics card games use, and the default playback
/// device. Nothing leaves the PC.
/// </summary>
public partial class SystemInfoService
{
    private static readonly Lazy<Task<PcSummary>> PcSummaryOnce = new(() => Task.Run(ReadPcSummary));

    /// <summary>This PC's parts, read the first time any Game Details window asks and kept for the run.</summary>
    public static Task<PcSummary> GetPcSummaryAsync() => PcSummaryOnce.Value;

    private static PcSummary ReadPcSummary()
    {
        var info = new SystemInfoService();
        var os = Safe(info.GetOsInfo, new OsEnvironmentInfo());
        var cpu = Safe(info.GetCpuInfo, new CpuHardwareInfo());
        var gpus = Safe(info.GetGpuInfoList, new List<GpuHardwareInfo>());
        var gpu = gpus.Where(g => g.IsDedicated).OrderByDescending(g => g.VramGigabytes).FirstOrDefault() ?? gpus.FirstOrDefault();
        var (dxMajor, dxLabel) = Safe(DirectXLevel, (null, ""));

        return new PcSummary
        {
            Os = os.WindowsEdition.Trim(),
            WindowsMajor = int.TryParse(os.BuildNumber, out int build) && build > 0 ? (build >= 22000 ? 11 : 10) : 0,
            Cpu = cpu.ModelName == "Unknown CPU" ? "" : cpu.ModelName,
            CpuCores = cpu.PhysicalCores,
            InstalledRamGb = Safe(InstalledMemoryGb, 0),
            Gpu = gpu?.ModelName ?? "",
            GpuMemoryGb = gpu?.VramGigabytes ?? 0,
            DirectXMajor = dxMajor,
            DirectXLabel = dxLabel,
            SoundDevice = AudioEndpointService.GetDefaultRenderName(),
            Drives = Safe(FixedDrives, []),
        };
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("SystemInfo", $"Requirements card: a part could not be read: {ex.Message}");
            return fallback;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    /// <summary>The memory installed, from the modules themselves: 32 for 32 GB, where Windows reports 31.7 usable.</summary>
    private static double InstalledMemoryGb() =>
        GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0 ? Math.Round(kb / (1024.0 * 1024.0), 1) : 0;

    private static IReadOnlyList<(string Letter, double FreeGb)> FixedDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => (d.Name.TrimEnd('\\'), Math.Round(d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1)))
            .ToList();

    // ---------------------------------------------------------------------------------------------
    // DirectX level. Every Windows 10 and 11 PC has DirectX 12 itself; what a game's "DirectX:
    // Version 12" line needs is a graphics card that supports it. DXGI lists the cards (the same
    // interfaces as MatchDxgiAdapters); the one with the most memory of its own is the one games use. D3D12CreateDevice with no device to return
    // only answers whether that card supports a level, so nothing is created.
    // ---------------------------------------------------------------------------------------------

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);

    private static readonly Guid D3D12DeviceIid = new("189819F1-1DB6-4B57-BE54-1821339B85F7");

    /// <summary>The highest DirectX level the graphics card games use supports, or (null, "") when that can't be told.</summary>
    private static (int? Major, string Label) DirectXLevel()
    {
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null) return (null, "");
        IDXGIAdapter1? best = null;
        try
        {
            nuint bestMemory = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0 && adapter != null; i++)
            {
                bool keep = adapter.GetDesc1(out var desc) == 0 && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0
                            && (best == null || desc.DedicatedVideoMemory > bestMemory);
                if (!keep)
                {
                    Marshal.ReleaseComObject(adapter);
                    continue;
                }
                if (best != null) Marshal.ReleaseComObject(best);
                best = adapter;
                bestMemory = desc.DedicatedVideoMemory;
            }
            if (best == null) return (null, "");

            IntPtr adapterPtr = Marshal.GetIUnknownForObject(best);
            try
            {
                var deviceIid = D3D12DeviceIid;
                foreach (var (level, major, label) in new[]
                         {
                             (0xc200, 12, "DirectX 12 Ultimate"), (0xc100, 12, "DirectX 12"), (0xc000, 12, "DirectX 12"),
                             (0xb100, 11, "DirectX 11"), (0xb000, 11, "DirectX 11"),
                         })
                {
                    // S_FALSE (1) with no device to return means "it would succeed".
                    if (D3D12CreateDevice(adapterPtr, level, ref deviceIid, IntPtr.Zero) is 0 or 1) return (major, label);
                }
                return (null, "");
            }
            finally
            {
                Marshal.Release(adapterPtr);
            }
        }
        finally
        {
            if (best != null) Marshal.ReleaseComObject(best);
            Marshal.ReleaseComObject(factory);
        }
    }
}
