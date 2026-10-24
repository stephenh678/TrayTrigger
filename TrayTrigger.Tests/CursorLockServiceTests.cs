using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>Windows' mouse and window state, kept in memory.</summary>
internal sealed class FakeCursor : ICursorBackend
{
    public static readonly ScreenRect Left = new(0, 0, 1920, 1080);
    public static readonly ScreenRect Right = new(1920, 0, 3840, 1080);
    public static readonly ScreenRect Desktop = new(0, 0, 3840, 1080);

    public event Action? Changed;
    public bool Watching;
    public ForegroundWindow? Front;
    public Dictionary<IntPtr, ScreenRect> Monitors = new() { [(IntPtr)1] = Left, [(IntPtr)2] = Right };
    /// <summary>Child pid, parent pid.</summary>
    public Dictionary<int, int> Parents = [];
    public ScreenRect Clip_ = Desktop;
    public List<string> Calls = [];
    public bool FailClip;
    public bool ThrowOnForeground;

    /// <summary>Runs inside Watch, as the window thread's own events would.</summary>
    public Action? DuringWatch;
    public void Watch(bool on) { Watching = on; Calls.Add(on ? "watch" : "unwatch"); DuringWatch?.Invoke(); }
    public List<int> Forgotten = [];
    public void Forget(int processId) => Forgotten.Add(processId);
    public void Raise() => Changed?.Invoke();

    public ForegroundWindow? Foreground()
    {
        if (ThrowOnForeground) throw new UnauthorizedAccessException("access denied");
        return Front;
    }

    public bool IsSameOrDescendant(int ancestor, int pid)
    {
        for (int depth = 0; depth < 8; depth++)
        {
            if (pid == ancestor) return true;
            if (!Parents.TryGetValue(pid, out pid)) return false;
        }
        return false;
    }

    public ScreenRect? MonitorOf(IntPtr window) => Monitors.TryGetValue(window, out var m) ? m : null;
    public IReadOnlyList<ScreenRect> AllMonitors() => Monitors.Values.ToList();
    public ScreenRect? CurrentClip() => Clip_;

    public bool Clip(ScreenRect? rect)
    {
        Calls.Add(rect is { } r ? $"clip {r}" : "free");
        if (FailClip) return false;
        Clip_ = rect ?? Desktop;
        return true;
    }
}

/// <summary>Keep the mouse on the game's screen: held only while the game is in front, and always freed.</summary>
public class CursorLockServiceTests
{
    private const int GamePid = 100;

    private static GameEntry Game(string id = "a", bool on = true) => new() { Id = id, Name = "Game " + id, KeepMouseOnGameScreen = on };

    private static (CursorLockService Service, FakeCursor Cursor) Make() { var c = new FakeCursor(); return (new CursorLockService(c), c); }

