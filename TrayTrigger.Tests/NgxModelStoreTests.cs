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
