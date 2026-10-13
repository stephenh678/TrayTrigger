using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

public class WhatsNewServiceTests
{
    private static SemanticVersion V(string s) => SemanticVersion.TryParse(s)!;

    [Theory]
    [InlineData("1.6.2", "1.6.2")]
    [InlineData("1.6.2-beta.4", "1.6.2")]
    [InlineData("v1.7.0-rc.1", "1.7.0")]
    public void StableKey_drops_prerelease(string version, string key) =>
        Assert.Equal(key, WhatsNewService.StableKey(V(version)));

    [Fact]
    public void Due_after_a_forward_update_to_a_new_stable_version() =>
        Assert.True(WhatsNewService.IsDue("1.6.1", V("1.6.2-beta.4"), ""));

    [Fact]
    public void Not_due_on_a_first_install() =>
        Assert.False(WhatsNewService.IsDue("", V("1.6.2"), ""));

    [Fact]
    public void Not_due_when_going_back() =>
        Assert.False(WhatsNewService.IsDue("1.6.2", V("1.6.2-beta.4"), ""));

    [Fact]
    public void Not_due_again_once_the_stable_version_was_shown()
    {
        Assert.False(WhatsNewService.IsDue("1.6.2-beta.4", V("1.6.2-beta.5"), "1.6.2"));
        Assert.False(WhatsNewService.IsDue("1.6.2-beta.5", V("1.6.2"), "1.6.2"));
    }

    [Fact]
    public void Due_for_the_next_stable_version()
    {
        Assert.True(WhatsNewService.IsDue("1.6.2", V("1.6.3"), "1.6.2"));
    }

    private const string Log = "# Changelog\n\nIntro.\n\n## 1.6.2\n\n- **A** - one\n- **B** - two\n\n## 1.6.1\n\n- **C** - three\n";

    [Fact]
    public void ExtractSection_returns_only_that_versions_bullets()
    {
        string? s = WhatsNewService.ExtractSection(Log, "1.6.2");
        Assert.Equal("- **A** - one\n- **B** - two".Replace("\n", System.Environment.NewLine), s);
    }

    [Fact]
    public void ExtractSection_is_null_when_missing() =>
        Assert.Null(WhatsNewService.ExtractSection(Log, "9.9.9"));

    [Fact]
    public void The_embedded_notes_have_the_running_versions_section()
    {
        // A release that forgets its WhatsNew.md entry would show no popup at all.
        string key = WhatsNewService.StableKey(UpdateService.CurrentSemVer);
        Assert.NotNull(WhatsNewService.LoadSection(key));
    }
}
