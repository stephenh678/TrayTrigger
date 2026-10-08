using System;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

public enum DriverCheckState { None, Checking, UpToDate, NewerAvailable, Problem }

/// <summary>
/// One GPU on System > Hardware Specs: what it is, its live load and video memory in use, its
/// driver, and a way to a newer driver. For NVIDIA the check asks NVIDIA's site and says whether
/// a newer Game Ready driver exists; AMD and Intel publish nothing to ask, so their button opens
/// the vendor's own driver page, where their updater takes over.
/// </summary>
public sealed class GpuCardViewModel : ViewModelBase
{
    public const string AmdDriversUrl = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelDriversUrl = "https://www.intel.com/content/www/us/en/support/detect.html";

    private readonly NvidiaDriverService _nvidia;
    private readonly Func<DateTime> _now;
    private string? _latestDriverUrl;

    public GpuCardViewModel(GpuHardwareInfo info, NvidiaDriverService nvidia, Func<DateTime>? now = null)
    {
        Info = info;
        _nvidia = nvidia;
        _now = now ?? (() => DateTime.Now);
        CheckDriverCommand = new AsyncRelayCommand(CheckDriverAsync, () => CheckState != DriverCheckState.Checking);
        GetDriverCommand = new RelayCommand(() => HelpCommands.OpenUrl.Execute(_latestDriverUrl ?? NvidiaDriverService.DriversPageUrl));
    }

    public GpuHardwareInfo Info { get; }

    public string Name => Info.ModelName;

    /// <summary>"Dedicated" or "Built-in": which one a game should be on matters on a laptop.</summary>
    public string KindLabel => Info.IsDedicated ? "Dedicated" : "Built-in";

    public string VramDisplay => Info.VramGigabytes > 0 ? $"{Info.VramGigabytes:0.#} GB VRAM" : "";

    /// <summary>"PCIe 5.0 x16"; empty for a built-in GPU, which has no link of its own.</summary>
    public string PcieDisplay => Info.PcieDisplay;
    public bool HasPcie => Info.HasPcieLink;
    public bool IsPcieNarrowed => Info.IsPcieNarrowed;
    public string PcieWarning => Info.PcieWarning;

    /// <summary>Resizable BAR read as off on a dedicated GPU: the BIOS switch is worth a few percent.</summary>
    public bool IsResizableBarOff => Info.ResizableBarEnabled == false;
    public string ResizableBarWarning => Info.ResizableBarWarning;

    /// <summary>"1.3 GB of 15.9 GB VRAM in use · PCIe 5.0 x16 · Resizable BAR on".</summary>
    public string DetailLine => string.Join(" · ", new[]
    {
        VramUsageDisplay,
        PcieDisplay,
        Info.ResizableBarEnabled == true ? "Resizable BAR on" : ""
    }.Where(part => part.Length > 0));

    // ---- Live ---------------------------------------------------------------------------------

    private int? _loadPercent;
    private double? _vramUsedGigabytes;

    /// <summary>The pill: GPU load once a reading is in, the video memory size until then.</summary>
    public string PillText => _loadPercent is int load ? $"Load: {load}%" : VramDisplay;
    public bool IsLive => _loadPercent.HasValue;
    public bool IsNotLive => !IsLive;
    public int LoadPercent => _loadPercent ?? 0;

    /// <summary>"5.2 GB of 16 GB VRAM in use", once a reading is in.</summary>
    public string VramUsageDisplay => _vramUsedGigabytes is double used && Info.VramGigabytes > 0
        ? $"{used:0.#} GB of {Info.VramGigabytes:0.#} GB VRAM in use"
        : VramDisplay;

