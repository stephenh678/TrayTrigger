using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// What the tray icon says while a game is up.
///
/// The tooltip is built from ActiveGameSession.GameStarted, which is false at dispatch and true
/// once the game is really running. The text was always right for the state it was handed; the bug
/// was that nothing rebuilt it when the state changed, so a session dispatched as "Starting Elden
/// Ring via Steam" still said that an hour later. ProcessLauncherService.SessionGameStarted is the
/// signal that brings App.UpdateTrayToolTip back; MarkGameStarted is the one place that raises it.
/// </summary>
public class TrayToolTipTests
{
    private static ActiveGameSession Session(string name, LaunchRoute route = LaunchRoute.Steam) =>
        new(new GameEntry { Name = name }, route);

    [Fact]
    public void BeforeTheGameIsUp_ItSaysStarting()
    {
        var session = Session("Elden Ring");
        string text = App.BuildTrayToolTipText([session], DateTime.Now);
        Assert.Equal("Starting Elden Ring via Steam", text);
    }

    [Fact]
    public void OnceTheGameIsUp_ItSaysPlayingWithElapsedTime()
    {
        var session = Session("Elden Ring");
        var now = DateTime.Now;
        session.GameStarted = true;
        session.StartedAt = now.AddMinutes(-95);

        string text = App.BuildTrayToolTipText([session], now);
        Assert.Equal("Playing Elden Ring · 1h 35m", text);
    }

    [Fact]
    public void NoSessions_FallsBackToTheProductName()
    {
        Assert.Equal("TrayTrigger - Game Launcher", App.BuildTrayToolTipText([], DateTime.Now));
    }

    [Fact]
    public void TooLongForTheShell_IsTruncated()
    {
        var session = Session(new string('x', 400));
        session.GameStarted = true;
        string text = App.BuildTrayToolTipText([session], DateTime.Now);

        // Windows silently drops a tooltip over 128 chars, so this must be clamped, not just long.
        Assert.True(text.Length <= 127, $"tooltip was {text.Length} characters");
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void MarkGameStarted_SetsTheFlagAndRaisesOnce()
    {
        var launcher = NewLauncher();
        var session = Session("Hades");
        int raised = 0;
        launcher.SessionGameStarted += _ => raised++;

        var startedAt = DateTime.Now.AddMinutes(-3);
        launcher.MarkGameStarted(session, startedAt);

        Assert.True(session.GameStarted);
        Assert.Equal(startedAt, session.StartedAt);
        Assert.Equal(1, raised);

        // A Steam poll tick and a later stub handoff can both reach this for one session; the
        // second must update the clock without announcing a second launch.
        var corrected = DateTime.Now;
        launcher.MarkGameStarted(session, corrected);
        Assert.Equal(corrected, session.StartedAt);
        Assert.Equal(1, raised);
    }

    /// <summary>The tooltip a subscriber would render before and after the event fires.</summary>
    [Fact]
    public void TheEventIsWhatTurnsStartingIntoPlaying()
    {
        var launcher = NewLauncher();
        var session = Session("Hades");
        string? textAtEvent = null;
        launcher.SessionGameStarted += s => textAtEvent = App.BuildTrayToolTipText([s], DateTime.Now);

        Assert.StartsWith("Starting Hades", App.BuildTrayToolTipText([session], DateTime.Now));
        launcher.MarkGameStarted(session, DateTime.Now);
        Assert.Equal("Playing Hades · 0m", textAtEvent);
    }

    private static ProcessLauncherService NewLauncher()
    {
        string root = Path.Combine(Path.GetTempPath(), "TrayTriggerTrayTip", Guid.NewGuid().ToString("N"));
        var storage = new StorageService(Path.Combine(root, "roaming"), Path.Combine(root, "local"));
        return new ProcessLauncherService(
            storage, new PerformanceProfileService(storage), new GameScriptService(),
            new SteamScannerService(), new GogScannerService(), new EaScannerService(),
            new EpicScannerService(), new UbisoftScannerService(), new XboxScannerService());
    }
}
