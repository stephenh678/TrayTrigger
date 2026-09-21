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
/// the settings ("Enable DLSS-SR override") and what NVIDIA App calls the feature, so a user has
/// an exact term to search for.</para>
///
/// <para>The card says two versions and offers one switch. The override covers all three features
/// in one write, so there is no per-feature breakdown; the switch's sub-line names the three
/// features instead.</para>
///
/// <para>Turning the switch on writes to the driver and saves <i>immediately</i>, not on Save
/// Changes: the driver change has already happened, so deferring the record would let Cancel
/// strand an override TrayTrigger could no longer undo.</para>
/// </summary>
public sealed class DlssCardViewModel : ViewModelBase
{
    /// <summary>What the card knows after probing. Pure, so the wording is testable without a driver.</summary>
    public sealed record Projection(
        bool HasDriver,
        bool HasDlss,
        string? GameVersion,
        string? DriverVersion,
        bool DriverIsNewer);

    private readonly string? _executablePath;
    private readonly string? _installDirectory;
    private string? _rendererPath;
    private readonly string _gameName;
    private readonly DlssOverrideService _overrides;
    private readonly List<DlssSettingRecord> _records;
    private readonly Action? _persist;
    private readonly GameEntry? _game;
    private readonly Func<string, string?, DlssProbeService.ProbeResult> _probe;

    private bool _isBusy;
    private bool _hasLoaded;
    private bool _isOnTab = true;
    private string? _status;
    private Projection _content = Empty;
    private Task? _load;

    private static readonly Projection Empty =
        new(false, false, null, null, false);

