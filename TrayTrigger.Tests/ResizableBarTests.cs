using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// Resizable BAR on the GPU card, told from the size of the GPU's biggest memory window: 256 MB
/// with it off, the card's whole memory with it on. The sizes here are what an RTX 5080 with it on
/// reports (a 16 GB window) and what every card reports with it off.
/// </summary>
public class ResizableBarTests
{
    private const long Mb = 1024L * 1024;

    private static GpuHardwareInfo Dedicated(long largestBar, double vramGb = 16, GpuVendor vendor = GpuVendor.Nvidia, string? model = null) =>
        new()
        {
            IsDedicated = true, VramGigabytes = vramGb, LargestBarBytes = largestBar, Vendor = vendor,
            ModelName = model ?? (vendor == GpuVendor.Amd ? "AMD Radeon RX 7900 XT" : "NVIDIA GeForce RTX 5080")
        };

    [Fact]
    public void WindowSpanningVram_ReadsAsOn()
    {
        var gpu = Dedicated(16384 * Mb);
        Assert.True(gpu.ResizableBarEnabled);
        Assert.Equal("", gpu.ResizableBarWarning);
    }

    [Fact]
    public void LegacyWindow_ReadsAsOff_WithTheBiosSwitchesNamed()
    {
        var gpu = Dedicated(256 * Mb);
        Assert.False(gpu.ResizableBarEnabled);
        Assert.Contains("Above 4G Decoding", gpu.ResizableBarWarning);
        Assert.Contains("Re-Size BAR Support", gpu.ResizableBarWarning);
        Assert.DoesNotContain("Smart Access Memory", gpu.ResizableBarWarning);
    }

    /// <summary>AMD users know it by AMD's name.</summary>
    [Fact]
    public void AmdWarning_SaysSmartAccessMemory()
    {
        Assert.Contains("Smart Access Memory", Dedicated(256 * Mb, vendor: GpuVendor.Amd).ResizableBarWarning);
    }

    /// <summary>
    /// A card from before Resizable BAR has a 256 MB window whatever the BIOS says: calling that
    /// "off" would send its owner looking for a setting that changes nothing.
    /// </summary>
    [Theory]
    [InlineData(GpuVendor.Nvidia, "NVIDIA GeForce GTX 1080 Ti")]
    [InlineData(GpuVendor.Nvidia, "NVIDIA GeForce GTX 1660 SUPER")]
    [InlineData(GpuVendor.Nvidia, "NVIDIA GeForce RTX 2070 SUPER")]
    [InlineData(GpuVendor.Nvidia, "NVIDIA TITAN RTX")]
    [InlineData(GpuVendor.Nvidia, "NVIDIA Quadro RTX 4000")]
    [InlineData(GpuVendor.Amd, "Radeon RX 580 Series")]
    [InlineData(GpuVendor.Amd, "Radeon RX Vega 64")]
    [InlineData(GpuVendor.Other, "Some Other Adapter")]
    public void CardThatCannotDoResizableBar_SaysNothing(GpuVendor vendor, string model)
    {
        var gpu = Dedicated(256 * Mb, vramGb: 8, vendor: vendor, model: model);
        Assert.Null(gpu.ResizableBarEnabled);
        Assert.Equal("", gpu.ResizableBarWarning);
    }

    [Theory]
    [InlineData(GpuVendor.Nvidia, "NVIDIA GeForce RTX 3060")]
    [InlineData(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4070 Laptop GPU")]
    [InlineData(GpuVendor.Nvidia, "NVIDIA RTX 4000 Ada Generation")]
    [InlineData(GpuVendor.Amd, "AMD Radeon RX 5700 XT")]
    [InlineData(GpuVendor.Amd, "AMD Radeon RX 6600M")]
    [InlineData(GpuVendor.Amd, "AMD Radeon RX 9070 XT")]
    [InlineData(GpuVendor.Intel, "Intel(R) Arc(TM) A770 Graphics")]
    public void CardThatCanDoResizableBar_ReadsAsOffWithALegacyWindow(GpuVendor vendor, string model)
    {
        Assert.False(Dedicated(256 * Mb, vramGb: 8, vendor: vendor, model: model).ResizableBarEnabled);
    }

    /// <summary>A window bigger than 256 MB is Resizable BAR at work, whatever the card is called.</summary>
    [Fact]
    public void LargeWindow_ReadsAsOn_EvenForANameNotRecognised()
    {
        Assert.True(Dedicated(8192 * Mb, vramGb: 8, vendor: GpuVendor.Other, model: "Some Other Adapter").ResizableBarEnabled);
    }

    /// <summary>A built-in GPU shares system memory: there is no BAR question to answer.</summary>
    [Fact]
    public void BuiltInGpu_SaysNothing()
    {
        var gpu = new GpuHardwareInfo { IsDedicated = false, VramGigabytes = 2, LargestBarBytes = 256 * Mb };
        Assert.Null(gpu.ResizableBarEnabled);
        Assert.Equal("", gpu.ResizableBarWarning);
    }

    /// <summary>No reading (a laptop GPU powered down, a failed query) is not "off".</summary>
    [Fact]
    public void NoReading_SaysNothing()
    {
        Assert.Null(Dedicated(0).ResizableBarEnabled);
    }

    [Fact]
    public void GpuCard_ShowsOnInTheDetailLine_AndWarnsOnlyWhenOff()
    {
        var on = new GpuCardViewModel(new GpuHardwareInfo
        {
            IsDedicated = true, VramGigabytes = 16, LargestBarBytes = 16384 * Mb,
            PcieCurrentWidth = 16, PcieMaxWidth = 16, PcieCurrentGeneration = 5, PcieMaxGeneration = 5
        }, new NvidiaDriverService());
        Assert.Equal("16 GB VRAM · PCIe 5.0 x16 · Resizable BAR on", on.DetailLine);
        Assert.False(on.IsResizableBarOff);

        var off = new GpuCardViewModel(Dedicated(256 * Mb), new NvidiaDriverService());
        Assert.Equal("16 GB VRAM", off.DetailLine);
        Assert.True(off.IsResizableBarOff);
    }
}
