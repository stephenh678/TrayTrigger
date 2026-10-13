using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class PcGamingWikiLinkTests
{
    [Fact]
    public void SteamGame_GoesByAppId()
    {
        Assert.Equal("https://www.pcgamingwiki.com/api/appid.php?appid=620",
            PcGamingWikiLink.For(" 620 ", "Portal 2"));
    }

    [Fact]
    public void OtherGame_SearchesByEscapedName()
    {
        Assert.Equal("https://www.pcgamingwiki.com/w/index.php?search=Baldur%27s%20Gate%203%20%26%20more",
            PcGamingWikiLink.For(null, "Baldur's Gate 3 & more"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12 34")]
    [InlineData("")]
    public void UnusableAppId_FallsBackToTheName(string appId)
    {
        Assert.Equal("https://www.pcgamingwiki.com/w/index.php?search=Hades",
            PcGamingWikiLink.For(appId, "Hades"));
    }

    [Fact]
    public void NoAppIdAndNoName_HasNoLink()
    {
        Assert.Null(PcGamingWikiLink.For("", "  "));
    }
}
