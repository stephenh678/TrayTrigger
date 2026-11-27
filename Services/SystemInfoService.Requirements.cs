using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// This PC's parts for Game Details' requirements card (<see cref="PcSummary"/>). The hardware is read once per run
/// on a background thread, from the same registry keys as the System page plus the memory installed and the graphics
/// card games use (DXGI, with its own memory and the newest DirectX it runs). The drives' free space and the default
/// playback device are read each time a window asks, since both change while TrayTrigger sits in the tray for days.
/// Nothing leaves the PC.
/// </summary>
public partial class SystemInfoService
{
    private static readonly Lazy<Task<PcSummary>> HardwareOnce = new(() => Task.Run(ReadHardware));

    /// <summary>This PC's parts: the hardware as read the first time any window asked, the free space and playback device now.</summary>
    public static async Task<PcSummary> GetPcSummaryAsync()
    {
        var hardware = await HardwareOnce.Value.ConfigureAwait(false);
        return await Task.Run(() => hardware with
        {
            SoundDevice = AudioEndpointService.GetDefaultRenderName(),
            Drives = Safe(FixedDrives, []),
        }).ConfigureAwait(false);
    }

    private static PcSummary ReadHardware()
    {
        var info = new SystemInfoService();
        var os = Safe(info.GetOsInfo, new OsEnvironmentInfo());
        var cpu = Safe(info.GetCpuInfo, new CpuHardwareInfo());
        // The card's name, memory and DirectX come from DXGI: its memory figure is 64-bit, where the registry's
        // older value stops at 4 GB. The registry's list only names the card when DXGI can't be read.
        var card = Safe(GamesCard, null);
        string registryName = card == null
            ? Safe(() => info.GetGpuInfoList().Where(g => g.IsDedicated).OrderByDescending(g => g.VramGigabytes).FirstOrDefault()?.ModelName, null) ?? ""
            : "";

        return new PcSummary
        {
            Os = os.WindowsEdition.Trim(),
            WindowsMajor = int.TryParse(os.BuildNumber, out int build) && build > 0 ? (build >= 22000 ? 11 : 10) : 0,
            Cpu = cpu.ModelName == "Unknown CPU" ? "" : cpu.ModelName,
            CpuCores = cpu.PhysicalCores,
            InstalledRamGb = Safe(InstalledMemoryGb, 0),
            UsableRamGb = Safe(() => info.GetRamUsage().TotalGigabytes, 0),
            Gpu = card?.Name ?? registryName,
            GpuMemoryGb = card?.MemoryGb ?? 0,
            DirectXMajor = card?.DirectXMajor,
            DirectXLabel = card?.DirectXLabel ?? "",
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

    /// <summary>The memory installed, from the modules themselves: 32 for 32 GB, where Windows reports 31.7 usable.
    /// 0 where the firmware doesn't say (some VMs).</summary>
    private static double InstalledMemoryGb() =>
        GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0 ? Math.Round(kb / (1024.0 * 1024.0), 1) : 0;

    /// <summary>Each fixed drive's letter and free space; a drive that can't be read is left out, not the whole list.</summary>
    private static IReadOnlyList<(string Letter, double FreeGb)> FixedDrives()
    {
        var drives = new List<(string, double)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    drives.Add((drive.Name.TrimEnd('\\'), Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.Verbose("SystemInfo", $"Requirements card: drive {drive.Name} could not be read: {ex.Message}");
            }
        }
        return drives;
    }

    // ---------------------------------------------------------------------------------------------
    // The graphics card games use, and the newest DirectX it runs. DXGI lists the cards (the same
    // interfaces as MatchDxgiAdapters); the one with the most memory of its own is the one games use.
    // Every Windows 10 and 11 PC has DirectX 12 itself; what a game's "DirectX: Version 12" line needs
    // is a card DirectX 12 accepts, at any feature level: a GTX 650 Ti runs DirectX 12 at feature level
    // 11_0. D3D12CreateDevice and D3D11CreateDevice with no device to return only answer whether a card
    // is accepted, so nothing is created.
    // ---------------------------------------------------------------------------------------------

    private sealed record GraphicsCard(string Name, double MemoryGb, int? DirectXMajor, string DirectXLabel);

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, IntPtr device);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int[] featureLevels,
        uint featureLevelCount, uint sdkVersion, IntPtr device, out int featureLevel, IntPtr immediateContext);

