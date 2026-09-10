using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class UrlProtocolHelperTests
{
    [Theory]
    [InlineData("steam://rungameid/440")]
    [InlineData("STEAM://rungameid/440")]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch")]
    [InlineData("origin2://game/launch?offerIds=123")]
    [InlineData("uplay://launch/123/0")]
    [InlineData("goggalaxy://openGameView/123")]
    [InlineData("https://example.com/play")]
    public void IsAllowedLaunchUrl_AcceptsLauncherAndWebSchemes(string url)
    {
        Assert.True(UrlProtocolHelper.IsAllowedLaunchUrl(url));
    }

    [Theory]
    [InlineData("ms-msdt:-id PCWDiagnostic /skip force")]
    [InlineData("search-ms:query=x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("shell:startup")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"C:\Games\Foo\foo.exe")]
    [InlineData(@"\\server\share\evil.exe")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void IsAllowedLaunchUrl_RejectsEverythingElse(string? url)
    {
        Assert.False(UrlProtocolHelper.IsAllowedLaunchUrl(url));
    }

    [Theory]
    [InlineData("440")]
    [InlineData("1245620")]
    public void IsValidSteamAppId_AcceptsDigits(string id)
    {
        Assert.True(UrlProtocolHelper.IsValidSteamAppId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("44a")]
    [InlineData(@"..\..\x")]
    [InlineData("440/extra")]
    [InlineData("1234567890123")]
    public void IsValidSteamAppId_RejectsAnythingElse(string? id)
    {
        Assert.False(UrlProtocolHelper.IsValidSteamAppId(id));
    }
}
