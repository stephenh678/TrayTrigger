using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>One tick box in the library filter flyout.</summary>
public sealed class LibraryFilterOption : ViewModelBase
{
    private readonly Action _onChanged;
    private bool _isChecked;

    /// <summary>Stable identifier used to persist the filter across restarts ("launcher:steam").</summary>
    public string Key { get; }
    public string Label { get; }
    /// <summary>Does this one card satisfy this option on its own.</summary>
    public Func<GameCardViewModel, bool> Match { get; }

    public LibraryFilterOption(string key, string label, Func<GameCardViewModel, bool> match, Action onChanged)
    {
        Key = key;
        Label = label;
        Match = match;
        _onChanged = onChanged;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    /// <summary>Sets the box without running the filter - for restoring saved state during construction.</summary>
    internal void SetSilently(bool value)
    {
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }
}

/// <summary>
/// A headed block of options in the flyout. Options within a group are OR'd (Steam or Epic);
/// groups are AND'd (Steam AND never played). A group with nothing ticked places no constraint,
/// which is why "no filters" and "every box ticked" mean the same thing and neither hides anything.
/// </summary>
public sealed class LibraryFilterGroup : ViewModelBase
{
    public string Title { get; }
    public ObservableCollection<LibraryFilterOption> Options { get; } = new();

    public LibraryFilterGroup(string title) => Title = title;

    /// <summary>False when no option in this group applies to anything in the library - the
    /// Launcher group on a library imported entirely by hand, for instance.</summary>
    public bool IsVisible => Options.Count > 0;

    internal bool Matches(GameCardViewModel card)
    {
        bool anyChecked = false;
        foreach (var option in Options)
        {
            if (!option.IsChecked) continue;
            anyChecked = true;
            if (option.Match(card)) return true;
        }
        return !anyChecked;
    }

    internal void NotifyVisibilityChanged() => OnPropertyChanged(nameof(IsVisible));
}

/// <summary>
/// The library's secondary filter - everything the category tabs do not already cover.
///
/// Categories are the user's own taxonomy and get the tab strip. This covers the facts TrayTrigger
/// already knows about an entry and the user never has to type: which launcher it came from, which
/// performance profile it is on, and a handful of states worth finding a whole library's worth of
/// at once ("what have I never played", "what is broken", "what runs elevated").
/// </summary>
public sealed class LibraryFilterViewModel : ViewModelBase
{
    private readonly Action _onChanged;
    private readonly LibraryFilterGroup _launchers = new("Launcher");
    private readonly LibraryFilterGroup _profiles = new("Performance Profile");
    private readonly LibraryFilterGroup _status = new("Status");

    public ObservableCollection<LibraryFilterGroup> Groups { get; } = new();

    private bool _isOpen;
    /// <summary>Whether the flyout is showing - bound two-way to the popup.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set { if (_isOpen != value) { _isOpen = value; OnPropertyChanged(); } }
    }

    public ICommand ClearCommand { get; }
    public ICommand ToggleOpenCommand { get; }

    public LibraryFilterViewModel(Action onChanged)
    {
        _onChanged = onChanged;

        AddProfile(PerformanceProfileMode.Off, "Off");
        AddProfile(PerformanceProfileMode.Optimized, "Optimized");
        AddProfile(PerformanceProfileMode.Aggressive, "Aggressive");

        AddStatus("neverplayed", "Never played", c => c.Game.LastPlayed == null);
        AddStatus("played", "Played at least once", c => c.Game.LastPlayed != null);
        AddStatus("favorite", "Favorite", c => c.Game.IsFavorite);
        AddStatus("missing", "Executable missing", c => c.IsMissing);
        AddStatus("hotkey", "Has a hotkey", c => !string.IsNullOrWhiteSpace(c.Game.Hotkey));
        AddStatus("scripts", "Has launch scripts", c => c.Game.HasScripts);
        AddStatus("admin", "Runs as administrator", c => c.Game.RunAsAdmin);

        Groups.Add(_launchers);
        Groups.Add(_profiles);
        Groups.Add(_status);

        ClearCommand = new RelayCommand(ClearAll);
        ToggleOpenCommand = new RelayCommand(() => IsOpen = !IsOpen);
    }

    private void AddProfile(PerformanceProfileMode mode, string label) =>
        _profiles.Options.Add(new LibraryFilterOption(
            "profile:" + mode.ToString().ToLowerInvariant(), label,
            c => c.Game.PerformanceProfile == mode, OnOptionChanged));

    private void AddStatus(string key, string label, Func<GameCardViewModel, bool> match) =>
        _status.Options.Add(new LibraryFilterOption("status:" + key, label, match, OnOptionChanged));

    private void OnOptionChanged()
    {
        NotifyActiveChanged();
        _onChanged();
    }

