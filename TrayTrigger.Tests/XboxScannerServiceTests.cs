using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The pure parts of Xbox / Game Pass discovery. The registry walk itself reads the
/// live machine and is only exercised by hand against a real Xbox-app install.</summary>
public class XboxScannerServiceTests
{
    [Theory]
    [InlineData("436609B6.FortniteClient_1.5781.9926.0_x64__9ncxwbgmmv7m8", "436609B6.FortniteClient_9ncxwbgmmv7m8")]
    [InlineData("ROBLOXCorporation.RobloxGDK_2.738.1396.0_x64__55nm5eh3cm0pr", "ROBLOXCorporation.RobloxGDK_55nm5eh3cm0pr")]
    [InlineData("Microsoft.GamingApp_2608.1001.17.0_x64__8wekyb3d8bbwe", "Microsoft.GamingApp_8wekyb3d8bbwe")]
    // A resource-id segment between arch and publisher hash is legal and must not be picked up.
    [InlineData("Publisher.Game_1.0.0.0_x64_en-us_abcdefghijklm", "Publisher.Game_abcdefghijklm")]
    public void ToPackageFamilyName_DropsVersionArchAndResourceId(string fullName, string expected)
    {
        Assert.Equal(expected, XboxScannerService.ToPackageFamilyName(fullName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NoUnderscores")]
    [InlineData("_leading")]
    [InlineData("trailing_")]
    public void ToPackageFamilyName_RejectsMalformedNames(string fullName)
    {
        Assert.Null(XboxScannerService.ToPackageFamilyName(fullName));
    }

    [Fact]
    public void ScanInstalledGames_NeverThrows_AndFlagsAlreadyImported()
    {
        // On a machine without Gaming Services this is simply empty; on one with it, every
        // record must carry a well-formed AUMID and the flag must reflect the input set.
        var scanner = new XboxScannerService();
        var all = scanner.ScanInstalledGames([]);
        foreach (var g in all)
        {
            Assert.Contains('!', g.Aumid);
            Assert.StartsWith(g.PackageFamilyName + "!", g.Aumid);
            Assert.False(g.IsAlreadyImported);
            Assert.False(string.IsNullOrWhiteSpace(g.Name));
        }

        if (all.Count > 0)
        {
            var again = scanner.ScanInstalledGames([all[0].Aumid]);
            Assert.True(again.Single(g => g.Aumid == all[0].Aumid).IsAlreadyImported);
            Assert.Equal(all[0].Aumid, scanner.FindByAumid(all[0].Aumid)?.Aumid);
        }

        Assert.Null(scanner.FindByAumid("Nobody.Nothing_0000000000000!App"));
    }

    [Fact]
    public void PlatformCategoryFor_XboxEntry()
    {
        var game = new GameEntry { IsXboxGame = true, XboxAumid = "a!b" };
        Assert.Equal(LibraryConstants.XboxCategory, LibraryConstants.PlatformCategoryFor(game));
        Assert.True(LibraryConstants.IsEnrichableCategory(LibraryConstants.XboxCategory));
        Assert.False(LibraryConstants.IsEnrichableCategory("Action"));
        Assert.Null(LibraryConstants.PlatformCategoryFor(new GameEntry()));
    }
}
