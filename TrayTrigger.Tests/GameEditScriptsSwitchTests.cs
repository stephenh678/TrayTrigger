using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>The Enable scripts switch in Edit Game's Scripts notice: the same setting as Settings, turned on without leaving the dialog.</summary>
public sealed class GameEditScriptsSwitchTests : IDisposable
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

    private GameEditViewModel Open(GameEntry game, bool scriptsEnabled, Action<bool>? set = null) =>
        new(game, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))),
            scriptsEnabled: scriptsEnabled, setScriptsEnabled: set);

    [Fact]
    public void WithScriptsOff_TheNoticeShows_AndTickingTheSwitchSetsTheSettingAndShowsTheCard() => Sta(() =>
    {
        bool? setting = null;
        var vm = Open(new GameEntry { Name = "Hades" }, scriptsEnabled: false, set: on => setting = on);
        var changed = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.False(vm.ShowScriptsCard);
        Assert.True(vm.ShowScriptsStub);

        vm.ScriptsEnabled = true;

        Assert.True(setting);
        Assert.True(vm.ShowScriptsCard);
        Assert.False(vm.ShowScriptsStub);
        Assert.Contains(nameof(GameEditViewModel.ShowScriptsCardOnTab), changed);
        Assert.Contains(nameof(GameEditViewModel.ShowScriptsStubOnTab), changed);
    });

    [Fact]
    public void AGameWithScripts_KeepsItsCardWhileOff_AndTheSwitchClearsTheWarning() => Sta(() =>
    {
        var vm = Open(new GameEntry { Name = "Hades", PreLaunchScriptPath = @"C:\Scripts\pre.ps1" }, scriptsEnabled: false);

        Assert.True(vm.ShowScriptsCard);
        Assert.True(vm.ShowScriptsDisabledNotice);

        vm.ScriptsEnabled = true;

        Assert.True(vm.ShowScriptsCard);
        Assert.False(vm.ShowScriptsDisabledNotice);
    });

    [Fact]
    public void TheSwitch_IsHarmlessWithNoSettingBehindIt_AndDoesNotMakeTheDialogDirty() => Sta(() =>
    {
        var vm = Open(new GameEntry { Name = "Hades" }, scriptsEnabled: false);

        vm.ScriptsEnabled = true;

        Assert.True(vm.ScriptsEnabled);
        Assert.False(vm.HasUnsavedChanges);
    });
}
