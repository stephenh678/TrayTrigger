using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// "Start when I launch a game" and "Close it when the game exits" in Edit Tool: a program only, and
/// Close only together with Start, whatever the boxes said when Save was pressed.
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
    public void Save_RetargetedToAScript_DropsBoth() => Sta(() =>
    {
        var tool = new ToolEntry { Name = "Backup", TargetPath = File("Backup.exe"), StartWithGames = true, CloseAfterGames = true };
        var vm = new ToolEditViewModel(tool, [], Icons()) { TargetPath = File("Backup.ps1") };
        Assert.False(vm.CanStartWithGames);

        vm.SaveCommand.Execute(null);

        Assert.False(tool.StartWithGames);
        Assert.False(tool.CloseAfterGames);
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
