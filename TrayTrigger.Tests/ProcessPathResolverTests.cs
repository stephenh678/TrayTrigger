using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class ProcessPathResolverTests
{
    [Theory]
    [InlineData(@"C:\Games\Foo\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Games\Foo\CrashReportClient.exe")]
    [InlineData(@"C:\Games\Foo\crashpad_handler.exe")]
    [InlineData(@"C:\Games\Foo\GameCrashUploader.exe")]
    [InlineData(@"C:\Games\Foo\unins000.exe")]
    [InlineData(@"C:\Games\Foo\Redist\vc_redist.x64.exe")]
    [InlineData(@"C:\Games\Foo\DXSetup.exe")]
    public void IsKnownHelperProcess_FlagsCrashToolsInstallersAndUninstallers(string path)
    {
        Assert.True(ProcessPathResolver.IsKnownHelperProcess(path));
    }

    // "crash" alone used to be enough, which hid these games from every install-folder search.
    [Theory]
    [InlineData(@"D:\Battle.net\Crash Bandicoot 4\CrashBandicoot4.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Crash Bandicoot N. Sane Trilogy\CrashBandicootNSaneTrilogy.exe")]
    [InlineData(@"C:\Steam\steamapps\common\Crashlands\Crashlands.exe")]
    [InlineData(@"C:\Games\Foo\Foo.exe")]
    public void IsKnownHelperProcess_LeavesGamesAlone(string path)
    {
        Assert.False(ProcessPathResolver.IsKnownHelperProcess(path));
    }
}
