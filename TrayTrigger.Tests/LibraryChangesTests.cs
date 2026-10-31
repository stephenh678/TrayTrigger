using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The Activity lines for Edit Game saves and for removing and restoring games.</summary>
public class LibraryChangesTests
{
    private static GameEntry Game() => new()
    {
        Name = "Hades",
        ExecutablePath = @"C:\Games\Hades\Hades.exe",
        Arguments = "-windowed",
    };

    private static (string Text, string Detail)? Edit(GameEntry game, System.Action change)
    {
        var before = LibraryChanges.Capture(game);
        change();
        return LibraryChanges.DescribeEdit(before, LibraryChanges.Capture(game));
    }

    [Fact]
    public void SavingWithNothingChanged_RecordsNothing()
    {
        Assert.Null(Edit(Game(), () => { }));
    }

    [Fact]
    public void ChangedArguments_ShowTheOldAndNewValue()
    {
        var game = Game();
        var line = Edit(game, () => game.Arguments = "-fullscreen -dx12");

        Assert.NotNull(line);
        Assert.Equal("Hades: launch arguments changed", line!.Value.Text);
        Assert.Equal("Launch arguments: -windowed -> -fullscreen -dx12", line.Value.Detail);
    }

    [Fact]
    public void ABlankValue_IsShownAsNone()
    {
        var game = Game();
        var line = Edit(game, () => game.Arguments = "");

        Assert.Equal("Launch arguments: -windowed -> (none)", line!.Value.Detail);
    }

    [Fact]
    public void SeveralChanges_AreOneEntryListingEach()
    {
        var game = Game();
        var line = Edit(game, () =>
        {
            game.RunAsAdmin = true;
            game.ExecutablePath = @"D:\Hades\Hades.exe";
            game.CloseLauncherOnExit = true;
        });

        Assert.Equal("Hades: executable, run as administrator and close launcher on exit changed", line!.Value.Text);
        Assert.Equal(
            "Executable: C:\\Games\\Hades\\Hades.exe -> D:\\Hades\\Hades.exe\nRun as administrator: off -> on\nClose launcher on exit: off -> on",
            line.Value.Detail);
    }

    [Fact]
    public void ARenamedGame_IsNamedByItsNewName()
    {
        var game = Game();
        var line = Edit(game, () => game.Name = "Hades II");

        Assert.Equal("Hades II: name changed", line!.Value.Text);
        Assert.Equal("Name: Hades -> Hades II", line.Value.Detail);
    }

    [Fact]
    public void ScriptSettings_AreRecorded()
    {
        var game = Game();
        var line = Edit(game, () =>
        {
            game.PreLaunchScriptPath = @"C:\Scripts\pre.ps1";
            game.RunScriptsAsAdmin = true;
            game.PreLaunchScriptTimeoutSeconds = 30;
        });

        Assert.Equal("Hades: pre-launch script, run scripts as administrator and script timeout changed", line!.Value.Text);
        Assert.Contains("Pre-launch script: (none) -> C:\\Scripts\\pre.ps1", line.Value.Detail);
        Assert.Contains("Script timeout: 10s -> 30s", line.Value.Detail);
    }

    [Fact]
    public void ADisplayMode_IsRecorded()
    {
        var game = Game();
        var line = Edit(game, () => { game.DisplayWidth = 1920; game.DisplayHeight = 1080; game.DisplayRefreshHz = 144; });

        Assert.Equal("Hades: display mode changed", line!.Value.Text);
        Assert.Equal("Display mode: (none) -> 1920 × 1080 @ 144 Hz", line.Value.Detail);
    }

    [Fact]
    public void KeepingTheMouseOnTheGamesScreen_IsRecorded()
    {
        var game = Game();
        var line = Edit(game, () => game.KeepMouseOnGameScreen = true);

        Assert.Equal("Hades: keep the mouse on the game's screen changed", line!.Value.Text);
        Assert.Equal("Keep the mouse on the game's screen: off -> on", line.Value.Detail);
    }

    [Fact]
    public void TheMonitorSoundAndDimmingChoices_AreRecorded_WithoutTheirIds()
    {
        var game = Game();
        var line = Edit(game, () =>
        {
            game.DisplayMonitorId = @"\\?\DISPLAY#X#1";
            game.MakeMainDisplay = true;
            game.DimOtherScreens = true;
            game.AudioOutputDeviceId = "{0.0.0}.{abc}";
        });

        Assert.Equal("Hades: monitor, make the monitor the main display, dim the other screens and sound output changed", line!.Value.Text);
        Assert.Contains("Monitor: the main display -> a chosen one", line.Value.Detail);
        Assert.Contains("Sound output: not changed -> a chosen one", line.Value.Detail);
        Assert.DoesNotContain("abc", line.Value.Detail);
    }

