using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>"Keep game launchers minimized": the setting's default, and the start-quietly switches
/// it adds to the Epic and GOG Galaxy launches.</summary>
public class KeepLaunchersMinimizedTests
{
    [Fact]
    public void DefaultsOn()
    {
        Assert.True(new AppSettings().KeepLaunchersMinimized);
    }

    [Fact]
    public void EpicLaunchUrl_AsksForSilentOnlyWhenKeptMinimized()
    {
        Assert.Equal("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true", ProcessLauncherService.BuildEpicLaunchUrl("Fortnite", silent: true));
        Assert.Equal("com.epicgames.launcher://apps/Fortnite?action=launch", ProcessLauncherService.BuildEpicLaunchUrl("Fortnite", silent: false));
        Assert.Equal("com.epicgames.launcher://apps/a%20b?action=launch&silent=true", ProcessLauncherService.BuildEpicLaunchUrl("a b", silent: true));
    }

    [Fact]
    public void GalaxyArguments_StartGalaxyInTheBackgroundOnlyWhenKeptMinimized()
    {
        Assert.Equal(new[] { "/launchViaAutostart", "/command=runGame", "/gameId=1599916752", @"/path=D:\Games\Trepang2" },
            ProcessLauncherService.BuildGalaxyRunGameArguments("1599916752", @"D:\Games\Trepang2", launchMinimized: true));
        Assert.Equal(new[] { "/command=runGame", "/gameId=1599916752", @"/path=D:\Games\Trepang2" },
            ProcessLauncherService.BuildGalaxyRunGameArguments("1599916752", @"D:\Games\Trepang2", launchMinimized: false));
        Assert.Equal(new[] { "/launchViaAutostart", "/command=runGame", "/gameId=1" },
            ProcessLauncherService.BuildGalaxyRunGameArguments("1", null, launchMinimized: true));
    }
}
