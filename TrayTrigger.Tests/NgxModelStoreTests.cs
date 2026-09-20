using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// The two pure pieces of the DLSS probe: NVIDIA's packed version number and the INI-ish
/// nvngx_config.txt. Everything else in the probe needs a driver, so it is exercised by
/// --test-dlss rather than here.
/// </summary>
public class NgxModelStoreTests
{
    [Theory]
    // Real folder names from the driver's store, with the versions nvngx_config.txt spells out
    // for the same entries - which is what pins the (major << 16) | (minor << 8) | patch layout.
    [InlineData(20318464u, "310.9.0")]
    [InlineData(65644u, "1.0.108")]
    [InlineData(131844u, "2.3.4")]
    [InlineData(131529u, "2.1.201")]
    [InlineData(131356u, "2.1.28")]
    [InlineData(131599u, "2.2.15")]
    public void DecodeVersion_MatchesTheVersionsNvidiaPrintsForTheSameFolders(uint encoded, string expected)
    {
        Assert.Equal(expected, NgxModelStore.DecodeVersion(encoded));
    }


    [Fact]
    public void ParseConfig_KeepsSectionForEachEntry()
    {
        string text = "[dlss]\napp_E658700 = 310.9.0\napp_B9DB490 = 2.3.4\n\n[dlssg]\napp_E658700 = 310.9.0\n";

        var entries = NgxModelStore.ParseConfig(text);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { "dlss", "dlss", "dlssg" }, entries.Select(e => e.Section));
        Assert.Equal("app_E658700", entries[0].AppId);
        Assert.Equal("310.9.0", entries[0].Version);
        Assert.Equal("2.3.4", entries[1].Version);
    }

    [Fact]
    public void ParseConfig_HandlesCrLfBlankLinesAndComments()
    {
        // The real file is CRLF; splitting on '\n' alone would leave a trailing '\r' on the value
        // and make every version string compare unequal.
        string text = "[dlss]\r\n\r\n; a comment\r\napp_E658700 = 310.9.0\r\n";

        var entries = NgxModelStore.ParseConfig(text);

        Assert.Single(entries);
        Assert.Equal("310.9.0", entries[0].Version);
        Assert.Equal("dlss", entries[0].Section);
    }

    [Fact]
    public void ParseConfig_IgnoresMalformedLines()
    {
        string text = "[dlss]\nno equals sign here\n= missing key\napp_A = 1.0.0\napp_B =\n";

        var entries = NgxModelStore.ParseConfig(text);

        Assert.Single(entries);
        Assert.Equal("app_A", entries[0].AppId);
    }


    [Theory]
    [InlineData("nvngx_dlss.dll", "Super Resolution")]
    [InlineData("nvngx_dlssd.dll", "Ray Reconstruction")]
    [InlineData("nvngx_dlssg.dll", "Frame Generation")]
    [InlineData("NVNGX_DLSSG.DLL", "Frame Generation")]
    public void FeatureForShippedFile_DistinguishesTheThreeFeatures(string fileName, string expected)
    {
        Assert.Equal(expected, DlssProbeService.FeatureForShippedFile(fileName));
    }

    [Fact]
    public void Settings_CoverSixIds_WithNoDuplicates()
    {
        // Six, not the seven the plan first listed: 0x00634291 turned out not to be a DRS setting
        // on any driver. See DlssProbeService.Settings.
        var ids = DlssProbeService.Settings.Select(s => s.Id).ToList();

        Assert.Equal(6, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(0x10E41DF1u, ids);   // frame generation, whose sentinel differs
        // All six live in NVIDIA's DLSS block; anything outside it would be a typo.
        Assert.All(ids, id => Assert.InRange(id, 0x10E41DF0u, 0x10E41E0Fu));
    }
}
