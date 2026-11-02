using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>One option row in a filter flyout (Library, Tools): off, ticked, and for a yes/no fact, ticked as "not".</summary>
public sealed class FilterOption : ViewModelBase
{
    private readonly Action _onChanged;
    private bool _isChecked;
    private bool _isExcluded;
    private bool _isAvailable = true;

    /// <summary>Stable identifier used to persist the filter across restarts ("launcher:steam" or "kind:program").</summary>
    public string Key { get; }
    public string Label { get; }
    /// <summary>Does this one card (a game, a tool) satisfy this option on its own.</summary>
    public Func<object, bool> Match { get; }

    /// <summary>
    /// True for a yes/no fact (has a hotkey, DLSS Override on): pressing it a second time asks for the
    /// opposite, worded by <see cref="NegatedLabel"/>. False for a choice between values (launcher,
    /// profile), where ticking the other values says the same thing and a plain tick is all there is.
    /// </summary>
    public bool AllowExclude { get; }

    /// <summary>The label while the option is a "not": "No hotkey", "DLSS Override off".</summary>
    public string NegatedLabel { get; }

    public FilterOption(string key, string label, Func<object, bool> match, Action onChanged,
        string? negatedLabel = null)
    {
        Key = key;
        Label = label;
        Match = match;
        _onChanged = onChanged;
        AllowExclude = negatedLabel != null;
        NegatedLabel = negatedLabel ?? label;
    }

    /// <summary>What the row says now, and what a screen reader says: the label, or its opposite once pressed twice.</summary>
    public string DisplayLabel => _isExcluded ? NegatedLabel : Label;

    public string? ToolTipText => AllowExclude ? "Click to include, again for the opposite, a third time to clear" : null;

    /// <summary>Ticked: a game has to match (with the rest of its group, per the group's rule).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetState(value, value ? false : _isExcluded, notify: true);
    }

    /// <summary>
    /// Ticked as "not": a game that matches is left out. A second press on a yes/no option turns it into
    /// this, which is how "DLSS Override off" is asked for. Always false on an option that doesn't allow it.
    /// </summary>
    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (value && !AllowExclude) return;
            SetState(value ? false : _isChecked, value, notify: true);
        }
    }

    /// <summary>Ticked either way. This is what counts as "a filter is on".</summary>
    public bool IsActive => _isChecked || _isExcluded;

    /// <summary>
    /// The three-state box the flyout binds: true is ticked, null is "not", false is off. A press cycles
    /// off, ticked, not, off.
    /// </summary>
    public bool? State
    {
        get => _isChecked ? true : _isExcluded ? null : false;
        set => SetState(value == true, value == null && AllowExclude, notify: true);
    }

    private void SetState(bool include, bool exclude, bool notify)
    {
        if (_isChecked == include && _isExcluded == exclude) return;
        _isChecked = include;
        _isExcluded = exclude;
        RaiseStateChanged();
        if (notify) _onChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsExcluded));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(DisplayLabel));
    }

    /// <summary>The key that saves this option's state: the plain key when ticked, "!" and the key when excluded.</summary>
    public string ExcludedKey => "!" + Key;

    /// <summary>
    /// False for an option that could only ever return nothing on this PC (DLSS Override with no
    /// NVIDIA driver, say). It is left out of the flyout, and a tick on it neither filters nor counts,
    /// the same rule that keeps a stranded tick from hiding games with no way to untick it.
    /// </summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        internal set
        {
            if (_isAvailable == value) return;
            _isAvailable = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Sets the box without running the filter - for restoring saved state during construction.</summary>
    internal void SetSilently(bool include, bool exclude = false)
    {
        _isChecked = include;
        _isExcluded = exclude && !include && AllowExclude;
        RaiseStateChanged();
    }
}

/// <summary>
/// A headed block of options in the flyout. How the ticks inside it combine depends on what the
/// options are. Values of one attribute (Steam or Epic, Optimized or Aggressive) are "any of":
/// a game is only one of them, so "all of" could never match. Independent yes/no flags (has a
/// hotkey, has scripts) are "all of", the way a player ticking two of them means both. Groups
/// themselves are always AND'd (Steam AND never played). A group with nothing ticked places no
/// constraint, which is why "no filters" and "every box ticked" mean the same thing and neither
/// hides anything.
/// </summary>
public sealed class FilterGroup : ViewModelBase
{
    public string Title { get; }

