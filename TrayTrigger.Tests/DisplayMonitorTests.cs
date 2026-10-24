using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The monitor a game's display settings apply to, and making that monitor the main display.</summary>
// Its problems are recorded into Activity & History's static Current, which other tests swap in while they run.
[Collection(ActivityCurrentCollection.Name)]
public sealed class DisplayMonitorTests : System.IDisposable
{
    private const string One = @"\\.\DISPLAY1";
    private const string Two = @"\\.\DISPLAY2";
    private const string Three = @"\\.\DISPLAY3";

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrayTriggerMonitors_" + System.Guid.NewGuid().ToString("N"));
    private string SnapshotPath => System.IO.Path.Combine(_dir, "display-session.json");
    public void Dispose() { try { System.IO.Directory.Delete(_dir, true); } catch (System.IO.IOException) { } }

    /// <summary>Two monitors side by side: the main display on the left, a second to its right.</summary>
    private (DisplayModeService Service, FakeDisplay Display) TwoMonitors()
    {
        var d = new FakeDisplay
        {
            Attached = [One, Two],
            Mons =
            [
                new(One, "MON1", "Left", true, new ScreenRect(0, 0, 3840, 2160)),
                new(Two, "MON2", "Right", false, new ScreenRect(3840, 0, 5760, 1080)),
            ],
        };
        return (new DisplayModeService(d, SnapshotPath), d);
    }

    private static GameEntry Game(string id = "a") => new() { Id = id, Name = "Game " + id };

    [Fact]
    public void TheLayoutMath_PutsTheNewMainDisplayAtZero_AndKeepsTheRest()
    {
        var layout = new[] { new MonitorPlacement(One, 0, 0), new MonitorPlacement(Two, 3840, 0) };

        var shifted = DisplayLayout.WithPrimary(layout, Two)!;

        Assert.Equal(new MonitorPlacement(Two, 0, 0), shifted.Single(p => p.Device == Two));
        Assert.Equal(new MonitorPlacement(One, -3840, 0), shifted.Single(p => p.Device == One));
        Assert.Equal(Two, DisplayLayout.PrimaryOf(shifted));
    }

    [Fact]
    public void StagingPutsTheMainDisplayFirst_AndOnlyItIsMarkedPrimary()
    {
        var shifted = DisplayLayout.WithPrimary([new MonitorPlacement(One, 0, 0), new MonitorPlacement(Two, 3840, 0), new MonitorPlacement(Three, 7680, 0)], Two)!;

        var plan = DisplayLayout.StagingPlan(shifted);

        Assert.Equal(Two, plan[0].Placement.Device);
        Assert.True(plan[0].SetPrimary);
        Assert.Equal([false, false], plan.Skip(1).Select(x => x.SetPrimary));
        Assert.Equal(3, plan.Count);
        // Three monitors in a row, the middle one made main: the left one goes left of 0, the right one right of it.
        Assert.Equal(-3840, plan.Single(x => x.Placement.Device == One).Placement.X);
        Assert.Equal(3840, plan.Single(x => x.Placement.Device == Three).Placement.X);
    }

