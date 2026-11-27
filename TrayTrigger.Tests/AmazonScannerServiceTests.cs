using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Amazon Games' uninstall entries as the scanner reads them, shaped like the real one on the test machine
/// (2026-11-26): key <c>AmazonGames/Darksiders II Deathinitive Edition</c>, InstallLocation under
/// <c>C:\Amazon Games\Library</c>, and <c>"...\__InstallData__\Amazon Game Remover.exe" -m Game -p 790f3b07-...</c>.
/// </summary>
public sealed class AmazonScannerServiceTests : IDisposable
{
    private const string NewStyleId = "790f3b07-fc9c-4efe-bb66-32bd348a9d23";
    private const string OldStyleId = "amzn1.adg.product.2bf7606d-d642-46ac-aae4-bb60175cfd2d";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerAmazonScanner", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeFolder(string folder, params string[] exes)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        foreach (string exe in exes)
        {
            string path = Path.Combine(dir, exe);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[400 * 1024]); // the folder scan passes over tiny exes
        }
        return dir;
    }

    private static string Remover(string id) => $"\"C:\\Amazon Games\\Library\\__InstallData__\\Amazon Game Remover.exe\" -m Game -p {id}";

    private static AmazonScannerService.UninstallEntry Entry(string title, string? dir, string? id) =>
        new($"AmazonGames/{title}", title, dir, id == null ? null : Remover(id));

    private static AmazonScannerService Scanner(params AmazonScannerService.UninstallEntry[] entries) => new(() => entries);

    [Fact]
    public void Install_IsFound_WithItsProductIdAndTitle_AndFuelExe()
    {
        string dir = MakeFolder("Darksiders II Deathinitive Edition", "Darksiders2.exe", @"DirectX\DXSETUP.exe");
        File.WriteAllText(Path.Combine(dir, "fuel.json"), """
            {
              "SchemaVersion":  "2",
              "PostInstall": [ { "Command": "DirectX\\DXSETUP.exe", "Args": ["/silent"] } ],
              "Main": { "Command": "Darksiders2.exe", "Args": [] }
            }
            """);

        var game = Assert.Single(Scanner(Entry("Darksiders II Deathinitive Edition", dir, NewStyleId)).ScanInstalledGames([]));

        Assert.Equal(NewStyleId, game.GameId);
        Assert.Equal("Darksiders II Deathinitive Edition", game.Name);
        Assert.Equal(dir, game.InstallDir);
        Assert.Equal(Path.Combine(dir, "Darksiders2.exe"), game.ExePath);
    }

    [Theory]
    [InlineData("\"C:\\X\\Amazon Game Remover.exe\" -m Game -p " + NewStyleId, NewStyleId)]
    [InlineData("\"C:\\X\\Amazon Game Remover.exe\" -m Game -p " + OldStyleId, OldStyleId)]
    [InlineData("\"C:\\X\\Amazon Game Remover.exe\" -m Game -p \"" + NewStyleId + "\"", NewStyleId)]
    [InlineData("\"C:\\X\\Amazon Game Remover.exe\" -m Game", null)]
    [InlineData("", null)]
    // A " -p " in the remover's own path is not the argument.
    [InlineData("\"D:\\Games -p Old\\__InstallData__\\Amazon Game Remover.exe\" -m Game -p " + NewStyleId, NewStyleId)]
    [InlineData("D:\\Games -p Old\\__InstallData__\\Amazon Game Remover.exe -m Game -p " + NewStyleId, NewStyleId)]
    [InlineData("\"D:\\Games -p Old\\__InstallData__\\Amazon Game Remover.exe\" -m Game", null)]
    public void ProductId_IsTheRemoversDashPValue(string uninstallString, string? expected)
    {
        Assert.Equal(expected, AmazonScannerService.ParseProductId(uninstallString));
    }

    [Theory]
    [InlineData("amazon-games://play/" + NewStyleId, NewStyleId)]
    [InlineData("AMAZON-GAMES://play/" + OldStyleId, OldStyleId)]
    [InlineData("amazon-games://play/" + NewStyleId + "?source=desktop", NewStyleId)]
    [InlineData("amazon-games://play/" + NewStyleId + "/", NewStyleId)]
    [InlineData("amazon-games://play/", null)]
    [InlineData("amazon-games://play/bad%20id", null)]
    [InlineData("amazon-games://store/" + NewStyleId, null)]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch", null)]
    [InlineData(@"C:\Amazon Games\Library\Game\Game.exe", null)]
    [InlineData(null, null)]
    public void LaunchUrl_IsReadBackToItsProductId(string? url, string? expected)
    {
        Assert.Equal(expected, AmazonScannerService.ParseLaunchUrl(url));
    }

    [Fact]
    public void FuelArgs_AreCarried_QuotedAsACommandLineNeeds()
    {
        string dir = MakeFolder("Args Game", "Game.exe");
        File.WriteAllText(Path.Combine(dir, "fuel.json"), """
            { "Main": { "Command": "Game.exe", "Args": ["-AmazonLauncher", "-profile", "C:\\My Saves", "", "say \"hi\""] } }
            """);

        var game = Assert.Single(Scanner(Entry("Args Game", dir, NewStyleId)).ScanInstalledGames([]));
        Assert.Equal("-AmazonLauncher -profile \"C:\\My Saves\" \"say \\\"hi\\\"\"", game.LaunchArgs);

        File.WriteAllText(Path.Combine(dir, "fuel.json"), """{ "Main": { "Command": "Game.exe", "Args": "-windowed -dx11" } }""");
        Assert.Equal("-windowed -dx11", AmazonScannerService.ReadFuel(dir)?.Arguments);

        File.WriteAllText(Path.Combine(dir, "fuel.json"), """{ "Main": { "Command": "Game.exe", "Args": [] } }""");
        Assert.Null(AmazonScannerService.ReadFuel(dir)?.Arguments);
    }

    [Theory]
    [InlineData("-dx11", "-dx11")]
    [InlineData("C:\\My Saves", "\"C:\\My Saves\"")]
    [InlineData("C:\\My Saves\\", "\"C:\\My Saves\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("", "\"\"")]
    public void QuoteArgument_FollowsWindowsRules(string arg, string expected)
    {
        Assert.Equal(expected, AmazonScannerService.QuoteArgument(arg));
    }

    [Fact]
    public void ADroppedAmazonShortcut_ResolvesToItsGame_ByProductId()
    {
        string dir = MakeFolder("Darksiders II Deathinitive Edition", "Darksiders2.exe");
        var lookup = new PlatformLookupService(new SteamScannerService(), new GogScannerService(), new EaScannerService(),
            new EpicScannerService(Path.Combine(_root, "NoManifests"), Path.Combine(_root, "NoEglv2")),
            new UbisoftScannerService(), new XboxScannerService(), new BattleNetScannerService(),
            amazon: Scanner(Entry("Darksiders II Deathinitive Edition", dir, NewStyleId)));

        var match = lookup.FindByPath($"amazon-games://play/{NewStyleId}");
        Assert.NotNull(match);
        Assert.Equal("Amazon", match.Platform);
        Assert.Equal(NewStyleId, match.Amazon!.GameId);
        Assert.Equal(Path.Combine(dir, "Darksiders2.exe"), match.Amazon.ExePath);

        // The exe itself resolves the same way, and a link to a game that isn't installed matches nothing.
        Assert.Equal(NewStyleId, lookup.FindByPath(Path.Combine(dir, "Darksiders2.exe"))?.Amazon?.GameId);
        Assert.Null(lookup.FindByPath("amazon-games://play/11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void LaunchUrl_IsTheOneAmazonsShortcutsUse()
    {
        Assert.Equal($"amazon-games://play/{NewStyleId}", AmazonScannerService.BuildLaunchUrl(NewStyleId));
        Assert.Equal($"amazon-games://play/{OldStyleId}", AmazonScannerService.BuildLaunchUrl(OldStyleId));
    }

    [Fact]
    public void WithoutFuelJson_TheFolderScanPicksTheExe()
    {
        string dir = MakeFolder("Some Game", "SomeGame.exe");
        var game = Assert.Single(Scanner(Entry("Some Game", dir, NewStyleId)).ScanInstalledGames([]));
        Assert.Equal(Path.Combine(dir, "SomeGame.exe"), game.ExePath);
    }

    [Fact]
    public void FuelJson_PointingOutsideTheFolderOrAtNothing_IsNotTrusted()
    {
        string dir = MakeFolder("Game", "Game.exe");
        MakeFolder("Other", "Other.exe"); // a real exe, so only the inside-the-folder check can turn it down
        File.WriteAllText(Path.Combine(dir, "fuel.json"), """{ "Main": { "Command": "..\\Other\\Other.exe" } }""");
        Assert.Null(AmazonScannerService.ReadFuelExe(dir));

        File.WriteAllText(Path.Combine(dir, "fuel.json"), """{ "Main": { "Command": "Missing.exe" } }""");
        Assert.Null(AmazonScannerService.ReadFuelExe(dir));

        File.WriteAllText(Path.Combine(dir, "fuel.json"), "not json");
        Assert.Null(AmazonScannerService.ReadFuelExe(dir));
    }

    [Fact]
    public void MissingFolder_NoId_AlreadyImported_AndNoExe_AreSkipped()
    {
        string empty = MakeFolder("Empty");
        string good = MakeFolder("Good Game", "Good.exe");
        var scanner = Scanner(
            Entry("Gone", Path.Combine(_root, "NotThere"), "11111111-1111-1111-1111-111111111111"),
            Entry("No Id", good, null),
            Entry("Empty", empty, "22222222-2222-2222-2222-222222222222"),
            Entry("Good Game", good, NewStyleId));

        Assert.Equal([NewStyleId], scanner.ScanInstalledGames([]).Select(g => g.GameId));
        Assert.Empty(scanner.ScanInstalledGames([NewStyleId.ToUpperInvariant()]));
    }

    [Fact]
    public void InstallFolder_IsNormalized_AndFoundById()
    {
        string dir = MakeFolder("Game", "Game.exe");
        var scanner = Scanner(Entry("Game", dir.Replace('\\', '/') + "/", NewStyleId));

        Assert.Equal(dir, Assert.Single(scanner.GetInstalls()).InstallDir);
        Assert.Equal(dir, scanner.FindInstallDir(NewStyleId));
        Assert.Null(scanner.FindInstallDir("someone-else"));
    }

    [Fact]
    public void TheAppsOwnFolders_AreNotGamesToAFolderScan()
    {
        string installData = MakeFolder("__InstallData__", "Amazon Game Remover.exe");
        string app = MakeFolder("App", "Amazon Games.exe");
        Assert.True(FolderScannerService.IsLauncherClientInstall(installData));
        Assert.True(FolderScannerService.IsLauncherClientInstall(app));
    }

    [Theory]
    [InlineData(true, false, LaunchRoute.AmazonClient)]
    [InlineData(false, false, LaunchRoute.AmazonDirect)]
    [InlineData(true, true, LaunchRoute.AmazonDirect)]
    public void Amazon_RoutesByClientState(bool installed, bool launchDirectly, LaunchRoute expected)
    {
        var game = new GameEntry { IsAmazonGame = true, AmazonGameId = NewStyleId, ExecutablePath = @"C:\Amazon Games\Library\G\G.exe", LaunchDirectly = launchDirectly };
        var clients = new LaunchClientAvailability(false, false, false, false, false, AmazonGamesInstalled: installed);
        Assert.Equal(expected, LaunchRouter.Resolve(game, clients, _ => true));
        Assert.Equal(LauncherPlatform.Amazon, LaunchRouter.ClientPlatformFor(expected));
        Assert.Equal("Amazon Games", LaunchRouter.PlatformLabelFor(expected));
    }

    [Fact]
    public void ACategoryNamedLikeAPlatform_IsOnlyAPlaceholder_ForThatPlatformsGames()
    {
        // Filed under "Amazon" by hand before Amazon Games had an integration: the user's own category, kept.
        var local = new GameEntry { Category = "Amazon", SteamAppId = "40" };
        Assert.False(LibraryConstants.IsEnrichableCategory(local));
        // The same name on an Amazon game is its placeholder, so a genre may replace it.
        Assert.True(LibraryConstants.IsEnrichableCategory(new GameEntry { Category = "amazon", IsAmazonGame = true, AmazonGameId = NewStyleId }));
        // Another platform's placeholder is a user's choice on this one; Uncategorized and blank always yield.
        Assert.False(LibraryConstants.IsEnrichableCategory(new GameEntry { Category = "GOG", IsAmazonGame = true, AmazonGameId = NewStyleId }));
        Assert.True(LibraryConstants.IsEnrichableCategory(new GameEntry { Category = LibraryConstants.Uncategorized }));
        Assert.True(LibraryConstants.IsEnrichableCategory(new GameEntry { Category = "" }));
        Assert.True(LibraryConstants.IsEnrichableCategory(new GameEntry { Category = "Steam", IsSteamGame = true, SteamAppId = "40" }));
    }

    [Fact]
    public void AnAmazonGame_IsALauncherGame_InItsOwnCategory()
    {
        var game = new GameEntry { IsAmazonGame = true, AmazonGameId = NewStyleId };
        Assert.Equal(LibraryConstants.AmazonCategory, LibraryConstants.PlatformCategoryFor(game));
        Assert.True(LibraryConstants.IsEnrichableCategory(LibraryConstants.AmazonCategory, game));
        Assert.Equal("Amazon", game.PlatformLabel);
        Assert.EndsWith("amazon_games.png", LauncherLogos.PackUriFor(game));
        Assert.True(UrlProtocolHelper.IsAllowedLaunchUrl($"amazon-games://play/{NewStyleId}"));
    }
}
