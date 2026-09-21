using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Unticking the DLSS overlays switch puts both NGX values back to what they were. The write itself
/// is an elevated HKLM import and cannot run here, but what it would write can: these hold the part
/// that could quietly cost a user something, which is a Frame Generation bar they had switched on
/// themselves before they ever touched the switch.
/// </summary>
public class DlssOverlayRestoreTests
{
    private const string NgxCoreKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";

    private static (SystemTweaksService Tweaks, AppSettings Settings) New()
    {
        var settings = new AppSettings();
        return (new SystemTweaksService(() => settings, () => { }), settings);
    }

    /// <summary>A value the user already had is written back, not deleted and not zeroed.</summary>
    [Fact]
    public void AValueTheUserAlreadyHad_IsPutBack()
    {
        var (tweaks, settings) = New();
        settings.TweakPriorState["dlss_g_indicator"] = "2";

        var entry = tweaks.RestoreNgxValue("dlss_g_indicator", "DLSSG_IndicatorText");

        Assert.False(entry.Delete);
        Assert.Equal(NgxCoreKey, entry.SubKey);
        Assert.Equal("DLSSG_IndicatorText", entry.ValueName);
        Assert.Equal(2, entry.Value);
        Assert.Equal(RegistryValueKind.DWord, entry.Kind);
    }

    /// <summary>
    /// Absent before means removing the value, not writing 0. They are not the same to a driver that
    /// checks whether the value exists, and 0 is a state NVIDIA never left there itself.
    /// </summary>
    [Fact]
    public void AValueThatWasNotThere_IsRemovedRatherThanZeroed()
    {
        var (tweaks, settings) = New();
        settings.TweakPriorState["dlss_g_indicator"] = "absent";

        var entry = tweaks.RestoreNgxValue("dlss_g_indicator", "DLSSG_IndicatorText");

        Assert.True(entry.Delete);
        Assert.Null(entry.Value);
    }

    /// <summary>
    /// No record at all - the switch was never on, or the settings were reset - is treated as absent
    /// rather than as a reason to leave a value TrayTrigger may have written.
    /// </summary>
    [Fact]
    public void NoRecordedPrior_IsTreatedAsAbsent()
    {
        var (tweaks, _) = New();

        Assert.True(tweaks.RestoreNgxValue("dlss_g_indicator", "DLSSG_IndicatorText").Delete);
    }

    /// <summary>
    /// The prior is taken, not copied: a second untick after the switch has already been put back
    /// must not write a stale value over whatever is there now.
    /// </summary>
    [Fact]
    public void ThePriorIsConsumed_SoASecondRestoreDoesNotRepeatIt()
    {
        var (tweaks, settings) = New();
        settings.TweakPriorState["dlss_indicator"] = "1024";

        Assert.Equal(1024, tweaks.RestoreNgxValue("dlss_indicator", "ShowDlssIndicator").Value);
        Assert.True(tweaks.RestoreNgxValue("dlss_indicator", "ShowDlssIndicator").Delete);
        Assert.DoesNotContain("dlss_indicator", settings.TweakPriorState.Keys);
    }

    /// <summary>The two overlays keep separate records, so one cannot restore over the other.</summary>
    [Fact]
    public void TheTwoOverlays_DoNotShareAPrior()
    {
        var (tweaks, settings) = New();
        settings.TweakPriorState["dlss_indicator"] = "absent";
        settings.TweakPriorState["dlss_g_indicator"] = "2";

        var corner = tweaks.RestoreNgxValue("dlss_indicator", "ShowDlssIndicator");
        var frameGen = tweaks.RestoreNgxValue("dlss_g_indicator", "DLSSG_IndicatorText");

        Assert.True(corner.Delete);
        Assert.False(frameGen.Delete);
        Assert.Equal(2, frameGen.Value);
    }
}
