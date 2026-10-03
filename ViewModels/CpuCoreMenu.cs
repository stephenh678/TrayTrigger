using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// Which items the library's CPU Cores submenus show, from this PC's CPU - the same choices Edit
/// Game lists. The submenu itself is hidden on a CPU with nothing to choose: an option that cannot
/// do anything is worse than no option. Edit Game still shows a game's existing choice there.
/// </summary>
public sealed class CpuCoreMenu
{
    private CpuCoreMenu(CpuTopology topology)
    {
        var options = topology.Options;
        IsVisible = options.Count > 0;
        ShowAuto = options.Contains(CpuAffinityMode.Auto);
        ShowPerformanceCores = options.Contains(CpuAffinityMode.PerformanceCoresOnly);
        ShowVCache = options.Contains(CpuAffinityMode.VCacheCores);
        ShowFrequency = options.Contains(CpuAffinityMode.FrequencyCores);
        ShowOneCcd = options.Contains(CpuAffinityMode.OneCcd);
    }

    public bool IsVisible { get; }
    public bool ShowAuto { get; }
    public bool ShowPerformanceCores { get; }
    public bool ShowVCache { get; }
    public bool ShowFrequency { get; }
    public bool ShowOneCcd { get; }

    public static CpuCoreMenu For(CpuTopology topology) => new(topology);

    private static CpuCoreMenu? _forThisPc;
    public static CpuCoreMenu ForThisPc => _forThisPc ??= For(CpuTopologyService.GetTopology());
}