    public void ApplySample(GpuLiveSample sample)
    {
        _loadPercent = sample.LoadPercent;
        _vramUsedGigabytes = sample.DedicatedUsedGigabytes;
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsNotLive));
        OnPropertyChanged(nameof(LoadPercent));
        OnPropertyChanged(nameof(VramUsageDisplay));
        OnPropertyChanged(nameof(DetailLine));
    }

    // ---- Driver -------------------------------------------------------------------------------

    /// <summary>"Driver 617.14 · Sep 17, 2026 (2 weeks ago)"; AMD's says Adrenalin when that's the version shown.</summary>
    public string DriverLine
    {
        get
        {
            string label = Info.Vendor == GpuVendor.Amd && Info.DriverVersion != Info.WindowsDriverVersion
                ? "AMD Software: Adrenalin Edition"
                : "Driver";
            string line = $"{label} {Info.DriverVersion}";
            if (Info.DriverDateValue is DateTime date)
                line += $" · {date:MMM d, yyyy} ({Age(date, _now())})";
            return line;
        }
    }

    /// <summary>"today", "3 days ago", "2 weeks ago", "5 months ago", "over a year ago".</summary>
    internal static string Age(DateTime date, DateTime now)
    {
        int days = Math.Max(0, (int)(now.Date - date.Date).TotalDays);
        return days switch
        {
            0 => "today",
            1 => "yesterday",
            < 14 => $"{days} days ago",
            < 60 => $"{days / 7} weeks ago",
            < 365 => $"{days / 30} months ago",
            < 730 => "over a year ago",
            _ => $"over {days / 365} years ago"
        };
    }

    public bool HasDriverCheck => Info.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel;

    public string CheckButtonText => Info.Vendor == GpuVendor.Nvidia ? "Check for Newer Driver" : "Get Latest Driver...";

    public string CheckButtonToolTip => Info.Vendor switch
    {
        GpuVendor.Nvidia => "Asks NVIDIA's site which Game Ready driver is newest for this GPU. Only runs when you click.",
        GpuVendor.Amd => "Opens AMD's driver page. AMD Software: Adrenalin Edition also checks for updates itself.",
        _ => "Opens Intel's Driver & Support Assistant page, which finds the right driver for this PC."
    };

    public ICommand CheckDriverCommand { get; }
    public ICommand GetDriverCommand { get; }

    private DriverCheckState _checkState;
    public DriverCheckState CheckState
    {
        get => _checkState;
        private set
        {
            if (!SetProperty(ref _checkState, value)) return;
            OnPropertyChanged(nameof(HasCheckMessage));
            OnPropertyChanged(nameof(ShowGetDriver));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _checkMessage = "";
    public string CheckMessage { get => _checkMessage; private set => SetProperty(ref _checkMessage, value); }
    public bool HasCheckMessage => CheckState != DriverCheckState.None;

    /// <summary>The Get Driver button: a newer driver to fetch, or NVIDIA's own page to look on.</summary>
    public bool ShowGetDriver => CheckState == DriverCheckState.NewerAvailable || (CheckState == DriverCheckState.Problem && _latestDriverUrl != null);

    private async Task CheckDriverAsync()
    {
        if (Info.Vendor != GpuVendor.Nvidia)
        {
            HelpCommands.OpenUrl.Execute(Info.Vendor == GpuVendor.Amd ? AmdDriversUrl : IntelDriversUrl);
            return;
        }

        _latestDriverUrl = null;
        CheckMessage = "Asking NVIDIA...";
        CheckState = DriverCheckState.Checking;

        var result = await _nvidia.CheckAsync(Info.ModelName, Info.DriverVersion);
        _latestDriverUrl = result.DetailsUrl;
        string released = string.IsNullOrWhiteSpace(result.ReleaseDate) ? "" : $", released {result.ReleaseDate}";
        (CheckState, CheckMessage) = result.Outcome switch
        {
            DriverCheckOutcome.UpToDate => (DriverCheckState.UpToDate,
                $"✓ Up to date. {result.LatestVersion} is NVIDIA's newest Game Ready driver for this GPU{released}."),
            DriverCheckOutcome.NewerAvailable => (DriverCheckState.NewerAvailable,
                $"A newer Game Ready driver is out: {result.LatestVersion}{released}. NVIDIA App can install it too."),
            DriverCheckOutcome.GpuNotListed => (DriverCheckState.Problem,
                "NVIDIA's driver service doesn't list this GPU by that name. Get Driver opens NVIDIA's driver page to pick it by hand."),
            _ => (DriverCheckState.Problem,
                "Couldn't reach NVIDIA's driver service. Check your connection and try again.")
        };
    }
}
