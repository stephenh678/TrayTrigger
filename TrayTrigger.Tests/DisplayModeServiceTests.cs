using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>A display that remembers what it was asked, standing in for Windows.</summary>
internal sealed class FakeDisplay : IDisplayBackend
{
    public bool Remote;
    public string? Primary = @"\\.\DISPLAY1";
    public HashSet<string> Attached = [@"\\.\DISPLAY1"];
    public List<DisplayMode> Listed = [new(3840, 2160, 60), new(2560, 1440, 144), new(1920, 1080, 144), new(1920, 1080, 60)];
    public DisplayMode Now = new(3840, 2160, 60);
    public DisplayChange TestResult = DisplayChange.Ok;
    public DisplayChange ApplyResult = DisplayChange.Ok;
    public DisplayChange RestoreResult = DisplayChange.Ok;
    public bool ThrowOnApply;
    public DisplayMode Desktop = new(3840, 2160, 60);
    public List<string> Calls = [];

    public bool IsRemoteSession => Remote;
    public string? PrimaryDevice() => Primary;
    public bool IsAttached(string device) => Attached.Contains(device);
    public IReadOnlyList<DisplayMode> Modes(string device) => Listed;
    public DisplayMode? Current(string device) => Now;
    public DisplayChange Test(string device, DisplayMode mode) { Calls.Add($"test {mode}"); return TestResult; }

    public DisplayChange ApplyTemporary(string device, DisplayMode mode)
    {
        Calls.Add($"apply {mode}");
        if (ThrowOnApply) throw new System.InvalidOperationException("driver exploded");
        if (ApplyResult == DisplayChange.Ok) Now = mode;
        return ApplyResult;
    }

    /// <summary>Runs inside the put-back, as another thread's launch would.</summary>
    public System.Action? DuringRestore;

    public DisplayChange RestoreDefault(string device)
    {
        Calls.Add($"restore {device}");
        DuringRestore?.Invoke();
        if (RestoreResult == DisplayChange.Ok) Now = Desktop;
        return RestoreResult;
    }
}

/// <summary>Per-game display mode: set before a game, put back after, and never left set.</summary>
public sealed class DisplayModeServiceTests : System.IDisposable
{
    private static GameEntry Game(string id, int w = 1920, int h = 1080, int hz = 144) =>
        new() { Id = id, Name = "Game " + id, DisplayWidth = w, DisplayHeight = h, DisplayRefreshHz = hz };

    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrayTriggerDisplay_" + System.Guid.NewGuid().ToString("N"));
    private string SnapshotPath => System.IO.Path.Combine(_dir, "display-session.json");

    public void Dispose() { try { System.IO.Directory.Delete(_dir, true); } catch (System.IO.IOException) { } }

    private (DisplayModeService Service, FakeDisplay Display) Make() { var d = new FakeDisplay(); return (new DisplayModeService(d, SnapshotPath), d); }

    [Fact]
    public void AGameWithNoDisplayMode_TouchesNothing()
    {
        var (service, display) = Make();

        Assert.Equal(DisplayApply.NotRequested, service.Begin(new GameEntry { Id = "a", Name = "A" }));
        Assert.Equal(DisplayApply.NotRequested, service.Begin(Game("b", hz: 0)));
        Assert.Empty(display.Calls);
    }

    [Fact]
    public void ALaunch_SetsTheModeThenPutsTheDesktopBack()
    {
        var (service, display) = Make();

        Assert.Equal(DisplayApply.Applied, service.Begin(Game("a")));
        Assert.Equal(new DisplayMode(1920, 1080, 144), display.Now);

        service.End("a");
        Assert.Equal(display.Desktop, display.Now);
        Assert.Equal(["test 1920 × 1080 @ 144 Hz", "apply 1920 × 1080 @ 144 Hz", @"restore \\.\DISPLAY1"], display.Calls);
    }

    [Fact]
    public void AModeTheDisplayDoesNotList_IsNeverAskedFor()
    {
        var (service, display) = Make();

        Assert.Equal(DisplayApply.Unsupported, service.Begin(Game("a", 2560, 1440, 60)));
        Assert.Empty(display.Calls);
        Assert.Equal(display.Desktop, display.Now);
    }

