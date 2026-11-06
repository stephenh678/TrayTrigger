using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The Tools page's filter flyout: the same flyout as the Library's (<see cref="FilterFlyoutViewModel"/>),
/// with the facts that are true of a tool. What kind of tool it is, what TrayTrigger has been told to do
/// with it (a hotkey, run as administrator, start with games), and what needs fixing. A tool has no
/// launcher, playtime or profile, so none of those groups are here.
/// </summary>
public sealed class ToolFilterViewModel : FilterFlyoutViewModel
{
    private readonly FilterGroup _kinds = new("Kind");
    private readonly FilterGroup _settings = new("TrayTrigger settings", matchAll: true);
    private readonly FilterGroup _attention = new("Needs attention");

    public override string NounSingular => "tool";
    public override string NounPlural => "tools";

    internal ToolFilterViewModel(Action onChanged, IEnumerable<SavedFilter>? saved = null, Action<IReadOnlyList<SavedFilter>>? onSavedChanged = null)
        : base(onChanged, saved, onSavedChanged)
    {
        AddTo(_kinds, "kind:program", "Program", c => !c.IsStoreApp && !c.IsScript);
        AddTo(_kinds, "kind:script", "Script", c => c.IsScript);
        AddTo(_kinds, "kind:storeapp", "Store app", c => c.IsStoreApp);

        // Each a yes/no fact, so the opposite is a real question, as it is for games.
        AddTo(_settings, "status:favorite", "Favorite", c => c.IsFavorite, "Not a favorite");
        AddTo(_settings, "status:hotkey", "Has a hotkey", c => c.HasHotkey, "No hotkey");
        AddTo(_settings, "status:admin", "Runs as administrator", c => c.RunAsAdmin, "Doesn't run as administrator");
        AddTo(_settings, "status:startswithgames", "Starts with games", c => c.StartsWithGames, "Doesn't start with games");
        AddTo(_settings, "status:closesforgames", "Closes for games", c => c.ClosesForGames, "Doesn't close for games");
        AddTo(_settings, "status:hidewindow", "Hides its window", c => c.Tool.HideWindow, "Shows its window");

        AddTo(_attention, "status:missing", "File missing", c => c.IsMissing);
        AddTo(_attention, "status:noicon", "No icon", c => !c.HasIcon);

        AddGroups(_kinds, _settings, _attention);
    }

    private void AddTo(FilterGroup group, string key, string label, Func<ToolCardViewModel, bool> match, string? negatedLabel = null) =>
        group.Options.Add(new FilterOption(key, label, c => match((ToolCardViewModel)c), OnOptionChanged, negatedLabel));

    /// <summary>True when the tool survives every group. Groups with nothing ticked pass everything.</summary>
    public bool Matches(ToolCardViewModel card) => MatchesItem(card);

    /// <summary>
    /// Offers a kind only when there is a tool of it, as the Library offers the launchers its games came
    /// from: a tick box that can only return nothing is no use, and the Kind group is left out of the
    /// flyout altogether while every tool is the same kind.
    /// </summary>
    public void RefreshAvailability(IEnumerable<ToolCardViewModel> tools)
    {
        var all = tools as IReadOnlyCollection<ToolCardViewModel> ?? tools.ToList();
        SetAvailable("kind:program", all.Any(c => !c.IsStoreApp && !c.IsScript));
        SetAvailable("kind:script", all.Any(c => c.IsScript));
        SetAvailable("kind:storeapp", all.Any(c => c.IsStoreApp));
        foreach (var group in Groups) group.NotifyVisibilityChanged();
        NotifyActiveChanged();
    }

    private void SetAvailable(string key, bool available)
    {
        var option = AllOptions.FirstOrDefault(o => o.Key == key);
        if (option != null) option.IsAvailable = available;
    }
}
