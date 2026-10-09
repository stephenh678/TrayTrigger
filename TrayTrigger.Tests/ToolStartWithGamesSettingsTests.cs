using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// "Start when I launch a game", "Close it when the game exits", "Close it when I launch a game" and
/// "Open it again when the game exits" in Edit Tool: a program only, each follow-up only together with
/// its own box, and Start and Close never together, whatever the boxes said when Save was pressed.
/// </summary>
public class ToolStartWithGamesSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public ToolStartWithGamesSettingsTests()
    {
        Directory.CreateDirectory(_root);
    }

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

    private IconExtractorService Icons() =>
        new(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local")));

    private string File(string name)
    {
        string path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, string.Empty);
        return path;
    }

    [Theory]
    [InlineData(@"C:\Tools\MSIAfterburner.exe", "", true)]
    [InlineData(@"C:\Tools\backup.ps1", "", false)]
    [InlineData(@"C:\Tools\cleanup.bat", "", false)]
    [InlineData("", "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", false)]
    public void CanStartWithGames_OnlyAProgram(string target, string appId, bool expected) =>
        Assert.Equal(expected, ToolCatalog.CanStartWithGames(new ToolEntry { TargetPath = target, AppId = appId }));

    [Fact]
    public void Tabs_GameTabsOnlyWhileAToolUsesThem_AfterFavorites_StartBeforeClose()
    {
        var afterburner = new ToolEntry { Name = "MSI Afterburner", TargetPath = @"C:\Tools\MSIAfterburner.exe", Category = "Graphics" };
        var discord = new ToolEntry { Name = "Discord", TargetPath = @"C:\Tools\Discord.exe", Category = "Chat" };
        var vortex = new ToolEntry { Name = "Vortex", TargetPath = @"C:\Tools\Vortex.exe", Category = "Mods" };
        Assert.Equal([LibraryConstants.AllCategory, LibraryConstants.FavoritesCategory, "Chat", "Graphics", "Mods"], ToolCatalog.TabsFor([afterburner, discord, vortex]));

        afterburner.StartWithGames = true;
        Assert.Equal([LibraryConstants.AllCategory, LibraryConstants.FavoritesCategory, ToolCatalog.StartWithGamesTab, "Chat", "Graphics", "Mods"], ToolCatalog.TabsFor([afterburner, discord, vortex]));

        discord.CloseForGames = true;
        Assert.Equal([LibraryConstants.AllCategory, LibraryConstants.FavoritesCategory, ToolCatalog.StartWithGamesTab, ToolCatalog.CloseWithGamesTab, "Chat", "Graphics", "Mods"], ToolCatalog.TabsFor([afterburner, discord, vortex]));

        afterburner.StartWithGames = false;
        Assert.Equal([LibraryConstants.AllCategory, LibraryConstants.FavoritesCategory, ToolCatalog.CloseWithGamesTab, "Chat", "Graphics", "Mods"], ToolCatalog.TabsFor([afterburner, discord, vortex]));
    }

    [Fact]
    public void GameTabs_ListTheirOwnTools()
    {
        var afterburner = new ToolEntry { TargetPath = @"C:\Tools\MSIAfterburner.exe", StartWithGames = true };
        var discord = new ToolEntry { TargetPath = @"C:\Tools\Discord.exe", CloseForGames = true };
        var vortex = new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" };
        // Ticked in a hand-edited tools.json; a script can't start with games, so it isn't listed.
        var script = new ToolEntry { TargetPath = @"C:\Tools\backup.ps1", StartWithGames = true };

        Assert.True(ToolCatalog.IsInTab(afterburner, ToolCatalog.StartWithGamesTab));
        Assert.False(ToolCatalog.IsInTab(afterburner, ToolCatalog.CloseWithGamesTab));
        Assert.True(ToolCatalog.IsInTab(discord, ToolCatalog.CloseWithGamesTab));
        Assert.False(ToolCatalog.IsInTab(discord, ToolCatalog.StartWithGamesTab));
        Assert.False(ToolCatalog.IsInTab(vortex, ToolCatalog.StartWithGamesTab));
        Assert.False(ToolCatalog.IsInTab(vortex, ToolCatalog.CloseWithGamesTab));
        Assert.False(ToolCatalog.IsInTab(script, ToolCatalog.StartWithGamesTab));
    }

    [Fact]
    public void ClosesForGames_AProgramOnly_AndNeverOneThatAlsoStarts()
    {
        Assert.True(ToolCatalog.ClosesForGames(new ToolEntry { TargetPath = @"C:\Tools\Discord.exe", CloseForGames = true }));
        Assert.False(ToolCatalog.ClosesForGames(new ToolEntry { TargetPath = @"C:\Tools\backup.ps1", CloseForGames = true }));
        Assert.False(ToolCatalog.ClosesForGames(new ToolEntry { AppId = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App", CloseForGames = true }));
        // A hand-edited tools.json with both: Start wins.
        var both = new ToolEntry { TargetPath = @"C:\Tools\Discord.exe", CloseForGames = true, StartWithGames = true };
        Assert.False(ToolCatalog.ClosesForGames(both));
        Assert.True(ToolCatalog.StartsWithGames(both));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Discord\Update.exe", "--processStart Discord.exe", @"C:\Users\me\AppData\Local\Discord", "Discord")]
    [InlineData(@"C:\Users\me\AppData\Local\slack\Update.exe", "--processStart=slack.exe", @"C:\Users\me\AppData\Local\slack", "slack")]
    [InlineData(@"C:\Users\me\AppData\Local\Teams\Update.exe", "--processStartAndWait Teams.exe", @"C:\Users\me\AppData\Local\Teams", "Teams")]
    [InlineData(@"C:\Users\me\AppData\Local\GitHubDesktop\Update.exe", "--processStart \"GitHubDesktop.exe\" --process-start-args \"--no-sandbox\"", @"C:\Users\me\AppData\Local\GitHubDesktop", "GitHubDesktop")]
    public void SquirrelApp_FollowsUpdateExeToTheApp(string target, string arguments, string folder, string processName)
    {
        var app = ToolCatalog.SquirrelApp(new ToolEntry { TargetPath = target, Arguments = arguments });

        Assert.NotNull(app);
        Assert.Equal(folder, app.Value.Folder);
        Assert.Equal(processName, app.Value.ProcessName);
    }

    [Theory]
    [InlineData(@"C:\Tools\Discord.exe", "")]                       // the app itself
    [InlineData(@"C:\Tools\Update.exe", "")]                        // an Update.exe with nothing to start
    [InlineData(@"C:\Tools\Update.exe", "--processStart ..\\x.exe")]  // not a file name
    public void SquirrelApp_NullForAnythingElse(string target, string arguments) =>
        Assert.Null(ToolCatalog.SquirrelApp(new ToolEntry { TargetPath = target, Arguments = arguments }));

    [Theory]
    [InlineData("start with games", "Start with Games")]
    [InlineData("CLOSE WITH GAMES", "Close with Games")]
    public void ACategoryNamedLikeAGameTab_IsUncategorized_SoItCantBeTakenForTheTab(string category, string tab)
    {
        var tool = new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe", Category = category };

        Assert.Equal(LibraryConstants.Uncategorized, ToolCatalog.NormalizeCategory(tool.Category));
        Assert.Equal([LibraryConstants.Uncategorized], ToolCatalog.CategoriesOf([tool]));
        Assert.False(ToolCatalog.IsInTab(tool, tab));
        // Adding a tool while the tab is selected doesn't make a category of it either.
        Assert.Equal(LibraryConstants.Uncategorized, ToolCatalog.CategoryForNewTool(tab));
    }

    [Fact]
    public void Save_Program_KeepsBothBoxes() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "MSI Afterburner", TargetPath = File("MSIAfterburner.exe") };
        var vm = new ToolEditViewModel(tool, [], Icons()) { StartWithGames = true, CloseAfterGames = true };

        vm.SaveCommand.Execute(null);

        Assert.True(tool.StartWithGames);
        Assert.True(tool.CloseAfterGames);
    });

    [Fact]
    public void Save_CloseWithoutStart_IsNotSaved() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "MSI Afterburner", TargetPath = File("MSIAfterburner.exe") };
        var vm = new ToolEditViewModel(tool, [], Icons()) { StartWithGames = false, CloseAfterGames = true };

        vm.SaveCommand.Execute(null);

        Assert.False(tool.StartWithGames);
        Assert.False(tool.CloseAfterGames);
    });

    [Fact]
    public void Save_RetargetedToAScript_DropsEveryGameBox() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Backup", TargetPath = File("Backup.exe"), StartWithGames = true, CloseAfterGames = true };
        var vm = new ToolEditViewModel(tool, [], Icons()) { TargetPath = File("Backup.ps1"), CloseForGames = true, ReopenAfterGames = true };
        Assert.False(vm.CanStartWithGames);

        vm.SaveCommand.Execute(null);

        Assert.False(tool.StartWithGames);
        Assert.False(tool.CloseAfterGames);
        Assert.False(tool.CloseForGames);
        Assert.False(tool.ReopenAfterGames);
    });

    [Fact]
    public void Save_CloseForGames_KeepsBothBoxes() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Discord", TargetPath = File("Discord.exe") };
        var vm = new ToolEditViewModel(tool, [], Icons()) { CloseForGames = true, ReopenAfterGames = true };

        vm.SaveCommand.Execute(null);

        Assert.True(tool.CloseForGames);
        Assert.True(tool.ReopenAfterGames);
        Assert.False(tool.StartWithGames);
    });

    [Fact]
    public void Save_ReopenWithoutClose_IsNotSaved() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Discord", TargetPath = File("Discord.exe") };
        var vm = new ToolEditViewModel(tool, [], Icons()) { CloseForGames = false, ReopenAfterGames = true };

        vm.SaveCommand.Execute(null);

        Assert.False(tool.CloseForGames);
        Assert.False(tool.ReopenAfterGames);
    });

    [Fact]
    public void WhenILaunchAGame_IsOneChoiceOfThree() => Sta(() =>
    {
        var vm = new ToolEditViewModel(new ToolEntry { Name = "Discord", TargetPath = @"C:\Tools\Discord.exe" }, [], Icons());
        Assert.True(vm.LeaveAlone);

        vm.StartWithGames = true;
        Assert.False(vm.LeaveAlone);
        vm.CloseForGames = true;
        Assert.False(vm.StartWithGames);
        Assert.True(vm.CloseForGames);

        vm.StartWithGames = true;
        Assert.False(vm.CloseForGames);
        Assert.True(vm.StartWithGames);

        vm.LeaveAlone = true;
        Assert.False(vm.StartWithGames);
        Assert.False(vm.CloseForGames);
        Assert.True(vm.LeaveAlone);
        // Un-ticking the default picks nothing else, so it stays ticked; clearing an action falls back to it.
        vm.LeaveAlone = false;
        Assert.True(vm.LeaveAlone);
        vm.CloseForGames = true;
        vm.CloseForGames = false;
        Assert.True(vm.LeaveAlone);
    });

    [Fact]
    public void Save_BothTickedInAHandEditedFile_KeepsStartOnly() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Discord", TargetPath = File("Discord.exe"), StartWithGames = true, CloseForGames = true, ReopenAfterGames = true };
        var vm = new ToolEditViewModel(tool, [], Icons());
        Assert.True(vm.StartWithGames);
        Assert.True(vm.CloseForGames);

        vm.SaveCommand.Execute(null);

        Assert.True(tool.StartWithGames);
        Assert.False(tool.CloseForGames);
        Assert.False(tool.ReopenAfterGames);
    });

    [Fact]
    public void Save_Wait_KeepsTheSeconds() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "MSI Afterburner", TargetPath = File("MSIAfterburner.exe") };
        var vm = new ToolEditViewModel(tool, [], Icons()) { StartWithGames = true, WaitBeforeGame = true, WaitBeforeGameSeconds = " 8 " };

        vm.SaveCommand.Execute(null);

        Assert.True(tool.WaitBeforeGame);
        Assert.Equal(8, tool.WaitBeforeGameSeconds);
    });

    [Fact]
    public void Save_WaitBlank_IsTheDefault() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "MSI Afterburner", TargetPath = File("MSIAfterburner.exe"), WaitBeforeGameSeconds = 12 };
        var vm = new ToolEditViewModel(tool, [], Icons()) { StartWithGames = true, WaitBeforeGame = true, WaitBeforeGameSeconds = "" };

        vm.SaveCommand.Execute(null);

        Assert.Equal(ToolCatalog.DefaultWaitSeconds, tool.WaitBeforeGameSeconds);
    });

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("five")]
    public void Save_WaitOutsideOneToSixty_IsRefused_AndNothingIsSaved(string seconds) => Sta(() =>
    {
        var tool = new ToolEntry { Name = "MSI Afterburner", TargetPath = File("MSIAfterburner.exe") };
        var vm = new ToolEditViewModel(tool, [], Icons()) { StartWithGames = true, WaitBeforeGame = true, WaitBeforeGameSeconds = seconds };
        bool closed = false;
        vm.RequestClose += _ => closed = true;

        vm.SaveCommand.Execute(null);

        Assert.False(closed);
        Assert.Contains("between 1 and 60", vm.ValidationMessage);
        Assert.False(tool.StartWithGames);
    });

    [Fact]
    public void Save_WaitOff_ABadNumberKeepsTheOldOne() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "MSI Afterburner", TargetPath = File("MSIAfterburner.exe"), WaitBeforeGameSeconds = 7 };
        var vm = new ToolEditViewModel(tool, [], Icons()) { StartWithGames = true, WaitBeforeGame = false, WaitBeforeGameSeconds = "abc" };

        vm.SaveCommand.Execute(null);

        Assert.False(tool.WaitBeforeGame);
        Assert.Equal(7, tool.WaitBeforeGameSeconds);
    });

    [Fact]
    public void Save_WaitWithoutStart_IsNotSaved() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "MSI Afterburner", TargetPath = File("MSIAfterburner.exe") };
        var vm = new ToolEditViewModel(tool, [], Icons()) { StartWithGames = false, WaitBeforeGame = true };

        vm.SaveCommand.Execute(null);

        Assert.False(tool.WaitBeforeGame);
    });

    [Fact]
    public void StartWithGames_IsAnUnsavedChange() => Sta(() =>
    {
        var vm = new ToolEditViewModel(new ToolEntry { Name = "MSI Afterburner", TargetPath = @"C:\Tools\MSIAfterburner.exe" }, [], Icons());
        Assert.False(vm.HasUnsavedChanges);

        vm.StartWithGames = true;
        Assert.True(vm.HasUnsavedChanges);
    });
}
