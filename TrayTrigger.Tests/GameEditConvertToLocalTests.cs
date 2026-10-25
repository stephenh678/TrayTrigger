using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>Edit Game's "Convert to Local game": what it changes, the way back before Save, and what Save keeps.</summary>
public sealed class GameEditConvertToLocalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private GameEditViewModel Open(GameEntry game) =>
        new(game, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))));

    /// <summary>A Battle.net import that Look Up has also matched to a Steam App ID, as Call of Duty is.</summary>
    private static GameEntry BattleNetGame() => new()
    {
        Name = "Call of Duty",
        Category = "Action",
        ExecutablePath = @"C:\Program Files (x86)\Call of Duty\_retail_\cod.exe",
        IsBattleNetGame = true,
        ImportedFrom = LauncherPlatform.BattleNet,
        BattleNetUid = "auks",
        BattleNetProgramId = "AUKS",
        SteamAppId = "1938090",
        CloseLauncherOnExit = true,
    };

    private static GameEntry SteamGame() => new()
    {
        Name = "Hades",
        Category = "Action",
        ExecutablePath = "steam://rungameid/1145360",
        IsSteamGame = true,
        ImportedFrom = LauncherPlatform.Steam,
        SteamAppId = "1145360",
    };

    [Fact]
    public void Convert_ShowsTheNoteInsteadOfThePlatformBox_AndOffersTheSteamOptionOnlyBecauseAnAppIdIsSet() => Sta(() =>
    {
        var vm = Open(BattleNetGame());
        Assert.True(vm.HasPlatform);
        Assert.False(vm.IsConvertedToLocal);

        vm.ConvertToLocalCommand.Execute(null);

        Assert.False(vm.HasPlatform);
        Assert.True(vm.IsConvertedToLocal);
        Assert.Contains("Battle.net", vm.ConvertedNotice);
        Assert.True(vm.CanOfferSteamLaunch);
    });

    [Fact]
    public void Undo_PutsTheLinkBack_AndLeavesNothingToSave() => Sta(() =>
    {
        var game = BattleNetGame();
        var vm = Open(game);

        vm.ConvertToLocalCommand.Execute(null);
        Assert.True(vm.HasUnsavedChanges);
        vm.UndoConvertToLocalCommand.Execute(null);

        Assert.True(vm.HasPlatform);
        Assert.False(vm.IsConvertedToLocal);
        Assert.False(vm.CanOfferSteamLaunch);
        Assert.False(vm.HasUnsavedChanges);

        vm.SaveCommand.Execute(null);
        Assert.True(game.IsBattleNetGame);
        Assert.Equal("auks", game.BattleNetUid);
        Assert.Equal(LauncherPlatform.BattleNet, game.ImportedFrom);
        Assert.True(game.CloseLauncherOnExit);
    });

    [Fact]
    public void Undo_OnASteamGame_PutsTheSteamFlagBack() => Sta(() =>
    {
        var game = SteamGame();
        var vm = Open(game);

        vm.ConvertToLocalCommand.Execute(null);
        Assert.False(vm.IsSteamGame);
        vm.UndoConvertToLocalCommand.Execute(null);

        Assert.True(vm.IsSteamGame);
        Assert.True(vm.HasPlatform);
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public void Save_AfterConvert_MakesAPlainLocalGame() => Sta(() =>
    {
        var game = BattleNetGame();
        var vm = Open(game);

        vm.ConvertToLocalCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.False(game.IsBattleNetGame);
        Assert.Null(game.BattleNetUid);
        Assert.Null(game.BattleNetProgramId);
        Assert.Null(game.ImportedFrom);
        Assert.False(game.IsSteamGame);
        Assert.False(game.LaunchDirectly);
        Assert.False(game.CloseLauncherOnExit);
        Assert.Equal("1938090", game.SteamAppId);
    });

    [Fact]
    public void LaunchThroughSteamTickedAfterConvert_IsKeptBySave() => Sta(() =>
    {
        var game = BattleNetGame();
        var vm = Open(game);

        vm.ConvertToLocalCommand.Execute(null);
        vm.IsSteamGame = true;
        vm.SaveCommand.Execute(null);

        Assert.True(game.IsSteamGame);
        Assert.False(game.IsBattleNetGame);
        Assert.Null(game.BattleNetUid);
    });

    [Fact]
    public void ConvertingASteamGame_NamesSteamAsWhatItWasLinkedTo() => Sta(() =>
    {
        var vm = Open(SteamGame());

        vm.ConvertToLocalCommand.Execute(null);

        Assert.Contains("Steam", vm.ConvertedNotice);
        Assert.Contains("Steam", vm.StatusMessage);
        Assert.DoesNotContain("Local)", vm.StatusMessage);
    });

    [Fact]
    public void Convert_IsNotOfferedForAnXboxGame_ButIsForTheOthers() => Sta(() =>
    {
        Assert.False(Open(new GameEntry { Name = "Halo", IsXboxGame = true, XboxAumid = "a!b" }).CanConvertToLocal);
        Assert.True(Open(BattleNetGame()).CanConvertToLocal);
        Assert.True(Open(SteamGame()).CanConvertToLocal);
    });

    [Fact]
    public void TheSteamBadgeOption_IsForLocalGames_AndStaysOnALauncherGameThatHasItTicked() => Sta(() =>
    {
        Assert.True(Open(new GameEntry { Name = "Indie", ExecutablePath = @"C:\Games\indie.exe" }).CanOfferSteamBadge);
        Assert.False(Open(BattleNetGame()).CanOfferSteamBadge);
        Assert.False(Open(SteamGame()).CanOfferSteamBadge);

        var ticked = BattleNetGame();
        ticked.ForceSteamOverlayTag = true;
        Assert.True(Open(ticked).CanOfferSteamBadge);

        var vm = Open(BattleNetGame());
        vm.ConvertToLocalCommand.Execute(null);
        Assert.True(vm.CanOfferSteamBadge);
        vm.UndoConvertToLocalCommand.Execute(null);
        Assert.False(vm.CanOfferSteamBadge);
    });

    [Fact]
    public void Convert_IsRefused_WhenThereIsNoExecutableToRun() => Sta(() =>
    {
        var game = BattleNetGame();
        game.ExecutablePath = string.Empty;
        var vm = Open(game);

        vm.ConvertToLocalCommand.Execute(null);

        Assert.True(vm.HasPlatform);
        Assert.False(vm.IsConvertedToLocal);
        Assert.True(vm.StatusIsProblem);

        vm.ExecutablePath = @"C:\Program Files (x86)\Call of Duty\_retail_\cod.exe";
        vm.ConvertToLocalCommand.Execute(null);

        Assert.True(vm.IsConvertedToLocal);
    });

    [Fact]
    public void ASaveRefusedForAFieldValue_LeavesABlankNameAsItWasTyped() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades", Category = "Action" };
        var vm = Open(game);

        vm.Name = "  ";
        vm.SteamAppId = "not-a-number";
        vm.SaveCommand.Execute(null);

        Assert.Equal("  ", vm.Name);
        Assert.Equal("Hades", game.Name);

        vm.SteamAppId = string.Empty;
        vm.SaveCommand.Execute(null);

        Assert.Equal("Unnamed Game", game.Name);
    });
}
