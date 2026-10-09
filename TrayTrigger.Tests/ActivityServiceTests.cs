using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Activity &amp; History: what is kept, how repeats group, what lights the bell, and what the page
/// shows. Each test owns its own <see cref="ActivityService"/> in a temp folder; none touches
/// <see cref="ActivityService.Current"/>, which other test classes may be recording into.
/// </summary>
public class ActivityServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TrayTriggerActivity_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 4, 20, 0, 0, DateTimeKind.Utc);

    public ActivityServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ActivityService Service(bool readOnly = false) => new(_dir, () => _now, readOnly);

    [Fact]
    public void Retention_FollowsTheSetting_AndAppliesAtOnce()
    {
        int window = 30;
        var service = new ActivityService(_dir, () => _now, retentionDays: () => window);
        service.Record(ActivityLevel.Activity, "a month ago");
        _now = _now.AddDays(31);
        service.Record(ActivityLevel.Activity, "today");
        Assert.DoesNotContain(service.Entries, e => e.Text == "a month ago");

        // Widening keeps what's there; narrowing below what's kept drops it as soon as it's applied.
        _now = _now.AddDays(40);
        window = 365;
        service.ApplyRetention();
        Assert.Contains(service.Entries, e => e.Text == "today");
        window = 30;
        service.ApplyRetention();
        Assert.Empty(service.Entries);
        Assert.Equal(30, service.RetentionDays);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(90, 90)]
    [InlineData(365, 365)]
    [InlineData(0, 90)]
    [InlineData(45, 90)]
    [InlineData(-1, 90)]
    public void RetentionDays_OutsideTheChoices_IsTheDefault(int stored, int expected) =>
        Assert.Equal(expected, ActivityService.NormalizeRetentionDays(stored));

    [Fact]
    public void AmendDetail_RewritesThePhrase_SavesIt_AndIgnoresAMissingEntryOrPhrase()
    {
        var service = Service();
        var row = service.Record(ActivityLevel.Change, "Played Hades · 48m", subject: "Hades", detail: "From 18:00 to 18:48.\nTools: SimHub started, closes after your last game.");
        int changed = 0;
        service.Changed += () => changed++;

        service.AmendDetail(row, "SimHub started, closes after your last game", "SimHub started, closed after");

        Assert.Equal("From 18:00 to 18:48.\nTools: SimHub started, closed after.", Service().Entries.Single().Detail);
        Assert.Equal(1, changed);
        // Already amended, or gone: nothing to do, nobody told.
        service.AmendDetail(row, "SimHub started, closes after your last game", "SimHub started, closed after");
        service.AmendDetail(new ActivityEntry { Id = "missing" }, "a", "b");
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Clear_EmptiesRecent_KeepsNeedsAttention_AndSurvivesARestart()
    {
        var service = Service();
        service.Record(ActivityLevel.Change, "Played Hades · 48m", subject: "Hades");
        service.Record(ActivityLevel.Problem, "A script failed");
        service.SetState(new AttentionItem("profile.unrestored", "Power plan wasn't put back", "Restore Previous puts it back.", "Restore Previous", () => { }, IsCritical: true));
        int changed = 0;
        service.Changed += () => changed++;

        service.Clear();

        Assert.Empty(service.Entries);
        Assert.Single(service.States);
        Assert.Equal(1, changed);
        Assert.Empty(Service().Entries);
        // Nothing to clear: no save, no notice.
        service.Clear();
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ClearHistory_AsksFirst_AndOnlyOffersItWhenThereIsSomething()
    {
        var service = Service();
        var page = new ActivityViewModel(service);
        Assert.False(page.ClearHistoryCommand.CanExecute(null));

        service.Record(ActivityLevel.Activity, "Played Hades · 48m", subject: "Hades");
        Assert.True(page.ClearHistoryCommand.CanExecute(null));

        page.ConfirmClear = () => false;
        page.ClearHistoryCommand.Execute(null);
        Assert.Single(service.Entries);

        page.ConfirmClear = () => true;
        page.ClearHistoryCommand.Execute(null);
        Assert.Empty(service.Entries);
        Assert.True(page.ShowNothingYet);
    }

    [Fact]
    public void KeepDropdown_SavesTheWindow_PrunesAtOnce_AndTheHeadingFollows()
    {
        var settings = new AppSettings();
        int saved = 0;
        var service = new ActivityService(_dir, () => _now, retentionDays: () => settings.ActivityRetentionDays);
        service.Record(ActivityLevel.Activity, "six weeks ago");
        _now = _now.AddDays(45);
        service.Record(ActivityLevel.Activity, "today");
        var page = new ActivityViewModel(service, settings: settings, saveSettings: () => saved++);
        Assert.Equal(ActivityViewModel.Keep90Days, page.RetentionOption);
        Assert.Equal("·  last 90 days", page.RecentWindowLabel);

        page.RetentionOption = ActivityViewModel.Keep30Days;

        Assert.Equal(30, settings.ActivityRetentionDays);
        Assert.Equal(1, saved);
        Assert.Equal("·  last 30 days", page.RecentWindowLabel);
        Assert.Equal(["today"], service.Entries.Select(e => e.Text));

        page.RetentionOption = ActivityViewModel.KeepAYear;
        Assert.Equal(365, settings.ActivityRetentionDays);
        Assert.Equal("·  last year", page.RecentWindowLabel);
        // The same choice again: nothing to save.
        page.RetentionOption = ActivityViewModel.KeepAYear;
        Assert.Equal(2, saved);
    }

    [Fact]
    public void KeepDropdown_ReadsAHandEditedValue_AsTheDefault()
    {
        var page = new ActivityViewModel(Service(), settings: new AppSettings { ActivityRetentionDays = 45 });
        Assert.Equal(ActivityViewModel.Keep90Days, page.RetentionOption);
    }

    [Fact]
    public void Entries_AreKept_AndComeBackNewestFirst_AfterARestart()
    {
        var service = Service();
        service.Record(ActivityLevel.Change, "Elden Ring: Aggressive profile applied, then put back", subject: "Elden Ring");
        _now = _now.AddMinutes(5);
        service.Record(ActivityLevel.Problem, "Elden Ring: post-exit script failed (exit code 1)", subject: "Elden Ring", detail: "close-apps.ps1");

        var reloaded = Service();

        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Equal("Elden Ring: post-exit script failed (exit code 1)", reloaded.Entries[0].Text);
        Assert.Equal("close-apps.ps1", reloaded.Entries[0].Detail);
        Assert.True(File.Exists(Path.Combine(_dir, ActivityService.FileName)));
    }

    [Fact]
    public void TheSameThingAgain_IsOneRow_WithEveryTime()
    {
        var service = Service();
        for (int i = 0; i < 3; i++)
        {
            service.Record(ActivityLevel.Problem, $"Elden Ring: post-exit script failed (exit code {i + 1})", groupKey: "script.postexit|er");
            _now = _now.AddDays(1);
        }
        service.Record(ActivityLevel.Activity, "Updated to 1.4.9");

        var groups = ActivityService.Group(service.Entries);

        Assert.Equal(2, groups.Count);
        var script = Assert.Single(groups, g => g.Key == "script.postexit|er");
        Assert.Equal(3, script.Count);
        Assert.Equal("Elden Ring: post-exit script failed (exit code 3)", script.Latest.Text);
    }

    [Fact]
    public void Problems_LightTheBell_UntilThePageIsOpened_ChangesNever()
    {
        var service = Service();
        service.Record(ActivityLevel.Change, "MSI Afterburner started with your game and closed after it");
        Assert.Equal(0, service.AttentionCount);

        _now = _now.AddMinutes(1);
        service.Record(ActivityLevel.Problem, "SimHub didn't start with Elden Ring");
        Assert.Equal(1, service.AttentionCount);

        _now = _now.AddMinutes(1);
        service.MarkViewed();
        Assert.Equal(0, service.AttentionCount);
        // Seen is remembered across a restart, so the dot doesn't come back on its own.
        Assert.Equal(0, Service().AttentionCount);
    }

    /// <summary>A live state and the entry that announced it are one thing to look at, not two.</summary>
    [Fact]
    public void AState_AndItsEntry_CountOnce_AndTheStateOutlivesBeingSeen()
    {
        var service = Service();
        service.Record(ActivityLevel.Critical, "Elden Ring: couldn't put back the power plan", groupKey: "profile.unrestored");
        service.SetState(new AttentionItem("profile.unrestored", "The power plan wasn't put back", "...", "Restore Previous", () => { }, IsCritical: true));

        Assert.Equal(1, service.AttentionCount);
        _now = _now.AddMinutes(1);
        service.MarkViewed();
        Assert.Equal(1, service.AttentionCount);

        service.ClearState("profile.unrestored");
        Assert.Equal(0, service.AttentionCount);
    }

    [Fact]
    public void OldEntries_GoByThemselves_AndPastTheCap_ActivityGoesFirst_CriticalLast()
    {
        var service = Service();
        service.Record(ActivityLevel.Activity, "ancient");
        _now = _now.AddDays(ActivityService.DefaultRetentionDays + 1);
        service.Record(ActivityLevel.Critical, "keep me");
        Assert.DoesNotContain(service.Entries, e => e.Text == "ancient");

        for (int i = 0; i < ActivityService.MaxEntries; i++)
        {
            _now = _now.AddSeconds(1);
            service.Record(ActivityLevel.Activity, $"filler {i}");
        }

        Assert.Equal(ActivityService.MaxEntries, service.Entries.Count);
        Assert.Contains(service.Entries, e => e.Text == "keep me");
        Assert.DoesNotContain(service.Entries, e => e.Text == "filler 0");
    }

    [Fact]
    public void ReadOnly_RecordsInMemory_AndWritesNothing()
    {
        var service = Service(readOnly: true);
        service.Record(ActivityLevel.Problem, "a capture run's problem");
        service.MarkViewed();

        Assert.Single(service.Entries);
        Assert.False(File.Exists(Path.Combine(_dir, ActivityService.FileName)));
    }

    [Fact]
    public void AnUnreadableHistory_StartsANewOne()
    {
        File.WriteAllText(Path.Combine(_dir, ActivityService.FileName), "{ not json");
        var service = Service();
        Assert.Empty(service.Entries);
        service.Record(ActivityLevel.Activity, "fresh start");
        Assert.Single(Service().Entries);
    }

    [Fact]
    public void RecentProblemLines_AreGrouped_AndLeaveOutChanges()
    {
        var service = Service();
        service.Record(ActivityLevel.Change, "Applied the Performance Preset (5 settings)");
        service.Record(ActivityLevel.Problem, "SimHub couldn't be closed after your game", groupKey: "tool.close|simhub");
        _now = _now.AddMinutes(1);
        service.Record(ActivityLevel.Problem, "SimHub couldn't be closed after your game", groupKey: "tool.close|simhub");

        var line = Assert.Single(service.RecentProblemLines());
        Assert.Contains("SimHub couldn't be closed after your game (2 times)", line);
    }

    // ------------------------------------------------------------------ the page

    [Fact]
    public void Page_Tabs_AndSearch_NarrowTheRows()
    {
        var service = Service();
        service.Record(ActivityLevel.Problem, "Elden Ring: post-exit script failed (exit code 1)", subject: "Elden Ring");
        service.Record(ActivityLevel.Change, "Hades: Optimized profile applied, then put back", subject: "Hades");
        service.Record(ActivityLevel.Activity, "The startup scan found 3 new games");
        var page = new ActivityViewModel(service);

        Assert.Equal(3, page.Groups.Count);
        page.Tab = ActivityTab.Problems;
        Assert.Equal("Elden Ring: post-exit script failed (exit code 1)", Assert.Single(page.Groups).Text);
        page.Tab = ActivityTab.Changes;
        Assert.Single(page.Groups);
        page.Tab = ActivityTab.All;
        page.SearchText = "hades";
        Assert.Equal("Hades: Optimized profile applied, then put back", Assert.Single(page.Groups).Text);
        page.SearchText = "nothing like this";
        Assert.True(page.ShowNoMatches);
    }

    [Fact]
    public void Page_HeaderStatus_AndTags()
    {
        var service = Service();
        var page = new ActivityViewModel(service);
        Assert.Equal("No actions needed", page.HeaderStatus);
        Assert.True(page.ShowNothingYet);

        service.Record(ActivityLevel.Problem, "SimHub didn't start with Elden Ring", groupKey: "tool.start|simhub");
        service.Record(ActivityLevel.Problem, "SimHub didn't start with Elden Ring", groupKey: "tool.start|simhub");
        service.Record(ActivityLevel.Change, "MSI Afterburner started with your game and closed after it", groupKey: "tool.closed|ab");
        service.Record(ActivityLevel.Change, "MSI Afterburner started with your game and closed after it", groupKey: "tool.closed|ab");
        page.Refresh();

        Assert.Equal("1 needs attention", page.HeaderStatus);
        var problem = page.Groups.Single(g => g.IsProblem);
        Assert.Equal("2 TIMES", problem.CountTag);
        Assert.Equal(string.Empty, problem.QuietCount);
        var change = page.Groups.Single(g => !g.IsProblem);
        Assert.Equal(string.Empty, change.CountTag);
        Assert.Equal("· 2 times", change.QuietCount);
        Assert.Contains("SimHub didn't start with Elden Ring", problem.DetailsText());
    }

    /// <summary>A row the user opened stays open when something new arrives.</summary>
    [Fact]
    public void Page_AnOpenRow_StaysOpen_WhenTheListRefreshes()
    {
        var service = Service();
        service.Record(ActivityLevel.Problem, "SimHub didn't start with Elden Ring", groupKey: "tool.start|simhub");
        var page = new ActivityViewModel(service);
        page.Groups.Single().IsExpanded = true;

        service.Record(ActivityLevel.Activity, "Updated to 1.4.9");

        Assert.True(page.Groups.Single(g => g.Group.Key == "tool.start|simhub").IsExpanded);
        Assert.False(page.Groups.Single(g => g.Text == "Updated to 1.4.9").IsExpanded);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    public void FormatWhen_SaysTodayAndYesterday(int daysAgo, string expectedStart)
    {
        var nowLocal = new DateTime(2026, 10, 4, 21, 0, 0, DateTimeKind.Local);
        var then = nowLocal.AddDays(-daysAgo).AddHours(-1).ToUniversalTime();
        Assert.StartsWith(expectedStart, ActivityViewModel.FormatWhen(then, nowLocal));
    }

    /// <summary>
    /// The date headers, with "now" a Thursday evening (2026-10-08) and weeks starting on Sunday,
    /// so the week began on the 4th and last week ran from Sep 27 to Oct 3.
    /// </summary>
    [Theory]
    [InlineData(2026, 10, 8, "Today")]
    [InlineData(2026, 10, 7, "Yesterday")]
    [InlineData(2026, 10, 6, "Earlier this week")]
    [InlineData(2026, 10, 4, "Earlier this week")]
    [InlineData(2026, 10, 3, "Last week")]
    [InlineData(2026, 9, 27, "Last week")]
    [InlineData(2026, 9, 26, "Last month")]
    [InlineData(2026, 9, 1, "Last month")]
    [InlineData(2026, 8, 31, "Older")]
    public void SectionFor_UsesCalendarHeaders(int y, int m, int d, string expected)
    {
        var nowLocal = new DateTime(2026, 10, 8, 21, 0, 0, DateTimeKind.Local);
        var then = new DateTime(y, m, d, 10, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(expected, ActivityViewModel.SectionFor(then, nowLocal, DayOfWeek.Sunday));
    }

    /// <summary>Later in the month there is room for "Earlier this month" between last week and last month.</summary>
    [Fact]
    public void SectionFor_EarlierThisMonth_SitsBetweenLastWeekAndLastMonth()
    {
        var nowLocal = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Local); // a Thursday; week began Oct 18
        string At(int d) => ActivityViewModel.SectionFor(new DateTime(2026, 10, d, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), nowLocal, DayOfWeek.Sunday);
        Assert.Equal("Last week", At(11));
        Assert.Equal("Earlier this month", At(10));
        Assert.Equal("Earlier this month", At(1));
        Assert.Equal("Last month", ActivityViewModel.SectionFor(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), nowLocal, DayOfWeek.Sunday));
    }

    /// <summary>On the first day of a week, Yesterday wins over Last week, and Earlier this week is empty.</summary>
    [Fact]
    public void SectionFor_OnAMonday_YesterdayBeatsLastWeek()
    {
        var nowLocal = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Local); // Monday, Monday-first week
        string At(int d) => ActivityViewModel.SectionFor(new DateTime(2026, 10, d, 12, 0, 0, DateTimeKind.Local).ToUniversalTime(), nowLocal, DayOfWeek.Monday);
        Assert.Equal("Today", At(5));
        Assert.Equal("Yesterday", At(4));
        Assert.Equal("Last week", At(3));
        Assert.Equal("Last week", At(1));
    }

    /// <summary>The page's sections hold the rows in order, newest section first.</summary>
    [Fact]
    public void Page_Sections_GroupTheRowsByDay()
    {
        var service = new ActivityService(_dir, () => _now);
        _now = DateTime.UtcNow.AddDays(-9);
        service.Record(ActivityLevel.Activity, "Played Hades · 1h");
        _now = DateTime.UtcNow.AddHours(-1);
        service.Record(ActivityLevel.Problem, "SimHub didn't start");
        var page = new ActivityViewModel(service);

        Assert.Equal(2, page.Sections.Count);
        Assert.Equal("TODAY", page.Sections[0].Label);
        Assert.Equal("SimHub didn't start", page.Sections[0].Rows.Single().Text);
        Assert.Equal("Played Hades · 1h", page.Sections[1].Rows.Single().Text);
        Assert.NotEqual("TODAY", page.Sections[1].Label);
    }

    [Fact]
    public void FormatWhen_OlderThanAYear_ShowsTheDateOnly()
    {
        var nowLocal = new DateTime(2026, 10, 4, 21, 0, 0, DateTimeKind.Local);
        var then = new DateTime(2025, 3, 2, 10, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Contains("2025", ActivityViewModel.FormatWhen(then, nowLocal));
    }

    /// <summary>The tray says it first, so a long tooltip never cuts it off.</summary>
    [Fact]
    public void TrayToolTip_LeadsWithWhatNeedsAttention()
    {
        Assert.StartsWith("2 things need attention\nTrayTrigger", App.BuildTrayToolTipText([], DateTime.Now, null, attentionCount: 2));
        Assert.StartsWith("1 thing needs attention\n", App.BuildTrayToolTipText([], DateTime.Now, null, attentionCount: 1));
        Assert.Equal("TrayTrigger - Game Launcher", App.BuildTrayToolTipText([], DateTime.Now, null, attentionCount: 0));
    }

    // ------------------------------------------------------------------ the notification

    [Fact]
    public void Notification_ForOne_IsTheLineItself_ForMore_ACountAndTheWorst()
    {
        var script = new ActivityEntry { Level = ActivityLevel.Problem, Text = "Elden Ring's post-exit script failed", GroupKey = "a" };
        var power = new ActivityEntry { Level = ActivityLevel.Critical, Text = "Elden Ring: couldn't put back the power plan", GroupKey = "b" };

        var (_, one) = App.ComposeActivityToast([script]);
        Assert.StartsWith("Elden Ring's post-exit script failed", one);

        var (_, two) = App.ComposeActivityToast([script, power]);
        Assert.StartsWith("2 things need attention: Elden Ring: couldn't put back the power plan", two);

        // The same problem twice in a burst is still one thing.
        var (_, same) = App.ComposeActivityToast([script, script]);
        Assert.StartsWith("Elden Ring's post-exit script failed", same);
    }
    // ------------------------------------------------------------------ games added

    [Fact]
    public void AddedGames_NameUpToThree_ThenCount_PlatformOnlyWhenOne()
    {
        GameEntry Steam(string n) => new() { Name = n, IsSteamGame = true };

        Assert.Equal("Added Elden Ring and Hades (Steam)",
            ImportCoordinator.DescribeAddedGames([Steam("Elden Ring"), Steam("Hades")]).Text);

        var mixed = ImportCoordinator.DescribeAddedGames([Steam("Hades"), new GameEntry { Name = "Doom" }]);
        Assert.Equal("Added Hades and Doom", mixed.Text);
        Assert.Contains("Doom - Local", mixed.Detail);

        var many = ImportCoordinator.DescribeAddedGames([Steam("A"), Steam("B"), Steam("C"), Steam("D")]);
        Assert.Equal("Added 4 games (Steam)", many.Text);
        Assert.Contains("D - Steam", many.Detail);
    }
    // ------------------------------------------------------------------ play sessions

    [Fact]
    public void PlaySession_TitleIsTheGameAndTheTimePlayed()
    {
        var start = new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc);

        Assert.Equal("Played Hades · 48m", ProcessLauncherService.DescribePlaySession("Hades", 48, PerformanceProfileMode.Off, start, start.AddMinutes(48)).Text);
        Assert.Equal("Played Elden Ring · 2h 17m", ProcessLauncherService.DescribePlaySession("Elden Ring", 137, PerformanceProfileMode.Aggressive, start, start.AddMinutes(137)).Text);
        Assert.Equal("Played Doom · 2h", ProcessLauncherService.DescribePlaySession("Doom", 120, PerformanceProfileMode.Off, start, start).Text);
        Assert.Equal("Played Doom · under a minute", ProcessLauncherService.DescribePlaySession("Doom", 0, PerformanceProfileMode.Off, start, start).Text);
    }

    /// <summary>The detail is the record of the launch: one line per section, each only when it has something to say.</summary>
    [Fact]
    public void PlaySession_DetailIsTheLaunchRecord_OneLinePerSection()
    {
        var start = new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<(string, IReadOnlyList<string>)> record =
        [
            (LaunchRecord.Launch, ["launched through Steam", "Steam closed after"]),
            (LaunchRecord.Profile, ["Ultimate Performance power plan", "skipped: HDR, no HDR-capable display", "put back: everything"]),
            (LaunchRecord.Game, ["kept on the 8 performance cores (CPUs 0-15)", "DLSS 310.2.1 from NVIDIA"]),
            (LaunchRecord.Scripts, ["pre-launch close-apps.ps1 ran"]),
            (LaunchRecord.Tools, ["Discord closed, opened again after", "MSI Afterburner started, closed after"]),
        ];

        var (text, detail) = ProcessLauncherService.DescribePlaySession("Hades", 48, PerformanceProfileMode.Aggressive, start, start.AddMinutes(48), record);

        Assert.Equal("Played Hades · 48m", text);
        var lines = detail.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.StartsWith("Played: from ", lines[0]);
        Assert.EndsWith(" · launched through Steam · Steam closed after.", lines[0]);
        Assert.Equal("Profile: Aggressive · changed: Ultimate Performance power plan.", lines[1]);
        Assert.Equal("Skipped: HDR, no HDR-capable display.", lines[2]);
        Assert.Equal("Put back: everything.", lines[3]);
        Assert.Equal("Game: kept on the 8 performance cores (CPUs 0-15) · DLSS 310.2.1 from NVIDIA.", lines[4]);
        Assert.Equal("Scripts: pre-launch close-apps.ps1 ran.", lines[5]);
        Assert.Equal("Tools: Discord closed, opened again after · MSI Afterburner started, closed after.", lines[6]);
    }

    [Fact]
    public void PlaySession_WithNothingDone_SaysTheProfileWasOff_AndNoMore()
    {
        var start = new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc);

        var (_, detail) = ProcessLauncherService.DescribePlaySession("Hades", 48, PerformanceProfileMode.Off, start, start.AddMinutes(48),
            [(LaunchRecord.Launch, ["launched directly"])]);

        var lines = detail.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(" · launched directly.", lines[0]);
        Assert.Equal("Profile: Off · nothing changed.", lines[1]);

        // No times (recorded at shutdown) and no record: the profile line stands alone, and says nothing changed.
        Assert.Equal("Profile: Optimized · nothing changed.", ProcessLauncherService.DescribePlaySession("Hades", 48, PerformanceProfileMode.Optimized, default, default).Detail);
    }

    [Fact]
    public void DetailLines_SplitTheSectionLabelsOff_AndLeaveOtherDetailsWhole()
    {
        var lines = ActivityGroupViewModel.SplitDetail("Played: from 09:47 to 10:10 · launched through Steam.\nProfile: Off · nothing changed.\nTools: Discord closed.");
        Assert.Equal([("Played: ", "from 09:47 to 10:10 · launched through Steam."), ("Profile: ", "Off · nothing changed."), ("Tools: ", "Discord closed.")],
            lines.Select(l => (l.Label, l.Body)));
        Assert.Equal([("Put back: ", "everything.")], ActivityGroupViewModel.SplitDetail("Put back: everything.").Select(l => (l.Label, l.Body)));

        // A problem row's detail: no label, however it begins, and blank lines dropped.
        Assert.Equal([("", "close-apps.ps1. Test it from Edit Game; what it printed is in the log.")],
            ActivityGroupViewModel.SplitDetail("close-apps.ps1. Test it from Edit Game; what it printed is in the log.\n").Select(l => (l.Label, l.Body)));
        Assert.Equal([("", "Note: this isn't a section.")], ActivityGroupViewModel.SplitDetail("Note: this isn't a section.").Select(l => (l.Label, l.Body)));
        Assert.Empty(ActivityGroupViewModel.SplitDetail(null));
    }

    [Fact]
    public void AppendToLine_AddsToTheLine_OrAddsTheLineInOrder_Once()
    {
        var service = Service();
        var row = service.Record(ActivityLevel.Change, "Played Hades · 48m", subject: "Hades",
            detail: "Played: from 18:00 to 18:48.\nProfile: Optimized · changed: Ultimate Performance power plan.\nPut back: everything.\nTools: Discord closed.");

        service.AppendToLine(row, LaunchRecord.Game, "kept on the 8 performance cores");
        service.AppendToLine(row, LaunchRecord.Game, "DLSS 310.2.1 from NVIDIA");
        service.AppendToLine(row, LaunchRecord.Game, "DLSS 310.2.1 from NVIDIA");

        Assert.Equal("Played: from 18:00 to 18:48.\nProfile: Optimized · changed: Ultimate Performance power plan.\nPut back: everything.\nGame: kept on the 8 performance cores · DLSS 310.2.1 from NVIDIA.\nTools: Discord closed.",
            Service().Entries.Single().Detail);
        service.AppendToLine(new ActivityEntry { Id = "gone" }, LaunchRecord.Game, "x");
    }

    [Fact]
    public void LaunchRecord_ANoteAfterTheRecordWasTaken_GoesToTheLateHandler()
    {
        var late = new List<string>();
        var previous = LaunchRecord.LateNote;
        LaunchRecord.LateNote = (g, s, t) => late.Add($"{g}|{s}|{t}");
        try
        {
            LaunchRecord.Begin("late-g1");
            LaunchRecord.Note("late-g1", LaunchRecord.Game, "in time");
            LaunchRecord.Take("late-g1");
            LaunchRecord.Note("late-g1", LaunchRecord.Game, "kept on the cores");

            Assert.Equal(["late-g1|Game|kept on the cores"], late);
            Assert.Empty(LaunchRecord.Take("late-g1"));
        }
        finally
        {
            LaunchRecord.LateNote = previous;
        }
    }

    [Fact]
    public void LaunchRecord_CollectsNotesPerGame_InSectionOrder_AndIsTakenOnce()
    {
        LaunchRecord.Begin("record-g1");
        LaunchRecord.Note("record-g1", LaunchRecord.Tools, "Discord closed");
        LaunchRecord.Note("record-g1", LaunchRecord.Profile, "HDR on");
        LaunchRecord.Note("record-g1", LaunchRecord.Profile, "HDR on");   // once
        LaunchRecord.Note("record-g2", LaunchRecord.Profile, "someone else's");
        Assert.Equal(["HDR on"], LaunchRecord.Peek("record-g1", LaunchRecord.Profile));

        var record = LaunchRecord.Take("record-g1");

        Assert.Equal([LaunchRecord.Profile, LaunchRecord.Tools], record.Select(r => r.Section));
        Assert.Equal(["Discord closed"], record[1].Notes);
        Assert.Empty(LaunchRecord.Take("record-g1"));
        // A new launch starts the record afresh.
        LaunchRecord.Note("record-g2", LaunchRecord.Tools, "stale");
        LaunchRecord.Begin("record-g2");
        Assert.Empty(LaunchRecord.Take("record-g2"));
    }
    // ------------------------------------------------------------------ profile changes

    [Fact]
    public void ProfileChange_ForOneGame_IsGroupedPerGame_ForMany_OneLine()
    {
        var er = new GameEntry { Id = "er", Name = "Elden Ring" };
        var hades = new GameEntry { Id = "h", Name = "Hades" };
        var now = DateTime.UtcNow;

        var hdr = PerformanceActivity.DescribeGamesChanged([(er, HdrMode.ProfileDefault)], HdrMode.On, now);
        Assert.Equal("Elden Ring's HDR changed from Profile setting to On for this game", hdr.Text);
        Assert.Equal($"hdr.game|{er.Id}", hdr.GroupKey);

        var one = PerformanceActivity.DescribeGamesChanged([(er, PerformanceProfileMode.Off)], PerformanceProfileMode.Aggressive, now);
        Assert.Equal("Elden Ring's profile changed from Off to Aggressive", one.Text);
        Assert.Equal("profile.game|er", one.GroupKey);

        // A game already on that profile isn't a change.
        var batch = PerformanceActivity.DescribeGamesChanged(
            [(er, PerformanceProfileMode.Off), (hades, PerformanceProfileMode.Aggressive)], PerformanceProfileMode.Aggressive, now);
        Assert.Equal("Elden Ring's profile changed from Off to Aggressive", batch.Text);

        var many = PerformanceActivity.DescribeGamesChanged(
            [(er, PerformanceProfileMode.Off), (hades, PerformanceProfileMode.Optimized)], PerformanceProfileMode.Aggressive, now);
        Assert.Equal("Profile set to Aggressive for 2 games", many.Text);
        Assert.Contains("Hades - was Optimized", many.Detail);

        Assert.Equal(string.Empty, PerformanceActivity.DescribeGamesChanged([(er, PerformanceProfileMode.Off)], PerformanceProfileMode.Off, now).Text);
    }

    [Fact]
    public void TierTweakChange_NamesTheTier_AndAChangeMadeElsewhere_IsSeenOnce()
    {
        var (text, detail, _) = PerformanceActivity.DescribeTierTweakChanged("Optimized", "\"Ultimate Plan - TrayTrigger\" Power Plan (profile)", false);
        Assert.Equal("Optimized profile: \"Ultimate Plan - TrayTrigger\" Power Plan turned off", text);
        Assert.Contains("Aggressive", detail);

        bool value = true;
        var toggle = new ProfileTweakToggleViewModel("Enable HDR", "", "", "", () => value, v => value = v) { Tier = "Optimized" };
        Assert.Null(toggle.TakeOutsideChange());
        value = false; // Reset to Defaults
        Assert.Equal("Optimized profile: Enable HDR turned off", toggle.TakeOutsideChange());
        Assert.Null(toggle.TakeOutsideChange());
        toggle.IsEnabled = true; // the user, on the System page: recorded there, not again here
        Assert.Null(toggle.TakeOutsideChange());
    }
    [Fact]
    public void CpuCoresAndDlssChanges_ReadLikeTheMenus()
    {
        var er = new GameEntry { Id = "er", Name = "Elden Ring" };
        var hades = new GameEntry { Id = "h", Name = "Hades" };
        var now = DateTime.UtcNow;

        var cpu = PerformanceActivity.DescribeGamesChanged([(er, CpuAffinityMode.Default)], CpuAffinityMode.Auto, now);
        Assert.Equal("Elden Ring's CPU Cores changed from All Cores (Windows default) to Auto (Recommended)", cpu.Text);
        Assert.Equal("cpu.game|er", cpu.GroupKey);
        Assert.Equal("CPU Cores set to Auto (Recommended) for 2 games",
            PerformanceActivity.DescribeGamesChanged([(er, CpuAffinityMode.Default), (hades, CpuAffinityMode.OneCcd)], CpuAffinityMode.Auto, now).Text);

        var on = PerformanceActivity.DescribeDlssChanged([("er", "Elden Ring")], true, now);
        Assert.Equal("DLSS Override turned on for Elden Ring", on.Text);
        Assert.Equal("dlss.game|er", on.GroupKey);
        var off = PerformanceActivity.DescribeDlssChanged([("er", "Elden Ring"), ("h", "Hades")], false, now);
        Assert.Equal("DLSS Override turned off for 2 games", off.Text);
        Assert.Contains("Hades", off.Detail);
        Assert.Equal(string.Empty, PerformanceActivity.DescribeDlssChanged([], true, now).Text);
    }
    [Fact]
    public void EveryRow_HasALevelBadge_CriticalSolid_TheRestTinted()
    {
        ActivityGroupViewModel Row(ActivityLevel level) => new(
            new ActivityGroup("k", new ActivityEntry { Level = level, Text = "x" }, [DateTime.UtcNow]), DateTime.Now);

        Assert.Equal("CRITICAL", Row(ActivityLevel.Critical).LevelTag);
        Assert.Equal("#DA3633", Row(ActivityLevel.Critical).LevelBackground);
        Assert.Equal("PROBLEM", Row(ActivityLevel.Problem).LevelTag);
        Assert.Equal("CHANGE", Row(ActivityLevel.Change).LevelTag);
        Assert.Equal("ACTIVITY", Row(ActivityLevel.Activity).LevelTag);
        Assert.Equal("Problem: x", Row(ActivityLevel.Problem).AccessibleName);
    }
    // ------------------------------------------------------------------ fixed

    [Fact]
    public void Fixing_ACritical_BadgesIt_StopsTheBell_AndALaterFailureStaysUnfixed()
    {
        var service = Service();
        var failed = service.Record(ActivityLevel.Critical, "Couldn't put back the power plan", groupKey: "profile.unrestored");
        Assert.Equal(1, service.UnseenProblemCount);

        _now = _now.AddMinutes(3);
        service.MarkFixed([failed.Id]);
        Assert.Equal(0, service.UnseenProblemCount);
        Assert.Equal(_now, Service().Entries[0].FixedUtc); // kept across a restart

        var row = new ActivityGroupViewModel(ActivityService.Group(service.Entries)[0], _now.ToLocalTime());
        Assert.True(row.IsFixed);
        Assert.StartsWith("Put back ", row.FixedLabel);
        Assert.Equal("Critical, fixed: Couldn't put back the power plan", row.AccessibleName);
        Assert.Contains("Fixed ", row.DetailsText());

        _now = _now.AddMinutes(10);
        service.Record(ActivityLevel.Critical, "Couldn't put back the power plan", groupKey: "profile.unrestored");
        var again = new ActivityGroupViewModel(ActivityService.Group(service.Entries)[0], _now.ToLocalTime());
        Assert.False(again.IsFixed);
        Assert.Equal(1, service.UnseenProblemCount);
    }

    /// <summary>
    /// FIXED goes only on the entries the fix covered. An earlier failure in the same row - from a
    /// run Restore Previous never retried - stays unfixed, and keeps the row's FIXED badge off.
    /// </summary>
    [Fact]
    public void Fixed_IsPerEntry_AndAnEarlierUnfixedFailure_KeepsTheRowUnfixed()
    {
        var service = Service();
        var earlier = service.Record(ActivityLevel.Critical, "Elden Ring: couldn't put back the power plan", groupKey: "profile.unrestored");
        _now = _now.AddDays(1);
        var later = service.Record(ActivityLevel.Critical, "Hades: couldn't put back HDR on a display", groupKey: "profile.unrestored");

        service.MarkFixed([later.Id]);

        Assert.Null(service.Entries.Single(e => e.Id == earlier.Id).FixedUtc);
        Assert.NotNull(service.Entries.Single(e => e.Id == later.Id).FixedUtc);
        var row = Assert.Single(ActivityService.Group(service.Entries));
        Assert.False(row.Fixed);
        Assert.DoesNotContain("fixed", Assert.Single(service.RecentProblemLines()));

        service.MarkFixed([earlier.Id]);
        Assert.True(Assert.Single(ActivityService.Group(service.Entries)).Fixed);
        Assert.EndsWith("- fixed with Restore Previous", Assert.Single(service.RecentProblemLines()));
    }
}
