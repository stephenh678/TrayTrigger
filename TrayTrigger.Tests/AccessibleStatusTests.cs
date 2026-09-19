using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// The screen-reader name of a System & Performance tweak row: one string that says what the
/// name, the status badge and the OPT-IN / RESTART / ADMIN tags say on screen.
/// </summary>
public class AccessibleStatusTests
{
    [Fact]
    public void PlainRow_IsNameAndLowercaseStatus() =>
        Assert.Equal("Windows Game Mode, optimal",
            SystemTweakViewModel.ComposeAccessibleStatus("Windows Game Mode", "OPTIMAL", false, false, false));

    [Fact]
    public void Tags_AreSpokenInScreenOrder() =>
        Assert.Equal("Hardware-Accelerated GPU Scheduling (HAGS), standard, opt-in, needs a restart, asks for administrator permission",
            SystemTweakViewModel.ComposeAccessibleStatus("Hardware-Accelerated GPU Scheduling (HAGS)", "STANDARD", true, true, true));

    [Fact]
    public void Unavailable_ReadsAsWords() =>
        Assert.Equal("Auto HDR, not available",
            SystemTweakViewModel.ComposeAccessibleStatus("Auto HDR", "N/A", false, false, false));

    [Fact]
    public void ProfileToggle_FollowsItsState()
    {
        bool enabled = false;
        var vm = new ProfileTweakToggleViewModel("Enable HDR", "d", "w", "profiles/hdr", () => enabled, v => enabled = v, isOptIn: true, requiresAdmin: false);
        Assert.Equal("Enable HDR, disabled, opt-in", vm.AccessibleStatus);
        vm.ToggleCommand.Execute(null);
        Assert.Equal("Enable HDR, enabled, opt-in", vm.AccessibleStatus);
    }
}
