using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Rockstar's registry keys as the scanner reads them. Only <c>InstallFolder</c> (a Rockstar Games
/// Launcher install) is a game here; a key with only <c>InstallFolderEpic</c> or
/// <c>InstallFolderSteam</c> - Red Dead Redemption 2 bought on Epic, as on the test machine -
/// arrives with no InstallFolder and is left to that store's scanner.
/// </summary>
public sealed class RockstarScannerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerRockstarScanner", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeFolder(string folder, params string[] exes)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        foreach (string exe in exes) File.WriteAllBytes(Path.Combine(dir, exe), new byte[400 * 1024]); // the folder scan passes over tiny exes
        return dir;
    }

    private static RockstarScannerService Scanner(params (string Key, string? InstallFolder)[] keys) => new(() => keys);

    [Fact]
    public void LauncherInstall_IsFound_WithTheKeyNameAsTitleAndId()
    {
        string gta = MakeFolder("Grand Theft Auto V", "PlayGTAV.exe");
        var games = Scanner(("Grand Theft Auto V", gta)).ScanInstalledGames([]);

        var game = Assert.Single(games);
        Assert.Equal("Grand Theft Auto V", game.GameId);
        Assert.Equal("Grand Theft Auto V", game.Name);
        Assert.Equal(gta, game.InstallDir);
        Assert.NotNull(game.ExePath);
        Assert.StartsWith(gta, game.ExePath);
    }

    [Fact]
    public void EpicOrSteamOnlyCopy_IsLeftToThatStore()
    {
        // RDR2 on Epic: the key exists, InstallFolder doesn't.
        Assert.Empty(Scanner(("Red Dead Redemption 2", null)).ScanInstalledGames([]));
        Assert.Empty(Scanner(("Red Dead Redemption 2", "  ")).ScanInstalledGames([]));
    }

    [Fact]
    public void RockstarsOwnSoftware_IsNotAGame()
    {
        string launcher = MakeFolder("Launcher", "Launcher.exe");
        string social = MakeFolder("Social Club", "SocialClubHelper.exe");
        var scanner = Scanner(("Launcher", launcher), ("Rockstar Games Social Club", social));

        Assert.Empty(scanner.ScanInstalledGames([]));
        Assert.Empty(scanner.GetInstallDirs());
    }

    [Fact]
    public void MissingFolder_AlreadyImported_AndNoExe_AreSkipped()
    {
        string empty = MakeFolder("Empty");
        string max = MakeFolder("Max Payne 3", "MaxPayne3.exe");
        var scanner = Scanner(("Gone", Path.Combine(_root, "NotThere")), ("Empty", empty), ("Max Payne 3", max));

        Assert.Equal(["Max Payne 3"], scanner.ScanInstalledGames([]).Select(g => g.GameId));
        Assert.Empty(scanner.ScanInstalledGames(["max payne 3"]));
    }

    [Fact]
    public void InstallFolder_IsNormalized()
    {
        string gta = MakeFolder("GTA", "PlayGTAV.exe");
        var dirs = Scanner(("Grand Theft Auto V", gta.Replace('\\', '/') + "/")).GetInstallDirs();
        Assert.Equal(gta, Assert.Single(dirs).InstallDir);
    }

    [Fact]
    public void LauncherPath_ComesFromTheLaunchersOwnKey()
    {
        string launcher = MakeFolder("Launcher", "Launcher.exe");
        Assert.Equal(Path.Combine(launcher, "Launcher.exe"), Scanner(("Launcher", launcher)).GetLauncherPath());
        Assert.True(Scanner(("Launcher", launcher)).IsRockstarLauncherInstalled());

        // Key present but the program gone, or no key at all: not installed, so games launch directly.
        Assert.Null(Scanner(("Launcher", MakeFolder("Uninstalled"))).GetLauncherPath());
        Assert.False(Scanner().IsRockstarLauncherInstalled());
    }

    [Fact]
    public void FindInstallDir_ReadsTheRecordedFolder()
    {
        string gta = MakeFolder("GTA", "PlayGTAV.exe");
        var scanner = Scanner(("Grand Theft Auto V", gta), ("Red Dead Redemption 2", null));

        Assert.Equal(gta, scanner.FindInstallDir("grand theft auto v"));
        Assert.Null(scanner.FindInstallDir("Red Dead Redemption 2"));
        Assert.Null(scanner.FindInstallDir(null));
    }

    [Fact]
    public void ADroppedExe_ResolvesToItsRockstarGame()
    {
        string gta = MakeFolder("Grand Theft Auto V", "PlayGTAV.exe");
        var lookup = new PlatformLookupService(new SteamScannerService(), new GogScannerService(), new EaScannerService(),
            new EpicScannerService(Path.Combine(_root, "NoManifests"), Path.Combine(_root, "NoEglv2")),
            new UbisoftScannerService(), new XboxScannerService(), new BattleNetScannerService(),
            Scanner(("Grand Theft Auto V", gta)));

        var match = lookup.FindByPath(Path.Combine(gta, "PlayGTAV.exe"));

        Assert.NotNull(match);
        Assert.Equal("Rockstar", match.Platform);
        Assert.Equal("Grand Theft Auto V", match.Rockstar!.GameId);
        Assert.Equal("Grand Theft Auto V", match.Name);
    }

    [Fact]
    public void AReadFailure_FindsNothing_RatherThanThrowing()
    {
        var scanner = new RockstarScannerService(() => throw new UnauthorizedAccessException());
        Assert.Empty(scanner.ScanInstalledGames([]));
        Assert.Null(scanner.GetLauncherPath());
    }
}
