using TrayTrigger.Models;

namespace TrayTrigger.Tests;

public class GitHubReleaseInfoTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("2.0", "2.0.0")] // two-part semver normalized to three parts
    [InlineData("v1.5.0-beta", "1.5.0")] // prerelease suffix stripped
    [InlineData("V3", "3.0.0")] // single-part, capital V prefix
    public void ParsedVersion_ParsesTagName(string tagName, string expected)
    {
        var release = new GitHubReleaseInfo { TagName = tagName };
        Assert.Equal(Version.Parse(expected), release.ParsedVersion);
    }

    [Theory]
    [InlineData("notaversion")]
    [InlineData("")]
    public void ParsedVersion_ReturnsNullForUnparsableTagName(string tagName)
    {
        var release = new GitHubReleaseInfo { TagName = tagName };
        Assert.Null(release.ParsedVersion);
    }

    [Fact]
    public void SemVer_KeepsPrereleaseSuffix()
    {
        var release = new GitHubReleaseInfo { TagName = "v1.5.0-beta.2" };
        Assert.Equal("1.5.0-beta.2", release.SemVer?.ToString());
        Assert.True(release.SemVer!.IsPrerelease);
    }
}
