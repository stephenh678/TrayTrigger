using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// UX-14e: the status bar's "Hotkey: ... to show or hide this window" retires after five
/// sessions, and the tray icon's idle tooltip names the hotkey instead.
/// </summary>
public class HotkeyHintTests : IDisposable
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

    [Theory]
    [InlineData(1, "Ctrl+Alt+G", true)]
    [InlineData(5, "Ctrl+Alt+G", true)]
    [InlineData(6, "Ctrl+Alt+G", false)]
    [InlineData(1, "", false)]
    public void StatusBarHint_ShowsForTheFirstFiveSessions(int sessions, string hotkey, bool expected) => Sta(() =>
    {
        var settings = new AppSettings { SessionsStarted = sessions, GlobalManageHotkey = hotkey };
        var storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var vm = new SettingsViewModel(settings, storage, new StartupManager(), new TrayPromotionService(), new SteamScannerService());
        Assert.Equal(expected, vm.ShowHotkeyHint);
    });

    [Fact]
    public void IdleTrayTooltip_NamesTheWindowHotkey()
    {
        Assert.Equal("TrayTrigger - Game Launcher\nCtrl+Alt+G shows or hides the window",
            App.BuildTrayToolTipText([], DateTime.Now, "Ctrl+Alt+G"));
        Assert.Equal("TrayTrigger - Game Launcher", App.BuildTrayToolTipText([], DateTime.Now, ""));
    }
}
