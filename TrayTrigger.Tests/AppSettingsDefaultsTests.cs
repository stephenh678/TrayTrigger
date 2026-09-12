using TrayTrigger.Models;

namespace TrayTrigger.Tests;

public class AppSettingsDefaultsTests
{
    /// <summary>
    /// RAWG ships on. It is inert without a key - every consumer requires both the switch and
    /// <see cref="AppSettings.RawgApiKey"/> - so a user who never enters one pays nothing, and a
    /// user who pastes one does not then have to hunt for a second switch to make it do anything.
    /// </summary>
    [Fact]
    public void UseRawgMetadata_DefaultsToEnabled()
    {
        Assert.True(new AppSettings().UseRawgMetadata);
    }

    /// <summary>
    /// ...and it stays inert on a fresh install, because there is no key yet. This is what keeps
    /// the default from being a behaviour change for anyone who does not opt in.
    /// </summary>
    [Fact]
    public void FreshSettings_HaveNoRawgKey_SoTheSourceStaysInert()
    {
        var settings = new AppSettings();
        Assert.True(string.IsNullOrWhiteSpace(settings.RawgApiKey));
    }
}