    [Fact]
    public void AGameWithoutTheOption_IsNeverWatched()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);

        service.Track(Game(on: false), GamePid);

        Assert.Empty(cursor.Calls);
        Assert.False(cursor.Watching);
    }

    [Fact]
    public void TheGameInFront_HoldsTheMouseOnItsOwnMonitor()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)2);

        service.Track(Game(), GamePid);

        Assert.True(cursor.Watching);
        Assert.Equal(FakeCursor.Right, cursor.Clip_);
    }

    [Fact]
    public void AltTab_FreesTheMouse_AndComingBackHoldsItAgain()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);
        Assert.Equal(FakeCursor.Left, cursor.Clip_);

        cursor.Front = new ForegroundWindow(555, (IntPtr)2);
        cursor.Raise();
        Assert.Equal(FakeCursor.Desktop, cursor.Clip_);

        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        cursor.Raise();
        Assert.Equal(FakeCursor.Left, cursor.Clip_);
    }

    [Fact]
    public void AWindowOfAProcessTheGameStarted_CountsAsTheGame()
    {
        var (service, cursor) = Make();
        cursor.Parents[200] = GamePid;
        cursor.Parents[300] = 200;
        cursor.Front = new ForegroundWindow(300, (IntPtr)1);

        service.Track(Game(), GamePid);

        Assert.Equal(FakeCursor.Left, cursor.Clip_);
    }

    [Fact]
    public void ATighterConfinementTheGameSetItself_IsLeftAlone_AndNotFreedByUs()
    {
        var (service, cursor) = Make();
        var camera = new ScreenRect(900, 500, 1000, 600);
        cursor.Clip_ = camera;
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);

        service.Track(Game(), GamePid);
        Assert.Equal(camera, cursor.Clip_);
        Assert.DoesNotContain(cursor.Calls, c => c.StartsWith("clip"));

        cursor.Front = new ForegroundWindow(555, (IntPtr)1);
        cursor.Raise();
        Assert.DoesNotContain(cursor.Calls, c => c == "free");
        Assert.Equal(camera, cursor.Clip_);
    }

    [Fact]
    public void AClipTheGameWidened_IsBroughtBackToTheMonitor()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        cursor.Clip_ = FakeCursor.Desktop;
        cursor.Raise();

        Assert.Equal(FakeCursor.Left, cursor.Clip_);
    }

    [Fact]
    public void NothingIsFreedThatWeNeverHeld()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(555, (IntPtr)1);
        service.Track(Game(), GamePid);

        cursor.Raise();
        service.Untrack("a");

        Assert.DoesNotContain(cursor.Calls, c => c == "free");
    }

    [Fact]
    public void TheGameEnding_FreesTheMouse_AndStopsWatching()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        service.Untrack("a");
        service.Untrack("a");

        Assert.Equal(FakeCursor.Desktop, cursor.Clip_);
        Assert.False(cursor.Watching);
        Assert.Single(cursor.Calls, c => c == "free");
    }

    [Fact]
    public void ClosingTrayTrigger_FreesTheMouse()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        service.ReleaseAll();

        Assert.Equal(FakeCursor.Desktop, cursor.Clip_);
    }

    [Fact]
    public void TwoGames_TheMouseIsHeldForWhicheverIsInFront_AndFreedWhenTheLastEnds()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game("a"), GamePid);
        service.Track(Game("b"), 400);

        cursor.Front = new ForegroundWindow(400, (IntPtr)2);
        cursor.Raise();
        Assert.Equal(FakeCursor.Right, cursor.Clip_);

        service.Untrack("a");
        Assert.True(cursor.Watching);
        Assert.Equal(FakeCursor.Right, cursor.Clip_);

        service.Untrack("b");
        Assert.Equal(FakeCursor.Desktop, cursor.Clip_);
        Assert.False(cursor.Watching);
    }

    [Fact]
    public void AMonitorThatChanged_IsFollowedOnTheNextEvent()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        cursor.Monitors[(IntPtr)1] = new ScreenRect(0, 0, 1280, 720);
        cursor.Raise();

        Assert.Equal(new ScreenRect(0, 0, 1280, 720), cursor.Clip_);
    }

    [Fact]
    public void AWindowThatCannotBeRead_FreesTheMouse_InsteadOfFailing()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);
        Assert.Equal(FakeCursor.Left, cursor.Clip_);

        cursor.ThrowOnForeground = true;
        cursor.Raise();

        Assert.Equal(FakeCursor.Desktop, cursor.Clip_);
    }

    [Fact]
    public void NoWindowInFront_FreesTheMouse()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        cursor.Front = null;
        cursor.Raise();

        Assert.Equal(FakeCursor.Desktop, cursor.Clip_);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartingAndStoppingTheWatch_DoesNotWaitOnAnEventFromAnotherThread()
    {
        // The window thread raising an event while Track or Untrack starts the watch must not deadlock them.
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        bool eventHandled = true;
        cursor.DuringWatch = () => eventHandled &= System.Threading.Tasks.Task.Run(cursor.Raise).Wait(TimeSpan.FromSeconds(2));

        var work = System.Threading.Tasks.Task.Run(() =>
        {
            service.Track(Game(), GamePid);
            service.Untrack("a");
        });

        var finished = await System.Threading.Tasks.Task.WhenAny(work, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(work, finished);
        Assert.True(eventHandled, "an event raised while the watch was starting waited on the service");
        Assert.Equal(FakeCursor.Desktop, cursor.Clip_);
    }

    [Fact]
    public void AnEndedGamesProcessId_IsForgotten_SoAProgramGivenItLaterIsNotTakenForTheGame()
    {
        var (service, cursor) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        service.Untrack("a");

        Assert.Equal([GamePid], cursor.Forgotten);
    }

    [Fact]
    public void AClipWindowsRefuses_DoesNotThrow_AndIsNotCountedAsHeld()
    {
        var (service, cursor) = Make();
        cursor.FailClip = true;
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);

        service.Track(Game(), GamePid);
        cursor.Front = new ForegroundWindow(555, (IntPtr)1);
        cursor.Raise();

        Assert.DoesNotContain(cursor.Calls, c => c == "free");
    }

    [Fact]
    public void WithOneMonitor_TheClipIsTheWholeDesktop_AndIsStillFreedAtTheEnd()
    {
        var (service, cursor) = Make();
        cursor.Monitors[(IntPtr)1] = FakeCursor.Desktop;
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);

        service.Track(Game(), GamePid);
        Assert.Contains(cursor.Calls, c => c.StartsWith("clip"));

        service.Untrack("a");
        Assert.Contains(cursor.Calls, c => c == "free");
    }
}