    [Fact]
    public void WhatPerformanceActivityRecords_IsNotRecordedAgain()
    {
        var game = Game();
        var line = Edit(game, () =>
        {
            game.PerformanceProfile = PerformanceProfileMode.Optimized;
            game.Category = "Action";
            game.IsFavorite = true;
        });

        Assert.Null(line);
    }

    [Fact]
    public void RemovingOneGame_NamesItAndSaysFilesAreKept()
    {
        var (text, detail) = LibraryChanges.DescribeRemoved(["Hades"], undoable: true);

        Assert.Equal("Hades was removed from the library", text);
        Assert.StartsWith("Its files on disk were not touched", detail);
        Assert.Contains("undone", detail);
    }

    [Fact]
    public void RemovingSeveralGames_CountsThemAndListsTheNames()
    {
        var (text, detail) = LibraryChanges.DescribeRemoved(["Hades", "Celeste"], undoable: false);

        Assert.Equal("2 games were removed from the library", text);
        Assert.StartsWith("Hades, Celeste. Their files on disk were not touched", detail);
        Assert.DoesNotContain("undone", detail);
    }

    [Fact]
    public void RestoringGames_SaysSo()
    {
        Assert.Equal("Hades was put back in the library", LibraryChanges.DescribeRestored(["Hades"]));
        Assert.Equal("3 games were put back in the library", LibraryChanges.DescribeRestored(["a", "b", "c"]));
    }

    // --- Pictures and the Steam match ---

    [Fact]
    public void ChoosingAPicture_IsSaid_AsAutomaticToAChosenOne()
    {
        var game = Game();
        var line = Edit(game, () => game.HeroImagePath = @"C:\Covers\x_1.jpg");
        Assert.Equal("Hades: hero banner changed", line!.Value.Text);
        Assert.Equal("Hero banner: automatic -> a chosen one", line.Value.Detail);

        var poster = Game();
        poster.CoverImagePath = @"C:\Covers\auto.jpg";
        var posterLine = Edit(poster, () => { poster.CoverImagePath = @"C:\Covers\mine.png"; poster.HasCustomPoster = true; });
        Assert.Equal("Poster: automatic -> a chosen one", posterLine!.Value.Detail);
    }

    [Fact]
    public void PuttingAPictureBackToAutomatic_AndPickingAnotherOne_AreEachSaid()
    {
        var game = Game();
        game.HeroImagePath = @"C:\Covers\x_1.jpg";
        game.IconPath = @"C:\Icons\mine.ico";
        game.HasCustomIcon = true;

        var back = Edit(game, () => { game.HeroImagePath = null; game.HasCustomIcon = false; });
        Assert.Equal("Hades: hero banner and icon changed", back!.Value.Text);
        Assert.Equal("Hero banner: a chosen one -> automatic\nIcon: a chosen one -> automatic", back.Value.Detail);

        var game2 = Game();
        game2.HeroImagePath = @"C:\Covers\x_1.jpg";
        Assert.Equal("Hero banner: a chosen one -> another chosen one", Edit(game2, () => game2.HeroImagePath = @"C:\Covers\x_2.jpg")!.Value.Detail);
    }

    [Fact]
    public void ANewAutomaticPoster_IsNotSaid_ButAChangedSteamMatchIs()
    {
        var game = Game();
        game.CoverImagePath = @"C:\Covers\1.jpg";
        Assert.Null(Edit(game, () => game.CoverImagePath = @"C:\Covers\2.jpg"));

        game.SteamAppId = "1145360";
        var line = Edit(game, () => game.SteamAppId = "292030");
        Assert.Equal("Hades: Steam App ID changed", line!.Value.Text);
        Assert.Equal("Steam App ID: 1145360 -> 292030", line.Value.Detail);
        Assert.Equal("Steam App ID: 292030 -> (none)", Edit(game, () => game.SteamAppId = null)!.Value.Detail);
    }

    [Fact]
    public void TheLibraryMenusLines_SayWhatChanged()
    {
        Assert.Equal(("Hades: poster changed", "Poster: automatic -> a chosen one"), LibraryChanges.DescribeArtChosen("Hades", "poster", false));
        Assert.Equal("Icon: a chosen one -> another chosen one", LibraryChanges.DescribeArtChosen("Hades", "icon", true).Detail);
        Assert.Null(LibraryChanges.DescribeSteamIdChange("Hades", "1145360", " 1145360 "));
        Assert.Equal("Steam App ID: (none) -> 1145360", LibraryChanges.DescribeSteamIdChange("Hades", null, "1145360")!.Value.Detail);
    }
}
