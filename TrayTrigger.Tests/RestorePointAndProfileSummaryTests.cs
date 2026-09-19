using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// UX-07: the status line after Apply Performance Preset / Restore Previous Settings says what
/// happened to the restore point, the per-row button says Restore Previous, and Edit Game lists
/// what the chosen profile tier will change for the game.
/// </summary>
public class RestorePointAndProfileSummaryTests : IDisposable
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
    [InlineData(SystemViewModel.RestorePointOutcome.Created, "A restore point was created first.")]
    [InlineData(SystemViewModel.RestorePointOutcome.Skipped, "that option is off in Settings")]
    [InlineData(SystemViewModel.RestorePointOutcome.Failed, "Windows refused it")]
    public void StatusLine_ReportsTheRestorePointOutcome(SystemViewModel.RestorePointOutcome outcome, string expected)
    {
        string status = SystemViewModel.BulkActionStatus("Previous settings restored.", outcome);
        Assert.StartsWith("Previous settings restored. ", status);
        Assert.Contains(expected, status);
    }

    private static ProfileTweakToggleViewModel Toggle(string name, bool enabled, bool admin = false, bool optIn = false)
    {
        bool value = enabled;
        return new ProfileTweakToggleViewModel(name, "d", "w", "profiles/x", () => value, v => value = v, isOptIn: optIn, requiresAdmin: admin);
    }

    [Fact]
    public void Tiers_ListOnlyEnabledTweaks_AggressiveAddsItsOwn()
    {
        var optimized = new[] { Toggle("Power Plan", true), Toggle("Enable HDR", false, optIn: true) };
        var aggressive = new[] { Toggle("System Responsiveness", true, admin: true), Toggle("Defender Exclusion", false, admin: true, optIn: true) };

        Assert.Empty(SystemViewModel.EnabledTweaksFor(PerformanceProfileMode.Off, optimized, aggressive));
        Assert.Equal(new[] { "Power Plan" }, SystemViewModel.EnabledTweaksFor(PerformanceProfileMode.Optimized, optimized, aggressive).Select(t => t.Name));
        Assert.Equal(new[] { "Power Plan", "System Responsiveness" }, SystemViewModel.EnabledTweaksFor(PerformanceProfileMode.Aggressive, optimized, aggressive).Select(t => t.Name));
    }

    [Fact]
    public void EditGame_SummaryFollowsTheProfileBox() => Sta(() =>
    {
        var optimized = new[] { Toggle("Power Plan", true) };
        var aggressive = new[] { Toggle("System Responsiveness", true, admin: true) };
        var vm = new GameEditViewModel(new GameEntry { Name = "Hades" }, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))),
            profileTweaks: mode => SystemViewModel.EnabledTweaksFor(mode, optimized, aggressive));

        Assert.True(vm.HasProfileSummary);
        Assert.Equal(new[] { "Off: nothing is changed when this game runs." }, vm.ProfileSummaryLines);

        vm.PerformanceProfile = PerformanceProfileMode.Aggressive;
        Assert.Equal(new[] { "Power Plan", "System Responsiveness (asks for administrator permission)" }, vm.ProfileSummaryLines);
    });

    [Fact]
    public void EditGame_WithoutTheSystemPage_ShowsNoSummary() => Sta(() =>
    {
        var vm = new GameEditViewModel(new GameEntry { Name = "Hades" }, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))));
        Assert.False(vm.HasProfileSummary);
        Assert.Empty(vm.ProfileSummaryLines);
    });
}
