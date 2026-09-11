using TrayTrigger.Services;

namespace TrayTrigger.Tests;

// The platform lookups themselves read the live registry / launcher manifests, so only the
// pure path-containment rule they all share is unit-tested here.
public class PlatformLookupServiceTests
{
    [Theory]
    [InlineData(@"C:\Games\Foo\bin\game.exe", @"C:\Games\Foo")]
    [InlineData(@"C:\Games\Foo\game.exe", @"C:\Games\Foo\")]
    [InlineData(@"c:\games\foo\GAME.EXE", @"C:\Games\Foo")]
    [InlineData(@"C:/Games/Foo/game.exe", @"C:\Games\Foo")]
    [InlineData(@"C:\Games\Foo\game.exe", @"C:/Games/Foo/")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Foo\Binaries\Win64\Foo-Win64-Shipping.exe", @"D:\SteamLibrary\steamapps\common\Foo")]
    [InlineData(@"C:\Games\Foo\..\Foo\game.exe", @"C:\Games\Foo")]
    public void IsPathUnderDirectory_MatchesPathsInsideTheDirectory(string path, string directory)
    {
        Assert.True(PlatformLookupService.IsPathUnderDirectory(path, directory));
    }

    [Fact]
    public void IsPathUnderDirectory_MatchesTheDirectoryItself()
    {
        Assert.True(PlatformLookupService.IsPathUnderDirectory(@"C:\Games\Foo", @"C:\Games\Foo"));
        Assert.True(PlatformLookupService.IsPathUnderDirectory(@"C:\Games\Foo\", @"C:\Games\Foo"));
    }

    [Theory]
    [InlineData(@"C:\Games\Foobar\game.exe", @"C:\Games\Foo")]
    [InlineData(@"C:\Games\Foo", @"C:\Games\Foo\bin")]
    [InlineData(@"C:\Games\game.exe", @"C:\Games\Foo")]
    [InlineData(@"D:\Games\Foo\game.exe", @"C:\Games\Foo")]
    public void IsPathUnderDirectory_RejectsPathsOutsideTheDirectory(string path, string directory)
    {
        Assert.False(PlatformLookupService.IsPathUnderDirectory(path, directory));
    }

    [Theory]
    [InlineData(null, @"C:\Games\Foo")]
    [InlineData("", @"C:\Games\Foo")]
    [InlineData("   ", @"C:\Games\Foo")]
    [InlineData(@"C:\Games\Foo\game.exe", null)]
    [InlineData(@"C:\Games\Foo\game.exe", "")]
    public void IsPathUnderDirectory_RejectsMissingInputs(string? path, string? directory)
    {
        Assert.False(PlatformLookupService.IsPathUnderDirectory(path, directory));
    }

    [Fact]
    public void IsPathUnderDirectory_RejectsInvalidPathsInsteadOfThrowing()
    {
        Assert.False(PlatformLookupService.IsPathUnderDirectory("C:\\Games\\Foo\0bad", @"C:\Games\Foo"));
    }

    [Fact]
    public void PlatformMatch_ExposesPlatformNameAndRecord()
    {
        var gog = new DiscoveredGogGame("123", "Foo", @"C:\GOG Games\Foo", @"C:\GOG Games\Foo\foo.exe", null, null, false);
        var match = PlatformMatch.ForGog(gog);

        Assert.Equal("GOG", match.Platform);
        Assert.Equal("Foo", match.Name);
        Assert.Same(gog, match.Gog);
        Assert.Null(match.Steam);
        Assert.Null(match.Ea);
        Assert.Null(match.Epic);
        Assert.Null(match.Ubisoft);
        Assert.Null(match.Xbox);
    }

    [Fact]
    public void PlatformMatch_ForXbox()
    {
        var xbox = new DiscoveredXboxGame(
            "436609B6.FortniteClient_9ncxwbgmmv7m8!AppFortniteShipping",
            "436609B6.FortniteClient_9ncxwbgmmv7m8",
            "436609B6.FortniteClient_1.5781.9926.0_x64__9ncxwbgmmv7m8",
            "Fortnite",
            @"C:\XboxGames\Fortnite\Content",
            @"C:\Program Files\WindowsApps\436609B6.FortniteClient_1.5781.9926.0_x64__9ncxwbgmmv7m8",
            null, null, false);
        var match = PlatformMatch.ForXbox(xbox);

        Assert.Equal("Xbox", match.Platform);
        Assert.Equal("Fortnite", match.Name);
        Assert.Same(xbox, match.Xbox);
        Assert.Null(match.Steam);
    }
}
