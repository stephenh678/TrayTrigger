using System.IO;
using System;
using System.Collections.Generic;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using Xunit;

namespace TrayTrigger.Tests;

[Collection(ActivityCurrentCollection.Name)]
public class AuditFixesTests
{
    [Theory]
    [InlineData("GpuPreference=2;", true)]
    [InlineData("GpuPreference=2;AutoHDREnable=2097;", true)]
    [InlineData("AutoHDREnable=1;", true)]
    [InlineData("", false)]
    [InlineData("GpuPreference=2", false)]
    [InlineData("GpuPreference=2;rm -rf;", false)]
    [InlineData("=2;", false)]
    public void GpuPreferenceValue_AcceptsWindowsTokenStrings(string value, bool valid)
    {
        Assert.Equal(valid, ProfileSnapshotValidator.IsValidGpuPreferenceValue(value));
    }

    [Fact]
    public void YearInCandidateTitle_DoesNotCountAsASequelNumber()
    {
        Assert.True(SteamSearchService.CalculateSimilarity("DOOM", "DOOM (2016)") >= 0.6);
    }

    [Theory]
    [InlineData("anim=1;shadow=1;fx=0", "0")]
    [InlineData("anim=0;shadow=1;fx=2", "2")]
    [InlineData("anim=1;shadow=1;fx=none", "none")]
    [InlineData("anim=1;shadow=1", null)]
    [InlineData("anim=1;shadow=1;fx=9", null)]
    [InlineData("anim=1;shadow=1;fx=abc", null)]
    public void VisualFxPrior_KeepsTheMarkerTheUserHad(string prior, string? expected)
    {
        Assert.Equal(expected, SystemTweaksService.PriorVisualFxMarker(prior));
    }

    private static GpuCardViewModel Card(string installed, SavedDriverFind? saved, List<SavedDriverFind?> writes) =>
        new(new TrayTrigger.Models.GpuHardwareInfo { Vendor = TrayTrigger.Models.GpuVendor.Nvidia, ModelName = "NVIDIA GeForce RTX 5080", DriverVersion = installed },
            new NvidiaDriverService(), () => new DateTime(2026, 10, 25), () => saved, writes.Add);

    private static SavedDriverFind Found(string version, string gpu = "NVIDIA GeForce RTX 5080") =>
        new(gpu, version, "Wed Oct 21, 2026", "https://www.nvidia.com/x", new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void SavedDriverFind_ShowsTheNoticeWithoutAskingNvidia()
    {
        var writes = new List<SavedDriverFind?>();
        var card = Card("617.42", Found("621.01"), writes);
        Assert.Equal(DriverCheckState.NewerAvailable, card.CheckState);
        Assert.True(card.ShowGetDriver);
        Assert.Contains("621.01", card.CheckMessage);
        Assert.Empty(writes);
    }

    [Theory]
    [InlineData("621.01")]
    [InlineData("622.10")]
    public void SavedDriverFind_IsForgottenOnceThatDriverIsInstalled(string installed)
    {
        var writes = new List<SavedDriverFind?>();
        var card = Card(installed, Found("621.01"), writes);
        Assert.Equal(DriverCheckState.None, card.CheckState);
        Assert.False(card.ShowGetDriver);
        Assert.Single(writes);
        Assert.Null(writes[0]);
    }

    [Fact]
    public void SavedDriverFind_ForAnotherGpuIsIgnoredAndForgotten()
    {
        var writes = new List<SavedDriverFind?>();
        var card = Card("617.42", Found("621.01", "NVIDIA GeForce RTX 4070"), writes);
        Assert.Equal(DriverCheckState.None, card.CheckState);
        Assert.Single(writes);
    }

    [Fact]
    public void NoSavedDriverFind_ShowsNothing()
    {
        var card = Card("617.42", null, new List<SavedDriverFind?>());
        Assert.Equal(DriverCheckState.None, card.CheckState);
    }

    private static T WithActivity<T>(Func<ActivityService, T> run)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tt-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var activity = new ActivityService(dir, readOnly: true);
        ActivityService.Current = activity;
        try { return run(activity); }
        finally { ActivityService.Current = null; try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void KnownNewerDriver_IsNeedsAttentionWithAGetDriverButton_AndNamesTheNvidiaApp()
    {
        var item = WithActivity(a =>
        {
            Card("617.42", Found("621.01"), new List<SavedDriverFind?>());
            return Assert.Single(a.States);
        });
        Assert.Equal(GpuCardViewModel.DriverAttentionKey, item.Key);
        Assert.Contains("621.01", item.Text);
        Assert.Equal("Get Driver", item.ActionLabel);
        Assert.Contains("NVIDIA app", item.Detail);
        Assert.False(item.IsCritical);
    }

    [Fact]
    public void InstalledDriver_ClearsTheNeedsAttentionItem()
    {
        int left = WithActivity(a =>
        {
            Card("617.42", Found("621.01"), new List<SavedDriverFind?>());
            Card("621.01", Found("621.01"), new List<SavedDriverFind?>());
            return a.States.Count;
        });
        Assert.Equal(0, left);
    }

    [Fact]
    public void IsStillRunning_SeesALiveProcessByIdWithoutAHandle_AndNotAnExitedOne()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        string? myPath = ProcessPathResolver.GetProcessPath(me.Id);
        Assert.True(ProcessLauncherService.IsStillRunning(me.Id, myPath));
        Assert.True(ProcessLauncherService.IsStillRunning(me.Id, null));
        // The same id with a different image is not the game.
        Assert.False(ProcessLauncherService.IsStillRunning(me.Id, @"C:\Games\Other\other.exe"));

        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true };
        using var gone = System.Diagnostics.Process.Start(psi)!;
        gone.WaitForExit();
        Assert.False(ProcessLauncherService.IsStillRunning(gone.Id, null));
    }

    [Theory]
    [InlineData(@"C:\Games\X\!start.bat", true)]
    [InlineData(@"C:\Games\Xun.CMD", true)]
    [InlineData(@"C:\Games\X\game.exe", false)]
    [InlineData(@"C:\Games\X\game.lnk", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ABatOrCmd_IsAScriptHost_NotTheGame(string? path, bool expected)
    {
        Assert.Equal(expected, ProcessLauncherService.IsScriptHost(path));
    }
}