    [Fact]
    public void AModeWindowsWouldRefuse_IsNotApplied_AndTheGameStillLaunches()
    {
        var (service, display) = Make();
        display.TestResult = DisplayChange.BadMode;

        Assert.Equal(DisplayApply.Unsupported, service.Begin(Game("a")));
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("apply"));
        service.End("a");
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("restore"));
    }

    [Fact]
    public void AFailedApply_LeavesNothingToPutBack()
    {
        var (service, display) = Make();
        display.ApplyResult = DisplayChange.Failed;

        Assert.Equal(DisplayApply.Failed, service.Begin(Game("a")));
        service.End("a");
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("restore"));
    }

    [Fact]
    public void ADriverThatThrows_DoesNotStopTheLaunch()
    {
        var (service, display) = Make();
        display.ThrowOnApply = true;

        Assert.Equal(DisplayApply.Failed, service.Begin(Game("a")));
        service.End("a");
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("restore"));
    }

    [Fact]
    public void AModeTheDisplayAlreadyRuns_IsNotChangedOrPutBack()
    {
        var (service, display) = Make();
        display.Now = new DisplayMode(1920, 1080, 144);

        Assert.Equal(DisplayApply.AlreadyInMode, service.Begin(Game("a")));
        service.End("a");
        Assert.Empty(display.Calls);
    }

    [Fact]
    public void TwoGamesAtOnce_OnlyTheFirstSetsTheDisplay_AndOnlyItPutsItBack()
    {
        var (service, display) = Make();

        Assert.Equal(DisplayApply.Applied, service.Begin(Game("a", 1920, 1080, 144)));
        Assert.Equal(DisplayApply.Busy, service.Begin(Game("b", 2560, 1440, 144)));
        Assert.Equal(new DisplayMode(1920, 1080, 144), display.Now);

        service.End("b");
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("restore"));
        Assert.Equal(new DisplayMode(1920, 1080, 144), display.Now);

        service.End("a");
        Assert.Single(display.Calls, c => c.StartsWith("restore"));
        Assert.Equal(display.Desktop, display.Now);

        service.End("a");
        Assert.Single(display.Calls, c => c.StartsWith("restore"));
    }

    [Fact]
    public void ARemoteSession_ChangesNothing()
    {
        var (service, display) = Make();
        display.Remote = true;

        Assert.Equal(DisplayApply.Remote, service.Begin(Game("a")));
        Assert.Empty(display.Calls);
        Assert.Empty(service.AvailableModes());
    }

    [Fact]
    public void NoMainDisplay_ChangesNothing()
    {
        var (service, display) = Make();
        display.Primary = null;

        Assert.Equal(DisplayApply.NoDisplay, service.Begin(Game("a")));
        Assert.Empty(display.Calls);
    }

    [Fact]
    public void ADisplayUnpluggedMidGame_IsNotAskedToRestore()
    {
        var (service, display) = Make();
        service.Begin(Game("a"));
        display.Attached.Clear();

        service.End("a");

        Assert.DoesNotContain(display.Calls, c => c.StartsWith("restore"));
    }

    [Fact]
    public void AFailedPutBack_DoesNotThrow_AndTheNextGameCanSetTheDisplay()
    {
        var (service, display) = Make();
        service.Begin(Game("a"));
        display.RestoreResult = DisplayChange.Failed;

        service.End("a");

        display.RestoreResult = DisplayChange.Ok;
        Assert.Equal(DisplayApply.Applied, service.Begin(Game("b", 2560, 1440, 144)));
    }

    [Fact]
    public void ClosingTrayTrigger_PutsTheDisplayBack_WhoeverSetIt()
    {
        var (service, display) = Make();
        service.Begin(Game("a"));

        service.ReleaseAll();
        service.ReleaseAll();

        Assert.Single(display.Calls, c => c.StartsWith("restore"));
        Assert.Equal(display.Desktop, display.Now);
    }

    [Fact]
    public void AGameStartingWhileTheDisplayIsBeingPutBack_IsToldItIsBusy_AndItsModeIsNotUndone()
    {
        var (service, display) = Make();
        service.Begin(Game("a"));
        DisplayApply duringPutBack = DisplayApply.NotRequested;
        display.DuringRestore = () => duringPutBack = service.Begin(Game("b", 2560, 1440, 144));

        service.End("a");

        Assert.Equal(DisplayApply.Busy, duringPutBack);
        Assert.DoesNotContain(display.Calls, c => c == "apply 2560 × 1440 @ 144 Hz");
        // And the display is free again afterwards.
        display.DuringRestore = null;
        Assert.Equal(DisplayApply.Applied, service.Begin(Game("b", 2560, 1440, 144)));
    }

    [Fact]
    public void AnEndThatIsRunTwiceAtOnce_PutsTheDisplayBackOnce()
    {
        var (service, display) = Make();
        service.Begin(Game("a"));
        display.DuringRestore = () => service.End("a");

        service.End("a");

        Assert.Single(display.Calls, c => c.StartsWith("restore"));
    }

    [Fact]
    public void ANoteIsWrittenBeforeTheDisplayChanges_AndRemovedWhenItIsPutBack()
    {
        var (service, display) = Make();

        service.Begin(Game("a"));
        Assert.True(System.IO.File.Exists(SnapshotPath));
        Assert.Contains("DISPLAY1", System.IO.File.ReadAllText(SnapshotPath));

        service.End("a");
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void IfTheNoteCannotBeWritten_TheDisplayIsNotChanged()
    {
        var display = new FakeDisplay();
        string blocker = System.IO.Path.Combine(_dir, "blocker");
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(blocker, "a file where a folder would have to be");
        var service = new DisplayModeService(display, System.IO.Path.Combine(blocker, "display-session.json"));

        Assert.Equal(DisplayApply.Failed, service.Begin(Game("a")));
        Assert.DoesNotContain(display.Calls, c => c.StartsWith("apply"));
    }

    [Fact]
    public void AFailedApply_LeavesNoNote()
    {
        var (service, display) = Make();
        display.ApplyResult = DisplayChange.Failed;

        service.Begin(Game("a"));

        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AfterACrash_TheNextStartPutsTheDisplayBack()
    {
        var (first, display) = Make();
        first.Begin(Game("a"));
        // TrayTrigger is killed here: nothing runs End. The next run starts with the display as the game left it.
        var next = new DisplayModeService(display, SnapshotPath);

        next.RecoverIfNeeded();

        Assert.Equal(display.Desktop, display.Now);
        Assert.Single(display.Calls, c => c.StartsWith("restore"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AfterACrash_ADisplayThatHasChangedSince_IsLeftAlone()
    {
        var (first, display) = Make();
        first.Begin(Game("a"));
        display.Now = new DisplayMode(2560, 1440, 144);
        var next = new DisplayModeService(display, SnapshotPath);

        next.RecoverIfNeeded();

        Assert.DoesNotContain(display.Calls, c => c.StartsWith("restore"));
        Assert.Equal(new DisplayMode(2560, 1440, 144), display.Now);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AfterACrash_AMonitorThatIsGone_IsNotAskedToRestore()
    {
        var (first, display) = Make();
        first.Begin(Game("a"));
        display.Attached.Clear();

        new DisplayModeService(display, SnapshotPath).RecoverIfNeeded();

        Assert.DoesNotContain(display.Calls, c => c.StartsWith("restore"));
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AfterACrash_ARestoreThatFails_IsTriedAgainAtTheNextStart()
    {
        var (first, display) = Make();
        first.Begin(Game("a"));
        display.RestoreResult = DisplayChange.Failed;
        new DisplayModeService(display, SnapshotPath).RecoverIfNeeded();
        Assert.True(System.IO.File.Exists(SnapshotPath));

        display.RestoreResult = DisplayChange.Ok;
        new DisplayModeService(display, SnapshotPath).RecoverIfNeeded();

        Assert.Equal(display.Desktop, display.Now);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void AnUnreadableNote_IsDiscarded_AndNothingIsChanged()
    {
        var (service, display) = Make();
        System.IO.Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(SnapshotPath, "{ not json");

        service.RecoverIfNeeded();

        Assert.Empty(display.Calls);
        Assert.False(System.IO.File.Exists(SnapshotPath));
    }

    [Fact]
    public void NoNote_MeansNothingToRecover()
    {
        var (service, display) = Make();

        service.RecoverIfNeeded();

        Assert.Empty(display.Calls);
    }

    private static readonly DisplayMode[] Sample =
    [
        new(3840, 2160, 60), new(2560, 1440, 144), new(2560, 1440, 60), new(1920, 1080, 144), new(1920, 1080, 60),
    ];

    [Fact]
    public void TheResolutionList_StartsWithNoChange_ThenEachResolutionOnce_LargestFirst()
    {
        var options = ViewModels.GameEditViewModel.BuildResolutionOptions(Sample, null);

        Assert.Equal([ViewModels.GameEditViewModel.NoDisplayChangeLabel, "3840 × 2160", "2560 × 1440", "1920 × 1080"], options.Select(o => o.Label).ToList());
        Assert.True(options[0].IsNone);
    }

    [Fact]
    public void TheRefreshRateList_ShowsOnlyWhatThatResolutionOffers_FastestFirst()
    {
        var qhd = new ViewModels.GameEditViewModel.ResolutionOption(2560, 1440, "2560 × 1440");

        var rates = ViewModels.GameEditViewModel.BuildRateOptions(Sample, qhd, null);

        Assert.Equal(["144 Hz", "60 Hz"], rates.Select(r => r.Label).ToList());
        Assert.Equal(["60 Hz"], ViewModels.GameEditViewModel.BuildRateOptions(Sample, new(3840, 2160, "4K"), null).Select(r => r.Label).ToList());
    }

    [Fact]
    public void NoResolutionChosen_MeansNoRefreshRateList()
    {
        Assert.Empty(ViewModels.GameEditViewModel.BuildRateOptions(Sample, new(0, 0, "none"), null));
    }

    [Fact]
    public void ASavedResolutionTheDisplayDoesNotList_IsKeptAndLabelled_SoSavingDoesNotDropIt()
    {
        var saved = new DisplayMode(1280, 720, 75);

        var resolutions = ViewModels.GameEditViewModel.BuildResolutionOptions(Sample, saved);
        var kept = resolutions[^1];
        var rates = ViewModels.GameEditViewModel.BuildRateOptions(Sample, kept, saved);

        Assert.Contains("this display doesn't list it", kept.Label);
        Assert.Equal(75, Assert.Single(rates).Hz);
    }

    [Fact]
    public void ASavedRateTheResolutionDoesNotOffer_IsKeptAndLabelled()
    {
        var saved = new DisplayMode(2560, 1440, 120);
        var qhd = ViewModels.GameEditViewModel.BuildResolutionOptions(Sample, saved).Single(o => o.Width == 2560);

        var rates = ViewModels.GameEditViewModel.BuildRateOptions(Sample, qhd, saved);

        Assert.Equal(["144 Hz", "60 Hz", "120 Hz (this display doesn't list it)"], rates.Select(r => r.Label).ToList());
    }

    [Fact]
    public void ASavedModeTheDisplayLists_IsNotAddedAgain()
    {
        var saved = new DisplayMode(1920, 1080, 60);

        Assert.Equal(4, ViewModels.GameEditViewModel.BuildResolutionOptions(Sample, saved).Count);
        Assert.Equal(2, ViewModels.GameEditViewModel.BuildRateOptions(Sample, new(1920, 1080, "x"), saved).Count);
    }

    [Fact]
    public void TheModeList_IsLargestFirst_ThenFastest()
    {
        var (service, _) = Make();

        Assert.Equal(
            [new DisplayMode(3840, 2160, 60), new DisplayMode(2560, 1440, 144), new DisplayMode(1920, 1080, 144), new DisplayMode(1920, 1080, 60)],
            service.AvailableModes().ToList());
    }
}
