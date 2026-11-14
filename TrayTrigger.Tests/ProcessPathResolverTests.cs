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
    [InlineData(@"C:\Program Files (x86)\Call of Duty\_bna_19264_0.exe")]
    public void IsKnownHelperProcess_FlagsCrashToolsInstallersAndUninstallers(string path)
    {
        Assert.True(ProcessPathResolver.IsKnownHelperProcess(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Call of Duty\_retail_\cod.exe", "cod.exe")]
    [InlineData(@"D:\Games\Foo\FOO.EXE", "FOO.EXE")]
    [InlineData("steam://rungameid/620", null)]
    [InlineData("com.epicgames.launcher://apps/x?action=launch", null)]
    [InlineData(@"C:\Games\Foo\start.bat", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void RecordedExeName_IsTheProgramOrNull(string? path, string? expected)
    {
        Assert.Equal(expected, ProcessPathResolver.RecordedExeName(path));
    }

    [Fact]
    public void PickGameProcess_PrefersTheRecordedExeOverABetterRankedStandIn()
    {
        var updater = new ProcessPathResolver.ProcessUnderDirectory(1, @"C:\Games\Foo\Patcher.exe", true, DateTime.UtcNow);
        var game = new ProcessPathResolver.ProcessUnderDirectory(2, @"C:\Games\Foo\_retail_\foo.exe", false, DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(2, ProcessPathResolver.PickGameProcess([updater, game], "Foo.exe").Pid);
    }

    [Fact]
    public void PickGameProcess_TakesTheBestCandidateWhenTheRecordedExeIsNotRunning()
    {
        var a = new ProcessPathResolver.ProcessUnderDirectory(1, @"C:\Games\Foo\a.exe", true, DateTime.UtcNow);
        var b = new ProcessPathResolver.ProcessUnderDirectory(2, @"C:\Games\Foo\b.exe", false, DateTime.UtcNow);
        Assert.Equal(1, ProcessPathResolver.PickGameProcess([a, b], "foo.exe").Pid);
        Assert.Equal(1, ProcessPathResolver.PickGameProcess([a, b], null).Pid);
    }

    // Red Dead Redemption 2 from Epic: PlayRDR2.exe (the stand-in) stays open as long as RDR2.exe does, so
    // the session must move to RDR2.exe when it appears, or it spends minutes looking for it after a quit.
    [Fact]
    public void FindRecordedGame_FindsTheGamesOwnProgramBesideTheStandIn()
    {
        var standIn = new ProcessPathResolver.ProcessUnderDirectory(10, @"C:\Epic\RedDeadRedemption2\PlayRDR2.exe", false, DateTime.UtcNow.AddMinutes(-1));
        var game = new ProcessPathResolver.ProcessUnderDirectory(11, @"C:\Epic\RedDeadRedemption2\RDR2.exe", true, DateTime.UtcNow);
        Assert.Equal(11, ProcessLauncherService.FindRecordedGame([standIn, game], "rdr2.exe", standInPid: 10)?.Pid);
    }

    [Fact]
    public void FindRecordedGame_IsNullUntilTheGameRuns_AndNeverReturnsTheStandIn()
    {
        var standIn = new ProcessPathResolver.ProcessUnderDirectory(10, @"C:\Epic\RedDeadRedemption2\PlayRDR2.exe", false, DateTime.UtcNow);
        var other = new ProcessPathResolver.ProcessUnderDirectory(12, @"C:\Epic\RedDeadRedemption2\Helper.exe", true, DateTime.UtcNow);
        Assert.Null(ProcessLauncherService.FindRecordedGame([standIn, other], "RDR2.exe", standInPid: 10));
        // A stand-in that is itself named like the recorded exe (it was the process followed) is not a switch.
        var sameName = new ProcessPathResolver.ProcessUnderDirectory(10, @"C:\Epic\RedDeadRedemption2\RDR2.exe", false, DateTime.UtcNow);
        Assert.Null(ProcessLauncherService.FindRecordedGame([sameName], "RDR2.exe", standInPid: 10));
    }

    // The watch for the game's own program scans every process; for a whole session that is time taken from the game.
    [Fact]
    public void RecordedGameWatch_ScansEveryPollAtFirst_ThenOnceInFifteen_ButNeverSkipsAPendingSighting()
    {
        Assert.True(ProcessLauncherService.ScanForRecordedGameThisTick(TimeSpan.FromSeconds(30), tick: 7, sightingPending: false));
        Assert.False(ProcessLauncherService.ScanForRecordedGameThisTick(TimeSpan.FromMinutes(5), tick: 7, sightingPending: false));
        Assert.True(ProcessLauncherService.ScanForRecordedGameThisTick(TimeSpan.FromMinutes(5), tick: 15, sightingPending: false));
        Assert.True(ProcessLauncherService.ScanForRecordedGameThisTick(TimeSpan.FromMinutes(5), tick: 7, sightingPending: true));
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
