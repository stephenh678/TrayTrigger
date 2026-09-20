using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Which entries an install snapshot marks, and - the half that matters more - which ones it
/// refuses to mark. A false "not installed" greys out a game that plays perfectly, and a user
/// whose external drive was asleep at startup would see it across half their library at once.
/// </summary>
public class InstalledGameIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { Console.WriteLine($"Temp folder left behind: {ex.Message}"); }
    }

    /// <summary>A real file on disk, so File.Exists answers yes for it.</summary>
    private string RealExe(string name = "game.exe")
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>A path on a drive letter this PC does not have, for the "drive is away" cases.</summary>
    private static string PathOnAbsentDrive()
    {
        foreach (char letter in "QVXYZ")
        {
            if (!Directory.Exists($"{letter}:\\")) return $@"{letter}:\Games\Absent\game.exe";
        }
        throw new InvalidOperationException("Every candidate drive letter exists on this PC.");
    }

    // ------------------------------------------------------------------ launchers that own the answer

    [Fact]
    public void SteamGame_TheIndexDoesNotList_IsNotInstalled()
    {
        var index = InstalledGameIndex.ForTest(steamAppIds: ["440"]);
        var game = new GameEntry { Name = "Cyberpunk", IsSteamGame = true, SteamAppId = "1091500", ExecutablePath = "steam://rungameid/1091500" };

        Assert.Equal(GameAvailability.NotInstalled, index.AvailabilityOf(game));
    }

    [Fact]
    public void SteamGame_TheIndexLists_IsAvailable()
    {
        var index = InstalledGameIndex.ForTest(steamAppIds: ["440", "1091500"]);
        var game = new GameEntry { Name = "Cyberpunk", IsSteamGame = true, SteamAppId = "1091500", ExecutablePath = "steam://rungameid/1091500" };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    [Fact]
    public void XboxGame_TheIndexDoesNotList_IsNotInstalled()
    {
        var index = InstalledGameIndex.ForTest(xboxAumids: ["Other.Game_8wekyb3d8bbwe!App"]);
        var game = new GameEntry { Name = "Forza", IsXboxGame = true, XboxAumid = "Microsoft.Forza_8wekyb3d8bbwe!App" };

        Assert.Equal(GameAvailability.NotInstalled, index.AvailabilityOf(game));
    }

    [Fact]
    public void BattleNetGame_TheIndexDoesNotList_IsNotInstalled()
    {
        var index = InstalledGameIndex.ForTest(battleNetUids: ["s2"]);
        var game = new GameEntry { Name = "Hearthstone", IsBattleNetGame = true, BattleNetUid = "hs_beta" };

        Assert.Equal(GameAvailability.NotInstalled, index.AvailabilityOf(game));
    }

    /// <summary>
    /// An Xbox entry's exe path is stale by design - the package folder is versioned and re-resolved
    /// on every launch - so the file being gone must never be what marks it.
    /// </summary>
    [Fact]
    public void XboxGame_TheIndexLists_IsAvailableEvenWithNoExecutableOnDisk()
    {
        var index = InstalledGameIndex.ForTest(xboxAumids: ["Microsoft.Forza_8wekyb3d8bbwe!App"]);
        var game = new GameEntry
        {
            Name = "Forza",
            IsXboxGame = true,
            XboxAumid = "Microsoft.Forza_8wekyb3d8bbwe!App",
            ExecutablePath = Path.Combine(_root, "a-version-that-was-updated-away", "forza.exe")
        };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    // ------------------------------------------------------------------ a launcher that could not be read

    [Fact]
    public void SteamGame_WhenSteamWasNotRead_IsLeftAlone()
    {
        // Steam not installed, a library folder on a sleeping drive, a registry read that threw:
        // all of them arrive here as "not read", and none of them may mark a game.
        var index = InstalledGameIndex.ForTest(xboxAumids: []);
        var game = new GameEntry { Name = "Cyberpunk", IsSteamGame = true, SteamAppId = "1091500", ExecutablePath = "steam://rungameid/1091500" };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    [Fact]
    public void XboxAndBattleNetGames_WhenTheirLauncherWasNotRead_AreLeftAlone()
    {
        var index = InstalledGameIndex.ForTest(steamAppIds: ["440"]);

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(
            new GameEntry { Name = "Forza", IsXboxGame = true, XboxAumid = "Microsoft.Forza_8wekyb3d8bbwe!App" }));
        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(
            new GameEntry { Name = "Hearthstone", IsBattleNetGame = true, BattleNetUid = "hs_beta" }));
    }

    /// <summary>An entry with no ID to look up cannot be answered for, whatever the index holds.</summary>
    [Fact]
    public void SteamGame_WithNoAppId_IsLeftAlone()
    {
        var index = InstalledGameIndex.ForTest(steamAppIds: ["440"]);
        var game = new GameEntry { Name = "Something", IsSteamGame = true, SteamAppId = null, ExecutablePath = "steam://open/games" };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    // ------------------------------------------------------------------ the exe-backed launchers

    [Fact]
    public void GogGame_WhoseExeIsGoneFromAnAttachedDrive_IsNotInstalled()
    {
        var index = InstalledGameIndex.ForTest();
        var game = new GameEntry { Name = "Witcher 3", IsGogGame = true, GogGameId = "1207664643", ExecutablePath = Path.Combine(_root, "gone", "witcher3.exe") };

        Assert.Equal(GameAvailability.NotInstalled, index.AvailabilityOf(game));
    }

    [Fact]
    public void GogGame_WhoseExeIsThere_IsAvailable()
    {
        var index = InstalledGameIndex.ForTest();
        var game = new GameEntry { Name = "Witcher 3", IsGogGame = true, GogGameId = "1207664643", ExecutablePath = RealExe("witcher3.exe") };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    /// <summary>
    /// A Steam App ID is not a Steam game. Another launcher's game carries one whenever its
    /// artwork and details were matched on Steam - a GOG copy of Cyberpunk 2077 is the ordinary
    /// case - and asking Steam about it would call every one of them uninstalled.
    /// </summary>
    [Fact]
    public void GogGame_CarryingASteamAppIdForItsArtwork_IsJudgedByItsExeNotBySteam()
    {
        var index = InstalledGameIndex.ForTest(steamAppIds: ["1872550"]);
        var game = new GameEntry
        {
            Name = "Cyberpunk 2077",
            IsGogGame = true,
            GogGameId = "1423049311",
            SteamAppId = "1091500",
            ExecutablePath = RealExe("REDprelauncher.exe")
        };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    /// <summary>
    /// The case this whole guard exists for: an unplugged external drive is not an uninstall, and
    /// must not grey out every game that lives on it.
    /// </summary>
    [Fact]
    public void LauncherGame_OnADriveThatIsNotAttached_IsLeftAlone()
    {
        var index = InstalledGameIndex.ForTest();
        var game = new GameEntry { Name = "Anno", IsUbisoftGame = true, UbisoftGameId = "12345", ExecutablePath = PathOnAbsentDrive() };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    /// <summary>A dropped .url shortcut resolves to a scheme, not a path - File.Exists says no to
    /// every one of them.</summary>
    [Fact]
    public void LauncherGame_LaunchedByProtocolUrl_IsLeftAlone()
    {
        var index = InstalledGameIndex.ForTest();
        var game = new GameEntry { Name = "Fortnite", IsEpicGame = true, EpicAppName = "Fortnite", ExecutablePath = "com.epicgames.launcher://apps/Fortnite?action=launch" };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    // ------------------------------------------------------------------ local games keep the old marker

    [Fact]
    public void LocalGame_WhoseExeIsGone_IsExecutableMissingNotUninstalled()
    {
        // "Missing" and not "not installed": nothing here can tell an uninstall from a move, and
        // a move is what "Locate Executable..." fixes without losing the entry.
        var index = InstalledGameIndex.ForTest();
        var game = new GameEntry { Name = "OpenTTD", ExecutablePath = Path.Combine(_root, "gone", "openttd.exe") };

        Assert.Equal(GameAvailability.ExecutableMissing, index.AvailabilityOf(game));
    }

    [Fact]
    public void LocalGame_WhoseExeIsThere_IsAvailable()
    {
        var index = InstalledGameIndex.ForTest();
        var game = new GameEntry { Name = "OpenTTD", ExecutablePath = RealExe("openttd.exe") };

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(game));
    }

    [Fact]
    public void GameWithNoExecutablePathAtAll_IsAvailable()
    {
        var index = InstalledGameIndex.ForTest();

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(new GameEntry { Name = "Blank" }));
    }

    /// <summary>
    /// A local exe wearing a forced Steam badge for the overlay is still a local game. Judging it
    /// against Steam's installed set would mark every one of them uninstalled - Steam has never
    /// heard of them.
    /// </summary>
    [Fact]
    public void LocalGame_WithAForcedSteamBadge_IsJudgedAsLocal()
    {
        var index = InstalledGameIndex.ForTest(steamAppIds: ["440"]);
        var game = new GameEntry { Name = "A Mod Launcher", ForceSteamOverlayTag = true, ExecutablePath = Path.Combine(_root, "gone", "mod.exe") };

        Assert.True(game.HasSteamOverlay);
        Assert.Equal(GameAvailability.ExecutableMissing, index.AvailabilityOf(game));
    }

    // ------------------------------------------------------------------ the default index

    /// <summary>
    /// Before the first pass has run, every card asks this one. It has read nothing, so the only
    /// thing it may still say is what File.Exists alone can prove.
    /// </summary>
    [Fact]
    public void TheUnknownIndex_MarksNoLauncherGame()
    {
        var index = InstalledGameIndex.Unknown;

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(
            new GameEntry { Name = "Cyberpunk", IsSteamGame = true, SteamAppId = "1091500" }));
        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(
            new GameEntry { Name = "Forza", IsXboxGame = true, XboxAumid = "Microsoft.Forza_8wekyb3d8bbwe!App" }));
        Assert.Equal(GameAvailability.ExecutableMissing, index.AvailabilityOf(
            new GameEntry { Name = "OpenTTD", ExecutablePath = Path.Combine(_root, "gone", "openttd.exe") }));
    }

    /// <summary>A real capture on the machine running the tests: it must not throw, whatever
    /// launchers are or aren't installed here, and it must leave <see cref="GameEntry"/> alone.</summary>
    [Fact]
    public void Capture_OnThisMachine_DoesNotThrow()
    {
        var index = InstalledGameIndex.Capture();

        Assert.Equal(GameAvailability.Available, index.AvailabilityOf(new GameEntry { Name = "Blank" }));
    }
}
