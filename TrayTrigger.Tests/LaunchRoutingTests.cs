using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class LaunchRoutingTests
{
    private static readonly LaunchClientAvailability AllInstalled = new(
        GogGalaxyInstalled: true, GogGalaxyRunning: false, EaAppInstalled: true, EpicLauncherInstalled: true, UbisoftConnectInstalled: true);

    private static readonly LaunchClientAvailability NoneInstalled = new(false, false, false, false, false);

    private static bool Exists(string _) => true;
    private static bool Missing(string _) => false;

    [Fact]
    public void BlankPath_IsMissing()
    {
        Assert.Equal(LaunchRoute.MissingPath, LaunchRouter.Resolve(new GameEntry { ExecutablePath = "" }, NoneInstalled, Exists));
    }

    [Fact]
    public void SteamTagged_GoesToSteam()
    {
        var game = new GameEntry { IsSteamGame = true, SteamAppId = "440", ExecutablePath = "steam://rungameid/440" };
        Assert.Equal(LaunchRoute.Steam, LaunchRouter.Resolve(game, NoneInstalled, Exists));
    }

    [Fact]
    public void SteamUrl_WithoutTag_GoesToSteam()
    {
        var game = new GameEntry { ExecutablePath = "steam://rungameid/440" };
        Assert.Equal(LaunchRoute.Steam, LaunchRouter.Resolve(game, NoneInstalled, Exists));
    }

    [Fact]
    public void SteamTagged_LaunchDirectly_WithRealExe_IsDirectExe()
    {
        var game = new GameEntry { IsSteamGame = true, SteamAppId = "440", ExecutablePath = @"C:\Games\tf2\hl2.exe", LaunchDirectly = true };
        Assert.Equal(LaunchRoute.DirectExe, LaunchRouter.Resolve(game, NoneInstalled, Exists));
        // ...but not when that exe doesn't exist - fall back to Steam rather than a missing-file error.
        Assert.Equal(LaunchRoute.Steam, LaunchRouter.Resolve(game, NoneInstalled, Missing));
    }

    [Theory]
    [InlineData(true, false, false, LaunchRoute.GogGalaxy)]   // installed, not running -> silent client launch
    [InlineData(true, true, false, LaunchRoute.GogDirect)]    // installed and running -> direct, avoids Play-button prompt
    [InlineData(false, false, false, LaunchRoute.GogDirect)]  // not installed
    [InlineData(true, false, true, LaunchRoute.GogDirect)]    // user asked for direct
    public void Gog_RoutesByClientState(bool installed, bool running, bool launchDirectly, LaunchRoute expected)
    {
        var game = new GameEntry { IsGogGame = true, GogGameId = "123", ExecutablePath = @"C:\GOG\cp2077\bin\x64\Cyberpunk2077.exe", LaunchDirectly = launchDirectly };
        var clients = new LaunchClientAvailability(installed, running, false, false, false);
        Assert.Equal(expected, LaunchRouter.Resolve(game, clients, Exists));
    }

    [Fact]
    public void Gog_WithoutGameId_FallsThroughToPlainExe()
    {
        var game = new GameEntry { IsGogGame = true, GogGameId = null, ExecutablePath = @"C:\GOG\g\g.exe" };
        Assert.Equal(LaunchRoute.DirectExe, LaunchRouter.Resolve(game, AllInstalled, Exists));
    }

    [Theory]
    [InlineData(true, false, LaunchRoute.EaClient)]
    [InlineData(false, false, LaunchRoute.EaDirect)]
    [InlineData(true, true, LaunchRoute.EaDirect)]
    public void Ea_RoutesByClientState(bool installed, bool launchDirectly, LaunchRoute expected)
    {
        var game = new GameEntry { IsEaGame = true, EaContentId = "Origin.OFR.50.0001", ExecutablePath = @"C:\EA\x\x.exe", LaunchDirectly = launchDirectly };
        var clients = new LaunchClientAvailability(false, false, installed, false, false);
        Assert.Equal(expected, LaunchRouter.Resolve(game, clients, Exists));
    }

    [Theory]
    [InlineData(true, false, LaunchRoute.EpicClient)]
    [InlineData(false, false, LaunchRoute.EpicDirect)]
    [InlineData(true, true, LaunchRoute.EpicDirect)]
    public void Epic_RoutesByClientState(bool installed, bool launchDirectly, LaunchRoute expected)
    {
        var game = new GameEntry { IsEpicGame = true, EpicAppName = "Fortnite", ExecutablePath = @"C:\Epic\f\f.exe", LaunchDirectly = launchDirectly };
        var clients = new LaunchClientAvailability(false, false, false, installed, false);
        Assert.Equal(expected, LaunchRouter.Resolve(game, clients, Exists));
    }

    [Theory]
    [InlineData(true, false, LaunchRoute.UbisoftClient)]
    [InlineData(false, false, LaunchRoute.UbisoftDirect)]
    [InlineData(true, true, LaunchRoute.UbisoftDirect)]
    public void Ubisoft_RoutesByClientState(bool installed, bool launchDirectly, LaunchRoute expected)
    {
        var game = new GameEntry { IsUbisoftGame = true, UbisoftGameId = "5678", ExecutablePath = @"C:\Ubi\r6\r6.exe", LaunchDirectly = launchDirectly };
        var clients = new LaunchClientAvailability(false, false, false, false, installed);
        Assert.Equal(expected, LaunchRouter.Resolve(game, clients, Exists));
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/x?action=launch", LaunchRoute.ProtocolUrl)]
    [InlineData("https://example.com/play", LaunchRoute.ProtocolUrl)]
    [InlineData("ms-msdt://something", LaunchRoute.RefusedUrl)]
    [InlineData("javascript:alert(1)", LaunchRoute.RefusedUrl)]
    public void ProtocolUrls_AreAllowListed(string url, LaunchRoute expected)
    {
        Assert.Equal(expected, LaunchRouter.Resolve(new GameEntry { ExecutablePath = url }, NoneInstalled, Exists));
    }

    [Fact]
    public void PlainExe_IsDirect()
    {
        Assert.Equal(LaunchRoute.DirectExe, LaunchRouter.Resolve(new GameEntry { ExecutablePath = @"C:\Games\a\a.exe" }, AllInstalled, Exists));
    }

    [Fact]
    public void SteamUrl_NoArguments_UsesRunGameId()
    {
        Assert.Equal("steam://rungameid/440", LaunchRouter.BuildSteamLaunchUrl("440", null));
        Assert.Equal("steam://rungameid/440", LaunchRouter.BuildSteamLaunchUrl("440", "   "));
    }

    [Fact]
    public void SteamUrl_WithArguments_UsesRunForm()
    {
        Assert.Equal("steam://run/440//-novid%20-console/", LaunchRouter.BuildSteamLaunchUrl("440", "-novid -console"));
        // "/" would terminate the URL early and "%" would be mis-decoded, so both are encoded.
        Assert.Equal("steam://run/1//-x%2Fy%2050%25/", LaunchRouter.BuildSteamLaunchUrl("1", "-x/y 50%"));
    }

    [Theory]
    [InlineData(LaunchRoute.Steam, LauncherPlatform.Steam)]
    [InlineData(LaunchRoute.GogGalaxy, LauncherPlatform.Gog)]
    [InlineData(LaunchRoute.GogDirect, LauncherPlatform.Gog)]
    [InlineData(LaunchRoute.EaClient, LauncherPlatform.Ea)]
    [InlineData(LaunchRoute.EpicDirect, LauncherPlatform.Epic)]
    [InlineData(LaunchRoute.UbisoftClient, LauncherPlatform.Ubisoft)]
    public void ClientPlatform_MapsRoutes(LaunchRoute route, LauncherPlatform expected)
    {
        Assert.Equal(expected, LaunchRouter.ClientPlatformFor(route));
    }

    [Theory]
    [InlineData(LaunchRoute.DirectExe)]
    [InlineData(LaunchRoute.ProtocolUrl)]
    public void ClientPlatform_NullForNonClientRoutes(LaunchRoute route)
    {
        Assert.Null(LaunchRouter.ClientPlatformFor(route));
    }
}
