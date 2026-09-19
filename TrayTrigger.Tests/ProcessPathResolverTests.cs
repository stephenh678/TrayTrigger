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

    // Force Close kills what it finds under a game's folder. Pointed at System32 it killed
    // Windows' own processes (a CRITICAL_PROCESS_DIED blue screen when running as administrator).
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\")]
    [InlineData(@"C:\WINDOWS\SysWOW64")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)\")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users")]
    [InlineData("")]
    public void IsUnsafeProcessFolder_RefusesWindowsRootsAndSystemFolders(string dir)
    {
        Assert.True(ProcessPathResolver.IsUnsafeProcessFolder(dir, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsUnsafeProcessFolder_RefusesDesktopDocumentsAndDownloads()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(ProcessPathResolver.IsUnsafeProcessFolder(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), out _));
        Assert.True(ProcessPathResolver.IsUnsafeProcessFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), out _));
        Assert.True(ProcessPathResolver.IsUnsafeProcessFolder(System.IO.Path.Combine(profile, "Downloads"), out _));
        // A game in its own folder under Downloads is fine.
        Assert.False(ProcessPathResolver.IsUnsafeProcessFolder(System.IO.Path.Combine(profile, "Downloads", "SomeGame"), out _));
    }

    [Fact]
    public void IsUnsafeProcessFolder_RefusesTheProfileAndAppDataRoots()
    {
        Assert.True(ProcessPathResolver.IsUnsafeProcessFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _));
        Assert.True(ProcessPathResolver.IsUnsafeProcessFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), out _));
    }

    [Theory]
    [InlineData(@"C:\Games\Hades")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Hades\")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077")]
    [InlineData(@"C:\XboxGames\Starfield\Content")]
    public void IsUnsafeProcessFolder_AllowsGameFolders(string dir)
    {
        Assert.False(ProcessPathResolver.IsUnsafeProcessFolder(dir, out _));
    }

    [Fact]
    public void FindProcessesUnderDirectory_FindsNothingInSystem32()
    {
        string system32 = ProcessPathResolver.NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.System));
        Assert.Empty(ProcessPathResolver.FindProcessesUnderDirectory(system32, includeHelpers: true));
    }
}