    /// <summary>The heading as shown, in capitals (WPF has no text transform). Screen readers get <see cref="Title"/>.</summary>
    public string HeadingText => Title.ToUpperInvariant();

    public ObservableCollection<FilterOption> Options { get; } = new();

    /// <summary>True: a game must satisfy every ticked option. False: any one is enough.</summary>
    public bool MatchAll { get; }

    public FilterGroup(string title, bool matchAll = false)
    {
        Title = title;
        MatchAll = matchAll;
        ClearCommand = new RelayCommand(() => ClearAction?.Invoke());
    }

    /// <summary>Clears just this group's ticks. Wired by the filter that owns the group.</summary>
    public ICommand ClearCommand { get; }
    internal Action? ClearAction { get; set; }

    /// <summary>Something in this group is on: shows its "clear" link.</summary>
    public bool HasChecked => CheckedCount > 0;

    /// <summary>Ticked options the player can see.</summary>
    public int CheckedCount => Options.Count(o => o.IsAvailable && o.IsActive);

    /// <summary>
    /// False when this group cannot narrow anything. That covers an empty group - the Launcher
    /// group on a library imported entirely by hand - and also a group down to a single option,
    /// which is worse than useless: a library of nothing but Steam games showed a lone "Steam"
    /// tickbox whose only effect was to hide nothing.
    /// </summary>
    public bool IsVisible => Options.Count(o => o.IsAvailable) > 1;

    internal bool Matches(object card)
    {
        // A group the flyout does not show is inert. Without this, a tick could survive in a group
        // that later collapsed to one option and hide games with no visible way to undo it - the
        // same trap RebuildLauncherOptions already avoids by dropping orphaned ticks.
        if (!IsVisible) return true;

        // "Not" ticks come first and are the same in either mode: a game that matches one is out.
        bool anyIncluded = false, anyIncludedMatches = false;
        foreach (var option in Options)
        {
            if (!option.IsAvailable) continue;
            if (option.IsExcluded)
            {
                if (option.Match(card)) return false;
                continue;
            }
            if (!option.IsChecked) continue;

            anyIncluded = true;
            bool matches = option.Match(card);
            if (MatchAll)
            {
                if (!matches) return false;
            }
            else if (matches)
            {
                anyIncludedMatches = true;
            }
        }
        return MatchAll || !anyIncluded || anyIncludedMatches;
    }

    internal void NotifyVisibilityChanged() => OnPropertyChanged(nameof(IsVisible));

    internal void NotifyCountChanged()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasChecked));
    }
}

/// <summary>One row of the flyout's Saved section.</summary>
public sealed class SavedFilterItem : ViewModelBase
{
    private bool _isSelected;
    private bool _isEdited;

