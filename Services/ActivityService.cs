using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Something that is wrong right now, shown at the top of Activity &amp; History under NEEDS
/// ATTENTION. Worked out live by whoever owns it and never stored: it stays while the problem
/// does and goes when it is fixed, so there is nothing to dismiss.
/// </summary>
/// <param name="Key">Who owns it; setting the same key again replaces it. Give the entry that
/// announced it the same group key, and the two count once in <see cref="ActivityService.AttentionCount"/>.</param>
/// <param name="Text">The line, e.g. "The power plan wasn't put back after Elden Ring".</param>
/// <param name="Detail">What it means, one sentence.</param>
/// <param name="ActionLabel">The fix button's label, or null for none.</param>
/// <param name="Action">What the button does. Runs on the UI thread.</param>
/// <param name="IsCritical">True when the PC was left changed; shows the CRITICAL tag.</param>
public sealed record AttentionItem(string Key, string Text, string Detail, string? ActionLabel, Action? Action, bool IsCritical);

/// <summary>One row on the page: every entry with the same <see cref="ActivityEntry.GroupKey"/>.</summary>
/// <param name="Fixed">Every Critical entry in the row has been put right (and there is one): the
/// FIXED badge. One left unfixed - an earlier run's, say - keeps it off, so FIXED never hides it.</param>
public sealed record ActivityGroup(string Key, ActivityEntry Latest, IReadOnlyList<DateTime> TimesUtc, bool Fixed = false)
{
    public int Count => TimesUtc.Count;
}

/// <summary>
/// Activity &amp; History: the plain-language record of what TrayTrigger did and what went wrong,
/// kept in activity.json beside games.json, and the live list of what needs attention now. See
/// docs/archive/roadmap-2026-10-05.md, "Notifications and Activity &amp; History".
///
/// <para>Events are chosen, never promoted from the log: each is an explicit
/// <see cref="Record(ActivityLevel, string, string?, string?, string?)"/> at a place in the code
/// where something meaningful to a player happened. There is no per-entry read state and no
/// dismiss: opening the page marks everything seen, and entries go by themselves after
/// <see cref="RetentionDays"/> days (the page's Keep dropdown) or past <see cref="MaxEntries"/>.
/// Clear history, beside it, empties Recent at once.</para>
/// </summary>
public sealed class ActivityService
{
    public const string FileName = "activity.json";
    public const int DefaultRetentionDays = 90;
    /// <summary>The windows the page's Keep dropdown offers: a month, a season, a year.</summary>
    public static readonly IReadOnlyList<int> RetentionChoices = [30, 90, 365];
    public const int MaxEntries = 500;

    /// <summary>A stored window, or the default when it isn't one of <see cref="RetentionChoices"/>.</summary>
    public static int NormalizeRetentionDays(int days) => RetentionChoices.Contains(days) ? days : DefaultRetentionDays;

    /// <summary>How far back Recent goes, read from settings each time so a change applies at once.</summary>
    public int RetentionDays => NormalizeRetentionDays(_retentionDays());
    private readonly Func<int> _retentionDays;

    /// <summary>The app's instance, set at startup. Null in tests and before startup, when
    /// <see cref="Add"/> does nothing - so a service can record without knowing whether anyone listens.</summary>
    public static ActivityService? Current { get; set; }

    /// <summary>Records on <see cref="Current"/>, if there is one. Never throws. Returns the entry,
    /// or null when nothing was recorded.</summary>
    public static ActivityEntry? Add(ActivityLevel level, string text, string? subject = null, string? detail = null, string? groupKey = null)
    {
        try { return Current?.Record(level, text, subject, detail, groupKey); }
        catch (Exception ex)
        {
            LoggingService.Swallowed("Activity", ex, "recording an activity entry");
            return null;
        }
    }

    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _lock = new();
    private readonly List<ActivityEntry> _entries = new();
    private readonly Dictionary<string, AttentionItem> _states = new(StringComparer.Ordinal);
    private DateTime _lastViewedUtc;

    /// <summary>A new entry, raised after it is saved, on the recording thread.</summary>
    public event Action<ActivityEntry>? Recorded;
    /// <summary>Anything on the page changed: an entry, a state, or the page being opened. Any thread.</summary>
    public event Action? Changed;

    /// <param name="readOnly">For a capture or test run: reads the history, records in memory, writes nothing.</param>
    /// <param name="retentionDays">The window from settings; the default when not given.</param>
    public ActivityService(string baseDirectory, Func<DateTime>? utcNow = null, bool readOnly = false, Func<int>? retentionDays = null)
    {
        _path = Path.Combine(baseDirectory, FileName);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _readOnly = readOnly;
        _retentionDays = retentionDays ?? (() => DefaultRetentionDays);
        Load();
    }

