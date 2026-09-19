using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// UX-11: the undo window is 10 seconds and pauses while the toast is held, and removing a scan
/// location or an ignored game / folder in Settings can be undone the same way.
/// </summary>
public class UndoWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void Window_IsTenSeconds()
    {
        var countdown = new UndoCountdown();
        countdown.Start(T0);
        Assert.False(countdown.IsExpired(T0.AddSeconds(9.9)));
        Assert.True(countdown.IsExpired(T0.AddSeconds(10)));
    }

    [Fact]
    public void Holding_PausesTheWindow_AndLettingGoResumesWithWhatWasLeft()
    {
        var countdown = new UndoCountdown();
        countdown.Start(T0);
        countdown.Pause(T0.AddSeconds(4));
        Assert.True(countdown.IsPaused);
        Assert.False(countdown.IsExpired(T0.AddMinutes(5)));
        Assert.Equal(TimeSpan.FromSeconds(6), countdown.Remaining(T0.AddMinutes(5)));

        countdown.Resume(T0.AddMinutes(5));
        Assert.False(countdown.IsExpired(T0.AddMinutes(5).AddSeconds(5.9)));
        Assert.True(countdown.IsExpired(T0.AddMinutes(5).AddSeconds(6)));
    }

    [Fact]
    public void Stopped_NeverExpires()
    {
        var countdown = new UndoCountdown();
        countdown.Start(T0);
        countdown.Stop();
        Assert.False(countdown.IsExpired(T0.AddHours(1)));
    }

    private SettingsViewModel CreateSettings(AppSettings settings)
    {
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        return new SettingsViewModel(settings, storage, new StartupManager(), new TrayPromotionService(), new SteamScannerService());
    }

    [Fact]
    public void RemovingAScanLocation_CanBeUndoneInPlace() => Sta(() =>
    {
        var settings = new AppSettings();
        settings.ScanLocations.Add(new ScanLocation { Path = @"D:\Games", Source = ScanLocationSource.Manual, IsEnabled = true });
        settings.ScanLocations.Add(new ScanLocation { Path = @"E:\More Games", Source = ScanLocationSource.Manual, IsEnabled = false });
        var vm = CreateSettings(settings);
        var row = vm.ScanLocations.Single(r => r.Path == @"D:\Games");

        vm.RemoveScanLocationCommand.Execute(row);
        Assert.DoesNotContain(settings.ScanLocations, l => l.Path == @"D:\Games");
        Assert.True(vm.IsUndoToastVisible);

        vm.UndoRemovalCommand.Execute(null);
        Assert.Equal(@"D:\Games", settings.ScanLocations[0].Path);
        Assert.Equal(2, settings.ScanLocations.Count);
        Assert.False(vm.IsUndoToastVisible);
    });

    [Fact]
    public void RemovingAnIgnoredEntry_CanBeUndone_AndASecondRemovalReplacesTheFirst() => Sta(() =>
    {
        var settings = new AppSettings();
        settings.IgnoredGamePaths.Add(new IgnoredGamePath { FolderPath = @"D:\Games\Mods", Name = "Mods" });
        settings.IgnoredGamePaths.Add(new IgnoredGamePath { Name = "Launcher Helper" });
        var vm = CreateSettings(settings);

        vm.RemoveIgnoredGamePathCommand.Execute(vm.IgnoredGamePaths.Single(r => r.Name == "Mods"));
        vm.RemoveIgnoredGamePathCommand.Execute(vm.IgnoredGamePaths.Single(r => r.Name == "Launcher Helper"));
        Assert.Empty(settings.IgnoredGamePaths);

        // Only the most recent removal is offered; the first stays removed.
        vm.UndoRemovalCommand.Execute(null);
        Assert.Equal(new[] { "Launcher Helper" }, settings.IgnoredGamePaths.Select(p => p.Name));

        // Undo with nothing pending does nothing.
        vm.UndoRemovalCommand.Execute(null);
        Assert.Single(settings.IgnoredGamePaths);
    });

    [Fact]
    public void Dismiss_EndsTheWindow_WithoutPuttingItBack() => Sta(() =>
    {
        var settings = new AppSettings();
        settings.IgnoredGamePaths.Add(new IgnoredGamePath { Name = "Tool" });
        var vm = CreateSettings(settings);

        vm.RemoveIgnoredGamePathCommand.Execute(vm.IgnoredGamePaths.Single());
        vm.DismissUndoToastCommand.Execute(null);
        vm.UndoRemovalCommand.Execute(null);

        Assert.Empty(settings.IgnoredGamePaths);
        Assert.False(vm.IsUndoToastVisible);
    });
}
