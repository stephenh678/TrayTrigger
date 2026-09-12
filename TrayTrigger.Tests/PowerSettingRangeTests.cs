using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// The Ultimate plan's powercfg values are not universal. Dylan's 1.4.0 log rejected
/// CPMINCORES=100 on every pass with "not within the range of the target power setting", so the
/// tweak now asks powercfg what the machine will take. These are the real shapes of
/// "powercfg /query" output, captured from a Windows 11 26200 machine.
/// </summary>
public class PowerSettingRangeTests
{
    private const string RangeSetting = """
        Power Scheme GUID: 4fce28e6-fe88-45ef-8e13-506c90b94943  (ZZ-Probe)
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
            GUID Alias: SUB_PROCESSOR
            Power Setting GUID: 0cc5b647-c1df-4637-891a-dec35c318583  (Processor performance core parking min cores)
              GUID Alias: CPMINCORES
              Minimum Possible Setting: 0x00000000
              Maximum Possible Setting: 0x00000064
              Possible Settings increment: 0x00000001
              Possible Settings units: %
            Current AC Power Setting Index: 0x00000004
            Current DC Power Setting Index: 0x00000004
        """;

    // The same setting on a machine that caps core parking below 100%, which is what the log's
    // "not within the range" rejection means.
    private const string CappedRangeSetting = """
              GUID Alias: CPMINCORES
              Minimum Possible Setting: 0x00000000
              Maximum Possible Setting: 0x00000032
              Possible Settings increment: 0x00000005
              Possible Settings units: %
        """;

    private const string EnumSetting = """
        Power Scheme GUID: 4fce28e6-fe88-45ef-8e13-506c90b94943  (ZZ-Probe)
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
            Power Setting GUID: be337238-0d82-4146-a960-4f3749d470c7  (Processor performance boost mode)
              GUID Alias: PERFBOOSTMODE
              Possible Setting Index: 000
              Possible Setting Friendly Name: Disabled
              Possible Setting Index: 001
              Possible Setting Friendly Name: Enabled
              Possible Setting Index: 002
              Possible Setting Friendly Name: Aggressive
            Current AC Power Setting Index: 0x00000002
        """;

    [Fact]
    public void RangeSetting_ValueInRange_IsKept()
    {
        Assert.Equal(100, SystemTweaksService.ResolveAcceptableValue(RangeSetting, 100));
    }

    [Fact]
    public void RangeSetting_ValueAboveCeiling_ClampsToCeiling()
    {
        // 100 is refused on this machine; 50 (0x32) is the most core parking it will give up.
        Assert.Equal(50, SystemTweaksService.ResolveAcceptableValue(CappedRangeSetting, 100));
    }

    [Fact]
    public void RangeSetting_ClampedValue_LandsOnAnExposedStep()
    {
        // Increment is 5, so 48 must come back as 45 rather than a value powercfg would refuse.
        Assert.Equal(45, SystemTweaksService.ResolveAcceptableValue(CappedRangeSetting, 48));
    }

    [Fact]
    public void EnumeratedSetting_OfferedIndex_IsKept()
    {
        Assert.Equal(2, SystemTweaksService.ResolveAcceptableValue(EnumSetting, 2));
    }

    [Fact]
    public void EnumeratedSetting_UnofferedIndex_IsNotSubstituted()
    {
        // A neighbouring index is a different mode, not a weaker version of the same one, so
        // there is nothing safe to fall back to.
        Assert.Null(SystemTweaksService.ResolveAcceptableValue(EnumSetting, 5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invalid Parameters -- try \"/?\" for help")]
    [InlineData("Power Scheme GUID: 4fce28e6-fe88-45ef-8e13-506c90b94943  (ZZ-Probe)")]
    public void SettingNotDescribed_ReturnsNull(string output)
    {
        Assert.Null(SystemTweaksService.ResolveAcceptableValue(output, 100));
    }
}
