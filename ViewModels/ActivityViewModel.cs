using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>Which entries the Activity &amp; History tab strip shows.</summary>
public enum ActivityTab { All, Problems, Changes, Activity }

/// <summary>
/// The Activity &amp; History page, opened from the bell at the bottom of the sidebar: NEEDS
/// ATTENTION (the live states, each with its fix) and RECENT (the history, grouped, searchable).
/// Nothing on it needs managing - see <see cref="ActivityService"/>.
/// </summary>
public sealed class ActivityViewModel : ViewModelBase
{
    private readonly ActivityService _service;
    private readonly Func<bool> _isOnScreen;
    private ActivityTab _tab = ActivityTab.All;
    private string _searchText = string.Empty;
    private bool _refreshQueued;
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    public ActivityViewModel(ActivityService service, Func<bool>? isOnScreen = null)
    {
        _service = service;
        _isOnScreen = isOnScreen ?? (() => false);
        SelectTabCommand = new RelayCommand(p =>
        {
            if (Enum.TryParse(p?.ToString(), out ActivityTab tab)) Tab = tab;
        });
        _service.Changed += QueueRefresh;
        Refresh();
    }

    public ObservableCollection<AttentionItemViewModel> NeedsAttention { get; } = new();
    public ObservableCollection<ActivityGroupViewModel> Groups { get; } = new();
    /// <summary>The same rows under date headers (Today, Yesterday, Earlier this week ...), newest section first.</summary>
    public ObservableCollection<ActivitySectionViewModel> Sections { get; } = new();

    public ICommand SelectTabCommand { get; }

    public ActivityTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsAllTab));
            OnPropertyChanged(nameof(IsProblemsTab));
            OnPropertyChanged(nameof(IsChangesTab));
            OnPropertyChanged(nameof(IsActivityTab));
            RebuildGroups();
        }
    }

    public bool IsAllTab => Tab == ActivityTab.All;
    public bool IsProblemsTab => Tab == ActivityTab.Problems;
    public bool IsChangesTab => Tab == ActivityTab.Changes;
    public bool IsActivityTab => Tab == ActivityTab.Activity;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty)) RebuildGroups();
        }
    }

    /// <summary>What lights the bell's dot.</summary>
    public bool HasAttention => _service.AttentionCount > 0;
    public bool HasCriticalAttention => _service.States.Any(s => s.IsCritical);
    public int AttentionCount => _service.AttentionCount;

    /// <summary>The page header's status text, top right, as every page has.</summary>
    public string HeaderStatus => AttentionCount switch
    {
        0 => "No actions needed",
        1 => "1 needs attention",
        int n => $"{n} need attention",
    };

    public bool ShowNeedsAttention => NeedsAttention.Count > 0;
    public bool HasAnyEntries => _service.Entries.Count > 0;
    public bool ShowNothingYet => !HasAnyEntries;
    public bool ShowNoMatches => HasAnyEntries && Groups.Count == 0;
    public bool ShowRecent => Groups.Count > 0;

    /// <summary>The page is on screen: everything recorded so far counts as seen.</summary>
    public void MarkViewed() => _service.MarkViewed();

    private void QueueRefresh()
    {
        // The thread that made the page owns its collections: the UI thread in the app.
        var dispatcher = _dispatcher;
        if (dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }
        if (_refreshQueued) return;
        _refreshQueued = true;
        dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>Re-reads the states and the history. On the UI thread.</summary>
    public void Refresh()
    {
        NeedsAttention.Clear();
        foreach (var state in _service.States) NeedsAttention.Add(new AttentionItemViewModel(state));
        RebuildGroups();

        // Something recorded while the page is in front of the user has been seen.
        if (_isOnScreen() && _service.UnseenProblemCount > 0) _service.MarkViewed();

        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(HasCriticalAttention));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(HeaderStatus));
        OnPropertyChanged(nameof(ShowNeedsAttention));
        OnPropertyChanged(nameof(HasAnyEntries));
        OnPropertyChanged(nameof(ShowNothingYet));
    }

    private void RebuildGroups()
    {
        string query = _searchText.Trim();
        var entries = _service.Entries.Where(e => Tab switch
        {
            ActivityTab.Problems => e.Level >= ActivityLevel.Problem,
            ActivityTab.Changes => e.Level == ActivityLevel.Change,
            ActivityTab.Activity => e.Level == ActivityLevel.Activity,
            _ => true,
        });
        if (query.Length > 0)
        {
            entries = entries.Where(e => Matches(e.Text, query) || Matches(e.Subject, query) || Matches(e.Detail, query));
        }

        // A row the user opened stays open when a new entry, or the page being seen, rebuilds the list.
        var expanded = Groups.Where(g => g.IsExpanded).Select(g => g.Group.Key).ToHashSet(StringComparer.Ordinal);
        Groups.Clear();
        Sections.Clear();
        DateTime now = DateTime.Now;
        foreach (var group in ActivityService.Group(entries))
        {
            Groups.Add(new ActivityGroupViewModel(group, now) { IsExpanded = expanded.Contains(group.Key) });
        }
        // Rows come newest first and every later section holds strictly older days, so grouping
        // in order keeps both the sections and the rows within them in date order.
        foreach (var section in Groups.GroupBy(g => g.Section))
        {
            Sections.Add(new ActivitySectionViewModel(section.Key, section.ToList()));
        }

        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowRecent));
    }

    private static bool Matches(string? text, string query) =>
        text != null && text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Which date header a row sits under: the calendar sections Outlook and File Explorer use,
    /// rather than counts of days. Weeks start on the locale's first day unless a test says
    /// otherwise. Each later section covers strictly older days than the one before it, which is
    /// what lets <see cref="RebuildGroups"/> group rows in the order they already come.
    /// </summary>
    internal static string SectionFor(DateTime utc, DateTime nowLocal, DayOfWeek? firstDayOfWeek = null)
    {
        DateTime day = utc.ToLocalTime().Date;
        DateTime today = nowLocal.Date;
        if (day >= today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";

        DayOfWeek first = firstDayOfWeek ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        DateTime weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)first + 7) % 7));
        if (day >= weekStart) return "Earlier this week";
        if (day >= weekStart.AddDays(-7)) return "Last week";

        DateTime monthStart = new(today.Year, today.Month, 1);
        if (day >= monthStart) return "Earlier this month";
        if (day >= monthStart.AddMonths(-1)) return "Last month";
        return "Older";
    }

    /// <summary>"Today 22:31", "Yesterday 09:12", "Mon 18:00", "Sep 28 18:00", "Sep 28 2025".</summary>
    internal static string FormatWhen(DateTime utc, DateTime nowLocal)
    {
        DateTime local = utc.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        string time = local.ToString("t", culture);
        int daysAgo = (nowLocal.Date - local.Date).Days;
        if (daysAgo <= 0) return $"Today {time}";
        if (daysAgo == 1) return $"Yesterday {time}";
        if (daysAgo < 7) return $"{local.ToString("ddd", culture)} {time}";
        if (local.Year == nowLocal.Year) return $"{local.ToString("MMM d", culture)} {time}";
        return local.ToString("MMM d yyyy", culture);
    }
}

