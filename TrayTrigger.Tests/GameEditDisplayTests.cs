using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The display and sound services are shared statics; the tests that stand a fake in for them take turns.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class SharedDisplayServicesCollection
{
    public const string Name = "Shared display and sound services";
}

/// <summary>Edit Game's Display and sound card: the lists follow the chosen monitor, and Save writes the choices back.</summary>
[Collection(SharedDisplayServicesCollection.Name)]
public sealed class GameEditDisplayTests : IDisposable
{
    private const string One = @"\\.\DISPLAY1";
    private const string Two = @"\\.\DISPLAY2";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));
    private readonly FakeDisplay _display;
    private readonly FakeAudio _audio = new();

    public GameEditDisplayTests()
    {
        _display = new FakeDisplay
        {
            Attached = [One, Two],
            Mons =
            [
                new(One, "MON1", "Left", true, new ScreenRect(0, 0, 3840, 2160)),
                new(Two, "MON2", "Right", false, new ScreenRect(3840, 0, 5760, 1080)),
            ],
        };
        _display.ModesByDevice[Two] = [new(1920, 1080, 144), new(1920, 1080, 60), new(1280, 720, 60)];
        DisplayModeService.UseBackendForTests(_display, Path.Combine(_root, "display.json"));
        AudioOutputService.UseBackendForTests(_audio, Path.Combine(_root, "audio.json"));
    }

    public void Dispose()
    {
        DisplayModeService.UseBackendForTests(null);
        AudioOutputService.UseBackendForTests(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
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

    private GameEditViewModel Open(GameEntry game, int monitors = 2) =>
        new(game, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))))
        { MonitorCount = () => monitors };

    private static GameEntry GameWithMode(string monitorId = "MON1") => new()
    {
        Name = "Hades", Category = "Action", DisplayMonitorId = monitorId,
        DisplayWidth = 1920, DisplayHeight = 1080, DisplayRefreshHz = 144,
    };

    [Fact]
    public void WithTwoMonitors_TheCardShowsEveryPart() => Sta(() =>
    {
        var vm = Open(new GameEntry { Name = "Hades", Category = "Action" });

        Assert.True(vm.ShowDisplayCard);
        Assert.True(vm.ShowMonitorChoice);
        Assert.True(vm.ShowMouseLock);
        Assert.True(vm.ShowDim);
        Assert.True(vm.ShowAudio);
        Assert.Equal(3, vm.MonitorOptions.Count);
    });

    [Fact]
    public void WithOneMonitorAndOneSoundDevice_OnlyTheResolutionPartShows() => Sta(() =>
    {
        _display.Mons = [_display.Mons[0]];
        _audio.Devices = [new AudioDevice("speakers", "Speakers")];
        var vm = Open(new GameEntry { Name = "Hades", Category = "Action" }, monitors: 1);

        Assert.True(vm.ShowDisplay);
        Assert.False(vm.ShowMonitorChoice);
        Assert.False(vm.ShowMouseLock);
        Assert.False(vm.ShowDim);
        Assert.False(vm.ShowAudio);
    });

    [Fact]
    public void GameWithTheOptionsOnAlready_ShowsThemEvenOnOneMonitor() => Sta(() =>
    {
        _display.Mons = [_display.Mons[0]];
        var game = new GameEntry { Name = "Hades", Category = "Action", KeepMouseOnGameScreen = true, DimOtherScreens = true };

        var vm = Open(game, monitors: 1);

        Assert.True(vm.ShowMouseLock);
        Assert.True(vm.ShowDim);
    });

    [Fact]
    public void SwitchingMonitor_ShowsThatMonitorsModes_AndKeepsTheChosenModeWhenItIsListed() => Sta(() =>
    {
        var vm = Open(GameWithMode("MON1"));
        Assert.Contains(vm.ResolutionOptions, o => o.Width == 3840);

        vm.SelectedMonitor = vm.MonitorOptions.Single(o => o.Id == "MON2");

        Assert.DoesNotContain(vm.ResolutionOptions, o => o.Width == 3840);
        Assert.Equal(1920, vm.SelectedResolution.Width);
        Assert.Equal(144, vm.SelectedRate!.Hz);
    });

    [Fact]
    public void SwitchingToAMonitorThatDoesNotListTheMode_KeepsItLabelled() => Sta(() =>
    {
        var vm = Open(new GameEntry { Name = "Hades", Category = "Action", DisplayMonitorId = "MON1", DisplayWidth = 2560, DisplayHeight = 1440, DisplayRefreshHz = 144 });

        vm.SelectedMonitor = vm.MonitorOptions.Single(o => o.Id == "MON2");

        Assert.Equal(2560, vm.SelectedResolution.Width);
        Assert.Contains("this display doesn't list it", vm.SelectedResolution.Label);
    });

    [Fact]
    public void ClearingTheResolution_ThenSwitchingMonitor_DoesNotBringTheSavedModeBack() => Sta(() =>
    {
        var vm = Open(GameWithMode("MON1"));
        vm.SelectedResolution = vm.ResolutionOptions[0];
        Assert.True(vm.SelectedResolution.IsNone);

        vm.SelectedMonitor = vm.MonitorOptions.Single(o => o.Id == "MON2");

        Assert.True(vm.SelectedResolution.IsNone);
        Assert.False(vm.ShowRate);
    });

    [Fact]
    public void ASavedMonitorThatIsNotConnected_StaysInTheList_AndIsKeptWhenSaved() => Sta(() =>
    {
        var game = GameWithMode("UNPLUGGED");
        var vm = Open(game);

        Assert.Equal("A monitor that isn't connected", vm.SelectedMonitor.Label);
        vm.SaveCommand.Execute(null);

        Assert.Equal("UNPLUGGED", game.DisplayMonitorId);
    });

    [Fact]
    public void Save_WritesTheMonitorMainDisplayDimmingMouseAndSound() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades", Category = "Action" };
        var vm = Open(game);

        vm.SelectedMonitor = vm.MonitorOptions.Single(o => o.Id == "MON2");
        vm.SelectedResolution = vm.ResolutionOptions.Single(o => o.Width == 1920);
        vm.SelectedRate = vm.RateOptions.Single(r => r.Hz == 60);
        vm.MakeMainDisplay = true;
        vm.DimOtherScreens = true;
        vm.KeepMouseOnGameScreen = true;
        vm.SelectedAudio = vm.AudioOptions.Single(o => o.Id == "headset");
        vm.SaveCommand.Execute(null);

        Assert.Equal("MON2", game.DisplayMonitorId);
        Assert.Equal((1920, 1080, 60), (game.DisplayWidth, game.DisplayHeight, game.DisplayRefreshHz));
        Assert.True(game.MakeMainDisplay);
        Assert.True(game.DimOtherScreens);
        Assert.True(game.KeepMouseOnGameScreen);
        Assert.Equal("headset", game.AudioOutputDeviceId);
    });

    [Fact]
    public void MakeMainDisplay_IsDropped_WhenTheChosenMonitorIsTheMainOneAlready() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades", Category = "Action", MakeMainDisplay = true, DisplayMonitorId = "MON2" };
        var vm = Open(game);
        Assert.True(vm.ShowMakeMainDisplay);

        vm.SelectedMonitor = vm.MonitorOptions.Single(o => o.Id == "MON1");
        Assert.False(vm.ShowMakeMainDisplay);
        vm.SaveCommand.Execute(null);

        Assert.False(game.MakeMainDisplay);
    });

    [Fact]
    public void EditingTheMonitorOrSound_MakesTheDialogDirty_AndChangingItBackCleansIt() => Sta(() =>
    {
        var vm = Open(new GameEntry { Name = "Hades", Category = "Action" });
        Assert.False(vm.HasUnsavedChanges);

        vm.SelectedAudio = vm.AudioOptions.Single(o => o.Id == "tv");
        Assert.True(vm.HasUnsavedChanges);
        vm.SelectedAudio = vm.AudioOptions[0];
        Assert.False(vm.HasUnsavedChanges);

        vm.DimOtherScreens = true;
        Assert.True(vm.HasUnsavedChanges);
    });

    [Fact]
    public void TheHint_SaysWhatWillHappen() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades", Category = "Action", DisplayMonitorId = "MON2", MakeMainDisplay = true };
        var vm = Open(game);

        Assert.Contains("made the main display", vm.DisplayHint);
        vm.MakeMainDisplay = false;
        Assert.Contains("left as it is", vm.DisplayHint);
    });
}