    private void NotifyActiveChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(ActiveFilterSummary));
    }

    private IEnumerable<LibraryFilterOption> AllOptions => Groups.SelectMany(g => g.Options);

    /// <summary>Drives the blue dot on the filter button.</summary>
    public bool HasActiveFilters => AllOptions.Any(o => o.IsChecked);
    public int ActiveFilterCount => AllOptions.Count(o => o.IsChecked);
    public string ActiveFilterSummary => ActiveFilterCount switch
    {
        0 => "No filters",
        1 => "1 filter active",
        _ => $"{ActiveFilterCount} filters active"
    };

    public void ClearAll()
    {
        bool any = false;
        foreach (var option in AllOptions.Where(o => o.IsChecked))
        {
            option.SetSilently(false);
            any = true;
        }
        if (!any) return;
        NotifyActiveChanged();
        _onChanged();
    }

    /// <summary>True when the card survives every group. Groups with nothing ticked pass everything.</summary>
    public bool Matches(GameCardViewModel card) => Groups.All(g => g.Matches(card));

    /// <summary>
    /// Rebuilds the Launcher group from what is actually in the library, so a user who has never
    /// touched Ubisoft Connect is not offered a tick box that can only ever return nothing.
    /// Ticks that no longer have a matching option are dropped - the alternative is an invisible
    /// filter hiding games with no way to reach it.
    /// </summary>
    public void RebuildLauncherOptions(IEnumerable<GameCardViewModel> cards)
    {
        var present = new HashSet<LauncherPlatform>();
        bool anyLocal = false;
        foreach (var card in cards)
        {
            var platform = PlatformOf(card.Game);
            if (platform is LauncherPlatform p) present.Add(p);
            else anyLocal = true;
        }

        // Ticks already on screen, plus any read from settings before this group existed.
        var checkedKeys = _launchers.Options.Where(o => o.IsChecked).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        checkedKeys.UnionWith(_pendingLauncherKeys);
        _pendingLauncherKeys.Clear();
        _launchers.Options.Clear();

        foreach (var (platform, label) in PlatformLabels)
        {
            if (!present.Contains(platform)) continue;
            AddLauncher("launcher:" + platform.ToString().ToLowerInvariant(), label, c => PlatformOf(c.Game) == platform, checkedKeys);
        }
        if (anyLocal)
        {
            AddLauncher("launcher:local", "Not from a launcher", c => PlatformOf(c.Game) == null, checkedKeys);
        }

        _launchers.NotifyVisibilityChanged();
        NotifyActiveChanged();
    }

    private void AddLauncher(string key, string label, Func<GameCardViewModel, bool> match, HashSet<string> restoreChecked)
    {
        var option = new LibraryFilterOption(key, label, match, OnOptionChanged);
        if (restoreChecked.Contains(key)) option.SetSilently(true);
        _launchers.Options.Add(option);
    }

    private static readonly (LauncherPlatform Platform, string Label)[] PlatformLabels =
    [
        (LauncherPlatform.Steam, "Steam"),
        (LauncherPlatform.Epic, "Epic Games"),
        (LauncherPlatform.Gog, "GOG"),
        (LauncherPlatform.Ea, "EA"),
        (LauncherPlatform.Ubisoft, "Ubisoft Connect"),
        (LauncherPlatform.Xbox, "Xbox"),
    ];

    /// <summary>
    /// Which launcher an entry belongs to. ImportedFrom is authoritative when it is set, but
    /// entries added before it existed (and anything added by hand) only carry the per-platform
    /// flags, so those are the fallback.
    /// </summary>
    internal static LauncherPlatform? PlatformOf(GameEntry game)
    {
        if (game.ImportedFrom is LauncherPlatform imported) return imported;
        if (game.IsSteamGame) return LauncherPlatform.Steam;
        if (game.IsEpicGame) return LauncherPlatform.Epic;
        if (game.IsGogGame) return LauncherPlatform.Gog;
        if (game.IsEaGame) return LauncherPlatform.Ea;
        if (game.IsUbisoftGame) return LauncherPlatform.Ubisoft;
        if (game.IsXboxGame) return LauncherPlatform.Xbox;
        return null;
    }

    /// <summary>The ticked keys, for <see cref="AppSettings.LibraryFilterKeys"/>.</summary>
    public List<string> ActiveKeys() => AllOptions.Where(o => o.IsChecked).Select(o => o.Key).ToList();

    /// <summary>
    /// Restores saved ticks without running the filter. Launcher keys are applied too, even though
    /// that group has not been built yet - <see cref="RebuildLauncherOptions"/> carries them over
    /// once the library is loaded.
    /// </summary>
    public void RestoreKeys(IEnumerable<string>? keys)
    {
        if (keys == null) return;
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;

        foreach (var option in AllOptions.Where(o => wanted.Contains(o.Key)))
        {
            option.SetSilently(true);
        }
        foreach (string key in wanted.Where(k => k.StartsWith("launcher:", StringComparison.Ordinal)))
        {
            _pendingLauncherKeys.Add(key);
        }
        NotifyActiveChanged();
    }

    /// <summary>Launcher ticks read from settings before the launcher options existed; consumed by
    /// the first <see cref="RebuildLauncherOptions"/>.</summary>
    private readonly HashSet<string> _pendingLauncherKeys = new(StringComparer.Ordinal);
}