/// <summary>A NEEDS ATTENTION row: what is wrong now, and its fix.</summary>
public sealed class AttentionItemViewModel
{
    public AttentionItemViewModel(AttentionItem item)
    {
        Item = item;
        FixCommand = new RelayCommand(() =>
        {
            try { item.Action?.Invoke(); }
            catch (Exception ex) { LoggingService.Warn("Activity", $"The fix for \"{item.Text}\" failed: {ex.Message}"); }
        });
    }

    public AttentionItem Item { get; }
    public string Text => Item.Text;
    public string Detail => Item.Detail;
    public bool IsCritical => Item.IsCritical;
    public bool HasAction => Item.Action != null && !string.IsNullOrWhiteSpace(Item.ActionLabel);
    public string ActionLabel => Item.ActionLabel ?? string.Empty;
    public ICommand FixCommand { get; }
}

/// <summary>A RECENT row: one entry, or every time the same thing happened.</summary>
public sealed class ActivityGroupViewModel : ViewModelBase
{
    private bool _isExpanded;

    public ActivityGroupViewModel(ActivityGroup group, DateTime nowLocal)
    {
        Group = group;
        When = ActivityViewModel.FormatWhen(group.Latest.TimeUtc, nowLocal);
        Section = ActivityViewModel.SectionFor(group.Latest.TimeUtc, nowLocal);
        Times = group.TimesUtc.Select(t => ActivityViewModel.FormatWhen(t, nowLocal)).ToList();
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        CopyCommand = new RelayCommand(CopyDetails);
        ShowLogCommand = new RelayCommand(LoggingService.OpenLogInEditor);
    }

    public ActivityGroup Group { get; }
    public string Text => Group.Latest.Text;
    public string? Detail => Group.Latest.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public string When { get; }
    /// <summary>The date header this row sits under; see <see cref="ActivityViewModel.SectionFor"/>.</summary>
    public string Section { get; }
    public IReadOnlyList<string> Times { get; }
    public bool IsProblem => Group.Latest.Level >= ActivityLevel.Problem;
    public bool IsCritical => Group.Latest.Level == ActivityLevel.Critical;
    public int Count => Group.Count;

