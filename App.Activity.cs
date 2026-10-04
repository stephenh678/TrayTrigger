using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger;

/// <summary>
/// Activity &amp; History's ties to the tray: the notification after the last game exits, the
/// tooltip line and the tray menu row while something needs attention, opening the page from a
/// notification, and the startup check for System tweaks Windows changed back. The history itself
/// is <see cref="ActivityService"/>; the page is <see cref="ActivityViewModel"/>.
/// </summary>
public partial class App
{
    /// <summary>How long a burst of entries is gathered into one notification.</summary>
    private static readonly TimeSpan ActivityToastGather = TimeSpan.FromSeconds(3);

    /// <summary>How long after startup the tweak check waits, so it doesn't slow the start.</summary>
    private static readonly TimeSpan StartupTweakCheckDelay = TimeSpan.FromSeconds(60);

    private readonly List<ActivityEntry> _pendingActivityToast = new();
    private readonly object _pendingActivityToastLock = new();
    private DispatcherTimer? _activityToastTimer;
    /// <summary>The last notification shown was Activity &amp; History's, so clicking it opens the page.</summary>
    private bool _lastToastOpensActivity;
    private bool _startupTweakCheckPending;

    /// <summary>Every tray notification goes through here, so a click knows whose it was.</summary>
    private void ShowTrayNotification(string title, string message) => ShowTrayNotification(title, message, opensActivity: false);

    private void ShowTrayNotification(string title, string message, bool opensActivity)
    {
        _lastToastOpensActivity = opensActivity;
        _trayIcon?.ShowNotification(title, message);
    }

    /// <summary>
    /// Notes an update once in Activity &amp; History. A first run (nothing recorded) is only
    /// remembered. Saved with the rest of the settings at startup or exit.
    /// </summary>
    private void NoteVersionChange(AppSettings settings)
    {
        string current = UpdateService.CurrentVersionDisplay;
        string previous = settings.LastRunVersion;
        if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return;
        settings.LastRunVersion = current;
        // Saved now rather than at exit, so a run that doesn't end cleanly can't note it twice.
        _storageService.SaveSettings(settings, source: "App.NoteVersionChange");
        if (string.IsNullOrWhiteSpace(previous)) return;
        // Only forwards: going back from a beta to the release isn't an update.
        if (SemanticVersion.TryParse(previous) is { } was && was.CompareTo(UpdateService.CurrentSemVer) >= 0) return;
        ActivityService.Add(ActivityLevel.Activity, $"Updated to {current}",
            detail: $"From {previous}. What's new is on the Releases page, from About.", groupKey: $"app.updated|{current}");
    }

