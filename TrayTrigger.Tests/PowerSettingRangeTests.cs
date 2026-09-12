using TrayTrigger.Services;
using Domain = TrayTrigger.Services.SystemTweaksService.PowerSettingDomain;

namespace TrayTrigger.Tests;

/// <summary>
/// The Ultimate plan's powercfg values are not universal: Dylan's 1.4.0 log rejected
/// CPMINCORES=100 on all four passes with "not within the range of the target power setting", so
/// the value is now taken from what the platform publishes for the setting. These are the shapes
/// Windows actually stores under
/// HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{subgroup}\{setting} - a range
/// setting carries ValueMin/ValueMax/ValueIncrement, an enumerated one has a numbered subkey per
/// valid index - both read from a Windows 11 26200 machine.
/// </summary>
public class PowerSettingRangeTests
{
    // CPMINCORES / PROCTHROTTLEMIN: percentages.
    private static Domain Range(int min, int max, int? increment = 1) => new(min, max, increment, []);

    // PERFBOOSTMODE has subkeys 0..6; USB selective suspend and SYSCOOLPOL have 0..1.
    private static Domain Enum(params int[] indexes) => new(null, null, null, indexes);

    [Fact]
    public void RangeSetting_ValueInRange_IsKept()
    {
        Assert.Equal(100, SystemTweaksService.ResolveAcceptableValue(Range(0, 100), 100));
    }

    [Fact]
    public void RangeSetting_ValueAboveCeiling_ClampsToCeiling()
    {
        // A processor that will only ever unpark half its cores gets half, rather than nothing.
        Assert.Equal(50, SystemTweaksService.ResolveAcceptableValue(Range(0, 50), 100));
    }

    [Fact]
    public void RangeSetting_ClampedValue_LandsOnAnExposedStep()
    {
        Assert.Equal(45, SystemTweaksService.ResolveAcceptableValue(Range(0, 50, increment: 5), 48));
    }

    [Fact]
    public void RangeSetting_MissingIncrement_IsTreatedAsOne()
    {
        Assert.Equal(50, SystemTweaksService.ResolveAcceptableValue(Range(0, 50, increment: null), 100));
    }

    [Fact]
    public void RangeSetting_CeilingOfZero_YieldsZeroRatherThanFailing()
    {
        // Core parking the platform will not give up at all: 0 is the Windows default, so this
        // ends up a no-op instead of a warning the user cannot act on.
        Assert.Equal(0, SystemTweaksService.ResolveAcceptableValue(Range(0, 0), 100));
    }

    [Fact]
    public void EnumeratedSetting_OfferedIndex_IsKept()
    {
        Assert.Equal(2, SystemTweaksService.ResolveAcceptableValue(Enum(0, 1, 2, 3, 4, 5, 6), 2));
    }

    [Fact]
    public void EnumeratedSetting_UnofferedIndex_IsNotSubstituted()
    {
        // Index 2 of a 0/1 setting is not "more" of anything - there is nothing safe to fall
        // back to, so the tweak is skipped rather than guessed at.
        Assert.Null(SystemTweaksService.ResolveAcceptableValue(Enum(0, 1), 2));
    }

    [Fact]
    public void MalformedRange_IsRejected()
    {
        Assert.Null(SystemTweaksService.ResolveAcceptableValue(Range(100, 0), 100));
        Assert.Null(SystemTweaksService.ResolveAcceptableValue(new Domain(null, null, null, []), 100));
    }
}
