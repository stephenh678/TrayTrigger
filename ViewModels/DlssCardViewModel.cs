using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The DLSS card in Edit Game. See docs/dlss-plan.md.
///
/// <para>One action plus undo, as the plan specifies: no version dropdown, no per-feature toggles,
/// no preset letters. Everything shown is something that was actually read; where the probe could
/// not read, the card says so rather than guessing.</para>
///
/// <para><b>Applying writes to the driver and persists immediately</b>, without waiting for Save
/// Changes. The driver change is not part of the edit - it has already happened - so deferring the
/// record until Save would let Cancel strand an override TrayTrigger could no longer undo.</para>
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
    private readonly string _gameName;
    private readonly DlssOverrideService _overrides;
    private readonly List<DlssSettingRecord> _records;
    private readonly Action? _persist;
    private readonly GameEntry? _game;
    private readonly Func<string, DlssProbeService.ProbeResult> _probe;

    private bool _isLoading;
    private bool _isBusy;
    private bool _hasLoaded;
    private bool _isOnTab = true;
    private string? _status;
    private Projection _content = Empty;
    private Task? _load;

    private static readonly Projection Empty = new(false, Array.Empty<FeatureRow>(), string.Empty, null);

    /// <param name="records">
    /// The game's live ownership records. Mutated in place and handed to <paramref name="persist"/>,
    /// so the record and the driver never disagree about what TrayTrigger owns.
    /// </param>
    /// <param name="persist">Saves the library. Called immediately after any driver change.</param>
    /// <param name="probe">
    /// Substituted by tests so the card's behaviour - including apply and undo - can be exercised
    /// without an NVIDIA machine. Null uses the real probe.
    /// </param>
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

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => CanUndo);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => CanVerify);
    }

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

    /// <summary>True while a driver write is in flight. Both buttons are disabled meanwhile.</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public IReadOnlyList<FeatureRow> Rows => _content.Rows;
    public string DriverLine => _content.DriverLine;
    public string? ExternalOverrideNotice => _content.ExternalOverrideNotice;
    public bool HasExternalOverrideNotice => !string.IsNullOrEmpty(_content.ExternalOverrideNotice);

    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand VerifyCommand { get; }

    /// <summary>
    /// Offered once TrayTrigger has settings of its own to check. It reads the running game, so it
    /// says so plainly when the game is not running rather than being greyed out with no reason.
    /// </summary>
    public bool CanVerify => !IsBusy && _records.Count > 0;

    public bool CanApply => !IsBusy && _hasLoaded && _content.HasDlss && !string.IsNullOrWhiteSpace(_executablePath);

    /// <summary>Undo is offered only for settings TrayTrigger actually recorded writing.</summary>
    public bool CanUndo => !IsBusy && _records.Count > 0;

    /// <summary>
    /// Set when a pre-launch reapply found this game's settings changed by something else, so
    /// TrayTrigger stopped managing them. Applying or undoing clears it - both are the user
    /// saying what they want.
    /// </summary>
    public bool IsConflicted => _game?.DlssConflicted == true;

    public string ConflictNotice =>
        "Something else changed this game's DLSS settings, so TrayTrigger stopped re-applying them before launch. Use recommended to take them over again, or undo to hand them back.";

    /// <summary>
    /// Draw NVIDIA's DLSS indicator while this game runs. Off by default. Saved immediately rather
    /// than on Save Changes, so it matches the rest of this card - everything here takes effect
    /// when it is set, not when the dialog is closed.
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

    /// <summary>The overlay needs a machine-wide registry value, which is an HKLM write.</summary>
    public string OverlayHint =>
        "Shows the DLSS version and preset in a corner of the game. Only while this game runs, and "
        + "it is the only way to see which preset is active. Turning it on asks for administrator permission.";

    /// <summary>The result of the last apply or undo, in the user's words. Null before either.</summary>
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

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

    private async Task LoadCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_executablePath)) { _hasLoaded = true; return; }
        IsLoading = true;
        try
        {
            // Probe the renderer, not the launcher, so the card reports the profile that Apply
            // will actually write to.
            string path = await Task.Run(() => DlssProbeService.ResolveRenderingExecutable(_executablePath)).ConfigureAwait(true);
            var result = await Task.Run(() => _probe(path)).ConfigureAwait(true);
            _content = Project(result, _records, _game?.DlssObservations, result.DriverVersion);
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
            RaiseContentChanged();
        }
    }

    private async Task ApplyAsync()
    {
        if (!CanApply) return;
        IsBusy = true;
        try
        {
            string path = _executablePath!;
            string name = _gameName;
            var result = await Task.Run(() => _overrides.Apply(path, name)).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                Status = result.Error ?? "The driver would not apply the change.";
                return;
            }

            // Replace rather than merge: the new capture describes the state immediately before
            // this write, and keeping stale records would undo to the wrong values.
            _records.Clear();
            _records.AddRange(result.Records);
            if (_game != null)
            {
                _game.DlssConflicted = false;
                // The override just changed, so anything observed under the old one describes a
                // setup that no longer exists. Keeping it would be the card's oldest failure mode:
                // saying something true about the wrong thing.
                _game.DlssObservations.Clear();
            }
            _persist?.Invoke();

            Status = DescribeApply(result);
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
            RaiseActionState();
        }
    }

    private async Task UndoAsync()
    {
        if (!CanUndo) return;
        IsBusy = true;
        try
        {
            var toUndo = _records.ToList();
            var result = await Task.Run(() => _overrides.Undo(toUndo)).ConfigureAwait(true);

            // Whatever the outcome, the records the service hands back are the ones still owned -
            // settings it could not undo keep theirs, so a later attempt can still try.
            _records.Clear();
            _records.AddRange(result.Records);
            if (_game != null)
            {
                _game.DlssConflicted = false;
                _game.DlssObservations.Clear();
            }
            _persist?.Invoke();

            Status = result.Succeeded
                ? (result.HadForeignChanges
                    ? "Put back what TrayTrigger changed. Some settings were left alone because something else has changed them since."
                    : "Put back what TrayTrigger changed.")
                : result.Error ?? "The driver would not undo the change.";

            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Dlss", $"Undoing the DLSS override failed: {ex.Message}", ex);
            Status = $"Something went wrong: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseActionState();
        }
    }

    /// <summary>
    /// Reads the running game now. The session observer already does this automatically while a
    /// game runs, so this is for checking on demand - and for saying "play it once" when there is
    /// nothing to read yet, which is the honest answer rather than an empty result.
    /// </summary>
    private async Task VerifyAsync()
    {
        if (!CanVerify) return;
        IsBusy = true;
        try
        {
            string path = _executablePath ?? string.Empty;
            var observations = await Task.Run(() =>
            {
                string renderer = DlssProbeService.ResolveRenderingExecutable(path);
                using var process = FindRunningProcess(renderer);
                if (process == null) return null;
                var probe = DlssProbeService.Probe(renderer, process);
                return DlssVerificationService.Interpret(probe.LoadedRuntimes, probe.ModuleScanNote, probe.DriverVersion, DateTime.UtcNow);
            }).ConfigureAwait(true);

            if (observations == null)
            {
                Status = "This game is not running. Start it, play for a moment, then check back - "
                       + "TrayTrigger records what loaded while a game runs.";
                return;
            }

            if (_game != null) _game.DlssObservations = observations;
            _persist?.Invoke();
            Status = string.Join("  ", observations.Select(o => $"{o.Feature}: {DlssVerificationService.Describe(o)}"));
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoggingService.Error("Dlss", $"Verifying DLSS failed: {ex.Message}", ex);
            Status = $"Something went wrong: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseActionState();
        }
    }

    /// <summary>The running renderer, or null. Matched on file name, as the driver does.</summary>
    private static System.Diagnostics.Process? FindRunningProcess(string exePath)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(exePath);
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var all = System.Diagnostics.Process.GetProcessesByName(name);
            var best = all.FirstOrDefault();
            foreach (var p in all.Skip(1)) p.Dispose();
            return best;
        }
        catch { return null; }
    }

    /// <summary>Re-probes so the rows show the driver's new answer rather than the one before the write.</summary>
    private async Task ReloadAsync()
    {
        _load = null;
        _hasLoaded = false;
        await LoadAsync().ConfigureAwait(true);
    }

    private static string DescribeApply(DlssOperationResult result)
    {
        if (result.HadWriteBackFailures)
        {
            // The save reported success but the values are not there. Saying "applied" would be
            // the most misleading thing the card could do.
            return "The driver accepted the change but did not report it back. It may not have taken effect.";
        }
        return "Set to NVIDIA's recommended model. Play the game once to confirm which runtime loads.";
    }

    private void RaiseContentChanged()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(DriverLine));
        OnPropertyChanged(nameof(ExternalOverrideNotice));
        OnPropertyChanged(nameof(HasExternalOverrideNotice));
        OnPropertyChanged(nameof(IsVisible));
        RaiseActionState();
    }

    private void RaiseActionState()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(IsConflicted));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
    }

    /// <summary>
    /// Turns a probe result into the card's content. Pure; exercised directly by tests.
    /// </summary>
    /// <param name="owned">
    /// The game's ownership records. Without them the card cannot tell TrayTrigger's own override
    /// from a stranger's, and would accuse itself the moment it applied one.
    /// </param>
    public static Projection Project(
        DlssProbeService.ProbeResult result,
        IReadOnlyCollection<DlssSettingRecord>? owned = null,
        IReadOnlyCollection<DlssObservation>? observations = null,
        string? currentDriverVersion = null)
    {
        if (result.ShippedRuntimes.Count == 0) return Empty;

        // Newest shipped copy per feature: a game can carry the same runtime in several folders,
        // and showing each one would be noise.
        var shipped = result.ShippedRuntimes
            .GroupBy(s => s.Feature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rows = new List<FeatureRow>();
        bool anyExternalOverride = false;

        foreach (var feature in NgxModelStore.Features)
        {
            if (!shipped.TryGetValue(feature.Name, out var ship)) continue;

            var state = DescribeState(result, feature, owned, observations, currentDriverVersion, out bool isExternalOverride);
            anyExternalOverride |= isExternalOverride;
            rows.Add(new FeatureRow(feature.Name, ship.FileVersion ?? "unknown", state));
        }

        if (rows.Count == 0) return Empty;

        return new Projection(true, rows, DescribeDriver(result), anyExternalOverride
            // The plan forbids silently overwriting an override TrayTrigger did not set, and a
            // user who does not know it is there cannot make sense of what the game reports.
            ? "Something else has already set a DLSS override for this game - NVIDIA App, Profile Inspector or a similar tool. Using recommended will replace it, and undo will put it back."
            : null);
    }

    private static string DescribeState(
        DlssProbeService.ProbeResult result, NgxModelStore.DlssFeature feature,
        IReadOnlyCollection<DlssSettingRecord>? owned,
        IReadOnlyCollection<DlssObservation>? observations,
        string? currentDriverVersion,
        out bool isExternalOverride)
    {
        isExternalOverride = false;

        if (result.Profile == null)
        {
            // Not a failure: NVIDIA simply has no entry for this executable. Saying "Game default"
            // here would assert something the probe did not read.
            return "No driver profile";
        }

        var toggle = result.SettingStates.FirstOrDefault(s =>
            string.Equals(s.Definition.FeatureCode, feature.Code, StringComparison.Ordinal) &&
            s.Definition.Name.Contains("Enable DLL Override", StringComparison.Ordinal));

        // What was actually seen outranks what is configured - the whole point of verifying.
        // Only for a feature TrayTrigger is managing: an observation against somebody else's
        // override would read as though TrayTrigger had produced it.
        bool managed = owned != null && owned.Count > 0;
        if (managed)
        {
            var seen = observations?.FirstOrDefault(o => o.Feature == feature.Code);
            string? shippedNow = result.ShippedRuntimes.FirstOrDefault(r => r.Feature == feature.Name)?.FileVersion;
            if (seen != null && DlssVerificationService.IsInvalidated(seen, shippedNow)) seen = null;

            if (seen != null)
            {
                string text = DlssVerificationService.Describe(seen);
                return DlssVerificationService.IsStale(seen, currentDriverVersion)
                    ? text + " (before the driver changed)"
                    : text;
            }
        }

        if (toggle?.Value == null || toggle.Value.CurrentValue == 0) return "Game default";

        // Ours, if a record says we wrote this exact value and the driver still reports it on the
        // game's own profile. Without this the card would accuse itself the moment it applied.
        bool isOurs = owned != null && owned.Any(r =>
            r.SettingId == toggle.Value.SettingId &&
            r.WrittenValue == toggle.Value.CurrentValue &&
            toggle.Value.Origin == NvApi.SettingOrigin.ApplicationProfile);

        if (isOurs) return "Settings saved";

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