    [Fact]
    public void WhenTheOldLayoutCannotBePutBackEither_TheNoteStaysForTheNextStart()
    {
        var (service, display) = TwoMonitors();
        display.LayoutResult = DisplayChange.Failed;
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;

        service.Begin(game);

        Assert.True(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AGameStartingWhileAnEarlierPutBackIsStillPending_ChangesNothing_UntilThatIsDone()
    {
        var (service, display) = TwoMonitors();
        var first = Game("a");
        first.DisplayMonitorId = "MON2";
        first.MakeMainDisplay = true;
        service.Begin(first);
        display.LayoutResult = DisplayChange.Failed;
        service.End("a");
        Assert.True(System.IO.File.Exists(SnapshotPath));
        string pending = System.IO.File.ReadAllText(SnapshotPath);

        var second = Game("b");
        second.DisplayMonitorId = "MON1";
        second.DisplayWidth = 1920; second.DisplayHeight = 1080; second.DisplayRefreshHz = 144;
        Assert.Equal(DisplayApply.Failed, service.Begin(second));

        // The first game's note is still the one on disk, and nothing was applied for the second.
        Assert.Equal(pending, System.IO.File.ReadAllText(SnapshotPath));
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("apply"));

        // Once Windows takes the old layout back, the next game goes ahead.
        display.LayoutResult = DisplayChange.Ok;
        Assert.Equal(DisplayApply.Applied, service.Begin(second));
        Assert.Equal(One, display.Primary);
    }

    [Fact]
    public void ThePlannedRefreshRate_IsTheModeAskedFor_WhenItIsForTheMainDisplay()
    {
        var (service, _) = TwoMonitors();
        var game = Game();
        game.DisplayWidth = 1920; game.DisplayHeight = 1080; game.DisplayRefreshHz = 144;

        Assert.Equal(144, service.PlannedMainRefreshHz(game));
    }

    [Fact]
    public void ThePlannedRefreshRate_IsThatMonitorsOwn_WhenItIsOnlyMadeTheMainDisplay()
    {
        var (service, display) = TwoMonitors();
        display.Now = new DisplayMode(1920, 1080, 75);
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;

        Assert.Equal(75, service.PlannedMainRefreshHz(game));
    }

    [Fact]
    public void ThePlannedRefreshRate_IsTheModeAskedFor_ForAMonitorMadeTheMainDisplay()
    {
        var (service, display) = TwoMonitors();
        display.ModesByDevice[Two] = [new(1920, 1080, 144)];
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;
        game.DisplayWidth = 1920; game.DisplayHeight = 1080; game.DisplayRefreshHz = 144;

        Assert.Equal(144, service.PlannedMainRefreshHz(game));
    }

    [Fact]
    public void ThePlannedRefreshRate_LeavesTheMainDisplayAlone_WhenNothingChangesIt()
    {
        var (service, display) = TwoMonitors();

        // Nothing asked.
        Assert.Null(service.PlannedMainRefreshHz(Game()));

        // A second monitor's mode, with that monitor staying second.
        var second = Game();
        second.DisplayMonitorId = "MON2";
        second.DisplayWidth = 1920; second.DisplayHeight = 1080; second.DisplayRefreshHz = 144;
        display.ModesByDevice[Two] = [new(1920, 1080, 144)];
        Assert.Null(service.PlannedMainRefreshHz(second));

        // A monitor that isn't connected.
        var gone = Game();
        gone.DisplayMonitorId = "UNPLUGGED";
        gone.MakeMainDisplay = true;
        Assert.Null(service.PlannedMainRefreshHz(gone));

        // A remote session changes nothing.
        display.Remote = true;
        var asked = Game();
        asked.DisplayWidth = 1920; asked.DisplayHeight = 1080; asked.DisplayRefreshHz = 144;
        Assert.Null(service.PlannedMainRefreshHz(asked));
    }

    [Fact]
    public void ThePlannedRefreshRate_IsTheCurrentOne_WhenTheModeAskedForIsNotListed()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayWidth = 1280; game.DisplayHeight = 720; game.DisplayRefreshHz = 240;

        // Begin would leave the display as it is, so that is what the game will run at.
        Assert.Equal(display.Now.RefreshHz, service.PlannedMainRefreshHz(game));
    }

    [Fact]
    public void DuplicatedMonitors_CannotBeMadeTheMainDisplay()
    {
        var (service, display) = TwoMonitors();
        display.Mons = display.Mons.Select(m => m with { Bounds = new ScreenRect(0, 0, 1920, 1080) }).ToList();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;

        Assert.Equal(DisplayApply.Unsupported, service.Begin(game));
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("layout"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void ANoteWithNoLayoutList_IsHandledAndDiscarded()
    {
        var (service, display) = TwoMonitors();
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(SnapshotPath, "{ \"GameId\": \"a\", \"GameName\": \"A\", \"MadePrimary\": \"\\\\\\\\.\\\\DISPLAY2\", \"LayoutBefore\": null }");

        service.RecoverIfNeeded();

        Assert.DoesNotContain(display.Calls, c => c.StartsWith("layout"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void TheLayoutMath_HandlesAMonitorAboveAndBelow()
    {
        var layout = new[] { new MonitorPlacement(One, 0, 0), new MonitorPlacement(Two, 500, -1080) };

        var shifted = DisplayLayout.WithPrimary(layout, Two)!;

        Assert.Equal(new MonitorPlacement(One, -500, 1080), shifted.Single(p => p.Device == One));
        Assert.Null(DisplayLayout.WithPrimary(layout, @"\\.\DISPLAY9"));
    }

    [Fact]
    public void AGameForAnotherMonitor_ChangesThatMonitorsMode_NotTheMainDisplays()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.DisplayWidth = 1920; game.DisplayHeight = 1080; game.DisplayRefreshHz = 144;

        Assert.Equal(DisplayApply.Applied, service.Begin(game));

        Assert.Equal([Two], display.AppliedOn);
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("layout"));
        service.End("a");
        Assert.Contains($"restore {Two}", display.Calls);
    }

    [Fact]
    public void AMonitorThatIsNotConnected_ChangesNothing_AndSaysSo()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "SOMEWHERE-ELSE";
        game.DisplayWidth = 1920; game.DisplayHeight = 1080; game.DisplayRefreshHz = 144;

        Assert.Equal(DisplayApply.MonitorMissing, service.Begin(game));
        Assert.Empty(display.Calls);
    }

    [Fact]
    public void MakingAMonitorTheMainDisplay_ShiftsTheLayout_AndEndPutsItBack()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;

        Assert.Equal(DisplayApply.Applied, service.Begin(game));
        Assert.Equal(Two, display.Primary);
        Assert.Contains("layout DISPLAY1@-3840,0 DISPLAY2@0,0", display.Calls);

        service.End("a");
        Assert.Equal(One, display.Primary);
        Assert.Equal("layout DISPLAY1@0,0 DISPLAY2@3840,0", display.Calls.Last());
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AMonitorThatIsAlreadyTheMainDisplay_IsNotChanged()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON1";
        game.MakeMainDisplay = true;

        Assert.Equal(DisplayApply.AlreadyInMode, service.Begin(game));
        Assert.Empty(display.Calls);
    }

    [Fact]
    public void TheNoteHoldsEveryMonitorsPlace_BeforeTheLayoutChanges()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;
        bool noteExistedAtTheChange = false;
        display.DuringLayout = () => noteExistedAtTheChange |= System.IO.File.Exists(SnapshotPath);

        service.Begin(game);

        Assert.True(noteExistedAtTheChange);
        string note = System.IO.File.ReadAllText(SnapshotPath);
        Assert.Contains("3840", note);
        Assert.Contains("DISPLAY2", note);
    }

