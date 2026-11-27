using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>
/// This PC's parts as Game Details' requirements card shows them beside a game's Minimum and
/// Recommended lists. Read once per run (SystemInfoService.GetPcSummaryAsync); every field may be
/// empty, zero or null when Windows wouldn't say, and the card then shows nothing for it.
/// </summary>
public sealed record PcSummary
{
    /// <summary>"Windows 11 Pro".</summary>
    public string Os { get; init; } = "";
    /// <summary>10 or 11 (from the build number), 0 when unknown.</summary>
    public int WindowsMajor { get; init; }
    public string Cpu { get; init; } = "";
    public int CpuCores { get; init; }
    /// <summary>The memory installed (32 for 32 GB), not what Windows reports as usable (31.7).</summary>
    public double InstalledRamGb { get; init; }
    /// <summary>The graphics card games use: the dedicated one with the most memory, else the first.</summary>
    public string Gpu { get; init; } = "";
    public double GpuMemoryGb { get; init; }
    /// <summary>The highest DirectX level that card supports (12 for 12_0 to 12_2), null when unknown.</summary>
    public int? DirectXMajor { get; init; }
    /// <summary>"DirectX 12 Ultimate", "DirectX 12", "DirectX 11".</summary>
    public string DirectXLabel { get; init; } = "";
    /// <summary>The default playback device's name, "Speakers (Realtek USB Audio)".</summary>
    public string SoundDevice { get; init; } = "";
    /// <summary>Each fixed drive's letter ("D:") and free space.</summary>
    public IReadOnlyList<(string Letter, double FreeGb)> Drives { get; init; } = [];
}
