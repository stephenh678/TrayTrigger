using TrayTrigger.Models;

namespace TrayTrigger.Tests;

/// <summary>
/// Each tier's defaults live on the config class itself, so a config constructed anywhere - not
/// only through AppSettings - describes the documented tier.
/// </summary>
public class TweakConfigDefaultsTests
{
    [Fact]
    public void OptimizedDefaults_MatchTheDocumentedTier()
    {
        var tweaks = new OptimizedProfileTweakConfig();
        Assert.True(tweaks.PowerPlanEnabled);
        Assert.True(tweaks.GpuPreferenceEnabled);
        Assert.False(tweaks.HdrEnabled);
        Assert.False(tweaks.DoNotDisturbEnabled);
        Assert.False(tweaks.UnmuteAudioEnabled);
    }

    [Fact]
    public void AggressiveDefaults_MatchTheDocumentedTier()
    {
        var tweaks = new AggressiveProfileTweakConfig();
        Assert.True(tweaks.SystemResponsivenessEnabled);
        Assert.True(tweaks.MmcssGamesPriorityEnabled);
        Assert.True(tweaks.AboveNormalPriorityEnabled);
        Assert.True(tweaks.TimerResolutionEnabled);
        Assert.False(tweaks.DefenderExclusionEnabled);
    }

    [Fact]
    public void AppSettings_UsesTheClassDefaults()
    {
        var settings = new AppSettings();
        Assert.True(settings.OptimizedProfileTweaks.PowerPlanEnabled);
        Assert.True(settings.AggressiveProfileTweaks.TimerResolutionEnabled);
        Assert.False(settings.AggressiveProfileTweaks.DefenderExclusionEnabled);
    }
}