    [Fact]
    public void ALayoutWindowsRefuses_IsRolledBack_AndTheGameStillLaunches()
    {
        var (service, display) = TwoMonitors();
        display.FailLayoutCalls = 1;
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;

        Assert.Equal(DisplayApply.Failed, service.Begin(game));

        // It tried the new layout, then the old one, and left no note.
        Assert.Equal(2, display.Calls.Count(c => c.StartsWith("layout")));
        Assert.Equal("layout DISPLAY1@0,0 DISPLAY2@3840,0", display.Calls.Last());
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AMainDisplayChangedBySomeoneElseMeanwhile_IsLeftAlone()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;
        service.Begin(game);
        // The user makes the first monitor the main display again by hand.
        display.SetLayout([new MonitorPlacement(One, 0, 0), new MonitorPlacement(Two, 3840, 0)]);
        int layoutCalls = display.Calls.Count(c => c.StartsWith("layout"));

        service.End("a");

        Assert.Equal(layoutCalls, display.Calls.Count(c => c.StartsWith("layout")));
    }

    [Fact]
    public void AMonitorUnpluggedMeanwhile_MeansTheOldPlacesAreNotApplied()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;
        service.Begin(game);
        display.Mons = display.Mons.Where(m => m.Device == Two).ToList();
        int layoutCalls = display.Calls.Count(c => c.StartsWith("layout"));

        service.End("a");

        Assert.Equal(layoutCalls, display.Calls.Count(c => c.StartsWith("layout")));
    }

    [Fact]
    public void AfterACrash_TheNextStartPutsTheMainDisplayBack()
    {
        var (first, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;
        first.Begin(game);
        Assert.Equal(Two, display.Primary);

        new DisplayModeService(display, SnapshotPath).RecoverIfNeeded();

        Assert.Equal(One, display.Primary);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AModeAndTheMainDisplayTogether_AreBothSetAndBothPutBack_ModeFirst()
    {
        var (service, display) = TwoMonitors();
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;
        game.DisplayWidth = 1920; game.DisplayHeight = 1080; game.DisplayRefreshHz = 144;

        Assert.Equal(DisplayApply.Applied, service.Begin(game));
        service.End("a");

        var order = display.Calls.Where(c => c.StartsWith("layout") || c.StartsWith("apply") || c.StartsWith("restore")).ToList();
        Assert.Equal(["layout DISPLAY1@-3840,0 DISPLAY2@0,0", "apply 1920 × 1080 @ 144 Hz", $"restore {Two}", "layout DISPLAY1@0,0 DISPLAY2@3840,0"], order);
    }

    [Fact]
    public void WhenTheModeFailsAfterTheMainDisplayWasChanged_TheMainDisplayIsStillPutBack()
    {
        var (service, display) = TwoMonitors();
        display.ApplyResult = DisplayChange.Failed;
        var game = Game();
        game.DisplayMonitorId = "MON2";
        game.MakeMainDisplay = true;
        game.DisplayWidth = 1920; game.DisplayHeight = 1080; game.DisplayRefreshHz = 144;

        service.Begin(game);
        service.End("a");

        Assert.Equal(One, display.Primary);
    }

    [Fact]
    public void TheMonitorList_NumbersAndNamesThem()
    {
        var (service, _) = TwoMonitors();

        var monitors = service.AvailableMonitors();

        Assert.Equal(2, monitors.Count);
        Assert.Equal(2, monitors[1].Number);
        Assert.Equal("Display 2: Right (1920 × 1080)", monitors[1].Label);
    }
}
