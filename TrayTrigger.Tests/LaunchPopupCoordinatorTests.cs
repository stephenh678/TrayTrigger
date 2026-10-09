using TrayTrigger.Models;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>When the launch popup shows, what it says, and when it goes away. Timers and the
/// clock are faked; the window is replaced by a recorder.</summary>
public class LaunchPopupCoordinatorTests
{
    private sealed class FakeView : ILaunchPopupView
    {
        public LaunchPopupContent? Shown;
        public bool Visible;
        public event Action? ActionClicked;
        public event Action? CloseClicked;
        public void Show(LaunchPopupContent content) { Shown = content; Visible = true; }
        public void Hide() => Visible = false;
        public void Dispose() { }
        public void ClickAction() => ActionClicked?.Invoke();
        public void ClickClose() => CloseClicked?.Invoke();
    }

    private sealed class Handle : IDisposable
    {
        public bool Cancelled;
        public void Dispose() => Cancelled = true;
    }

    private sealed class Rig
    {
        public bool Enabled = true;
        public bool EveryLaunch;
        public bool AppInFront;
        public bool WaitingForGame = true;
        public int MainWindowShown;
        public DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        public readonly FakeView View = new();
        private readonly List<(TimeSpan Delay, Action Action, Handle Handle)> _timers = new();
        public readonly LaunchPopupCoordinator Popup;

        public Rig()
        {
            Popup = new LaunchPopupCoordinator(
                isEnabled: () => Enabled,
                showWhileAppInFront: () => EveryLaunch,
                isAppInFront: () => AppInFront,
                isWaitingForGame: _ => WaitingForGame,
                showMainWindow: () => MainWindowShown++,
                iconFor: _ => null,
                view: View,
                schedule: (delay, action) =>
                {
                    var handle = new Handle();
                    _timers.Add((delay, action, handle));
                    return handle;
                },
                utcNow: () => Now);
        }

        /// <summary>How many timers with this delay were ever scheduled, and how many are still live.</summary>
        public (int Scheduled, int Live) Timers(TimeSpan delay) =>
            (_timers.Count(t => t.Delay == delay), _timers.Count(t => t.Delay == delay && !t.Handle.Cancelled));

        /// <summary>Fires every live timer whose delay matches.</summary>
        public void Fire(Func<TimeSpan, bool> which)
        {
            foreach (var t in _timers.ToList())
            {
                if (t.Handle.Cancelled || !which(t.Delay)) continue;
                t.Handle.Cancelled = true;
                t.Action();
            }
        }
    }

    private static LaunchTarget Game(string id, string name = "DOOM: The Dark Ages") => new(id, name);

    [Fact]
    public void ShowLaunchPopup_DefaultsToOn_OnlyWhenHidden()
    {
        var settings = new AppSettings();
        Assert.True(settings.ShowLaunchPopup);
        Assert.False(settings.ShowLaunchPopupOnEveryLaunch);
    }

    [Fact]
    public void Begin_AppInFront_EveryLaunchOn_Shows()
    {
        var rig = new Rig { AppInFront = true, EveryLaunch = true };
        Assert.True(rig.Popup.TryBeginLaunch(Game("a")));
        Assert.Equal(LaunchPopupKind.Launching, rig.View.Shown!.Kind);
    }

    [Fact]
    public void Begin_EveryLaunchOn_ButPopupOff_ShowsNothing()
    {
        var rig = new Rig { Enabled = false, EveryLaunch = true };
        Assert.False(rig.Popup.TryBeginLaunch(Game("a")));
        Assert.Null(rig.View.Shown);
    }

    [Fact]
    public void EveryLaunch_FailureWithWindowInFront_ClosesThePopup_AndLeavesItToTheDialog()
    {
        var rig = new Rig { AppInFront = true, EveryLaunch = true };
        var game = Game("a");
        rig.Popup.TryBeginLaunch(game);

        Assert.False(rig.Popup.TryShowFailure(game, "msg", "Open TrayTrigger", null));
        Assert.False(rig.View.Visible);
    }

    [Fact]
    public void Begin_SettingOff_LeavesItToTheWindowToast()
    {
        var rig = new Rig { Enabled = false };
        Assert.False(rig.Popup.TryBeginLaunch(Game("a")));
        Assert.Null(rig.View.Shown);
    }

    [Fact]
    public void Begin_AppInFront_LeavesItToTheWindowToast()
    {
        var rig = new Rig { AppInFront = true };
        Assert.False(rig.Popup.TryBeginLaunch(Game("a")));
        Assert.Null(rig.View.Shown);
    }

