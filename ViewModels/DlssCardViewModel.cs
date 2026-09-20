using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The read-only DLSS card in Edit Game - step 1 of the build order in docs/dlss-plan.md.
///
/// <para>It reports and does not act. There is no apply button yet, because applying requires the
/// ownership record (step 3) and nothing here can write. Everything shown is something the probe
/// actually read; where it could not read, the card says so rather than guessing.</para>
/// </summary>
public sealed class DlssCardViewModel : ViewModelBase
{
    /// <summary>One feature's row: what the game ships and whether anything is overriding it.</summary>
    public sealed record FeatureRow(string Feature, string Version, string State);

    /// <summary>
    /// The card's whole content, derived from a probe result. Pure - no driver, no disk - so the
    /// display rules are testable without an NVIDIA machine.
    /// </summary>
    public sealed record Projection(
        bool HasDlss,
        IReadOnlyList<FeatureRow> Rows,
        string DriverLine,
        string? ExternalOverrideNotice);

    private readonly string? _executablePath;
    private bool _isLoading;
    private bool _hasLoaded;
    private bool _isOnTab = true;
    private Projection _content = Empty;

    private static readonly Projection Empty = new(false, Array.Empty<FeatureRow>(), string.Empty, null);

    public DlssCardViewModel(string? executablePath) => _executablePath = executablePath;

    /// <summary>
    /// Whether the card's tab is selected. Owned here rather than combined in XAML so the card has
    /// one visibility binding instead of a multi-binding over two view models.
    /// </summary>
    public bool IsOnTab
    {
        get => _isOnTab;
        set { if (SetProperty(ref _isOnTab, value)) OnPropertyChanged(nameof(IsVisible)); }
    }

    /// <summary>Hidden until the probe has run and found a DLSS runtime the game ships.</summary>
    public bool IsVisible => _hasLoaded && _content.HasDlss && IsOnTab;
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(IsVisible)); } }

    public IReadOnlyList<FeatureRow> Rows => _content.Rows;
    public string DriverLine => _content.DriverLine;
    public string? ExternalOverrideNotice => _content.ExternalOverrideNotice;
    public bool HasExternalOverrideNotice => !string.IsNullOrEmpty(_content.ExternalOverrideNotice);

    /// <summary>
    /// The sentence the plan calls load-bearing: it is what reassures someone who has heard that
    /// swapping DLSS DLLs is risky. It is also strictly true, which is why it may be said at all.
    /// </summary>
    public string NoFilesChangedNotice => "No game files are changed - the driver supplies the model.";

    /// <summary>
    /// Runs the probe off the UI thread. Called from the dialog's Loaded handler rather than the
    /// constructor, so building a view model - as the tests do - never touches the driver.
    ///
    /// <para>Idempotent, and returns the <i>same</i> task on every call. A second caller that
    /// wants to wait for the card - the screenshot harness does - must be able to await the work
    /// already in flight rather than get back a completed task while the probe is still running.</para>
    /// </summary>
    public Task LoadAsync() => _load ??= LoadCoreAsync();

    private Task? _load;

    private async Task LoadCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_executablePath)) { _hasLoaded = true; return; }
        IsLoading = true;
        try
        {
            string path = _executablePath;
            var result = await Task.Run(() => DlssProbeService.Probe(path)).ConfigureAwait(true);
            _content = Project(result);
        }
        catch (Exception ex)
        {
            // A diagnostic card that breaks the dialog would be worse than no card.
            LoggingService.Warn("Dlss", $"DLSS card probe failed: {ex.Message}");
            _content = Empty;
        }
        finally
        {
            _hasLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(DriverLine));
            OnPropertyChanged(nameof(ExternalOverrideNotice));
            OnPropertyChanged(nameof(HasExternalOverrideNotice));
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    /// <summary>Turns a probe result into the card's content. Pure; exercised directly by tests.</summary>
    public static Projection Project(DlssProbeService.ProbeResult result)
    {
        if (result.ShippedRuntimes.Count == 0) return Empty;

        // Newest shipped copy per feature: a game can carry the same runtime in several folders,
        // and showing each one would be noise.
        var shipped = result.ShippedRuntimes
            .GroupBy(s => s.Feature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rows = new List<FeatureRow>();
        bool anyExternalOverride = false;

        foreach ((_, string feature) in NgxModelStore.Features)
        {
            if (!shipped.TryGetValue(feature, out var ship)) continue;

            var state = DescribeState(result, feature, out bool isExternalOverride);
            anyExternalOverride |= isExternalOverride;
            rows.Add(new FeatureRow(feature, ship.FileVersion ?? "unknown", state));
        }

        if (rows.Count == 0) return Empty;

        return new Projection(true, rows, DescribeDriver(result), anyExternalOverride
            // TrayTrigger has written nothing - there is no write path yet - so any override found
            // came from somewhere else. The plan forbids silently overwriting it, and a user who
            // does not know it is there cannot make sense of what the game reports.
            ? "Something else has already set a DLSS override for this game - NVIDIA App, Profile Inspector or a similar tool. TrayTrigger has not changed anything."
            : null);
    }

    private static string DescribeState(DlssProbeService.ProbeResult result, string feature, out bool isExternalOverride)
    {
        isExternalOverride = false;

        if (result.Profile == null)
        {
            // Not a failure: NVIDIA simply has no entry for this executable. Saying "Game default"
            // here would assert something the probe did not read.
            return "No driver profile";
        }

        var toggle = result.SettingStates.FirstOrDefault(s =>
            string.Equals(s.Definition.Feature, feature, StringComparison.Ordinal) &&
            s.Definition.Name.Contains("Enable DLL Override", StringComparison.Ordinal));

        if (toggle?.Value == null || toggle.Value.CurrentValue == 0) return "Game default";

        isExternalOverride = true;
        return toggle.Value.Origin switch
        {
            NvApi.SettingOrigin.ApplicationProfile => "Overridden for this game",
            NvApi.SettingOrigin.GlobalProfile => "Overridden by your global settings",
            NvApi.SettingOrigin.BaseProfile => "Overridden by the driver",
            _ => "Overridden"
        };
    }

    private static string DescribeDriver(DlssProbeService.ProbeResult result)
    {
        var newest = result.DriverRuntimes
            .GroupBy(d => d.Feature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.EncodedVersion).First().Version, StringComparer.Ordinal);

        if (newest.Count == 0) return "The driver holds no DLSS runtimes of its own.";

        // One version covers all three features in practice, so say it once rather than three
        // times; only spell them out when they genuinely differ.
        var distinct = newest.Values.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1
            ? $"Your driver holds DLSS {distinct[0]}."
            : "Your driver holds " + string.Join(", ", newest.OrderBy(k => k.Key, StringComparer.Ordinal)
                                                              .Select(k => $"{k.Key} {k.Value}")) + ".";
    }
}
