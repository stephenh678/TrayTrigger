using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// When Stutter Check runs by itself, and what the preset's confirmation says about the restore
/// point: both are small pure decisions the page leans on.
/// </summary>
public class StutterCheckPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 22, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    [Fact]
    public void FirstRun_IsDue_UnlessAGameIsRunning()
    {
        Assert.True(SystemViewModel.ShouldRunStutterCheck(null, Now, gameRunning: false, MaxAge));
        Assert.False(SystemViewModel.ShouldRunStutterCheck(null, Now, gameRunning: true, MaxAge));
    }

    [Fact]
    public void AFreshResult_IsKept_AStaleOne_IsRedone()
    {
        Assert.False(SystemViewModel.ShouldRunStutterCheck(Now.AddMinutes(-3), Now, gameRunning: false, MaxAge));
        Assert.True(SystemViewModel.ShouldRunStutterCheck(Now.AddMinutes(-10), Now, gameRunning: false, MaxAge));
        Assert.True(SystemViewModel.ShouldRunStutterCheck(Now.AddHours(-2), Now, gameRunning: false, MaxAge));
        Assert.False(SystemViewModel.ShouldRunStutterCheck(Now.AddHours(-2), Now, gameRunning: true, MaxAge));
    }

    [Fact]
    public void TheConfirmation_SaysWhetherARestorePointComesFirst()
    {
        string on = SystemViewModel.BulkActionDetail(["Windows Game Mode", "HAGS"], restorePointOn: true);
        Assert.StartsWith("Affected: Windows Game Mode, HAGS.", on);
        Assert.EndsWith("A System Restore point will be created first.", on);

        string off = SystemViewModel.BulkActionDetail(["Windows Game Mode"], restorePointOn: false);
        Assert.EndsWith("No System Restore point will be created first; turn that on in Settings > Performance Tweaks.", off);
    }
}
