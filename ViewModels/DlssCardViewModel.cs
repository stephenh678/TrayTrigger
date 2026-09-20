using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The DLSS Override card in Edit Game. See docs/dlss-plan.md.
///
/// <para><b>DLSS Override is NVIDIA's name for this</b>, not ours - it is what the driver calls
/// the settings ("Enable DLSS-SR override") and what NVIDIA App calls the feature. An earlier
/// version of this card invented "Use recommended", which appears in nobody's vocabulary and left
/// the user with nothing to search for.</para>
///
/// <para>The card says two versions and offers one switch. Everything else it used to show - a row
/// per feature, a Verify button, a details expander, what was observed loading - was the mechanism
/// on display rather than the outcome, and is gone. The override covers all three features in one
/// write, so a per-feature breakdown answered a question nobody was asking; the switch's sub-line
/// names the three features instead.</para>
///
/// <para>Turning the switch on writes to the driver and saves <i>immediately</i>, not on Save
/// Changes: the driver change has already happened, so deferring the record would let Cancel
/// strand an override TrayTrigger could no longer undo.</para>
/// </summary>
public sealed class DlssCardViewModel : ViewModelBase
{
    /// <summary>What the card knows after probing. Pure, so the wording is testable without a driver.</summary>
    public sealed record Projection(
        bool HasDlss,
        string? GameVersion,
        string? DriverVersion,
        bool DriverIsNewer,
        IReadOnlyDictionary<string, string?> ShippedByFeature,
        string? ExternalOverrideNotice);

    private readonly string? _executablePath;
    private readonly string _gameName;
    private readonly DlssOverrideService _overrides;
    private readonly List<DlssSettingRecord> _records;
    private readonly Action? _persist;
    private readonly GameEntry? _game;
    private readonly Func<string, DlssProbeService.ProbeResult> _probe;

    private bool _isBusy;
    private bool _hasLoaded;
    private bool _isOnTab = true;
    private string? _status;
    private Projection _content = Empty;
    private Task? _load;

    private static readonly Projection Empty =
        new(false, null, null, false, new Dictionary<string, string?>(StringComparer.Ordinal), null);

