using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class SteamMetadataServiceTests
{
    [Fact]
    public void CleanHtmlText_StripsTagsAndDecodesEntities()
    {
        string html = "<p>Hello &amp; welcome to <b>the game</b>!</p>";
        Assert.Equal("Hello & welcome to the game !", SteamMetadataService.CleanHtmlText(html));
    }

    [Fact]
    public void CleanHtmlText_StripsBbCodeAndImgBlocks()
    {
        string bbcode = "[b]Bold[/b] text [img]https://example.com/x.jpg[/img] here";
        Assert.Equal("Bold text here", SteamMetadataService.CleanHtmlText(bbcode));
    }

    [Fact]
    public void CleanHtmlText_CollapsesWhitespace()
    {
        string messy = "Line one\n\n\n   Line   two";
        Assert.Equal("Line one Line two", SteamMetadataService.CleanHtmlText(messy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CleanHtmlText_EmptyOrWhitespaceInput_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, SteamMetadataService.CleanHtmlText(input!));
    }
}
