using System.IO;
using System.IO.Compression;
using System.Text;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// itch app installs as the scanner reads them, shaped like the real ones on the test machine (2026-11-26):
/// <c>%AppData%\itch\apps\14dayswithyou\.itch\receipt.json.gz</c>, whose <c>game</c> object has id 1495405, the
/// title "14 Days With You [DEMO]" and its itch.io page, with the exe a folder down
/// (<c>14DaysWithYou-5.5-pc\14DaysWithYou.exe</c>); and <c>votv</c>, an archive the app never unpacked.
/// </summary>
public sealed class ItchScannerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerItchScanner", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Apps => Path.Combine(_root, "apps");

    private string MakeInstall(string folder, long? gameId, string? title, string? url, params string[] exes)
    {
        string dir = Path.Combine(Apps, folder);
        Directory.CreateDirectory(dir);
        foreach (string exe in exes)
        {
            string path = Path.Combine(dir, exe);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[400 * 1024]); // the folder scan passes over tiny exes
        }
        if (gameId != null) WriteReceipt(dir, $$"""
            { "game": { "id": {{gameId}}, "url": {{Json(url)}}, "title": {{Json(title)}}, "classification": "game" },
              "upload": { "id": 15679747 }, "files": [], "installerName": "archive" }
            """);
        return dir;
    }

    private static string Json(string? value) => value == null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    private static void WriteReceipt(string dir, string json)
    {
        Directory.CreateDirectory(Path.Combine(dir, ".itch"));
        using var file = File.Create(Path.Combine(dir, ItchScannerService.ReceiptRelativePath));
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(json));
    }

    private ItchScannerService Scanner() => new(() => [Apps]);

    [Fact]
    public void Install_IsFound_WithItsGameIdTitleAndPage_AndTheExeAFolderDown()
    {
        string dir = MakeInstall("14dayswithyou", 1495405, "14 Days With You [DEMO]", "https://cutiesai.itch.io/14dayswithyou",
            @"14DaysWithYou-5.5-pc\14DaysWithYou.exe", @"14DaysWithYou-5.5-pc\lib\py3-windows-x86_64\pythonw.exe");

        var game = Assert.Single(Scanner().ScanInstalledGames([]));

        Assert.Equal("1495405", game.GameId);
        Assert.Equal("14 Days With You [DEMO]", game.Name);
        Assert.Equal(dir, game.InstallDir);
        Assert.Equal(Path.Combine(dir, "14DaysWithYou-5.5-pc", "14DaysWithYou.exe"), game.ExePath);
        Assert.Equal("https://cutiesai.itch.io/14dayswithyou", game.StoreUrl);
    }

    [Fact]
    public void AGameInstalledTwice_IsTheFolderWithSomethingToRun()
    {
        MakeInstall("game", 42, "Game", null); // an upload with nothing to run (a soundtrack, say), listed first
        string build = MakeInstall("game-1", 42, "Game", null, @"Game\Game.exe");

        var game = Assert.Single(Scanner().ScanInstalledGames([]));

        Assert.Equal(build, game.InstallDir);
        Assert.Equal(2, Scanner().GetInstalls().Count);
    }

    [Fact]
    public void AHelperTheAppsListStillHas_IsNotTheGame()
    {
        string dir = MakeInstall("unity-game", 7, "Unity Game", null, "UnityCrashHandler64.exe", "Unity Game.exe");
        var verdict = Verdict(dir, ("UnityCrashHandler64.exe", "windows"), ("Unity Game.exe", "windows"));

        var targets = ItchScannerService.ParseLaunchTargets([Page(verdict)]);

        Assert.Equal(Path.Combine(dir, "Unity Game.exe"), targets[dir]);
    }

    [Fact]
    public void AFolderWrittenAnotherWay_StillFindsItsLaunchTarget()
    {
        string dir = MakeInstall("renpy-game", 9, "Ren'Py Game", null);
        string exe = Path.Combine(dir, "Game", "Game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, new byte[57344]);
        string slashed = Apps.Replace('\\', '/') + "/";

        var scanner = new ItchScannerService(() => [slashed], () => [Page(Verdict(dir, ("Game/Game.exe", "windows")))]);

        Assert.Equal(exe, Assert.Single(scanner.ScanInstalledGames([])).ExePath);
    }

    [Fact]
    public void AnUnreadableVerdict_LeavesTheOthersStanding()
    {
        string dir = MakeInstall("game", 7, "Game", null, "Game.exe");
        byte[] good = Page(Verdict(dir, ("Game.exe", "windows")));
        byte[] numberFlavor = Encoding.UTF8.GetBytes("{\"basePath\":\"C:\\\\x\",\"candidates\":[{\"path\":\"a.exe\",\"flavor\":3}]}");
        byte[] nulInPath = Encoding.UTF8.GetBytes("{\"basePath\":\"C:\\\\x\\u0000y\",\"candidates\":[{\"path\":\"a.exe\",\"flavor\":\"windows\"}]}");

        var targets = ItchScannerService.ParseLaunchTargets([[.. good, .. numberFlavor, .. nulInPath]]);

        Assert.Equal(Path.Combine(dir, "Game.exe"), Assert.Single(targets).Value);
    }

    /// <summary>A write-ahead log as SQLite writes it: a 32-byte header with the run's salts, then frames of a
    /// 24-byte header (page number, size, the salts that frame was written under, checksums) and one page.</summary>
    private static byte[] Wal(uint salt1, uint salt2, params (uint Salt1, uint Salt2, byte[] Page)[] frames)
    {
        const int pageSize = 4096;
        var wal = new byte[32 + frames.Length * (24 + pageSize)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal, 0x377F0682);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(4), 3007000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(8), pageSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(16), salt1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(20), salt2);
        for (int i = 0; i < frames.Length; i++)
        {
            int offset = 32 + i * (24 + pageSize);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(offset), (uint)i + 2);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(offset + 8), frames[i].Salt1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(offset + 12), frames[i].Salt2);
            frames[i].Page.CopyTo(wal.AsSpan(offset + 24));
        }
        return wal;
    }

    [Fact]
    public void AFrameLeftFromAnEarlierRunOfTheLog_DoesNotOverrideTheCurrentOne()
    {
        string dir = MakeInstall("game", 7, "Game", null, @"New\Game.exe", @"Old\Game.exe");
        byte[] current = Page(Verdict(dir, (@"New\Game.exe", "windows")));
        byte[] older = Page(Verdict(dir, (@"Old\Game.exe", "windows")));
        // After a checkpoint the log is written again from the start under new salts; the old run's frame stays after it.
        byte[] wal = Wal(0xBBBB, 0xCCCC, (0xBBBB, 0xCCCC, current), (0xAAAA, 0xCCCC, older));

        var targets = ItchScannerService.ParseLaunchTargets([ItchScannerService.CurrentWalPages(wal)]);

        Assert.Equal(Path.Combine(dir, "New", "Game.exe"), targets[dir]);
        Assert.Same(current, ItchScannerService.CurrentWalPages(current)); // not a log: as it is
    }

    /// <summary>A database page as butler.db has it: binary bytes around the cave's verdict, kept as JSON text.</summary>
    private static byte[] Page(params string[] verdicts)
    {
        var bytes = new List<byte> { 0x0D, 0x00, 0x00, 0x00, 0x01, 0x0F };
        foreach (string verdict in verdicts)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes("bd9fe7fa-3de2-42b7-8c8d-aa23ac055f13" + "2026-11-27T01:23:32Z"));
            bytes.AddRange(Encoding.UTF8.GetBytes(verdict));
            bytes.AddRange(new byte[] { 0x00, 0x81, 0x7F, 0x02 });
        }
        return bytes.ToArray();
    }

    private static string Verdict(string basePath, params (string Path, string Flavor)[] candidates) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            basePath,
            totalSize = 244169625,
            candidates = candidates.Select(c => new { path = c.Path, depth = 2, flavor = c.Flavor, arch = "amd64", size = 57344 }),
        });

    [Fact]
    public void TheExeTheAppLaunches_IsReadFromItsDatabase_EvenAStubTheFolderScanPassesOver()
    {
        string dir = MakeInstall("14dayswithyou", 1495405, "14 Days With You [DEMO]", null);
        string exe = Path.Combine(dir, "14DaysWithYou-5.5-pc", "14DaysWithYou.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, new byte[57344]); // a Ren'Py game's own exe: under the folder scan's 60 KB floor

        var withoutDatabase = new ItchScannerService(() => [Apps]);
        Assert.Empty(withoutDatabase.ScanInstalledGames([]));

        var verdict = Verdict(dir, ("14DaysWithYou-5.5-pc/14DaysWithYou.sh", "script"), ("14DaysWithYou-5.5-pc/14DaysWithYou.exe", "windows"));
        var scanner = new ItchScannerService(() => [Apps], () => [Page(), Page(verdict)]);
        Assert.Equal(exe, Assert.Single(scanner.ScanInstalledGames([])).ExePath);
    }

    [Fact]
    public void LaunchTargets_TakeTheNewestCopy_AndPassOverBrokenOrOutsideOnes()
    {
        string dir = MakeInstall("game", 7, "Game", null, @"Old\Game.exe", @"New\Game.exe");
        string outside = MakeInstall("other", 8, "Other", null, "Other.exe");
        string broken = Verdict(outside, ("Other.exe", "windows"));

        var targets = ItchScannerService.ParseLaunchTargets([
            Page(Verdict(dir, (@"Old\Game.exe", "windows"))),
            Page(Verdict(dir, (@"..\other\Other.exe", "windows"), ("Missing.exe", "windows"), (@"New\Game.exe", "windows")),
                 broken[..(broken.Length / 2)]),
        ]);

        Assert.Equal(Path.Combine(dir, "New", "Game.exe"), targets[dir]);
        Assert.False(targets.ContainsKey(outside));
    }

    [Fact]
    public void AnArchiveTheAppNeverUnpacked_HasNoExe_AndIsSkipped()
    {
        string dir = MakeInstall("votv", 1635465, "\"Voices Of The Void\" Alpha", "https://mrdrnose.itch.io/votv");
        File.WriteAllBytes(Path.Combine(dir, "a0.10.0_2.7z"), new byte[1024]);

        Assert.Empty(Scanner().ScanInstalledGames([]));
        Assert.Single(Scanner().GetInstalls()); // it is an install, just not one that can run
    }

    [Fact]
    public void GamesAlreadyInTheLibrary_AndFoldersWithoutAReceipt_AreNotOffered()
    {
        MakeInstall("14dayswithyou", 1495405, "14 Days With You", null, @"Game\Game.exe");
        MakeInstall("downloads", null, null, null);
        MakeInstall("broken", 0, "No id", null, @"Game\Game.exe");

        Assert.Empty(Scanner().ScanInstalledGames(["1495405"]));
        Assert.Single(Scanner().GetInstalls());
    }

    [Fact]
    public void AReceiptWithNoTitle_UsesTheFolderName_AndOnlyAnHttpsPageIsKept()
    {
        string dir = MakeInstall("some-game", 42, null, "javascript:alert(1)", @"Game.exe");

        var receipt = ItchScannerService.ReadReceipt(dir);

        Assert.NotNull(receipt);
        Assert.Equal("some-game", receipt.Value.Name);
        Assert.Null(receipt.Value.StoreUrl);
        Assert.Null(ItchScannerService.ReadReceipt(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void ADroppedExe_IsFoundInItsInstall_ByTheReceiptAboveIt()
    {
        string dir = MakeInstall("14dayswithyou", 1495405, "14 Days With You", null, @"14DaysWithYou-5.5-pc\14DaysWithYou.exe");
        string exe = Path.Combine(dir, "14DaysWithYou-5.5-pc", "14DaysWithYou.exe");

        var install = ItchScannerService.FindInstallContaining(exe);

        Assert.NotNull(install);
        Assert.Equal("1495405", install.Value.GameId);
        Assert.Equal(dir, install.Value.InstallDir);
        Assert.Null(ItchScannerService.FindInstallContaining(Path.Combine(_root, "elsewhere", "Game.exe")));

        var match = new PlatformLookupService(new SteamScannerService(), new GogScannerService(), new EaScannerService(), new EpicScannerService(),
            new UbisoftScannerService(), new XboxScannerService(), new BattleNetScannerService(), itch: Scanner()).CreateIndex().Match(exe);
        Assert.NotNull(match?.Itch);
        Assert.Equal(exe, match.Itch.ExePath);
    }

    [Fact]
    public void InstallLocations_AreTheDefaultFolder_PlusAnyTheAppsPreferencesList()
    {
        const string data = @"C:\Users\Someone\AppData\Roaming\itch";
        string preferences = """
            {"installLocations":{"a1":{"path":"D:\\Games\\itch\\"},"b2":"E:\\itch","bad":{"path":"relative"}},"defaultInstallLocation":"appdata"}
            """;

        var locations = ItchScannerService.InstallLocationsFrom(data, preferences);

        Assert.Equal([Path.Combine(data, "apps"), @"D:\Games\itch", @"E:\itch"], locations);
        Assert.Equal([Path.Combine(data, "apps")], ItchScannerService.InstallLocationsFrom(data, """{"installLocations":{}}"""));
        Assert.Equal([Path.Combine(data, "apps")], ItchScannerService.InstallLocationsFrom(data, "not json"));
        Assert.Equal([Path.Combine(data, "apps")], ItchScannerService.InstallLocationsFrom(data, null));
    }

    [Fact]
    public void TheAppsOwnFolder_IsNotAGameToAFolderScan()
    {
        string client = Path.Combine(_root, "itch");
        Directory.CreateDirectory(Path.Combine(client, "app-26.22.0"));
        File.WriteAllBytes(Path.Combine(client, "itch-setup.exe"), new byte[16]);
        File.WriteAllBytes(Path.Combine(client, "app-26.22.0", "itch.exe"), new byte[16]);

        Assert.True(FolderScannerService.IsLauncherClientInstall(client));
        Assert.True(FolderScannerService.IsLauncherClientInstall(Path.Combine(client, "app-26.22.0")));
        Assert.False(FolderScannerService.IsLauncherClientInstall(MakeInstall("game", 1, "Game", null, "Game.exe")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnItchGame_AlwaysLaunchesItsExe(bool launchDirectly)
    {
        var game = new GameEntry { IsItchGame = true, ItchGameId = "1495405", ExecutablePath = @"C:\itch\apps\g\G.exe", LaunchDirectly = launchDirectly };
        var clients = new LaunchClientAvailability(true, false, true, true, true, true, true);

        var route = LaunchRouter.Resolve(game, clients, _ => true);

        Assert.Equal(LaunchRoute.ItchDirect, route);
        Assert.Null(LaunchRouter.ClientPlatformFor(route)); // no app to close after the game
        Assert.Equal("itch.io", LaunchRouter.PlatformLabelFor(route));
    }

    [Fact]
    public void AnItchGame_IsALauncherGame_InItsOwnCategory()
    {
        var game = new GameEntry { IsItchGame = true, ItchGameId = "1495405" };
        Assert.Equal(LibraryConstants.ItchCategory, LibraryConstants.PlatformCategoryFor(game));
        Assert.True(LibraryConstants.IsEnrichableCategory(LibraryConstants.ItchCategory, game));
        Assert.False(LibraryConstants.IsEnrichableCategory(new GameEntry { Category = "itch.io", SteamAppId = "40" }));
        Assert.Equal("itch.io", game.PlatformLabel);
        Assert.EndsWith("itchio.png", LauncherLogos.PackUriFor(game));
        Assert.Null(LauncherPages.OpenLabel(game)); // nothing to open the game in: it always runs directly
    }
}
