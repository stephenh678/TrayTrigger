using TrayTrigger.Models;

namespace TrayTrigger.Tests;

/// <summary>UX-14c: the CPU card shows the current and the max clock to the same precision.</summary>
public class ClockSpeedDisplayTests
{
    [Fact]
    public void CurrentAndMax_UseTwoDecimals()
    {
        var cpu = new CpuHardwareInfo { CurrentClockSpeedGhz = 3.2, MaxClockSpeedGhz = 3.19 };
        Assert.Equal("3.20 GHz Current, 3.19 GHz Max", cpu.ClockSpeedDisplay);
    }

    [Fact]
    public void MaxOnly_UsesTwoDecimals()
    {
        var cpu = new CpuHardwareInfo { CurrentClockSpeedGhz = 0, MaxClockSpeedGhz = 3.6 };
        Assert.Equal("3.60 GHz Max Clock", cpu.ClockSpeedDisplay);
    }
}
