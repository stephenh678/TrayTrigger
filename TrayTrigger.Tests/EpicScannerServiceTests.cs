using System;
using System.IO;
using System.Linq;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public sealed class EpicScannerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerEpicScanner", Guid.NewGuid().ToString("N"));
    private string ManifestsDir => Path.Combine(_root, "Manifests");
    private string Eglv2Dir => Path.Combine(_root, "eglv2");
    private string GamesDir => Path.Combine(_root, "Games");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private EpicScannerService NewScanner() => new(ManifestsDir, Eglv2Dir);

    private static string J(string s) => s.Replace("\\", "\\\\");

    private string MakeGame(string folder, string exeRelative)
    {
        string dir = Path.Combine(GamesDir, folder);
        string exe = Path.Combine(dir, exeRelative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        return dir;
    }

    private void WriteEglv2(string relative, string content)
    {
        string path = Path.Combine(Eglv2Dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Record(string installationId, bool complete, string dir, string itemId, string appName, string exe) =>
        $"{{\"installationId\":\"{installationId}\",\"complete\":{(complete ? "true" : "false")},\"dir\":\"{J(dir)}\",\"platform\":\"Windows\",\"namespace\":\"ns\",\"itemId\":\"{itemId}\",\"artifactId\":\"{appName}\",\"launchCommand\":\"\",\"launchExecutable\":\"{exe}\"}}";

    private const string Header = "{\"version\":1,\"writtenAt\":\"2026-11-13T11:56:59Z\",\"entries\":0}";

    [Fact]
    public void NewLauncher_CompleteInstall_IsFound_WithCatalogTitle()
    {
        string dir = MakeGame("BioShock2Remastered", "Build/FinalEpic/Bioshock2HD.exe");
        WriteEglv2("installations.jsonl", string.Join("\n",
            Header,
            Record("A1", false, dir, "item1", "b22ce34b", "Build/FinalEpic/Bioshock2HD.exe"),
            Record("A1", true, dir, "item1", "b22ce34b", "Build/FinalEpic/Bioshock2HD.exe")) + "\n");
        WriteEglv2(@"cache\prod\catalog-cache.json",
            "{\"items\":{\"item1\":{\"item\":{\"id\":\"item1\",\"title\":\"BioShock 2 Remastered\",\"categories\":[{\"path\":\"games\"},{\"path\":\"applications\"}],\"mainGameItem\":null}}}}");

        var game = Assert.Single(NewScanner().ScanInstalledGames([]));
        Assert.Equal("b22ce34b", game.AppName);
        Assert.Equal("BioShock 2 Remastered", game.Name);
        Assert.Equal(dir, game.InstallDir);
        Assert.Equal(Path.Combine(dir, @"Build\FinalEpic\Bioshock2HD.exe"), game.ExePath);
        Assert.False(game.IsAlreadyImported);
    }

    [Fact]
    public void NewLauncher_DownloadInProgress_IsNotOffered()
    {
        string dir = MakeGame("RedDeadRedemption2", "PlayRDR2.exe");
        WriteEglv2("installations.jsonl", Header + "\n" + Record("B1", false, dir, "item2", "Heather", "PlayRDR2.exe") + "\n");

        Assert.Empty(NewScanner().ScanInstalledGames([]));
    }

    [Fact]
    public void NewLauncher_TitleFallsBackToLibraryCache_ThenFolderName()
    {
        string rdr = MakeGame("RedDeadRedemption2", "PlayRDR2.exe");
        string other = MakeGame("SomeGame", "Game.exe");
        WriteEglv2("installations.jsonl", string.Join("\n",
            Header,
            Record("B1", true, rdr, "item2", "Heather", "PlayRDR2.exe"),
            Record("C1", true, other, "item3", "Other", "Game.exe")));
        WriteEglv2(@"cache\prod\account\acct\library-cache.json",
            "{\"items\":[{\"appId\":{\"namespaceId\":\"ns\",\"itemId\":\"item2\",\"appName\":\"Heather\"},\"title\":\"Red Dead Redemption 2\"}]}");

        var games = NewScanner().ScanInstalledGames(["Heather"]);
        Assert.Equal(2, games.Count);
        var rdrGame = games.Single(g => g.AppName == "Heather");
        Assert.Equal("Red Dead Redemption 2", rdrGame.Name);
        Assert.True(rdrGame.IsAlreadyImported);
        Assert.Equal("SomeGame", games.Single(g => g.AppName == "Other").Name);
    }

    [Fact]
    public void NewLauncher_AddOnsAndMissingExes_AreSkipped()
    {
        string dlc = MakeGame("Dlc", "Dlc.exe");
        string gone = Path.Combine(GamesDir, "Uninstalled");
        WriteEglv2("installations.jsonl", string.Join("\n",
            Header,
            Record("D1", true, dlc, "dlcItem", "DlcApp", "Dlc.exe"),
            Record("E1", true, gone, "goneItem", "GoneApp", "Gone.exe")));
        WriteEglv2(@"cache\prod\catalog-cache.json",
            "{\"items\":{\"dlcItem\":{\"item\":{\"title\":\"Some DLC\",\"categories\":[{\"path\":\"addons\"}],\"mainGameItem\":{\"id\":\"base\"}}}}}");

        Assert.Empty(NewScanner().ScanInstalledGames([]));
    }

    [Fact]
    public void NewLauncher_ExeOutsideInstallDir_IsRejected()
    {
        string dir = MakeGame("Game", "Game.exe");
        MakeGame("Elsewhere", "Evil.exe");
        WriteEglv2("installations.jsonl", Record("F1", true, dir, "i", "App", "../Elsewhere/Evil.exe"));

        Assert.Empty(NewScanner().ScanInstalledGames([]));
    }

    [Fact]
    public void BothLaunchers_SameAppName_IsOfferedOnce()
    {
        string dir = MakeGame("Fortnite", "Fortnite.exe");
        Directory.CreateDirectory(ManifestsDir);
        File.WriteAllText(Path.Combine(ManifestsDir, "x.item"),
            $"{{\"bIsIncompleteInstall\":false,\"bIsApplication\":true,\"AppName\":\"Fortnite\",\"DisplayName\":\"Fortnite (old)\",\"InstallLocation\":\"{J(dir)}\",\"LaunchExecutable\":\"Fortnite.exe\",\"AppCategories\":[\"games\"]}}");
        WriteEglv2("installations.jsonl", Record("G1", true, dir, "fn", "Fortnite", "Fortnite.exe"));

        var game = Assert.Single(NewScanner().ScanInstalledGames([]));
        Assert.Equal("Fortnite", game.Name); // folder name: the new launcher's record wins
    }

    [Fact]
    public void NewLauncher_RedirectorLaunchExe_RecordsTheGamesOwnProgram()
    {
        // Red Dead Redemption 2: Epic starts PlayRDR2.exe, which hands over to the Rockstar launcher
        // and exits; the game is RDR2.exe, named in the catalog as MainWindowProcessName.
        string dir = MakeGame("RedDeadRedemption2", "PlayRDR2.exe");
        File.WriteAllText(Path.Combine(dir, "RDR2.exe"), "");
        WriteEglv2("installations.jsonl", Record("R1", true, dir, "rdr", "Heather", "PlayRDR2.exe"));
        WriteEglv2(@"cache\prod\catalog-cache.json",
            "{\"items\":{\"rdr\":{\"item\":{\"title\":\"Red Dead Redemption 2\",\"categories\":[{\"path\":\"games\"}],\"mainGameItem\":null,"
            + "\"customAttributes\":{\"MainWindowProcessName\":{\"type\":\"STRING\",\"value\":\"RDR2.exe\"}}}}}}");

        var game = Assert.Single(NewScanner().ScanInstalledGames([]));
        Assert.Equal(Path.Combine(dir, "RDR2.exe"), game.ExePath);
    }

    [Fact]
    public void ClassicLauncher_MainWindowProcessName_InASubfolder_IsRecorded()
    {
        string dir = MakeGame("SomeRockstarGame", "Launch.exe");
        string real = Path.Combine(dir, "bin", "x64", "Game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        File.WriteAllText(real, "");
        Directory.CreateDirectory(ManifestsDir);
        File.WriteAllText(Path.Combine(ManifestsDir, "x.item"),
            $"{{\"bIsApplication\":true,\"AppName\":\"Some\",\"DisplayName\":\"Some Game\",\"InstallLocation\":\"{J(dir)}\",\"LaunchExecutable\":\"Launch.exe\",\"MainWindowProcessName\":\"Game.exe\",\"AppCategories\":[\"games\"]}}");

        Assert.Equal(real, Assert.Single(NewScanner().ScanInstalledGames([])).ExePath);
    }

    [Theory]
    [InlineData("Missing.exe")]   // named but not installed: keep the launch exe
    [InlineData("..\\Evil.exe")]  // not a bare file name
    [InlineData("*.exe")]         // a wildcard would match any program in the install
    [InlineData("Ev?l.exe")]
    [InlineData("Game.dll")]      // not a program
    [InlineData("")]
    public void MainWindowProcessName_ThatCannotBeUsed_KeepsTheLaunchExe(string mainWindow)
    {
        string dir = MakeGame("Game", "Game.exe");
        MakeGame("Evil", "Evil.exe");
        // Other programs in the install, which a wildcard would match first.
        File.WriteAllText(Path.Combine(dir, "Anticheat.exe"), "");
        File.WriteAllText(Path.Combine(dir, "Evil.exe"), "");
        Directory.CreateDirectory(ManifestsDir);
        File.WriteAllText(Path.Combine(ManifestsDir, "x.item"),
            $"{{\"bIsApplication\":true,\"AppName\":\"App\",\"DisplayName\":\"Game\",\"InstallLocation\":\"{J(dir)}\",\"LaunchExecutable\":\"Game.exe\",\"MainWindowProcessName\":\"{J(mainWindow)}\",\"AppCategories\":[\"games\"]}}");

        Assert.Equal(Path.Combine(dir, "Game.exe"), Assert.Single(NewScanner().ScanInstalledGames([])).ExePath);
    }

    [Fact]
    public void NoLauncherData_ReturnsEmpty_AndTruncatedLineIsIgnored()
    {
        Assert.Empty(NewScanner().ScanInstalledGames([]));

        string dir = MakeGame("Game", "Game.exe");
        WriteEglv2("installations.jsonl", Record("H1", true, dir, "i", "App", "Game.exe") + "\n{\"installationId\":\"H2\",\"compl");
        Assert.Single(NewScanner().ScanInstalledGames([]));
    }
}