    private static readonly Guid D3D12DeviceIid = new("189819F1-1DB6-4B57-BE54-1821339B85F7");
    private const int D3DDriverTypeUnknown = 0;
    private const uint D3D11SdkVersion = 7;

    /// <summary>The card with the most memory of its own, or null when DXGI lists none.</summary>
    private static GraphicsCard? GamesCard()
    {
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null) return null;
        IDXGIAdapter1? best = null;
        DXGI_ADAPTER_DESC1 bestDesc = default;
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0 && adapter != null; i++)
            {
                bool keep = adapter.GetDesc1(out var desc) == 0 && (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0
                            && (best == null || desc.DedicatedVideoMemory > bestDesc.DedicatedVideoMemory);
                if (!keep)
                {
                    Marshal.ReleaseComObject(adapter);
                    continue;
                }
                if (best != null) Marshal.ReleaseComObject(best);
                best = adapter;
                bestDesc = desc;
            }
            if (best == null) return null;

            IntPtr adapterPtr = Marshal.GetIUnknownForObject(best);
            try
            {
                int? d3d12 = D3D12Level(adapterPtr);
                var (major, label) = DirectXOf(d3d12, d3d12 == null ? Safe(() => D3D11Level(adapterPtr), null) : null);
                // Built-in graphics has a token amount of its own and borrows the rest: no figure to compare a
                // game's "4 GB VRAM" against, so none is given (and nothing is warned on).
                double memory = Math.Round(bestDesc.DedicatedVideoMemory / (1024.0 * 1024.0 * 1024.0), 1);
                return new GraphicsCard(bestDesc.Description?.Trim() ?? "", memory >= 0.5 ? memory : 0, major, label);
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

    /// <summary>The highest feature level DirectX 12 accepts the card at, or null when it doesn't.</summary>
    private static int? D3D12Level(IntPtr adapter)
    {
        var iid = D3D12DeviceIid;
        foreach (int level in new[] { 0xc200, 0xc100, 0xc000, 0xb100, 0xb000 })
        {
            // S_FALSE (1) with no device to return means "it would succeed". A Windows too old to know 12_2 refuses
            // that level as invalid, and the next one is tried.
            if (Safe(() => D3D12CreateDevice(adapter, level, ref iid, IntPtr.Zero), -1) is 0 or 1) return level;
        }
        return null;
    }

    /// <summary>The highest feature level DirectX 11 accepts the card at, or null when it doesn't.</summary>
    private static int? D3D11Level(IntPtr adapter)
    {
        int[] levels = [0xb100, 0xb000, 0xa100, 0xa000];
        int hr = D3D11CreateDevice(adapter, D3DDriverTypeUnknown, IntPtr.Zero, 0, levels, (uint)levels.Length, D3D11SdkVersion,
            IntPtr.Zero, out int level, IntPtr.Zero);
        return hr is 0 or 1 ? level : null;
    }

    /// <summary>
    /// The newest DirectX a card runs, from the feature levels DirectX 12 and DirectX 11 accept it at: any DirectX 12
    /// level is DirectX 12 (12_2 is DirectX 12 Ultimate); a card only DirectX 11 accepts, at 11_0 or above, is DirectX
    /// 11; anything less is not claimed, so a game's DirectX line is never warned on from a guess.
    /// </summary>
    internal static (int? Major, string Label) DirectXOf(int? d3d12Level, int? d3d11Level) =>
        d3d12Level is int level12 ? (12, level12 >= 0xc200 ? "DirectX 12 Ultimate" : "DirectX 12")
        : d3d11Level is >= 0xb000 ? (11, "DirectX 11")
        : (null, "");
}