    /// <summary>
    /// Every row's level badge, in the System page's badge style: CRITICAL solid red, as OPTIMAL is
    /// solid, so it stands out from the tinted rest - PROBLEM amber (RESTART's colours), CHANGE blue,
    /// ACTIVITY grey (OPT-IN's). The owner's choice, option C of the 2026-10-04 mockups.
    /// </summary>
    public string LevelTag => Group.Latest.Level switch
    {
        ActivityLevel.Critical => "CRITICAL",
        ActivityLevel.Problem => "PROBLEM",
        ActivityLevel.Change => "CHANGE",
        _ => "ACTIVITY",
    };
    /// <summary>The row for a screen reader, its level first: "Problem: Elden Ring's post-exit script failed".</summary>
    public string AccessibleName => $"{char.ToUpperInvariant(LevelTag[0])}{LevelTag[1..].ToLowerInvariant()}{(IsFixed ? ", fixed" : "")}: {Text}";

    public string LevelGlyph => Group.Latest.Level switch
    {
        ActivityLevel.Critical => "",
        ActivityLevel.Problem => "",
        ActivityLevel.Change => "",
        _ => "",
    };
    public string LevelBackground => Group.Latest.Level switch
    {
        ActivityLevel.Critical => "#DA3633",
        ActivityLevel.Problem => "#3D2F14",
        ActivityLevel.Change => "#14304D",
        _ => "#2A2A3A",
    };
    public string LevelForeground => Group.Latest.Level switch
    {
        ActivityLevel.Critical => "#FFFFFF",
        ActivityLevel.Problem => "#E8B84E",
        ActivityLevel.Change => "#6CB6FF",
        _ => "#B8B8CC",
    };

    /// <summary>Every Critical entry in the row put right since: a green FIXED badge, as OPTIMAL is green.</summary>
    public bool IsFixed => Group.Fixed;
    /// <summary>When, for the expanded row: "Put back Today 1:20 PM, with Restore Previous."</summary>
    public string FixedLabel => IsFixed && Group.Latest.FixedUtc is { } t
        ? $"Put back {ActivityViewModel.FormatWhen(t, DateTime.Now)}, with Restore Previous."
        : string.Empty;

    /// <summary>"7 TIMES" on a repeated problem, the way MISSING flags a card; blank otherwise.</summary>
    public string CountTag => IsProblem && Count > 1 ? $"{Count} TIMES" : string.Empty;
    public bool ShowCountTag => CountTag.Length > 0;
    /// <summary>A repeated change or activity gets a quiet count instead of a tag.</summary>
    public string QuietCount => !IsProblem && Count > 1 ? $"· {Count} times" : string.Empty;
    public bool ShowQuietCount => QuietCount.Length > 0;
    public bool ShowTimes => Count > 1;
    /// <summary>The latest ten times; Copy details has them all.</summary>
    public string TimesLabel => Count <= 1 ? string.Empty
        : Count <= 10 ? $"Each time: {string.Join(", ", Times)}"
        : $"Each time: {string.Join(", ", Times.Take(10))} and {Count - 10} earlier";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ICommand ToggleCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ShowLogCommand { get; }

    /// <summary>The row as text, for a bug report or a Discussions post.</summary>
    internal string DetailsText()
    {
        var sb = new StringBuilder();
        sb.Append(Group.Latest.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.Append("  ").Append(Group.Latest.Level).Append(": ").AppendLine(Text);
        if (HasDetail) sb.AppendLine(Detail);
        if (IsFixed && Group.Latest.FixedUtc is { } fixedAt)
            sb.AppendLine("Fixed " + fixedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ", with Restore Previous.");
        if (Count > 1) sb.AppendLine($"{Count} times: " + string.Join(", ", Group.TimesUtc.Select(t => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))));
        sb.Append("TrayTrigger ").Append(UpdateService.CurrentVersionDisplay);
        return sb.ToString();
    }

    private void CopyDetails()
    {
        try { Clipboard.SetText(DetailsText()); }
        catch (Exception ex) { LoggingService.Warn("Activity", $"Could not copy the entry to the clipboard: {ex.Message}"); }
    }
}

/// <summary>One date header on the RECENT list and the rows under it.</summary>
public sealed class ActivitySectionViewModel(string label, IReadOnlyList<ActivityGroupViewModel> rows)
{
    /// <summary>Upper case, as the page's and Settings' section labels are.</summary>
    public string Label { get; } = label.ToUpperInvariant();
    public IReadOnlyList<ActivityGroupViewModel> Rows { get; } = rows;
}