    /// <param name="records">
    /// The game's live ownership records, mutated in place and handed to <paramref name="persist"/>,
    /// so the record and the driver never disagree about what TrayTrigger owns.
    /// </param>
    /// <param name="probe">Substituted by tests, so the card works without an NVIDIA machine.</param>
    public DlssCardViewModel(
        string? executablePath,
        string gameName = "",
        List<DlssSettingRecord>? records = null,
        Action? persist = null,
        DlssOverrideService? overrides = null,
        Func<string, DlssProbeService.ProbeResult>? probe = null,
        GameEntry? game = null)
    {
        _game = game;
        _executablePath = executablePath;
        _gameName = gameName;
        _records = records ?? new List<DlssSettingRecord>();
        _persist = persist;
        _overrides = overrides ?? new DlssOverrideService();
        _probe = probe ?? (path => DlssProbeService.Probe(path));

        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanRestore);
    }

    /// <summary>Whether the card's tab is selected. Owned here so the XAML needs one binding.</summary>
    public bool IsOnTab
    {
        get => _isOnTab;
        set { if (SetProperty(ref _isOnTab, value)) OnPropertyChanged(nameof(IsVisible)); }
    }

    /// <summary>Hidden until the probe has run and found a DLSS runtime the game ships.</summary>
    public bool IsVisible => _hasLoaded && _content.HasDlss && IsOnTab;

    /// <summary>True while a driver write is in flight.</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    // ---- The one thing the card says --------------------------------------------------------

    /// <summary>
    /// What the game has now and what it would get, as a pair: <c>310.1.0 -> 310.9.0</c>. It sits
    /// on the switch's own row, so the before and after are read together and no sentence is needed
    /// to join them. An earlier card printed the two numbers in prose and left the reader to work
    /// out which was which.
    /// </summary>
    public string VersionLine =>
        _content.GameVersion == null ? string.Empty
        : _content.DriverVersion == null ? _content.GameVersion
        : _content.DriverIsNewer
            ? $"{_content.GameVersion} → {_content.DriverVersion}"
            : $"{_content.GameVersion} (already current)";

    /// <summary>
    /// NVIDIA App's own wording for this toggle. Anyone who has seen it there recognises it, and
    /// anyone who has not has an exact term to search. The version pair is beside it rather than
    /// in it, because the label names the feature and the pair states the effect.
    /// </summary>
    public string OverrideLabel => "Enable DLSS Override for this game";

    /// <summary>
    /// The switch. On means TrayTrigger has written the override for this game; off means it has
    /// not, or has put it back. Setting it does the work - there is no separate apply.
    /// </summary>
    public bool OverrideEnabled
    {
        get => _records.Count > 0;
        set
        {
            if (value == OverrideEnabled || IsBusy) return;
            _ = value ? ApplyAsync() : RestoreAsync();
        }
    }

    /// <summary>
    /// Off by default, and labelled as a testing aid because that is what it is: it draws over the
    /// game, needs administrator permission, and tells you nothing you need for normal play.
    /// </summary>
    public bool ShowOverlay
    {
        get => _game?.DlssShowOverlay == true;
        set
        {
            if (_game == null || _game.DlssShowOverlay == value) return;
            _game.DlssShowOverlay = value;
            _persist?.Invoke();
            OnPropertyChanged();
        }
    }

    /// <summary>Puts back everything TrayTrigger changed, whatever the switch currently says.</summary>
    public ICommand RestoreCommand { get; }

    public bool CanRestore => !IsBusy && _records.Count > 0;

    /// <summary>The result of the last change. Null until something happens.</summary>
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    /// <summary>
    /// Something else - NVIDIA App, Profile Inspector - already overrides this game, or has changed
    /// what TrayTrigger wrote. Only ever set when true.
    /// </summary>
    public string? Notice => _game?.DlssConflicted == true
        ? "Something else changed this game's DLSS settings, so TrayTrigger stopped re-applying them. Switch it on again to take them over, or restore to hand them back."
        : _content.ExternalOverrideNotice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>
    /// What DLSS actually loaded the last time this game ran - the one question the rest of the
    /// card cannot answer, because everything else on it is read off disk before you play.
    ///
    /// <para>Empty until the game has been played with the override on, and empty again once a
    /// game patch changes what it ships, since the observation then describes a setup that no
    /// longer exists. It reports what was seen and stops: a runtime loaded from the game's own
    /// files is a reading, not a verdict that the override failed.</para>
    /// </summary>
    public string LastRunLine
    {
        get
        {
            var seen = (_game?.DlssObservations ?? new List<DlssObservation>())
                .Where(o => o.State == DlssObservationState.RuntimeObserved && o.Version != null)
                .Where(o => !DlssVerificationService.IsInvalidated(
                    o, _content.ShippedByFeature.TryGetValue(o.Feature, out string? v) ? v : null))
                .ToList();

            if (seen.Count == 0) return string.Empty;

            string Versions(bool fromNvidia) => string.Join(" and ", seen
                .Where(o => o.FromDriverStore == fromNvidia)
                .Select(o => o.Version!)
                .Distinct(StringComparer.Ordinal));

            string nvidia = Versions(true);
            string game = Versions(false);

            return (nvidia.Length > 0, game.Length > 0) switch
            {
                (true, false) => $"Last run: loaded {nvidia} from NVIDIA.",
                (false, true) => $"Last run: loaded {game} from the game's own files.",
                _ => $"Last run: loaded {nvidia} from NVIDIA and {game} from the game's own files."
            };
        }
    }

    public bool HasLastRun => LastRunLine.Length > 0;

    // ---- Work --------------------------------------------------------------------------------

    /// <summary>
    /// Runs the probe off the UI thread. Called from the dialog's Loaded handler, not the
    /// constructor, so building a view model - as the tests do - never touches the driver.
    /// Idempotent, and returns the same task each time so a caller can wait for work in flight.
    /// </summary>
    public Task LoadAsync() => _load ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_executablePath)) { _hasLoaded = true; return; }
        try
        {
            // The renderer, not the launcher: that is the profile an override is written to.
            string path = await Task.Run(() => DlssProbeService.ResolveRenderingExecutable(_executablePath)).ConfigureAwait(true);
            var result = await Task.Run(() => _probe(path)).ConfigureAwait(true);
            _content = Project(result, _records);
        }
        catch (Exception ex)
        {
            // A card that breaks the dialog would be worse than no card.
            LoggingService.Warn("Dlss", $"DLSS card probe failed: {ex.Message}");
            _content = Empty;
        }
        finally
        {
            _hasLoaded = true;
            RaiseAll();
        }
    }

    private async Task ApplyAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(_executablePath)) return;
        IsBusy = true;
        try
        {
            string path = _executablePath;
            string name = _gameName;
            var result = await Task.Run(() => _overrides.Apply(path, name)).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                Status = result.Error ?? "NVIDIA would not accept the change.";
                return;
            }

            // Replace rather than merge: the capture describes the state immediately before this
            // write, and stale records would restore the wrong values.
            _records.Clear();
            _records.AddRange(result.Records);
            ClearDerivedState();
            _persist?.Invoke();

            Status = result.HadWriteBackFailures
                // The save reported success but the values are not there. Saying it worked would
                // be the most misleading thing the card could do.
                ? "NVIDIA accepted the change but did not report it back, so it may not have taken effect."
                : "On. It takes effect next time you play.";

            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Dlss", $"Applying the DLSS override failed: {ex.Message}", ex);
            Status = $"Something went wrong: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseAll();
        }
    }

    private async Task RestoreAsync()
    {
        if (IsBusy || _records.Count == 0) return;
        IsBusy = true;
        try
        {
            var toUndo = _records.ToList();
            var result = await Task.Run(() => _overrides.Undo(toUndo)).ConfigureAwait(true);

            // The records handed back are the ones still owned: anything that could not be undone
            // keeps its record, so a later attempt can still try.
            _records.Clear();
            _records.AddRange(result.Records);
            ClearDerivedState();
            _persist?.Invoke();

            Status = !result.Succeeded
                ? result.Error ?? "NVIDIA would not undo the change."
                : result.HadForeignChanges
                    ? "Put back what TrayTrigger changed. Some were left alone because something else has changed them since."
                    : "Put back the way it was.";

            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Dlss", $"Restoring the DLSS settings failed: {ex.Message}", ex);
            Status = $"Something went wrong: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseAll();
        }
    }

    /// <summary>
    /// The override just changed, so a conflict and anything observed under the old one describe a
    /// setup that no longer exists.
    /// </summary>
    private void ClearDerivedState()
    {
        if (_game == null) return;
        _game.DlssConflicted = false;
        _game.DlssObservations.Clear();
    }

    private async Task ReloadAsync()
    {
        _load = null;
        _hasLoaded = false;
        await LoadAsync().ConfigureAwait(true);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(VersionLine));
        OnPropertyChanged(nameof(OverrideLabel));
        OnPropertyChanged(nameof(OverrideEnabled));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
        OnPropertyChanged(nameof(LastRunLine));
        OnPropertyChanged(nameof(HasLastRun));
    }

    // ---- Projection --------------------------------------------------------------------------

    /// <summary>Turns a probe result into what the card says. Pure; exercised directly by tests.</summary>
    public static Projection Project(
        DlssProbeService.ProbeResult result,
        IReadOnlyCollection<DlssSettingRecord>? owned = null)
    {
        if (result.ShippedRuntimes.Count == 0) return Empty;

        // One version for the card. Where a game ships its three features at different versions,
        // this shows the oldest: it is the one with the most to gain, and the override lifts all
        // three to the same place regardless.
        var shipped = result.ShippedRuntimes
            .GroupBy(s => s.Feature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().FileVersion, StringComparer.Ordinal);

        string? gameVersion = shipped.Values.Where(v => v != null).OrderBy(Order).FirstOrDefault();
        string? driverVersion = result.DriverRuntimes
            .OrderByDescending(d => d.EncodedVersion)
            .Select(d => d.Version)
            .FirstOrDefault();

        bool newer = gameVersion != null && driverVersion != null && Compare(driverVersion, gameVersion) > 0;

        return new Projection(
            HasDlss: true,
            GameVersion: gameVersion,
            DriverVersion: driverVersion,
            DriverIsNewer: newer,
            // Keyed by feature code, because that is how an observation names itself. Not shown:
            // it exists so a stored observation can be thrown away once the game ships a different
            // version for that feature.
            ShippedByFeature: NgxModelStore.Features.ToDictionary(
                f => f.Code,
                f => shipped.TryGetValue(f.Name, out string? v) ? v : null,
                StringComparer.Ordinal),
            ExternalOverrideNotice: HasForeignOverride(result, owned)
                ? "Something else already overrides DLSS for this game - NVIDIA App, Profile Inspector or similar. Turning this on replaces it; Restore puts it back."
                : null);
    }

    /// <summary>
    /// Whether an override is present that TrayTrigger did not write. Without the records the card
    /// cannot tell its own work from a stranger's, and would accuse itself the moment it applied.
    /// </summary>
    private static bool HasForeignOverride(DlssProbeService.ProbeResult result, IReadOnlyCollection<DlssSettingRecord>? owned)
    {
        foreach (var s in result.SettingStates)
        {
            if (s.Value == null || s.Value.CurrentValue == 0) continue;
            if (!s.Definition.Name.Contains("Enable DLL Override", StringComparison.Ordinal)) continue;

            bool ours = owned != null && owned.Any(r =>
                r.SettingId == s.Value.SettingId &&
                r.WrittenValue == s.Value.CurrentValue &&
                s.Value.Origin == NvApi.SettingOrigin.ApplicationProfile);

            if (!ours) return true;
        }
        return false;
    }

    /// <summary>
    /// Version strings compared numerically. "310.9.0" beats "310.7.128", which a string compare
    /// gets backwards. Unparseable parts sort lowest rather than throwing.
    /// </summary>
    internal static (int Major, int Minor, int Patch) Order(string? version)
    {
        var parts = (version ?? string.Empty).Split('.');
        int At(int i) => parts.Length > i && int.TryParse(parts[i], out int n) ? n : 0;
        return (At(0), At(1), At(2));
    }

    /// <summary>Negative, zero or positive, as string comparison would give the wrong answer.</summary>
    internal static int Compare(string? left, string? right)
    {
        var a = Order(left);
        var b = Order(right);
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        return a.Patch.CompareTo(b.Patch);
    }
}