    public string Name { get; }
    public List<string> Keys { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DeleteCommand { get; }

    internal SavedFilterItem(string name, IEnumerable<string> keys, Action<SavedFilterItem> apply, Action<SavedFilterItem> delete)
    {
        Name = name;
        Keys = keys.ToList();
        ApplyCommand = new RelayCommand(() => apply(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    /// <summary>The ticks on screen are exactly this filter's.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>This filter was applied, then a tick changed: the ticks are no longer what is saved.</summary>
    public bool IsEdited
    {
        get => _isEdited;
        internal set
        {
            if (_isEdited == value) return;
            _isEdited = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>What a screen reader says: the name, and "edited" while the ticks no longer match what is saved.</summary>
    public string AccessibleName => IsEdited ? $"Saved filter {Name}, edited" : $"Saved filter {Name}";
}

/// <summary>
/// What a filter flyout does, for any list that has one (the Library's games, the Tools page's tools):
/// groups of tick boxes, "not" ticks on yes/no facts, a count of what is on, "Showing 3 of 9", and named
/// combinations that can be saved and applied. The rules are written once here so the two flyouts
/// behave the same; what differs is only the options each one offers and what an item is.
/// <para>
/// Ticks in a group that holds values of one attribute are "any of", ticks on independent yes/no facts are
/// "all of", groups are always "and", and a group with nothing ticked places no restriction (see
/// <see cref="FilterGroup"/>).
/// </para>
/// </summary>
public abstract class FilterFlyoutViewModel : ViewModelBase
{
    private const int MaxSavedNameLength = 40;

    private readonly Action _onChanged;
    private readonly Action<IReadOnlyList<SavedFilter>>? _onSavedChanged;
    private string? _appliedSavedName;

    public ObservableCollection<FilterGroup> Groups { get; } = new();

    /// <summary>The filters the player saved, in the order they were saved.</summary>
    public ObservableCollection<SavedFilterItem> SavedFilters { get; } = new();
    public bool HasSavedFilters => SavedFilters.Count > 0;

    /// <summary>One thing the list holds, as the flyout counts it: "game" / "tool".</summary>
    public abstract string NounSingular { get; }
    public abstract string NounPlural { get; }

    /// <summary>Asks the player for a name. Set by the view; null result means cancelled.</summary>
    public Func<string?>? PromptForName { get; set; }

    /// <summary>
    /// Asks before a saved filter is replaced: the name, what it holds now, and what it would hold. Set by
    /// the view; unset means yes.
    /// </summary>
    public Func<string, string, string, bool>? ConfirmReplace { get; set; }

    /// <summary>Asks before a saved filter is deleted. Set by the view; unset means yes.</summary>
    public Func<string, bool>? ConfirmDelete { get; set; }

    private bool _isOpen;
    /// <summary>Whether the flyout is showing - bound two-way to the popup.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value) return;
            _isOpen = value;
            OnPropertyChanged();
            // Opened after a tab or search change: the total is worked out now, not when the ticks last moved.
            if (value) OnPropertyChanged(nameof(ResultSummary));
        }
    }

    /// <summary>Counts the items the current tab and search would show with and without the flyout's ticks.</summary>
    internal Func<(int Shown, int InView)>? CountProvider { get; set; }

    /// <summary>
    /// "Showing 31 of 112": what the ticks did. One number worked out when it is asked for, not a count on
    /// every option, so it can't be misread as a different group's total and costs one pass over the list.
    /// </summary>
    public string ResultSummary
    {
        get
        {
            if (CountProvider == null) return string.Empty;
            var (shown, inView) = CountProvider();
            if (!HasActiveFilters) return inView == 1 ? $"1 {NounSingular}" : $"{inView} {NounPlural}";
            return $"Showing {shown} of {inView}";
        }
    }

    public ICommand ClearCommand { get; }
    public ICommand ToggleOpenCommand { get; }
    public ICommand SaveCurrentCommand { get; }

    protected FilterFlyoutViewModel(Action onChanged, IEnumerable<SavedFilter>? saved, Action<IReadOnlyList<SavedFilter>>? onSavedChanged)
    {
        _onChanged = onChanged;
        _onSavedChanged = onSavedChanged;

        if (saved != null)
        {
            foreach (var filter in saved)
            {
                if (string.IsNullOrWhiteSpace(filter.Name) || SavedFilters.Any(s => SameName(s.Name, filter.Name))) continue;
                SavedFilters.Add(NewSavedItem(filter.Name.Trim(), filter.Keys ?? new List<string>()));
            }
        }

        // Clear is a reset, not a close: the flyout stays open so the user can tick something else
        // straight away. The button is hidden (not collapsed) at zero so nothing reflows under the
        // pointer, and the view moves keyboard focus off it (FilterFlyout.OnClearClick).
        ClearCommand = new RelayCommand(ClearAll);
        ToggleOpenCommand = new RelayCommand(() => IsOpen = !IsOpen);
        SaveCurrentCommand = new RelayCommand(SaveCurrent, () => HasActiveFilters);
    }

    /// <summary>Puts the groups in the flyout, in this order, and gives each its "clear".</summary>
    protected void AddGroups(params FilterGroup[] groups)
    {
        foreach (var group in groups)
        {
            Groups.Add(group);
            var captured = group;
            captured.ClearAction = () => ClearGroup(captured);
        }
    }

    protected void OnOptionChanged()
    {
        NotifyActiveChanged();
        _onChanged();
    }

    private void ClearGroup(FilterGroup group)
    {
        var active = group.Options.Where(o => o.IsActive).ToList();
        if (active.Count == 0) return;
        foreach (var option in active) option.SetSilently(false);
        NotifyActiveChanged();
        _onChanged();
    }

    protected void NotifyActiveChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(ActiveFilterCount));
        OnPropertyChanged(nameof(ActiveFilterSummary));
        OnPropertyChanged(nameof(AccessibleName));
        // A pass over the list: only while someone can see it. Opening the flyout raises it again.
        if (_isOpen) OnPropertyChanged(nameof(ResultSummary));
        foreach (var group in Groups) group.NotifyCountChanged();
        RefreshSavedState();
    }

    protected IEnumerable<FilterOption> AllOptions => Groups.SelectMany(g => g.Options);

    /// <summary>
    /// Options the user can actually see and untick. Ticks stranded in a group the flyout hides, or
    /// on an option that is left out here, are excluded, because <see cref="FilterGroup.Matches"/>
    /// ignores them too - counting them would advertise a filter that is not filtering and cannot be reached.
    /// </summary>
    protected IEnumerable<FilterOption> ReachableOptions =>
        Groups.Where(g => g.IsVisible).SelectMany(g => g.Options).Where(o => o.IsAvailable);

    /// <summary>Drives the count badge on the filter button.</summary>
    public bool HasActiveFilters => ReachableOptions.Any(o => o.IsActive);
    public int ActiveFilterCount => ReachableOptions.Count(o => o.IsActive);

    /// <summary>
    /// The filter button is icon-only and its badge is not reachable by a screen reader, so the
    /// count has to travel on the button's own automation name instead.
    /// </summary>
    public string AccessibleName => $"Filter {NounPlural}. {ActiveFilterSummary}.";
    public string ActiveFilterSummary => ActiveFilterCount switch
    {
        0 => "No filters",
        1 => "1 filter active",
        _ => $"{ActiveFilterCount} filters active"
    };

    public void ClearAll()
    {
        _appliedSavedName = null;
        bool any = false;
        foreach (var option in AllOptions.Where(o => o.IsActive))
        {
            option.SetSilently(false);
            any = true;
        }
        if (!any)
        {
            RefreshSavedState();
            return;
        }
        NotifyActiveChanged();
        _onChanged();
    }

    /// <summary>True when the item survives every group. Groups with nothing ticked pass everything.</summary>
    protected bool MatchesItem(object item) => Groups.All(g => g.Matches(item));

    /// <summary>The ticked keys, for the settings file.</summary>
    public List<string> ActiveKeys() => KeysOf(AllOptions).ToList();

    /// <summary>The saved form of each ticked option: its key, or "!" and its key when it is a "not".</summary>
    protected static IEnumerable<string> KeysOf(IEnumerable<FilterOption> options) =>
        options.Where(o => o.IsActive).Select(o => o.IsExcluded ? o.ExcludedKey : o.Key);

    /// <summary>
    /// Restores saved ticks without running the filter. A key with no option yet is handed to
    /// <see cref="OnKeysWithoutOptions"/>: the Library's launcher options are built from the library once it
    /// has loaded, and carry their ticks over then.
    /// </summary>
    public void RestoreKeys(IEnumerable<string>? keys)
    {
        if (keys == null) return;
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;

        foreach (var option in AllOptions.Where(o => wanted.Contains(o.Key) || wanted.Contains(o.ExcludedKey)))
        {
            option.SetSilently(wanted.Contains(option.Key), wanted.Contains(option.ExcludedKey));
        }
        OnKeysWithoutOptions(wanted);
        NotifyActiveChanged();
    }

    /// <summary>Keys read from settings, offered before any option of a group built later exists.</summary>
    protected virtual void OnKeysWithoutOptions(IReadOnlySet<string> keys) { }

    // --- Saved filters ---

    /// <summary>One space between words, no more than <see cref="MaxSavedNameLength"/> characters. Applied before the
    /// "already saved?" check, so a name that only differs by spacing or by what is cut off still finds its match.</summary>
    private static string NormalizeName(string? name)
    {
        name = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length <= MaxSavedNameLength) return name;

        // Not between the two halves of an emoji, which would leave half a character.
        int cut = MaxSavedNameLength;
        if (char.IsHighSurrogate(name[cut - 1])) cut--;
        return name[..cut].TrimEnd();
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private SavedFilterItem NewSavedItem(string name, IEnumerable<string> keys) =>
        new(name, keys, ApplySaved, DeleteSaved);

    /// <summary>"Save current...": asks for a name, then saves the ticks on screen under it.</summary>
    private void SaveCurrent()
    {
        if (!HasActiveFilters) return;
        string name = NormalizeName(PromptForName?.Invoke());
        if (name.Length == 0) return;

        var existing = SavedFilters.FirstOrDefault(s => SameName(s.Name, name));
        if (existing != null && ConfirmReplace != null &&
            !ConfirmReplace(existing.Name, Describe(existing.Keys), Describe(KeysOf(ReachableOptions))))
        {
            return;
        }
        SaveAs(name);
    }

    /// <summary>A filter's ticks in words, for the replace question: "Steam, Never played, No hotkey".</summary>
    private string Describe(IEnumerable<string> keys)
    {
        var byKey = AllOptions.ToDictionary(o => o.Key, StringComparer.Ordinal);
        var words = new List<string>();
        foreach (string key in keys)
        {
            bool not = key.StartsWith('!');
            if (byKey.TryGetValue(not ? key[1..] : key, out var option))
            {
                words.Add(not ? option.NegatedLabel : option.Label);
            }
        }
        return words.Count == 0 ? "nothing ticked" : string.Join(", ", words);
    }

    /// <summary>
    /// Saves the ticks on screen under <paramref name="name"/>. A name already in use is updated, which
    /// is how an edited saved filter is saved back. Returns false when nothing is ticked or the name is blank.
    /// </summary>
    public bool SaveAs(string name)
    {
        name = NormalizeName(name);
        var keys = KeysOf(ReachableOptions).ToList();
        if (name.Length == 0 || keys.Count == 0) return false;

        var existing = SavedFilters.FirstOrDefault(s => SameName(s.Name, name));
        var item = NewSavedItem(existing?.Name ?? name, keys);
        if (existing != null) SavedFilters[SavedFilters.IndexOf(existing)] = item;
        else SavedFilters.Add(item);

        _appliedSavedName = item.Name;
        RefreshSavedState();
        RaiseSavedChanged();
        return true;
    }

    /// <summary>Puts exactly this filter's ticks on screen. A key with no option here (a launcher that has left the library) is skipped.</summary>
    public void ApplySaved(SavedFilterItem item)
    {
        var wanted = item.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var option in AllOptions)
        {
            option.SetSilently(wanted.Contains(option.Key), wanted.Contains(option.ExcludedKey));
        }
        _appliedSavedName = item.Name;
        NotifyActiveChanged();
        _onChanged();
    }

    private void DeleteSaved(SavedFilterItem item)
    {
        if (ConfirmDelete != null && !ConfirmDelete(item.Name)) return;
        if (!SavedFilters.Remove(item)) return;
        if (SameName(_appliedSavedName ?? string.Empty, item.Name)) _appliedSavedName = null;
        RefreshSavedState();
        RaiseSavedChanged();
    }

    /// <summary>
    /// Marks which saved filter the ticks on screen are, and which one they were once. Compared against
    /// the options that exist now, so a saved launcher that has gone doesn't make a filter read as edited.
    /// </summary>
    private void RefreshSavedState()
    {
        // The ticks that count, as Save stores them: one left on an option this PC doesn't offer filters nothing.
        var reachable = ReachableOptions.ToList();
        var known = reachable.SelectMany(o => new[] { o.Key, o.ExcludedKey }).ToHashSet(StringComparer.Ordinal);
        var ticked = KeysOf(reachable).ToHashSet(StringComparer.Ordinal);
        foreach (var item in SavedFilters)
        {
            var saved = item.Keys.Where(known.Contains).ToHashSet(StringComparer.Ordinal);
            bool selected = saved.Count > 0 && ticked.SetEquals(saved);
            item.IsSelected = selected;
            item.IsEdited = !selected && _appliedSavedName != null && SameName(_appliedSavedName, item.Name);
        }
        OnPropertyChanged(nameof(HasSavedFilters));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseSavedChanged() =>
        _onSavedChanged?.Invoke(SavedFilters.Select(s => new SavedFilter { Name = s.Name, Keys = s.Keys.ToList() }).ToList());
}
