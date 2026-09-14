using TrayTrigger.Models;

namespace TrayTrigger.Tests;

public class HardwareDisplayTests
{
    [Fact]
    public void FullOsTitle_IncludesEveryPartWhenPresent()
    {
        var os = new OsEnvironmentInfo { WindowsEdition = "Windows 11 Pro", DisplayVersion = "24H2", BuildNumber = "26100" };
        Assert.Equal("Windows 11 Pro 24H2 (Build 26100)", os.FullOsTitle);
    }

    /// <summary>Windows 10 before 20H2 has no DisplayVersion value: no double space.</summary>
    [Fact]
    public void FullOsTitle_OmitsMissingDisplayVersion()
    {
        var os = new OsEnvironmentInfo { WindowsEdition = "Windows 10 Pro", DisplayVersion = "", BuildNumber = "19041" };
        Assert.Equal("Windows 10 Pro (Build 19041)", os.FullOsTitle);
    }

    /// <summary>A failed registry read leaves both empty: no "(Build )".</summary>
    [Fact]
    public void FullOsTitle_OmitsEmptyBuildSuffix()
    {
        var os = new OsEnvironmentInfo();
        Assert.Equal("Windows", os.FullOsTitle);
    }

    [Fact]
    public void PingDisplay_SaysUnavailableWhenThePingFailed()
    {
        Assert.Equal("Unavailable", new NetworkTelemetryInfo().PingDisplay);
        Assert.Equal("Unavailable", new NetworkTelemetryInfo { PingMs = -1 }.PingDisplay);
        Assert.Equal("0 ms", new NetworkTelemetryInfo { PingMs = 0 }.PingDisplay);
        Assert.Equal("23 ms", new NetworkTelemetryInfo { PingMs = 23 }.PingDisplay);
    }

    /// <summary>EnumDisplaySettings reports 0 or 1 for "the default refresh rate".</summary>
    [Theory]
    [InlineData(144, "1920 × 1080 @ 144Hz")]
    [InlineData(0, "1920 × 1080")]
    [InlineData(1, "1920 × 1080")]
    public void ResolutionDisplay_LeavesOffAnUnknownRefreshRate(int hz, string expected)
    {
        var display = new DisplayHardwareInfo { Width = 1920, Height = 1080, RefreshRateHz = hz };
        Assert.Equal(expected, display.ResolutionDisplay);
    }

    [Fact]
    public void DriveUsage_IsDerivedFromTotalsAndFree()
    {
        var drive = new DriveStorageInfo { DriveLetter = "C:", TotalGigabytes = 200, FreeGigabytes = 50 };
        Assert.Equal(150, drive.UsedGigabytes);
        Assert.Equal(75, drive.UsagePercent);
        Assert.Equal("Local Disk (C:)", drive.DisplayName);
    }
}