    /// <summary>
    /// Rewrites part of a row's detail once an outcome it foretold is known: a Played row's Tools line
    /// says "closes after your last game" until the tool has closed, or hasn't. Nothing happens when the
    /// entry has gone (cleared, aged out) or the phrase isn't there any more.
    /// </summary>
    public void AmendDetail(ActivityEntry row, string from, string to)
    {
        bool changed = false;
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == row.Id);
            if (entry?.Detail is { } detail && detail.Contains(from, StringComparison.Ordinal))
            {
                entry.Detail = detail.Replace(from, to, StringComparison.Ordinal);
                changed = true;
                SaveLocked();
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Adds a note to one line of a Played row's detail ("Game: ..."), or adds the line, for something
    /// learned after the row was written. Nothing happens when the entry has gone.
    /// </summary>
    public void AppendToLine(ActivityEntry row, string label, string text)
    {
        bool changed = false;
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == row.Id);
            if (entry == null) return;
            var lines = (entry.Detail ?? string.Empty).Split('\n').Where(l => l.Length > 0).ToList();
            int at = lines.FindIndex(l => l.StartsWith(label + ": ", StringComparison.Ordinal));
            if (at >= 0)
            {
                if (lines[at].Contains(text, StringComparison.Ordinal)) return;
                lines[at] = lines[at].TrimEnd('.') + " · " + text + ".";
            }
            else
            {
                // In the row's order: a Game line sits after Profile, before Scripts and Tools.
                int order = Array.IndexOf(LineOrder, label);
                int insertAt = lines.Count;
                for (int i = 0; i < lines.Count; i++)
                {
                    int o = Array.IndexOf(LineOrder, lines[i].Split(':')[0]);
                    if (o > order) { insertAt = i; break; }
                }
                lines.Insert(insertAt, $"{label}: {text}.");
            }
            entry.Detail = string.Join("\n", lines);
            changed = true;
            SaveLocked();
        }
        if (changed) Changed?.Invoke();
    }

    private static readonly string[] LineOrder = ["Played", LaunchRecord.Profile, "Skipped", "Put back", LaunchRecord.Game, LaunchRecord.Scripts, LaunchRecord.Tools];

    /// <summary>The window changed: anything now outside it goes, as it would on the next record.</summary>
    public void ApplyRetention()
    {
        bool changed;
        lock (_lock)
        {
            int before = _entries.Count;
            PruneLocked();
            changed = _entries.Count != before;
            if (changed) SaveLocked();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Clear history: empties Recent. Needs Attention is left alone - those are live problems with a
    /// fix to apply, not history - and nothing is recorded about the clearing, so the page is empty.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return;
            LoggingService.Info("Activity", $"Activity & History cleared: {_entries.Count} entries.");
            _entries.Clear();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    private readonly bool _readOnly;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActivityEntry> Entries
    {
        get { lock (_lock) return _entries.OrderByDescending(e => e.TimeUtc).ToList(); }
    }

    public IReadOnlyList<AttentionItem> States
    {
        get { lock (_lock) return _states.Values.OrderByDescending(s => s.IsCritical).ThenBy(s => s.Text, StringComparer.CurrentCulture).ToList(); }
    }

    public DateTime LastViewedUtc
    {
        get { lock (_lock) return _lastViewedUtc; }
    }

    /// <summary>Problems and critical entries recorded since the page was last opened, one per group.</summary>
    public int UnseenProblemCount
    {
        get
        {
            lock (_lock)
            {
                return _entries.Where(e => IsUnseenProblemLocked(e))
                               .Select(e => e.GroupKey).Distinct(StringComparer.Ordinal).Count();
            }
        }
    }

    /// <summary>What lights the bell's dot, the page header's count and the tray's "need attention"
    /// row: current states plus problems not yet seen.</summary>
    public int AttentionCount
    {
        get
        {
            lock (_lock)
            {
                // A state and the entry that announced it share a key, and are one thing to look at.
                int unseen = _entries.Where(e => IsUnseenProblemLocked(e))
                                     .Select(e => e.GroupKey).Distinct(StringComparer.Ordinal)
                                     .Count(k => !_states.ContainsKey(k));
                return _states.Count + unseen;
            }
        }
    }

    /// <summary>A problem since the page was last opened, and not already put right.</summary>
    private bool IsUnseenProblemLocked(ActivityEntry e) =>
        e.Level >= ActivityLevel.Problem && e.TimeUtc > _lastViewedUtc && e.FixedUtc == null;

    public ActivityEntry Record(ActivityLevel level, string text, string? subject = null, string? detail = null, string? groupKey = null)
    {
        var entry = new ActivityEntry
        {
            TimeUtc = _utcNow(),
            Level = level,
            Text = text.Trim(),
            Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(),
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            GroupKey = string.IsNullOrWhiteSpace(groupKey) ? $"{level}|{text.Trim()}" : groupKey,
        };

        lock (_lock)
        {
            _entries.Add(entry);
            PruneLocked();
            SaveLocked();
        }
        LoggingService.Verbose("Activity", $"{level}: {entry.Text}");
        Recorded?.Invoke(entry);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Adds or replaces the state with this key.</summary>
    public void SetState(AttentionItem item)
    {
        lock (_lock) _states[item.Key] = item;
        Changed?.Invoke();
    }

    public void ClearState(string key)
    {
        bool removed;
        lock (_lock) removed = _states.Remove(key);
        if (removed) Changed?.Invoke();
    }

    /// <summary>
    /// What these Critical entries reported has been put right: each gets a FIXED badge, and no
    /// longer lights the bell. By entry, not by row: only what the fix actually covered, never an
    /// earlier failure that shares the row.
    /// </summary>
    public void MarkFixed(IEnumerable<string> entryIds)
    {
        var ids = entryIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;
        bool any = false;
        lock (_lock)
        {
            DateTime now = _utcNow();
            foreach (var e in _entries.Where(e => ids.Contains(e.Id) && e.Level == ActivityLevel.Critical && e.FixedUtc == null))
            {
                e.FixedUtc = now;
                any = true;
            }
            if (any) SaveLocked();
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>The page was opened: everything recorded so far counts as seen.</summary>
    public void MarkViewed()
    {
        lock (_lock)
        {
            _lastViewedUtc = _utcNow();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Rows for the page, newest first: entries with the same group key become one row.</summary>
    public static IReadOnlyList<ActivityGroup> Group(IEnumerable<ActivityEntry> entries)
    {
        return entries
            .GroupBy(e => e.GroupKey, StringComparer.Ordinal)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(e => e.TimeUtc).ToList();
                var critical = ordered.Where(e => e.Level == ActivityLevel.Critical).ToList();
                bool isFixed = critical.Count > 0 && critical.All(e => e.FixedUtc != null);
                return new ActivityGroup(g.Key, ordered[0], ordered.Select(e => e.TimeUtc).ToList(), isFixed);
            })
            .OrderByDescending(g => g.Latest.TimeUtc)
            .ToList();
    }

    /// <summary>The "Recent problems" section of a diagnostic report: problems and critical entries
    /// from the last <paramref name="days"/> days, grouped, newest first, one line each.</summary>
    public IReadOnlyList<string> RecentProblemLines(int days = 30)
    {
        DateTime since = _utcNow().AddDays(-days);
        var problems = Entries.Where(e => e.Level >= ActivityLevel.Problem && e.TimeUtc >= since);
        return Group(problems)
            .Select(g => $"{g.Latest.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {g.Latest.Level}: {g.Latest.Text}"
                         + (g.Count > 1 ? $" ({g.Count} times)" : string.Empty)
                         + (g.Fixed ? " - fixed with Restore Previous" : string.Empty))
            .ToList();
    }

    private void PruneLocked()
    {
        DateTime cutoff = _utcNow().AddDays(-RetentionDays);  // the window as set now
        _entries.RemoveAll(e => e.TimeUtc < cutoff);
        if (_entries.Count > MaxEntries)
        {
            // Oldest Activity first, then oldest Change, then oldest Problem; Critical last.
            var drop = _entries.OrderBy(e => e.Level).ThenBy(e => e.TimeUtc).Take(_entries.Count - MaxEntries).ToHashSet();
            _entries.RemoveAll(drop.Contains);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.ActivityLogFile);
            if (file == null) return;
            lock (_lock)
            {
                _lastViewedUtc = file.LastViewedUtc;
                _entries.AddRange(file.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Text)));
                foreach (var e in _entries.Where(e => string.IsNullOrWhiteSpace(e.GroupKey)))
                {
                    e.GroupKey = $"{e.Level}|{e.Text}";
                }
                PruneLocked();
            }
        }
        catch (Exception ex)
        {
            // An unreadable history costs only the history; the app starts a new one.
            LoggingService.Warn("Activity", $"Could not read {_path}, starting a new history: {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        if (_readOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var file = new ActivityLogFile { LastViewedUtc = _lastViewedUtc, Entries = _entries.OrderBy(e => e.TimeUtc).ToList() };
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, AppJsonContext.Default.ActivityLogFile));
            StorageService.SafeReplaceFile(temp, _path);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Activity", $"Could not save {_path}: {ex.Message}");
        }
    }
}
