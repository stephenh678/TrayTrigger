using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class ShortcutIconLocationTests
{
    [Theory]
    [InlineData(@"C:\Games\Foo\foo.ico")]
    [InlineData(@"%SystemRoot%\System32\shell32.dll")]
    [InlineData(@"""C:\Games\Foo\foo.exe""")]
    [InlineData(@"\\?\C:\Very\Long\Path\icon.ico")]
    public void LocalPaths_AreUsed(string location)
    {
        Assert.True(ShortcutService.IsLocalIconLocation(location));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\attacker\share\icon.ico")]
    [InlineData(@"//attacker/share/icon.ico")]
    [InlineData(@"\\?\UNC\attacker\share\icon.ico")]
    [InlineData("https://attacker.example/icon.ico")]
    [InlineData("file://attacker/share/icon.ico")]
    public void RemoteOrEmptyLocations_AreNot(string? location)
    {
        Assert.False(ShortcutService.IsLocalIconLocation(location));
    }
}
