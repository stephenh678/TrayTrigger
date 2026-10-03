using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Backup &amp; Restore: what a backup holds, that a restore puts it back on the next start with
/// this PC's own settings kept, and that paths are pointed at where things are now. Everything runs
/// against StorageService's folder-scoped constructor, so the real %AppData% library is never touched.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerBackup", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private StorageService Storage(string name) =>
        new(Path.Combine(_root, name, "roaming"), Path.Combine(_root, name, "local"));

    private static readonly IReadOnlyDictionary<(LauncherPlatform, string), (string, string?)> NoLaunchers =
        new Dictionary<(LauncherPlatform, string), (string, string?)>();

    // ------------------------------------------------------------------ back up and restore

    [Fact]
    public void ABackup_RestoredOnTheNextStart_BringsBackTheLibraryToolsSettingsScriptsAndArt()
    {
        var before = Storage("pc");
        string icon = Path.Combine(before.IconsDirectory, "g1.png");
        File.WriteAllBytes(icon, [1, 2, 3]);
        before.SaveGames([new GameEntry { Id = "g1", Name = "Hades", IconPath = icon, CumulativePlaytimeMinutes = 90, CpuAffinity = CpuAffinityMode.Auto }]);
        before.SaveTools([new ToolEntry { Name = "Afterburner", TargetPath = @"C:\Tools\ab.exe" }]);
        before.SaveSettings(new AppSettings { TrayMenuHotkey = "Ctrl+Alt+Y" });
        Directory.CreateDirectory(Path.Combine(before.BaseDirectory, "Scripts"));
        File.WriteAllText(Path.Combine(before.BaseDirectory, "Scripts", "mine.ps1"), "Write-Output hi");

        string zip = Path.Combine(_root, "backup.zip");
        var manifest = new BackupService(before).Create(zip);
        Assert.Equal(1, manifest.Games);
        Assert.Equal(1, manifest.Tools);
        Assert.Equal(1, manifest.Scripts);
        Assert.Equal(1, manifest.ArtFiles);

        // Then everything changes, and the backup is restored.
        before.SaveGames([]);
        before.SaveTools([]);
        before.SaveSettings(new AppSettings());
        File.Delete(icon);

        var service = new BackupService(before);
        string safety = service.CreateSafetyBackup();
        service.StageRestore(zip, safety);
        Assert.True(BackupService.HasPendingRestore(before.BaseDirectory));

        var nextStart = new StorageService(before.BaseDirectory, before.LocalCacheDirectory);
        var result = new BackupService(nextStart).ApplyPendingRestore(() => NoLaunchers);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(safety, result.SafetyBackupPath);
        Assert.False(BackupService.HasPendingRestore(before.BaseDirectory));
        var game = Assert.Single(nextStart.LoadGames());
        Assert.Equal("Hades", game.Name);
        Assert.Equal(90, game.CumulativePlaytimeMinutes);
        Assert.Equal(CpuAffinityMode.Auto, game.CpuAffinity);
        Assert.Equal("Afterburner", Assert.Single(nextStart.LoadTools()).Name);
        Assert.Equal("Ctrl+Alt+Y", nextStart.LoadSettings().TrayMenuHotkey);
        Assert.Equal("Write-Output hi", File.ReadAllText(Path.Combine(before.BaseDirectory, "Scripts", "mine.ps1")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(icon));
        Assert.False(Directory.Exists(Path.Combine(before.BaseDirectory, BackupService.StagingFolderName)));

        // The safety backup holds what there was just before: an empty library.
        Assert.Equal(0, BackupService.ReadManifest(safety).Games);
    }

    [Fact]
    public void ABackupFromAnotherPc_KeepsThisPcsMachineSettings_AndMovesItsTrayTriggerPaths()
    {
        var oldPc = Storage("old");
        string oldIcon = Path.Combine(oldPc.IconsDirectory, "g1.png");
        File.WriteAllBytes(oldIcon, [9]);
        string oldScript = Path.Combine(oldPc.BaseDirectory, "Scripts", "pre.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(oldScript)!);
        File.WriteAllText(oldScript, "x");
        oldPc.SaveGames([new GameEntry { Id = "g1", Name = "Hades", IconPath = oldIcon, PreLaunchScriptPath = oldScript }]);
        oldPc.SaveSettings(new AppSettings
        {
            TweakPriorState = new() { ["power_plan"] = "old-pc-plan" },
            MainWindowLeft = 5000,
            CompactTrayMenu = true,
        });
        string zip = Path.Combine(_root, "old.zip");
        new BackupService(oldPc).Create(zip);
        // The old PC's folders aren't on the new one.
        Directory.Delete(Path.Combine(_root, "old"), recursive: true);

        var newPc = Storage("new");
        newPc.SaveSettings(new AppSettings
        {
            TweakPriorState = new() { ["power_plan"] = "new-pc-plan" },
            MainWindowLeft = 100,
            HasSeenWelcomePrompt = true,
        });
        var service = new BackupService(newPc);
        service.StageRestore(zip, service.CreateSafetyBackup());
        var result = new BackupService(newPc).ApplyPendingRestore(() => NoLaunchers);

        Assert.True(result.Succeeded, result.Error);
        var settings = newPc.LoadSettings();
        Assert.True(settings.CompactTrayMenu);                          // a preference: from the backup
        Assert.Equal("new-pc-plan", settings.TweakPriorState["power_plan"]); // this PC's state: kept
        Assert.Equal(100, settings.MainWindowLeft);
        Assert.True(settings.HasSeenWelcomePrompt);

        var game = Assert.Single(newPc.LoadGames());
        Assert.Equal(Path.Combine(newPc.IconsDirectory, "g1.png"), game.IconPath);
        Assert.Equal(Path.Combine(newPc.BaseDirectory, "Scripts", "pre.ps1"), game.PreLaunchScriptPath);
    }

    [Fact]
    public void ARestoreThatFails_SaysWhy_AndIsNotRetriedAtTheNextStart()
    {
        var storage = Storage("pc");
        string staging = Path.Combine(storage.BaseDirectory, BackupService.StagingFolderName);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, BackupService.ReadyMarkerName), "safety.zip");
        // No backup.json or data in it.

        var result = new BackupService(storage).ApplyPendingRestore(() => NoLaunchers);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Equal("safety.zip", result.SafetyBackupPath);
        Assert.False(BackupService.HasPendingRestore(storage.BaseDirectory));
    }

    // ------------------------------------------------------------------ what a file must be

    [Fact]
    public void AZipThatIsntABackup_IsRefusedWithAReason()
    {
        string zip = Path.Combine(_root, "photos.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("holiday.jpg");
        }

        var ex = Assert.Throws<InvalidDataException>(() => BackupService.ReadManifest(zip));
        Assert.Equal("This file isn't a TrayTrigger backup.", ex.Message);
    }

    [Fact]
    public void AFileThatIsntAZip_IsRefusedAsNotABackup()
    {
        Directory.CreateDirectory(_root);
        string notes = Path.Combine(_root, "notes.zip");
        File.WriteAllText(notes, "just some text");

        var ex = Assert.Throws<InvalidDataException>(() => BackupService.ReadManifest(notes));
        Assert.Equal("This file isn't a TrayTrigger backup.", ex.Message);
    }

    [Fact]
    public void AFileDatedBeforeZipsCanHoldIt_IsStillBackedUp()
    {
        // A zip can only hold dates from 1980 on; an icon stamped earlier must not cost the backup.
        var storage = Storage("pc");
        storage.SaveGames([]);
        storage.SaveSettings(new AppSettings());
        string icon = Path.Combine(storage.IconsDirectory, "old.png");
        File.WriteAllBytes(icon, [1]);
        File.SetLastWriteTime(icon, new DateTime(1975, 1, 1));

        var manifest = new BackupService(storage).Create(Path.Combine(_root, "dated.zip"));

        Assert.Equal(1, manifest.ArtFiles);
        using var zip = ZipFile.OpenRead(Path.Combine(_root, "dated.zip"));
        Assert.NotNull(zip.GetEntry("cache/Icons/old.png"));
    }

    [Fact]
    public void AnArtFileThatCantBeRead_IsLeftOutOfTheBackup_NotPutInEmpty()
    {
        var storage = Storage("pc");
        string good = Path.Combine(storage.IconsDirectory, "good.png");
        string locked = Path.Combine(storage.IconsDirectory, "locked.png");
        File.WriteAllBytes(good, [1, 2, 3]);
        File.WriteAllBytes(locked, [4, 5, 6]);
        storage.SaveGames([new GameEntry { Id = "g1", Name = "Hades", IconPath = locked }]);
        storage.SaveSettings(new AppSettings());

        string zip = Path.Combine(_root, "backup.zip");
        // Held open by "another program" with no sharing, as an icon being written or scanned can be.
        BackupManifest manifest;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            manifest = new BackupService(storage).Create(zip);
        }

        // The summary counts what went in, not what was found.
        Assert.Equal(1, manifest.ArtFiles);
        Assert.Equal(1, BackupService.ReadManifest(zip).ArtFiles);

        // The backup was still made, with the rest of the art, and the unreadable file isn't in it
        // at all: an empty entry for it would be restored over the real file.
        using var archive = ZipFile.OpenRead(zip);
        Assert.NotNull(archive.GetEntry("cache/Icons/good.png"));
        Assert.Equal(3, archive.GetEntry("cache/Icons/good.png")!.Length);
        Assert.Null(archive.GetEntry("cache/Icons/locked.png"));
        Assert.NotNull(archive.GetEntry("data/games.json"));
    }

    [Fact]
    public void ABackupFromANewerTrayTrigger_IsRefused()
    {
        string zip = Path.Combine(_root, "newer.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = archive.CreateEntry(BackupService.ManifestEntry).Open();
            JsonSerializer.Serialize(writer, new BackupManifest { FormatVersion = BackupManifest.CurrentFormatVersion + 1, AppVersion = "v9.0.0" }, AppJsonContext.Default.BackupManifest);
        }

        var ex = Assert.Throws<InvalidDataException>(() => BackupService.ReadManifest(zip));
        Assert.Contains("newer TrayTrigger (v9.0.0)", ex.Message);
    }

    [Fact]
    public void AnEntryNamedOutsideTrayTriggersFolders_IsNeverUnpacked()
    {
        var storage = Storage("pc");
        storage.SaveGames([]);
        storage.SaveSettings(new AppSettings());
        string zip = Path.Combine(_root, "evil.zip");
        new BackupService(storage).Create(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            using (var w = new StreamWriter(archive.CreateEntry("data/Scripts/../../../escaped.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(archive.CreateEntry("data/autorun.ps1").Open())) w.Write("x");
        }

        new BackupService(storage).StageRestore(zip, "safety.zip");

        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(storage.BaseDirectory, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(storage.BaseDirectory, BackupService.StagingFolderName, "data", "autorun.ps1")));
    }

    [Theory]
    [InlineData("backup.json", true)]
    [InlineData("data/games.json", true)]
    [InlineData("data/settings.json", true)]
    [InlineData("data/Scripts/My Game.ps1", true)]
    [InlineData("data/Scripts/sub/helper.ps1", true)]
    [InlineData("cache/Icons/g1.png", true)]
    [InlineData("cache/Covers/g1.jpg", true)]
    [InlineData("data/profile-session.json", false)]
    [InlineData("data/games.json.bak", false)]
    [InlineData("cache/Icons/sub/g1.png", false)]
    [InlineData("data/Scripts/../../x.ps1", false)]
    [InlineData("C:/Windows/x.dll", false)]
    [InlineData("data\\games.json", false)]
    public void OnlyWhatABackupHolds_IsTakenFromIt(string name, bool taken)
    {
        Assert.Equal(taken, BackupService.IsBackupEntry(name));
    }

    // ------------------------------------------------------------------ this PC's settings

    [Fact]
    public void AnApiKeyThisAccountCantRead_GivesWayToOneItCan_OrIsCleared()
    {
        Func<string, bool> canDecrypt = key => key.StartsWith("mine:", StringComparison.Ordinal);

        var keepCurrent = new AppSettings { SteamGridDbApiKey = "theirs:1" };
        Assert.False(BackupService.MergeMachineSettings(keepCurrent, new AppSettings { SteamGridDbApiKey = "mine:2" }, canDecrypt));
        Assert.Equal("mine:2", keepCurrent.SteamGridDbApiKey);

        var cleared = new AppSettings { RawgApiKey = "theirs:1" };
        Assert.True(BackupService.MergeMachineSettings(cleared, new AppSettings(), canDecrypt));
        Assert.Equal(string.Empty, cleared.RawgApiKey);

        var readable = new AppSettings { RawgApiKey = "mine:3" };
        Assert.False(BackupService.MergeMachineSettings(readable, null, canDecrypt));
        Assert.Equal("mine:3", readable.RawgApiKey);
    }

    // ------------------------------------------------------------------ paths

    private static BackupPathFixer.Context Context(IEnumerable<string> files, IEnumerable<string>? dirs = null,
        IReadOnlyDictionary<(LauncherPlatform Platform, string Id), (string InstallDir, string? ExePath)>? launchers = null)
    {
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        // The three drives are plugged in.
        var dirSet = new HashSet<string>((dirs ?? []).Concat([@"C:\", @"D:\", @"E:\"]), StringComparer.OrdinalIgnoreCase);
        return new BackupPathFixer.Context
        {
            OldDataDirectory = @"C:\Users\old\AppData\Roaming\TrayTrigger",
            NewDataDirectory = @"C:\Users\new\AppData\Roaming\TrayTrigger",
            OldCacheDirectory = @"C:\Users\old\AppData\Local\TrayTrigger",
            NewCacheDirectory = @"C:\Users\new\AppData\Local\TrayTrigger",
            OldUserProfile = @"C:\Users\old",
            NewUserProfile = @"C:\Users\new",
            FileExists = fileSet.Contains,
            DirectoryExists = dirSet.Contains,
            DriveRoots = [@"C:\", @"D:\", @"E:\"],
            LauncherInstalls = launchers ?? new Dictionary<(LauncherPlatform, string), (string, string?)>(),
        };
    }

    [Fact]
    public void AGameOnADriveWhoseLetterChanged_IsFoundOnTheNewOne()
    {
        var game = new GameEntry { Name = "Hades", ExecutablePath = @"D:\Games\Hades\Hades.exe", WorkingDirectory = @"D:\Games\Hades" };
        var report = BackupPathFixer.Fix([game], [], new AppSettings(), Context([@"E:\Games\Hades\Hades.exe"], [@"E:\Games\Hades"]));

        Assert.Equal(@"E:\Games\Hades\Hades.exe", game.ExecutablePath);
        Assert.Equal(@"E:\Games\Hades", game.WorkingDirectory);
        Assert.Equal(2, report.Moved);
        Assert.Empty(report.StillMissing);
    }

    [Fact]
    public void APathOnTwoOtherDrives_IsLeftAlone_RatherThanGuessed()
    {
        var game = new GameEntry { Name = "Hades", ExecutablePath = @"D:\Games\Hades\Hades.exe" };
        var report = BackupPathFixer.Fix([game], [], new AppSettings(),
            Context([@"C:\Games\Hades\Hades.exe", @"E:\Games\Hades\Hades.exe"]));

        Assert.Equal(@"D:\Games\Hades\Hades.exe", game.ExecutablePath);
        Assert.Equal(["Hades"], report.StillMissing);
    }

    [Fact]
    public void OnlyGamesTheLibraryWillGreyOutForTheirExe_AreReportedMissing()
    {
        // Steam and Battle.net answer for their own games whatever the path says, and a launcher
        // game on a drive that isn't plugged in hasn't been uninstalled.
        var steam = new GameEntry { Name = "Portal", IsSteamGame = true, SteamAppId = "400", ExecutablePath = @"D:\Steam\Portal\hl2.exe" };
        var battleNet = new GameEntry { Name = "Overwatch", IsBattleNetGame = true, BattleNetUid = "prometheus", ExecutablePath = @"D:\Overwatch\Overwatch.exe" };
        var unplugged = new GameEntry { Name = "On USB", IsEpicGame = true, EpicAppName = "usb", ExecutablePath = @"G:\Epic\usb.exe" };
        var gone = new GameEntry { Name = "Gone", ExecutablePath = @"D:\Games\gone.exe" };
        var goneFromGog = new GameEntry { Name = "Gone from GOG", IsGogGame = true, GogGameId = "1", ExecutablePath = @"D:\GOG\gone.exe" };

        var report = BackupPathFixer.Fix([steam, battleNet, unplugged, gone, goneFromGog], [], new AppSettings(), Context([]));

        Assert.Equal(["Gone", "Gone from GOG"], report.StillMissing);
    }

    [Fact]
    public void AGameInTheOldUserFolder_MovesToTheNewOne()
    {
        var game = new GameEntry { Name = "Itch Game", ExecutablePath = @"C:\Users\old\Games\itch\game.exe" };
        BackupPathFixer.Fix([game], [], new AppSettings(), Context([@"C:\Users\new\Games\itch\game.exe"]));
        Assert.Equal(@"C:\Users\new\Games\itch\game.exe", game.ExecutablePath);
    }

    [Fact]
    public void ALauncherGame_IsRelinkedThroughItsLauncher()
    {
        var game = new GameEntry
        {
            Name = "Cyberpunk", IsGogGame = true, GogGameId = "1423049311",
            ExecutablePath = @"D:\GOG\Cyberpunk\bin\x64\Cyberpunk2077.exe", WorkingDirectory = @"D:\GOG\Cyberpunk",
        };
        var launchers = new Dictionary<(LauncherPlatform, string), (string, string?)>
        {
            [(LauncherPlatform.Gog, "1423049311")] = (@"F:\Games\Cyberpunk 2077", @"F:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe"),
        };

        var report = BackupPathFixer.Fix([game], [], new AppSettings(), Context([], launchers: launchers));

        Assert.Equal(@"F:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", game.ExecutablePath);
        Assert.Equal(@"F:\Games\Cyberpunk 2077", game.WorkingDirectory);
        Assert.Equal(1, report.Relinked);
        Assert.Empty(report.StillMissing);
    }

    [Fact]
    public void AGameThatsThere_AnXboxGame_AndALauncherLink_AreLeftAsTheyAre()
    {
        var there = new GameEntry { Name = "Here", ExecutablePath = @"C:\Games\here.exe" };
        var xbox = new GameEntry { Name = "Halo", IsXboxGame = true, ExecutablePath = @"D:\XboxGames\Halo\Content\halo.exe" };
        var link = new GameEntry { Name = "Portal", IsSteamGame = true, ExecutablePath = "steam://rungameid/400" };

        var report = BackupPathFixer.Fix([there, xbox, link], [], new AppSettings(), Context([@"C:\Games\here.exe"]));

        Assert.Equal(@"C:\Games\here.exe", there.ExecutablePath);
        Assert.Equal(@"D:\XboxGames\Halo\Content\halo.exe", xbox.ExecutablePath);
        Assert.Equal("steam://rungameid/400", link.ExecutablePath);
        Assert.Empty(report.StillMissing);
    }

    [Fact]
    public void ToolsScriptDefaultsAndScanLocations_AreMovedToo()
    {
        var tool = new ToolEntry { Name = "SimHub", TargetPath = @"D:\Tools\SimHub\SimHubWPF.exe", IconPath = @"C:\Users\old\AppData\Local\TrayTrigger\Icons\t.png" };
        var settings = new AppSettings
        {
            ScriptDefaults = new ScriptDefaults { PreLaunchScriptPath = @"C:\Users\old\AppData\Roaming\TrayTrigger\Scripts\all.ps1" },
            ScanLocations = [new ScanLocation { Path = @"D:\Games", Source = ScanLocationSource.Manual }, new ScanLocation { Path = @"D:\SteamLibrary", Source = ScanLocationSource.Steam }],
        };

        BackupPathFixer.Fix([], [tool], settings, Context(
            [@"E:\Tools\SimHub\SimHubWPF.exe", @"C:\Users\new\AppData\Roaming\TrayTrigger\Scripts\all.ps1"],
            [@"E:\Games", @"E:\SteamLibrary"]));

        Assert.Equal(@"E:\Tools\SimHub\SimHubWPF.exe", tool.TargetPath);
        Assert.Equal(@"C:\Users\new\AppData\Local\TrayTrigger\Icons\t.png", tool.IconPath);
        Assert.Equal(@"C:\Users\new\AppData\Roaming\TrayTrigger\Scripts\all.ps1", settings.ScriptDefaults.PreLaunchScriptPath);
        Assert.Equal(@"E:\Games", settings.ScanLocations[0].Path);
        // Steam's own library folders are found again by Steam integration, not moved here.
        Assert.Equal(@"D:\SteamLibrary", settings.ScanLocations[1].Path);
    }
}
