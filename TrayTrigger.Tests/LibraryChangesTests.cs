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
        Assert.Contains("files on disk were not touched", detail);
        Assert.Contains("undone", detail);
    }

    [Fact]
    public void RemovingSeveralGames_CountsThemAndListsTheNames()
    {
        var (text, detail) = LibraryChanges.DescribeRemoved(["Hades", "Celeste"], undoable: false);

        Assert.Equal("2 games were removed from the library", text);
        Assert.StartsWith("Hades, Celeste. ", detail);
        Assert.DoesNotContain("undone", detail);
    }

    [Fact]
    public void RestoringGames_SaysSo()
    {
        Assert.Equal("Hades was put back in the library", LibraryChanges.DescribeRestored(["Hades"]));
        Assert.Equal("3 games were put back in the library", LibraryChanges.DescribeRestored(["a", "b", "c"]));
    }
}