    [Fact]
    public void Begin_ShowsLaunchingWithProgress_AndNothingToClick()
    {
        var rig = new Rig();
        Assert.True(rig.Popup.TryBeginLaunch(Game("a")));

        Assert.True(rig.View.Visible);
        Assert.Equal(LaunchPopupKind.Launching, rig.View.Shown!.Kind);
        Assert.Equal("DOOM: The Dark Ages", rig.View.Shown.Name);
        Assert.True(rig.View.Shown.ShowsProgress);
        Assert.False(rig.View.Shown.IsInteractive);
    }

    [Fact]
    public void GameStarted_AfterMinimumVisible_ClosesAtOnce()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));
        rig.Now += TimeSpan.FromSeconds(2);

        rig.Popup.OnGameStarted("a");
        Assert.False(rig.View.Visible);
    }

    [Fact]
    public void GameStarted_Instantly_HoldsTheMinimumSoItDoesNotFlash()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));
        rig.Now += TimeSpan.FromSeconds(0.5);

        rig.Popup.OnGameStarted("a");
        Assert.True(rig.View.Visible);

        rig.Fire(d => d < LaunchPopupCoordinator.MinVisible);
        Assert.False(rig.View.Visible);
    }

    [Fact]
    public void Waiting_NamesTheLauncherTheGameWaitsOn()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a", "Diablo III"));
        rig.Popup.OnSessionStarted("a", "Battle.net");
        Assert.Equal("Starting through Battle.net", rig.View.Shown!.Detail);

        rig.Fire(d => d == LaunchPopupCoordinator.WaitingAfter);
        Assert.Equal(LaunchPopupKind.Waiting, rig.View.Shown!.Kind);
        Assert.Equal("Waiting for Battle.net", rig.View.Shown.Status);
        Assert.Contains("signing in", rig.View.Shown.Detail);
    }

    [Fact]
    public void StartingTool_NamesTheToolUntilItHasStarted_EvenPastTheWaitingTime()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));

        rig.Popup.OnStartingTool("a", "MSI Afterburner");
        Assert.Equal("Launching", rig.View.Shown!.Status);
        Assert.Equal("Starting MSI Afterburner first", rig.View.Shown.Detail);

        // An administrator prompt can take longer than WaitingAfter to answer.
        rig.Fire(d => d == LaunchPopupCoordinator.WaitingAfter);
        Assert.Equal("Starting MSI Afterburner first", rig.View.Shown!.Detail);

        rig.Popup.OnStartingTool("a", null);
        Assert.Equal("Still starting", rig.View.Shown!.Status);
    }

    [Fact]
    public void ClosingTool_NamesTheToolBeingClosed_ThenTheOneStarting()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));

        rig.Popup.OnClosingTool("a", "Discord");
        Assert.Equal("Launching", rig.View.Shown!.Status);
        Assert.Equal("Closing Discord first", rig.View.Shown.Detail);

        rig.Popup.OnClosingTool("a", null);
        rig.Popup.OnStartingTool("a", "MSI Afterburner");
        Assert.Equal("Starting MSI Afterburner first", rig.View.Shown!.Detail);

        // Another game's tools don't show on this popup.
        rig.Popup.OnClosingTool("b", "Slack");
        Assert.Equal("Starting MSI Afterburner first", rig.View.Shown!.Detail);
    }

    [Fact]
    public void Closing_SaysWhatThePromptIsFor_UntilItIsAnswered()
    {
        var rig = new Rig();

        Assert.True(rig.Popup.ShowClosing(new LaunchTarget("t1", "MSI Afterburner")));
        Assert.Equal(LaunchPopupKind.Closing, rig.View.Shown!.Kind);
        Assert.Equal("MSI Afterburner", rig.View.Shown.Name);
        Assert.Equal("Closing", rig.View.Shown.Status);
        Assert.Contains("Windows asks for permission to close it", rig.View.Shown.Detail);
        Assert.False(rig.View.Shown.IsInteractive);

        rig.Popup.EndClosing("other");
        Assert.True(rig.View.Visible);

        rig.Popup.EndClosing("t1");
        Assert.False(rig.View.Visible);
    }

    /// <summary>
    /// A launch failure still waiting for the user isn't lost to a tool's closing prompt: it's covered
    /// while the prompt explains itself, and comes back, action and all, once the prompt is answered.
    /// </summary>
    [Fact]
    public void Closing_PutsBackAFailureItCovered()
    {
        var rig = new Rig();
        int located = 0;
        Assert.True(rig.Popup.TryShowFailure(Game("a", "Elden Ring"), "Its program wasn't found.", "Locate program...", () => located++));

        rig.Popup.ShowClosing(new LaunchTarget("t1", "MSI Afterburner"));
        Assert.Equal(LaunchPopupKind.Closing, rig.View.Shown!.Kind);

        rig.Popup.EndClosing("t1");
        Assert.True(rig.View.Visible);
        Assert.Equal(LaunchPopupKind.Failed, rig.View.Shown!.Kind);
        Assert.Equal("Elden Ring", rig.View.Shown.Name);
        Assert.Equal("Its program wasn't found.", rig.View.Shown.Detail);
        Assert.Equal("Locate program...", rig.View.Shown.ActionText);

        rig.View.ClickAction();
        Assert.Equal(1, located);
    }

    [Fact]
    public void Closing_ANewerMessageWins_OverTheOneItCovered()
    {
        var rig = new Rig();
        rig.Popup.TryShowFailure(Game("a", "Elden Ring"), "Its program wasn't found.", null, null);
        rig.Popup.ShowClosing(new LaunchTarget("t1", "MSI Afterburner"));

        rig.Popup.TryShowNotice(Game("b", "Diablo IV"), "Battle.net needs you.");
        rig.Popup.EndClosing("t1");

        Assert.Equal("Diablo IV", rig.View.Shown!.Name);
        Assert.True(rig.View.Visible);
    }

    [Fact]
    public void Closing_WithThePopupOff_ShowsNothing()
    {
        var rig = new Rig { Enabled = false };

        Assert.False(rig.Popup.ShowClosing(new LaunchTarget("t1", "MSI Afterburner")));
        Assert.Null(rig.View.Shown);
    }

    /// <summary>
    /// A tool's prompt and wait come before the game is dispatched, so the time the popup gives the
    /// game starts again once the tools are done, instead of being used up by them.
    /// </summary>
    [Fact]
    public void StartingTool_TheGameStillGetsTheWholeMaxWait()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));

        rig.Popup.OnStartingTool("a", "MSI Afterburner");
        rig.Popup.OnStartingTool("a", null);

        Assert.Equal((3, 1), rig.Timers(LaunchPopupCoordinator.MaxWait));
        rig.Fire(d => d == LaunchPopupCoordinator.MaxWait);
        Assert.False(rig.View.Visible);
    }

    [Fact]
    public void StartingTool_ForAnotherLaunch_IsIgnored()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));

        rig.Popup.OnStartingTool("b", "MSI Afterburner");

        Assert.Null(rig.View.Shown!.Detail);
    }

    [Fact]
    public void Waiting_LocalGame_DoesNotClaimALauncher()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));
        rig.Popup.OnSessionStarted("a", "Local");

        rig.Fire(d => d == LaunchPopupCoordinator.WaitingAfter);
        Assert.Equal("Still starting", rig.View.Shown!.Status);
    }

    [Fact]
    public void LaunchingDetail_IncludesTheProfileTier()
    {
        var game = LaunchTarget.ForGame(new GameEntry { Id = "a", Name = "X", PerformanceProfile = PerformanceProfileMode.Optimized });
        Assert.Equal("Starting through Xbox · Optimized profile", LaunchPopupCoordinator.LaunchingDetail(game, "Xbox"));
        Assert.Equal("Optimized profile", LaunchPopupCoordinator.LaunchingDetail(game, "Local"));
        Assert.Null(LaunchPopupCoordinator.LaunchingDetail(new LaunchTarget("b", "Y"), null));
    }

    [Fact]
    public void EventsForAnotherGame_AreIgnored()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));
        rig.Now += TimeSpan.FromSeconds(5);

        rig.Popup.OnGameStarted("b");
        rig.Popup.OnSessionEnded("b");
        Assert.True(rig.View.Visible);
    }

    [Fact]
    public void Dispatched_WithNothingToWaitFor_Closes_BecauseTheGameWasAlreadyRunning()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));
        rig.Now += TimeSpan.FromSeconds(2);

        rig.Popup.LaunchDispatched("a");
        Assert.True(rig.View.Visible);

        rig.WaitingForGame = false;
        rig.Popup.LaunchDispatched("a");
        Assert.False(rig.View.Visible);
    }

    [Fact]
    public void ProgressPopup_ClosesAtTheCap()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a"));

        rig.Fire(d => d == LaunchPopupCoordinator.MaxWait);
        Assert.False(rig.View.Visible);
    }

    [Fact]
    public void Failure_ReplacesProgress_StaysPastSessionEvents_AndActionOpensWindowFirst()
    {
        var rig = new Rig();
        var game = Game("a");
        rig.Popup.TryBeginLaunch(game);

        int windowCountWhenActionRan = -1;
        Assert.True(rig.Popup.TryShowFailure(game, "It's no longer installed through the Xbox app.", "Open TrayTrigger",
            () => windowCountWhenActionRan = rig.MainWindowShown));

        Assert.Equal(LaunchPopupKind.Failed, rig.View.Shown!.Kind);
        Assert.True(rig.View.Shown.IsInteractive);
        Assert.Equal("It's no longer installed through the Xbox app.", rig.View.Shown.Detail);

        rig.Now += TimeSpan.FromMinutes(5);
        rig.Popup.OnSessionEnded("a");
        rig.Fire(_ => true);
        Assert.True(rig.View.Visible);

        rig.View.ClickAction();
        Assert.False(rig.View.Visible);
        Assert.Equal(1, windowCountWhenActionRan);
    }

    [Fact]
    public void Failure_NotShownInPopup_WhenTheWindowIsInFrontAndThePopupWasNotShowingIt()
    {
        var rig = new Rig { AppInFront = true };
        Assert.False(rig.Popup.TryShowFailure(Game("a"), "msg", null, null));
        Assert.Null(rig.View.Shown);
    }

    [Fact]
    public void Failure_SettingOff_FallsBackToTheDialog()
    {
        var rig = new Rig { Enabled = false };
        Assert.False(rig.Popup.TryShowFailure(Game("a"), "msg", null, null));
    }

    [Fact]
    public void Notice_WhenWindowHidden_ShowsUntilClosed()
    {
        var rig = new Rig();
        Assert.True(rig.Popup.TryShowNotice(Game("a", "Diablo III"), "Battle.net may need you."));
        Assert.Equal(LaunchPopupKind.Notice, rig.View.Shown!.Kind);

        rig.Fire(_ => true);
        Assert.True(rig.View.Visible);

        rig.View.ClickClose();
        Assert.False(rig.View.Visible);
        Assert.Equal(0, rig.MainWindowShown);
    }

    [Fact]
    public void Notice_WhenWindowInFront_GoesToTheTray()
    {
        var rig = new Rig { AppInFront = true };
        Assert.False(rig.Popup.TryShowNotice(Game("a"), "msg"));
    }

    [Fact]
    public void SecondLaunch_ReplacesTheFirst_AndItsTimersNoLongerApply()
    {
        var rig = new Rig();
        rig.Popup.TryBeginLaunch(Game("a", "First"));
        rig.Popup.TryBeginLaunch(Game("b", "Second"));

        rig.Fire(d => d == LaunchPopupCoordinator.WaitingAfter);
        Assert.Equal("Second", rig.View.Shown!.Name);
        Assert.Equal(LaunchPopupKind.Waiting, rig.View.Shown.Kind);

        rig.Now += TimeSpan.FromSeconds(10);
        rig.Popup.OnGameStarted("a");
        Assert.True(rig.View.Visible);
    }

    [Fact]
    public void LaunchTarget_ForGame_CarriesIdNameProfileAndLauncherLogo()
    {
        var game = new GameEntry { Id = "g1", Name = "Diablo IV", PerformanceProfile = PerformanceProfileMode.Aggressive, IsBattleNetGame = true };
        var target = LaunchTarget.ForGame(game);

        Assert.Equal("g1", target.Id);
        Assert.Equal("Diablo IV", target.Name);
        Assert.Equal(PerformanceProfileMode.Aggressive, target.PerformanceProfile);
        Assert.Equal(TrayTrigger.Services.LauncherLogos.PackUriFor(game), target.FallbackIconUri);
        Assert.Null(LaunchTarget.ForGame(new GameEntry { Id = "g2", Name = "Local" }).FallbackIconUri);
    }

    [Fact]
    public void NonGameTarget_ShowsItsName_AndNoProfileDetail()
    {
        var rig = new Rig();
        Assert.True(rig.Popup.TryBeginLaunch(new LaunchTarget("t1", "DLSS Swapper")));

        Assert.Equal("DLSS Swapper", rig.View.Shown!.Name);
        Assert.Null(rig.View.Shown.Detail);
    }

    [Fact]
    public void IconLookup_ReceivesTheTargetBeingShown()
    {
        LaunchTarget? asked = null;
        var view = new FakeView();
        var popup = new LaunchPopupCoordinator(
            isEnabled: () => true,
            showWhileAppInFront: () => false,
            isAppInFront: () => false,
            isWaitingForGame: _ => true,
            showMainWindow: () => { },
            iconFor: t => { asked = t; return null; },
            view: view,
            schedule: (_, _) => new Handle(),
            utcNow: () => DateTime.UtcNow);

        var target = new LaunchTarget("t1", "Vortex", FallbackIconUri: "pack://application:,,,/x.png");
        popup.TryBeginLaunch(target);

        Assert.Equal(target, asked);
    }
}
