using TrayTrigger.Models;

namespace TrayTrigger.Tests;

public class AppSettingsDefaultsTests
{
    /// <summary>
    /// The two third-party metadata sources both need the user's own API key, and neither is on
    /// until the user asks for it. They were briefly inconsistent - RAWG on, SteamGridDB off -
    /// which read as an accident rather than a decision, since being on without a key does nothing
    /// either way.
    /// </summary>
    [Fact]
    public void KeyGatedMetadataSources_DefaultToDisabled()
    {
        var settings = new AppSettings();
        Assert.False(settings.UseRawgMetadata);
        Assert.False(settings.UseSteamGridDbArt);
    }

    /// <summary>
    /// Every tweak that changes something the user can see or hear outside the game is opt-in, so
    /// picking a profile tier never has a surprise attached. Picking Optimized must not start
    /// turning speakers on, forcing HDR, or silencing notifications.
    /// </summary>
    [Fact]
    public void TweaksWithAVisibleSideEffect_AreOptIn()
    {
        var tweaks = new AppSettings().OptimizedProfileTweaks;
        Assert.False(tweaks.UnmuteAudioEnabled);
        Assert.False(tweaks.HdrEnabled);
        Assert.False(tweaks.DoNotDisturbEnabled);

        // ...while the two that are invisible outside the session stay on, which is what Optimized
        // is for.
        Assert.True(tweaks.PowerPlanEnabled);
        Assert.True(tweaks.GpuPreferenceEnabled);
    }

    /// <summary>A fresh install starts with nothing filtered out of the library.</summary>
    [Fact]
    public void FreshSettings_HaveNoLibraryFilters()
    {
        Assert.Empty(new AppSettings().LibraryFilterKeys);
    }

    /// <summary>...and a fresh install has no key for either, so nothing can call out regardless.</summary>
    [Fact]
    public void FreshSettings_HaveNoApiKeys()
    {
        var settings = new AppSettings();
        Assert.True(string.IsNullOrWhiteSpace(settings.RawgApiKey));
        Assert.True(string.IsNullOrWhiteSpace(settings.SteamGridDbApiKey));
    }
}
