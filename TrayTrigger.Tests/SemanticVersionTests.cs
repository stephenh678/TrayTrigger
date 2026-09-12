using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("2.0", "2.0.0", false)]
    [InlineData("V3", "3.0.0", false)]
    [InlineData("1.2.3.0", "1.2.3", false)] // System.Version 4-part form tolerated
    [InlineData("v1.3.0-beta.1", "1.3.0-beta.1", true)]
    [InlineData("1.3.0-rc.2+abc123", "1.3.0-rc.2", true)] // build metadata dropped
    [InlineData("1.2.1+sha.deadbeef", "1.2.1", false)] // SDK appends +commit to InformationalVersion
    public void TryParse_ParsesValidVersions(string input, string expected, bool isPrerelease)
    {
        var v = SemanticVersion.TryParse(input);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.ToString());
        Assert.Equal(isPrerelease, v.IsPrerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notaversion")]
    [InlineData("1.x.3")]
    [InlineData("1.2.3-")] // empty prerelease
    [InlineData("1.2.3.4.5")]
    [InlineData(null)]
    public void TryParse_ReturnsNullForInvalid(string? input)
    {
        Assert.Null(SemanticVersion.TryParse(input));
    }

    [Theory]
    // Core numeric ordering
    [InlineData("1.2.1", "1.3.0", -1)]
    [InlineData("1.3.0", "1.2.9", 1)]
    [InlineData("1.3.0", "1.3.0", 0)]
    // Pre-release < release of the same core version
    [InlineData("1.3.0-beta.1", "1.3.0", -1)]
    [InlineData("1.3.0", "1.3.0-rc.9", 1)]
    // Pre-release of a newer core version still beats an older release
    [InlineData("1.3.0-beta.1", "1.2.1", 1)]
    // Pre-release identifier ordering (SemVer 2.0 §11)
    [InlineData("1.3.0-beta.1", "1.3.0-beta.2", -1)]
    [InlineData("1.3.0-beta.2", "1.3.0-beta.10", -1)] // numeric, not lexical
    [InlineData("1.3.0-alpha", "1.3.0-beta", -1)]
    [InlineData("1.3.0-beta", "1.3.0-beta.1", -1)] // longer wins when prefix equal
    [InlineData("1.3.0-beta.1", "1.3.0-rc.1", -1)]
    [InlineData("1.3.0-1", "1.3.0-alpha", -1)] // numeric identifiers < alphanumeric
    [InlineData("1.3.0-rc.1", "1.3.0-rc.1", 0)]
    public void CompareTo_OrdersPerSemVer(string a, string b, int expectedSign)
    {
        var va = SemanticVersion.TryParse(a)!;
        var vb = SemanticVersion.TryParse(b)!;
        Assert.Equal(expectedSign, Math.Sign(va.CompareTo(vb)));
        Assert.Equal(-expectedSign, Math.Sign(vb.CompareTo(va)));
        Assert.Equal(expectedSign > 0, va > vb);
        Assert.Equal(expectedSign < 0, va < vb);
        Assert.Equal(expectedSign == 0, va.Equals(vb));
    }

    [Fact]
    public void FromVersion_DropsRevisionAndNegatives()
    {
        Assert.Equal("1.2.3", SemanticVersion.FromVersion(new Version(1, 2, 3, 7)).ToString());
        Assert.Equal("1.0.0", SemanticVersion.FromVersion(new Version(1, 0)).ToString());
    }

    [Fact]
    public void ToDisplayString_PrefixesV()
    {
        Assert.Equal("v1.3.0-beta.1", SemanticVersion.TryParse("1.3.0-beta.1")!.ToDisplayString());
    }
}

public class UpdateServiceSelectLatestTests
{
    private static GitHubReleaseInfo R(string tag, bool prerelease = false, bool draft = false) =>
        new() { TagName = tag, Prerelease = prerelease, Draft = draft };

    [Fact]
    public void StableOnly_SkipsPrereleasesAndDrafts()
    {
        var releases = new[]
        {
            R("v1.3.0-beta.2", prerelease: true),
            R("v1.4.0", draft: true),
            R("v1.2.1"),
            R("v1.3.0-beta.1", prerelease: true),
            R("v1.2.0"),
        };

        var latest = UpdateService.SelectLatest(releases, includePrerelease: false);
        Assert.Equal("v1.2.1", latest?.TagName);
    }

    [Fact]
    public void IncludePrerelease_PicksHighestSemVerRegardlessOfListOrder()
    {
        var releases = new[]
        {
            R("v1.2.1"),
            R("v1.3.0-beta.1", prerelease: true),
            R("v1.3.0-beta.10", prerelease: true),
            R("v1.3.0-beta.2", prerelease: true),
            R("v1.4.0", draft: true),
        };

        var latest = UpdateService.SelectLatest(releases, includePrerelease: true);
        Assert.Equal("v1.3.0-beta.10", latest?.TagName);
    }

    [Fact]
    public void IncludePrerelease_StableOutranksItsOwnBetas()
    {
        var releases = new[]
        {
            R("v1.3.0-rc.1", prerelease: true),
            R("v1.3.0"),
            R("v1.3.0-beta.3", prerelease: true),
        };

        var latest = UpdateService.SelectLatest(releases, includePrerelease: true);
        Assert.Equal("v1.3.0", latest?.TagName);
    }

    [Fact]
    public void StableOnly_TreatsHyphenTagAsPrereleaseEvenIfFlagMissing()
    {
        // Guards against a release accidentally published without the prerelease flag.
        var releases = new[] { R("v1.3.0-beta.1", prerelease: false), R("v1.2.1") };
        var latest = UpdateService.SelectLatest(releases, includePrerelease: false);
        Assert.Equal("v1.2.1", latest?.TagName);
    }

    [Fact]
    public void ReturnsNull_WhenNothingQualifies()
    {
        var releases = new[] { R("nightly"), R("v9.9.9", draft: true) };
        Assert.Null(UpdateService.SelectLatest(releases, includePrerelease: true));
    }

    /// <summary>
    /// A tester who installs a beta without ticking "Receive pre-release updates" would otherwise
    /// be pinned to it: GitHub keeps pre-releases out of /releases/latest, so the only thing the
    /// build is ever offered is the older stable, which loses the comparison and reports "up to
    /// date" indefinitely.
    /// </summary>
    [Theory]
    [InlineData("1.4.1-beta.1", false, true)]   // on a beta with the setting off - track betas anyway
    [InlineData("1.4.1-beta.1", true, true)]
    [InlineData("1.4.0", false, false)]         // on a stable build the setting alone decides
    [InlineData("1.4.0", true, true)]
    public void ShouldIncludePrerelease_TracksBetasWhileRunningOne(string current, bool setting, bool expected)
    {
        var version = SemanticVersion.TryParse(current)!;
        Assert.Equal(expected, UpdateService.ShouldIncludePrerelease(setting, version));
    }
}