    private static readonly Projection NoDlss =
        new(true, false, null, null, false);

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
        Func<string, string?, DlssProbeService.ProbeResult>? probe = null,
        GameEntry? game = null)
    {
        _game = game;
        _executablePath = executablePath;
        // A platform game launched by link has no executable path; its install folder is what
        // there is to look in.
        _installDirectory = game?.WorkingDirectory;
        _gameName = gameName;
        _records = records ?? new List<DlssSettingRecord>();
        _persist = persist;
        _overrides = overrides ?? new DlssOverrideService();
        _probe = probe ?? ((path, root) => DlssProbeService.Probe(path, root));

        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanRestore);
    }

    /// <summary>Whether the card's tab is selected. Owned here so the XAML needs one binding.</summary>
    public bool IsOnTab
    {
        get => _isOnTab;
        set { if (SetProperty(ref _isOnTab, value)) OnPropertyChanged(nameof(IsVisible)); }
    }

    /// <summary>
    /// Shown on any machine with an NVIDIA driver. A game that ships no DLSS keeps the card, as one
    /// line saying so, so its absence is never mistaken for a fault.
    /// </summary>
    public bool IsVisible => _hasLoaded && _content.HasDriver && IsOnTab;

    /// <summary>
    /// The card has two states and no more. The whole card, for a game that ships DLSS - and for
    /// one TrayTrigger still holds an override for, whatever the game ships now, so Restore can
    /// always be reached. Otherwise <see cref="NotAvailableLine"/> and nothing else: a greyed-out
    /// switch, a description and a disabled Restore for a feature that cannot apply read as
    /// something broken, and most games ship no DLSS.
    /// </summary>
    public bool ShowFullCard => _content.HasDlss || _records.Count > 0;

    /// <summary>The one-line state. See <see cref="ShowFullCard"/>.</summary>
    public bool ShowNotAvailable => !ShowFullCard;

    /// <summary>
    /// Not "No DLSS files found": that describes a search that failed, and invites the reader to
    /// go and fix it. "Include", not "support": what is known is what is in the game's folder.
    /// </summary>
    public const string NotAvailableLine = "Not available. This game doesn't include DLSS.";

    /// <summary><see cref="NotAvailableLine"/>, where the XAML can bind to it.</summary>
    public string NotAvailableText => NotAvailableLine;

    /// <summary>False when the game ships no DLSS: there is nothing for an override to replace.</summary>
    public bool CanEnable => _content.HasDlss;

    /// <summary>True while a driver write is in flight.</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    // ---- The one thing the card says --------------------------------------------------------

    /// <summary>
    /// What the game has now and what it would get, as a pair: <c>310.1.0 -> 310.9.0</c>. It sits
    /// on the switch's own row, so the before and after are read together and no sentence is needed
    /// to join them.
    /// </summary>
    public string VersionLine =>
        _content.GameVersion == null ? string.Empty
        : _content.DriverVersion == null ? _content.GameVersion
        : _content.DriverIsNewer
            ? $"{_content.GameVersion} → {_content.DriverVersion}"
            : $"{_content.GameVersion} (already current)";

    /// <summary>
    /// The switch. On means TrayTrigger holds an override for this game - it was ticked here and
    /// has not been unticked or restored. It does not re-read the driver: another tool changing a
    /// value (NVIDIA App turns Frame Generation off for a game that has none) is not the user
    /// switching this off. Last run says what actually loaded. Setting it does the work - there
    /// is no separate apply.
    /// </summary>
    public bool OverrideEnabled
    {
        get => _records.Count > 0;
        set => _ = SetOverrideAsync(value);
    }

    /// <summary>
    /// What the switch does, awaitable. A property setter cannot hand its task back, and the game's
    /// right-click menu has to wait for the driver write before it can say in the status bar what
    /// happened. Same guards as the switch: a no-op returns a completed task.
    /// </summary>
    public Task SetOverrideAsync(bool on)
    {
        if (on == OverrideEnabled || IsBusy || (on && !CanEnable)) return Task.CompletedTask;
        return on ? ApplyAsync() : RestoreAsync();
    }

    /// <summary>Puts back everything TrayTrigger changed, whatever the switch currently says.</summary>
    public ICommand RestoreCommand { get; }

    public bool CanRestore => !IsBusy && _records.Count > 0;

    /// <summary>Why the last change did not work. Null otherwise: when it works, the switch says so.</summary>
    public string? Status
    {
        get => _status;
        private set { if (SetProperty(ref _status, value)) LoggingService.Shown("DLSS card status", value); }
    }
    public bool HasStatus => !string.IsNullOrEmpty(_status);

    /// <summary>
    /// What DLSS actually loaded the last time this game ran - the one question the rest of the
    /// card cannot answer, because everything else on it is read off disk before you play.
    ///
    /// <para>Nothing recorded until the game has been played with the override on, and again once a game
    /// patch changes what it ships, since the observation then describes a setup that no longer
    /// exists. The line is always there, so its absence is never mistaken for a fault. It reports
    /// what was seen and stops: a runtime loaded from the game's own files is a reading, not a
    /// verdict that the override failed.</para>
    /// </summary>
    public string LastRunLine => LastRunReading is { Length: > 0 } reading ? reading : NoLastRun;

    public const string NoLastRun = "Last run: nothing recorded yet. Play the game with the override on.";

    private string LastRunReading
    {
        get
        {
            var last = _game?.DlssLastRun;
            if (last == null) return string.Empty;

            // The game has since shipped a different DLSS version - a patch - so the reading
            // describes a setup that no longer exists. Unknown-versus-known is not a change.
            if (!string.IsNullOrEmpty(last.GameVersion) && !string.IsNullOrEmpty(_content.GameVersion) &&
                !string.Equals(last.GameVersion, _content.GameVersion, StringComparison.Ordinal))
                return string.Empty;

            string nvidia = string.Join(" and ", last.FromNvidia);
            string game = string.Join(" and ", last.FromGame);

            return (nvidia.Length > 0, game.Length > 0) switch
            {
                (false, false) => string.Empty,
                (true, false) => $"Last run: loaded {nvidia} from NVIDIA.",
                (false, true) => $"Last run: loaded {game} from the game's own files.",
                _ => $"Last run: loaded {nvidia} from NVIDIA and {game} from the game's own files."
            };
        }
    }

    // ---- Work --------------------------------------------------------------------------------

    /// <summary>
    /// Runs the probe off the UI thread. Called from the dialog's Loaded handler, not the
    /// constructor, so building a view model - as the tests do - never touches the driver.
    /// Idempotent, and returns the same task each time so a caller can wait for work in flight.
    /// </summary>
    public Task LoadAsync() => _load ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_executablePath) && string.IsNullOrWhiteSpace(_installDirectory))
        {
            LoggingService.Verbose("Dlss", $"DLSS card hidden for '{_gameName}': no executable and no install folder to look in.");
            _hasLoaded = true;
            return;
        }
        try
        {
            // The renderer, not the launcher: that is the profile an override is written to.
            var where = await Task.Run(() => DlssProbeService.Locate(_executablePath ?? string.Empty, _installDirectory)).ConfigureAwait(true);
            _rendererPath = where.Renderer;
            // The folder the renderer was found in, not one worked out again from the renderer.
            var result = await Task.Run(() => _probe(where.Renderer, where.Root)).ConfigureAwait(true);
            _content = Project(result);

            // Verbose only: most games ship no DLSS, and this runs every time Edit Game opens.
            LoggingService.Verbose("Dlss",
                !_content.HasDriver ? $"DLSS card hidden for '{_gameName}': no NVIDIA driver."
                : !_content.HasDlss ? $"No DLSS files found for '{_gameName}': searched '{where.Root ?? "(no folder)"}' (launched '{_executablePath}', install folder '{_installDirectory}')."
                : $"DLSS card for '{_gameName}': game {_content.GameVersion ?? "?"}, driver {_content.DriverVersion ?? "?"}, renderer '{where.Renderer}', searched '{where.Root}'.");
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
        // The renderer the probe found, not the launch path: for a Steam game that is a link.
        if (IsBusy || string.IsNullOrWhiteSpace(_rendererPath)) return;
        IsBusy = true;
        try
        {
            string path = _rendererPath;
            string name = _gameName;
            var before = _records.ToList();
            var result = await Task.Run(() => _overrides.Apply(path, name, before)).ConfigureAwait(true);

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

            // Success says nothing: the switch is the status. Only a problem gets a line, because
            // then the switch drops back to off and something has to say why.
            Status = result.HadWriteBackFailures
                ? "NVIDIA accepted the change but did not report it back, so it may not have taken effect."
                : null;

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

            // The records handed back are the ones still owned: anything that failed keeps its
            // record, so a later attempt can still try. A setting something else has changed is
            // different - Restore is the user handing those back, as the notice says, and a record
            // kept for a value that is no longer TrayTrigger's could never be cleared.
            var handedBack = result.Details
                .Where(d => d.Outcome == DlssSettingOutcome.SkippedForeignChange)
                .Select(d => d.SettingId)
                .ToHashSet();
            _records.Clear();
            if (result.Succeeded)
                _records.AddRange(result.Records.Where(r => !handedBack.Contains(r.SettingId)));
            else
                _records.AddRange(result.Records);
            ClearDerivedState();
            _persist?.Invoke();

            Status = result.Succeeded ? null : result.Error ?? "NVIDIA would not undo the change.";

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

    /// <summary>The override just changed, so what loaded under the old one no longer describes this setup.</summary>
    private void ClearDerivedState()
    {
        if (_game != null) _game.DlssLastRun = null;
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
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(ShowFullCard));
        OnPropertyChanged(nameof(ShowNotAvailable));
        OnPropertyChanged(nameof(VersionLine));
        OnPropertyChanged(nameof(OverrideEnabled));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(LastRunLine));
    }

    // ---- Projection --------------------------------------------------------------------------

    /// <summary>Turns a probe result into what the card says. Pure; exercised directly by tests.</summary>
    public static Projection Project(DlssProbeService.ProbeResult result)
    {
        // No NVIDIA driver means nothing here can work, whatever the game ships: the card stays
        // hidden rather than offering a switch that can only fail.
        if (!result.DriverAvailable) return Empty;
        if (result.ShippedRuntimes.Count == 0) return NoDlss;

        // One version for the card. Where a game ships its three features at different versions,
        // this shows the oldest: it is the one with the most to gain, and the override lifts all
        // three to the same place regardless.
        var shipped = result.ShippedRuntimes
            .GroupBy(s => s.Feature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().FileVersion, StringComparer.Ordinal);

        // The driver's version is taken for that same feature. The newest across all three would
        // pair one feature's "before" with another's "after".
        var oldest = shipped.Where(kv => kv.Value != null).OrderBy(kv => DlssProbeService.VersionKey(kv.Value)).FirstOrDefault();
        string? gameVersion = oldest.Value;
        string? driverVersion = result.DriverRuntimes
            .Where(d => string.Equals(d.Feature, oldest.Key, StringComparison.Ordinal))
            .OrderByDescending(d => d.EncodedVersion)
            .Select(d => d.Version)
            .FirstOrDefault();

        bool newer = gameVersion != null && driverVersion != null && Compare(driverVersion, gameVersion) > 0;

        return new Projection(
            HasDriver: true,
            HasDlss: true,
            GameVersion: gameVersion,
            DriverVersion: driverVersion,
            DriverIsNewer: newer);
    }

    /// <summary>Negative, zero or positive, as string comparison would give the wrong answer.</summary>
    internal static int Compare(string? left, string? right)
    {
        var a = DlssProbeService.VersionKey(left);
        var b = DlssProbeService.VersionKey(right);
        if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
        if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
        return a.Patch.CompareTo(b.Patch);
    }
}