    /// <summary>Called once the tray icon exists.</summary>
    private void InitializeActivityNotifications()
    {
        if (ActivityService.Current is not { } activity) return;

        _mainViewModel.IsWindowActive = () => _mainWindow is { IsVisible: true, IsActive: true };

        activity.Recorded += OnActivityRecorded;
        // Queued, never waited on: an entry can be recorded by a thread holding a service's lock
        // (closing a tool, ending a profile), and the menu rebuild runs on the UI thread. The menu
        // is rebuilt only when its attention row would change, not for every change recorded.
        int menuAttention = activity.AttentionCount;
        activity.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateTrayToolTip();
            int now = activity.AttentionCount;
            if (now == menuAttention) return;
            menuAttention = now;
            UpdateTrayContextMenu();
        });

        // Problems recorded during startup, before this subscription - crash recovery that couldn't
        // put a setting back, an edited script moved aside - get their notification too.
        DateTime startedUtc;
        try { startedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch (Exception ex) { LoggingService.Swallowed("Activity", ex, "reading when this run started"); startedUtc = DateTime.UtcNow.AddMinutes(-1); }
        foreach (var early in activity.Entries.Where(e => e.TimeUtc >= startedUtc && e.TimeUtc > activity.LastViewedUtc).Reverse())
        {
            OnActivityRecorded(early);
        }
        _launcherService.SessionEnded += _ => Dispatcher.BeginInvoke(OnSessionEndedForActivity);

        if (_trayIcon != null)
        {
            _trayIcon.TrayBalloonTipClicked += (_, _) =>
            {
                if (!_lastToastOpensActivity) return;
                _lastToastOpensActivity = false;
                OpenSection(NavSection.Activity);
            };
        }

        // Tweaks TrayTrigger applied that Windows has changed back since: read once, a minute in,
        // and only when it has applied some - never while a game is running.
        if (_mainViewModel.Settings.TweaksAppliedByTrayTrigger.Count > 0)
        {
            _startupTweakCheckPending = true;
            var timer = new DispatcherTimer { Interval = StartupTweakCheckDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RunStartupTweakCheckIfIdle();
            };
            timer.Start();
        }
    }

    private void OnSessionEndedForActivity()
    {
        if (_startupTweakCheckPending) RunStartupTweakCheckIfIdle();
        bool pending;
        lock (_pendingActivityToastLock) pending = _pendingActivityToast.Count > 0;
        if (pending) ScheduleActivityToast();
    }

    private void RunStartupTweakCheckIfIdle()
    {
        if (!_startupTweakCheckPending || _isShuttingDown) return;
        if ((_launcherService?.GetActiveSessions().Count ?? 0) > 0) return; // tried again when the game exits
        _startupTweakCheckPending = false;
        _ = RunStartupTweakCheckAsync();
    }

    /// <summary>Nothing awaits the check, so a failure is logged here or it would vanish.</summary>
    private async System.Threading.Tasks.Task RunStartupTweakCheckAsync()
    {
        try { await _mainViewModel.SystemVM.CheckAppliedTweaksAsync(); }
        catch (Exception ex) { LoggingService.Warn("Activity", $"Checking for System tweaks changed back since they were applied failed: {ex.Message}"); }
    }

    /// <summary>
    /// A problem, or a scan's finds when "Notify me when a scan finds new games" is on, is held for
    /// one notification once no game is running. Not a data file that couldn't be read: the startup
    /// dialog is already saying so. Any thread.
    /// </summary>
    private void OnActivityRecorded(ActivityEntry entry)
    {
        bool notify = (entry.Level >= ActivityLevel.Problem && !entry.GroupKey.StartsWith(MainViewModel.DataFileActivityKeyPrefix, StringComparison.Ordinal))
                      || (entry.GroupKey == ImportCoordinator.ScanFoundActivityKey && _mainViewModel?.Settings.NotifyWhenScanAddsGames == true);
        if (!notify) return;
        lock (_pendingActivityToastLock) _pendingActivityToast.Add(entry);
        ScheduleActivityToast();
    }

    private void ScheduleActivityToast()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isShuttingDown) return;
            _activityToastTimer ??= CreateActivityToastTimer();
            _activityToastTimer.Stop();
            _activityToastTimer.Start();
        });
    }

    private DispatcherTimer CreateActivityToastTimer()
    {
        var timer = new DispatcherTimer { Interval = ActivityToastGather };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Never during a game: the session's end schedules this again.
            if ((_launcherService?.GetActiveSessions().Count ?? 0) > 0) return;
            List<ActivityEntry> entries;
            lock (_pendingActivityToastLock)
            {
                entries = _pendingActivityToast.ToList();
                _pendingActivityToast.Clear();
            }
            if (entries.Count == 0) return;
            var (title, message) = ComposeActivityToast(entries);
            ShowTrayNotification(title, message, opensActivity: true);
        };
        return timer;
    }

    /// <summary>One notification for a burst: the line itself for one, a count and the worst for more.</summary>
    internal static (string Title, string Message) ComposeActivityToast(IReadOnlyList<ActivityEntry> entries)
    {
        var groups = entries.GroupBy(e => e.GroupKey).Select(g => g.Last()).ToList();
        if (groups.Count == 1)
        {
            var only = groups[0];
            return ("TrayTrigger", only.Level >= ActivityLevel.Problem ? $"{only.Text}. Click to open Activity & History." : only.Text);
        }
        var worst = groups.OrderByDescending(e => e.Level).ThenByDescending(e => e.TimeUtc).First();
        int problems = groups.Count(e => e.Level >= ActivityLevel.Problem);
        string headline = problems == groups.Count
            ? $"{groups.Count} things need attention"
            : $"{groups.Count} things happened while you played";
        return ("TrayTrigger", $"{headline}: {worst.Text}. Click to open Activity & History.");
    }

    /// <summary>The tray menu's first row while something needs attention; opens the page.</summary>
    private MenuItemSpec? AttentionMenuRow()
    {
        int count = ActivityService.Current?.AttentionCount ?? 0;
        if (count == 0) return null;
        return new MenuItemSpec(count == 1 ? "1 thing needs attention" : $"{count} things need attention",
            () => OpenSection(NavSection.Activity));
    }

    /// <summary>A tray menu row before it becomes a <see cref="System.Windows.Controls.MenuItem"/>.</summary>
    private sealed record MenuItemSpec(string Header, Action OnClick);
}
