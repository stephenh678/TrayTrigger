using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>Play on a game TrayTrigger is already tracking: what the status line says, since nothing is launched again.</summary>
public class PlayOnTrackedGameTests
{
    [Theory]
    [InlineData(ProcessLauncherService.TrackedLaunch.Running, "Hades is already running - switched to it")]
    [InlineData(ProcessLauncherService.TrackedLaunch.Starting, "Hades is still starting")]
    [InlineData(ProcessLauncherService.TrackedLaunch.Suspended, "Resumed Hades")]
    public void TheStatusLine_SaysWhatHappened_NotThatItLaunched(ProcessLauncherService.TrackedLaunch state, string expected)
    {
        string message = LibraryViewModel.AlreadyTrackedMessage("Hades", state);

        Assert.Equal(expected, message);
        Assert.DoesNotContain("Launched", message);
    }
}
