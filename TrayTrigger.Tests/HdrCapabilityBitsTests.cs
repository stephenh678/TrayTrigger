using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// Locks down which bit of DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2.value means "this display has
/// an HDR mode". Reading bit 0 (advancedColorSupported) instead of bit 4
/// (highDynamicRangeSupported) made every wide-colour-gamut monitor look HDR-capable, so Enable
/// HDR fired SET_HDR_STATE at panels with no HDR mode and Windows returned 50
/// (ERROR_NOT_SUPPORTED) on every launch and every restore.
///
/// Bitfield, in declaration order: 0 advancedColorSupported, 1 advancedColorActive, 2 reserved1,
/// 3 advancedColorLimitedByPolicy, 4 highDynamicRangeSupported, 5 highDynamicRangeUserEnabled,
/// 6 wideColorSupported, 7 wideColorUserEnabled.
/// </summary>
public class HdrCapabilityBitsTests
{
    [Fact]
    public void WideColourOnlyDisplayIsNotReportedAsHdrCapable()
    {
        // advancedColorSupported + wideColorSupported + wideColorUserEnabled, no HDR bit.
        // This is the shape a WCG-only panel reports, and the case that regressed.
        const uint wcgOnly = 0b1100_0001;

        Assert.False(HdrControlService.HdrSupportedFromAdvancedColorInfo2(wcgOnly));
    }

    [Fact]
    public void HdrCapableDisplayIsReportedAsHdrCapable()
    {
        // advancedColorSupported + highDynamicRangeSupported.
        const uint hdrCapable = 0b0001_0001;

        Assert.True(HdrControlService.HdrSupportedFromAdvancedColorInfo2(hdrCapable));
    }

    [Fact]
    public void HdrBitAloneIsEnough()
    {
        Assert.True(HdrControlService.HdrSupportedFromAdvancedColorInfo2(1u << 4));
    }

    [Fact]
    public void NoBitsMeansNoHdr()
    {
        Assert.False(HdrControlService.HdrSupportedFromAdvancedColorInfo2(0));
    }

    [Theory]
    [InlineData(0)] // advancedColorSupported
    [InlineData(1)] // advancedColorActive
    [InlineData(2)] // reserved1
    [InlineData(3)] // advancedColorLimitedByPolicy
    [InlineData(5)] // highDynamicRangeUserEnabled
    [InlineData(6)] // wideColorSupported
    [InlineData(7)] // wideColorUserEnabled
    public void NoOtherCapabilityBitImpliesHdrSupport(int bitIndex)
    {
        Assert.False(HdrControlService.HdrSupportedFromAdvancedColorInfo2(1u << bitIndex));
    }
}
