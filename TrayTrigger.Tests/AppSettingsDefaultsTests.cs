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

    /// <summary>...and a fresh install has no key for either, so nothing can call out regardless.</summary>
    [Fact]
    public void FreshSettings_HaveNoApiKeys()
    {
        var settings = new AppSettings();
        Assert.True(string.IsNullOrWhiteSpace(settings.RawgApiKey));
        Assert.True(string.IsNullOrWhiteSpace(settings.SteamGridDbApiKey));
    }
}
