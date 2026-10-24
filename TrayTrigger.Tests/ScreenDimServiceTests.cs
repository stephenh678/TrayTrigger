using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

internal sealed class FakeDimSurface : IDimSurface
{
    public List<string> Calls = [];
    public IReadOnlyList<ScreenRect> Shown = [];
    public void Show(IReadOnlyList<ScreenRect> areas) { Calls.Add("show " + string.Join(" ", areas)); Shown = areas; }
    public void Hide() { Calls.Add("hide"); Shown = []; }
}

/// <summary>Dim the other screens: only while the game is in front, and always lifted.</summary>
public class ScreenDimServiceTests
{
    private const int GamePid = 100;

    private static GameEntry Game(string id = "a", bool on = true) => new() { Id = id, Name = "Game " + id, DimOtherScreens = on };

    private static (ScreenDimService Service, FakeCursor Cursor, FakeDimSurface Surface) Make()
    {
        var cursor = new FakeCursor();
        var surface = new FakeDimSurface();
        return (new ScreenDimService(cursor, surface), cursor, surface);
    }

    [Fact]
    public void AGameWithoutTheOption_DimsNothing()
    {
        var (service, cursor, surface) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);

        service.Track(Game(on: false), GamePid);

        Assert.Empty(surface.Calls);
        Assert.False(cursor.Watching);
    }

    [Fact]
    public void TheGameInFront_DimsEveryOtherMonitor()
    {
        var (service, cursor, surface) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);

        service.Track(Game(), GamePid);

        Assert.Equal([FakeCursor.Right], surface.Shown);
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)2);
        cursor.Raise();
        Assert.Equal([FakeCursor.Left], surface.Shown);
    }

    [Fact]
    public void AltTab_LiftsTheDimming_AndComingBackDimsAgain()
    {
        var (service, cursor, surface) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        cursor.Front = new ForegroundWindow(555, (IntPtr)2);
        cursor.Raise();
        Assert.Empty(surface.Shown);

        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        cursor.Raise();
        Assert.Equal([FakeCursor.Right], surface.Shown);
    }

    [Fact]
    public void TheSameScreensAgain_AreNotShownAgain()
    {
        var (service, cursor, surface) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        cursor.Raise();
        cursor.Raise();

        Assert.Single(surface.Calls);
    }

    [Fact]
    public void WithOneMonitor_NothingIsDimmed()
    {
        var (service, cursor, surface) = Make();
        cursor.Monitors = new() { [(IntPtr)1] = FakeCursor.Desktop };
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);

        service.Track(Game(), GamePid);

        Assert.DoesNotContain(surface.Calls, c => c.StartsWith("show"));
    }

    [Fact]
    public void TheGameEnding_LiftsTheDimming_AndStopsWatching()
    {
        var (service, cursor, surface) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        service.Untrack("a");
        service.Untrack("a");

        Assert.Empty(surface.Shown);
        Assert.False(cursor.Watching);
        Assert.Single(surface.Calls, c => c == "hide");
    }

    [Fact]
    public void ClosingTrayTrigger_LiftsTheDimming()
    {
        var (service, cursor, surface) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        service.ReleaseAll();

        Assert.Empty(surface.Shown);
    }

    [Fact]
    public void AWindowThatCannotBeRead_LiftsTheDimming_InsteadOfFailing()
    {
        var (service, cursor, surface) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        service.Track(Game(), GamePid);

        cursor.ThrowOnForeground = true;
        cursor.Raise();

        Assert.Empty(surface.Shown);
    }

    [Fact]
    public void AProgramTheGameStarted_CountsAsTheGame()
    {
        var (service, cursor, surface) = Make();
        cursor.Parents[200] = GamePid;
        cursor.Front = new ForegroundWindow(200, (IntPtr)2);

        service.Track(Game(), GamePid);

        Assert.Equal([FakeCursor.Left], surface.Shown);
    }

    [Fact]
    public async System.Threading.Tasks.Task StartingTheWatch_DoesNotWaitOnAnEventFromAnotherThread()
    {
        var (service, cursor, _) = Make();
        cursor.Front = new ForegroundWindow(GamePid, (IntPtr)1);
        bool eventHandled = true;
        cursor.DuringWatch = () => eventHandled &= System.Threading.Tasks.Task.Run(cursor.Raise).Wait(TimeSpan.FromSeconds(2));

        var work = System.Threading.Tasks.Task.Run(() => { service.Track(Game(), GamePid); service.Untrack("a"); });

        Assert.Same(work, await System.Threading.Tasks.Task.WhenAny(work, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.True(eventHandled);
    }
}
