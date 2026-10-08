using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// The Ultimate plan holds every core unparked, except on a Ryzen with 3D V-Cache on one of two
/// CCDs: there AMD's V-Cache driver keeps a game on the cache CCD by parking the other one, and a
/// plan that forbids parking quietly undoes it for every game not pinned with CPU Cores.
/// </summary>
public class X3dCoreParkingTests
{
    [Fact]
    public void DualCcdX3d_KeepsCoreParking() =>
        Assert.True(SystemTweaksService.KeepsCoreParking(CpuCoreLayout.VCacheMultiCcd));

    [Theory]
    [InlineData(CpuCoreLayout.Uniform)]
    [InlineData(CpuCoreLayout.Hybrid)]
    [InlineData(CpuCoreLayout.VCacheSingleCcd)]   // 7800X3D/9800X3D: every core has the cache, nothing to park
    [InlineData(CpuCoreLayout.MultiCcd)]          // 7950X/9950X: no cache difference to steer towards
    public void EveryOtherLayout_UnparksAllCores(CpuCoreLayout layout) =>
        Assert.False(SystemTweaksService.KeepsCoreParking(layout));
}
