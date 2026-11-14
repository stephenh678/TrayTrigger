using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>
/// What this PC can do that makes an option worth offering. Read when the library changes, not
/// when the flyout opens, and never allowed to throw: a probe that fails means "no".
/// </summary>
internal sealed record FilterCapabilities(Func<bool> Dlss, Func<bool> CpuCores, Func<bool> Hdr)
{
    public static FilterCapabilities ThisPc { get; } = new(
        () => GameCardViewModel.NvidiaDriverPresent,
        () => CpuCoreMenu.ForThisPc.IsVisible,
        () => GameEditViewModel.HdrDisplayPresentOnThisPc);

    public static bool Probe(Func<bool> capability)
    {
        try { return capability(); }
        catch (Exception ex)
        {
            LoggingService.Verbose("LibraryFilter", $"A capability probe failed, so its filter is left out: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// The library's secondary filter - everything the category tabs do not already cover.
///
/// Categories are the user's own taxonomy and get the tab strip. This covers the facts TrayTrigger
/// already knows about an entry and the user never has to type: which launcher it came from, when
/// it was last played, which performance profile it is on, what TrayTrigger has been told to do
/// for it, and what needs fixing - and lets a combination of them be saved under a name. How the
/// flyout works is <see cref="FilterFlyoutViewModel"/>'s, shared with the Tools page's.
/// </summary>
public sealed class LibraryFilterViewModel : FilterFlyoutViewModel
{
    private readonly Func<DateTime> _utcNow;
    private readonly FilterCapabilities _capabilities;
    private readonly FilterGroup _launchers = new("Launcher");
    private readonly FilterGroup _lastPlayed = new("Last played");
    private readonly FilterGroup _profiles = new("Performance Profile");
    private readonly FilterGroup _settings = new("TrayTrigger settings", matchAll: true);
    private readonly FilterGroup _attention = new("Needs attention");

    public override string NounSingular => "game";
    public override string NounPlural => "games";

    internal LibraryFilterViewModel(Action onChanged, Func<DateTime>? utcNow = null, FilterCapabilities? capabilities = null,
        IEnumerable<SavedFilter>? saved = null, Action<IReadOnlyList<SavedFilter>>? onSavedChanged = null)
        : base(onChanged, saved, onSavedChanged)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _capabilities = capabilities ?? FilterCapabilities.ThisPc;

        AddTo(_lastPlayed, "neverplayed", "Never played", c => c.Game.LastPlayed == null);
        AddTo(_lastPlayed, "played30d", "Played in last 30 days",
            c => c.Game.LastPlayed is DateTime t && t.ToUniversalTime() >= _utcNow().AddDays(-30));
        AddTo(_lastPlayed, "stale6m", "Not played in over 6 months",
            c => c.Game.LastPlayed is DateTime t && t.ToUniversalTime() < _utcNow().AddMonths(-6));

        AddProfile(PerformanceProfileMode.Off, "Off");
        AddProfile(PerformanceProfileMode.Optimized, "Optimized");
        AddProfile(PerformanceProfileMode.Aggressive, "Aggressive");

        // The only group with a third state: each is a yes/no fact, so the opposite is a real question.
        AddTo(_settings, "favorite", "Favorite", c => c.Game.IsFavorite, "Not a favorite");
        AddTo(_settings, "hotkey", "Has a hotkey", c => !string.IsNullOrWhiteSpace(c.Game.Hotkey), "No hotkey");
        AddTo(_settings, "scripts", "Has launch scripts", c => c.Game.HasScripts, "No launch scripts");
        AddTo(_settings, "admin", "Runs as administrator", c => c.Game.RunAsAdmin, "Doesn't run as administrator");
        AddTo(_settings, "dlss", "DLSS Override on", c => c.DlssOverrideOn, "DLSS Override off");
        AddTo(_settings, "cpucores", "CPU Cores set", c => c.Game.CpuAffinity != CpuAffinityMode.Default, "CPU Cores not set");
        AddTo(_settings, "hdr", "HDR set for this game", c => c.Game.Hdr != HdrMode.ProfileDefault, "HDR not set for this game");

        AddTo(_attention, "missing", "Executable missing", c => c.IsMissing);
        AddTo(_attention, "notinstalled", "Not installed", c => c.IsNotInstalled);
        AddTo(_attention, "noposter", "No poster", c => string.IsNullOrWhiteSpace(c.Game.CoverImagePath));
        AddTo(_attention, "nomatch", "Not matched to Steam or RAWG",
            c => string.IsNullOrWhiteSpace(c.Game.SteamAppId) && c.Game.RawgId == 0);

        AddGroups(_launchers, _lastPlayed, _profiles, _settings, _attention);
    }

    private void AddProfile(PerformanceProfileMode mode, string label) =>
        _profiles.Options.Add(new FilterOption(
            "profile:" + mode.ToString().ToLowerInvariant(), label,
            c => ((GameCardViewModel)c).Game.PerformanceProfile == mode, OnOptionChanged));

    private void AddTo(FilterGroup group, string key, string label, Func<GameCardViewModel, bool> match,
        string? negatedLabel = null)
    {
        // Keys keep their historical prefixes where an option already existed, so a filter saved by an
        // earlier version still restores; the group an option sits in is not part of its key.
        string prefix = key is "dlss" or "cpucores" or "hdr" ? "setting:" : "status:";
        group.Options.Add(new FilterOption(prefix + key, label, c => match((GameCardViewModel)c), OnOptionChanged, negatedLabel));
    }

    /// <summary>True when the card survives every group. Groups with nothing ticked pass everything.</summary>
    public bool Matches(GameCardViewModel card) => MatchesItem(card);

    /// <summary>
    /// Rebuilds the Launcher group from what is actually in the library, so a user who has never
    /// touched Ubisoft Connect is not offered a tick box that can only ever return nothing, and works
    /// out which of the capability-dependent settings options are worth offering on this PC.
    /// Ticks that no longer have a matching option are dropped - the alternative is an invisible
    /// filter hiding games with no way to reach it.
    /// </summary>
    public void RebuildLauncherOptions(IEnumerable<GameCardViewModel> cards)
    {
        var all = cards as IReadOnlyCollection<GameCardViewModel> ?? cards.ToList();
        var present = new HashSet<LauncherPlatform>();
        bool anyLocal = false;
        foreach (var card in all)
        {
            var platform = PlatformOf(card.Game);
            if (platform is LauncherPlatform p) present.Add(p);
            else anyLocal = true;
        }

        // Ticks already on screen, plus any read from settings before this group existed.
        var checkedKeys = KeysOf(_launchers.Options).ToHashSet(StringComparer.Ordinal);
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

        // Offered when this PC can use it, or when a game already has it set (so a setting made
        // before a driver went away can still be found).
        SetAvailable("setting:dlss", FilterCapabilities.Probe(_capabilities.Dlss) || all.Any(c => c.DlssOverrideOn));
        SetAvailable("setting:cpucores", FilterCapabilities.Probe(_capabilities.CpuCores) || all.Any(c => c.Game.CpuAffinity != CpuAffinityMode.Default));
        SetAvailable("setting:hdr", FilterCapabilities.Probe(_capabilities.Hdr) || all.Any(c => c.Game.Hdr != HdrMode.ProfileDefault));

        foreach (var group in Groups) group.NotifyVisibilityChanged();
        NotifyActiveChanged();
    }

    private void SetAvailable(string key, bool available)
    {
        var option = AllOptions.FirstOrDefault(o => o.Key == key);
        if (option != null) option.IsAvailable = available;
    }

    private void AddLauncher(string key, string label, Func<GameCardViewModel, bool> match, HashSet<string> restoreChecked)
    {
        var option = new FilterOption(key, label, c => match((GameCardViewModel)c), OnOptionChanged);
        option.SetSilently(restoreChecked.Contains(key), restoreChecked.Contains(option.ExcludedKey));
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
        (LauncherPlatform.BattleNet, "Battle.net"),
        (LauncherPlatform.Rockstar, "Rockstar"),
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
        if (game.IsBattleNetGame) return LauncherPlatform.BattleNet;
        if (game.IsRockstarGame) return LauncherPlatform.Rockstar;
        return null;
    }

    /// <summary>
    /// Launcher ticks are applied too, even though that group has not been built yet -
    /// <see cref="RebuildLauncherOptions"/> carries them over once the library is loaded.
    /// </summary>
    protected override void OnKeysWithoutOptions(IReadOnlySet<string> keys)
    {
        foreach (string key in keys.Where(k => k.StartsWith("launcher:", StringComparison.Ordinal) || k.StartsWith("!launcher:", StringComparison.Ordinal)))
        {
            _pendingLauncherKeys.Add(key);
        }
    }

    /// <summary>Launcher ticks read from settings before the launcher options existed; consumed by
    /// the first <see cref="RebuildLauncherOptions"/>.</summary>
    private readonly HashSet<string> _pendingLauncherKeys = new(StringComparer.Ordinal);
}
