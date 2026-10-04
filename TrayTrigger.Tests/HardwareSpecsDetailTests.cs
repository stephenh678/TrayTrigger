using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// System > Hardware Specs: which GPU is which, the driver version each vendor uses, the live GPU
/// counters, a screen running below its refresh rate, and reading NVIDIA's driver service. The
/// Windows and network calls themselves are exercised by hand; these are the decisions on top.
/// </summary>
public class HardwareSpecsDetailTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7900 XTX", GpuVendor.Amd)]
    [InlineData("AMD Radeon(TM) Graphics", GpuVendor.Amd)]
    [InlineData("Intel(R) UHD Graphics 770", GpuVendor.Intel)]
    [InlineData("Parsec Virtual Display Adapter", GpuVendor.Other)]
    public void Vendor_IsReadFromTheName(string name, GpuVendor expected) =>
        Assert.Equal(expected, SystemInfoService.VendorFromName(name));

    [Theory]
    // The bug: AMD's built-in GPUs were counted as dedicated, as only Intel's were treated as built in.
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M Graphics", true)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", true)]
    [InlineData("AMD Radeon RX 7800 XT", false)]
    [InlineData("AMD Radeon Pro W7900", false)]
    [InlineData("Intel(R) UHD Graphics 770", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", true)]
    [InlineData("Intel(R) Arc(TM) 140V GPU", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", false)]
    public void BuiltInGpus_AreToldApartFromCards(string name, bool integrated) =>
        Assert.Equal(integrated, SystemInfoService.IsIntegratedGpu(SystemInfoService.VendorFromName(name), name));

    [Theory]
    [InlineData(GpuVendor.Nvidia, "32.0.16.1714", null, "617.14")]
    [InlineData(GpuVendor.Nvidia, "31.0.15.5186", null, "551.86")]
    [InlineData(GpuVendor.Amd, "32.0.21001.9024", "24.9.1", "24.9.1")]
    [InlineData(GpuVendor.Amd, "32.0.21001.9024", null, "32.0.21001.9024")]
    // NVIDIA's arithmetic must never reach Intel: it would turn 31.0.101.5382 into "153.82".
    [InlineData(GpuVendor.Intel, "31.0.101.5382", null, "31.0.101.5382")]
    [InlineData(GpuVendor.Nvidia, "", null, "Unknown")]
    public void DriverVersion_IsTheOneTheVendorUses(GpuVendor vendor, string windows, string? radeon, string expected) =>
        Assert.Equal(expected, SystemInfoService.VendorDriverVersion(vendor, windows, radeon));

    [Fact]
    public void DriverDate_IsReadMonthFirst()
    {
        Assert.Equal(new DateTime(2026, 9, 17), SystemInfoService.ParseDriverDate("9-17-2026"));
        Assert.Null(SystemInfoService.ParseDriverDate("Unknown"));
        Assert.Null(SystemInfoService.ParseDriverDate(null));
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(5, "5 days ago")]
    [InlineData(16, "2 weeks ago")]
    [InlineData(150, "5 months ago")]
    [InlineData(400, "over a year ago")]
    [InlineData(800, "over 2 years ago")]
    public void DriverAge_ReadsNaturally(int days, string expected)
    {
        var now = new DateTime(2026, 10, 3);
        Assert.Equal(expected, GpuCardViewModel.Age(now.AddDays(-days), now));
    }

    // ---- Live GPU counters -------------------------------------------------------------------

    [Fact]
    public void CounterInstanceLuid_MatchesDxgisPacking()
    {
        Assert.True(GpuTelemetryService.TryParseLuid("pid_1234_luid_0x00000000_0x0000D1F3_phys_0_eng_0_engtype_3D", out long luid));
        Assert.Equal(0xD1F3L, luid);
        Assert.True(GpuTelemetryService.TryParseLuid("luid_0x00000001_0x00000002_phys_0", out luid));
        Assert.Equal((1L << 32) | 2, luid);
        Assert.False(GpuTelemetryService.TryParseLuid("_Total", out _));
    }

    /// <summary>Task Manager's load: each engine type summed over processes, and the busiest type wins.</summary>
    [Fact]
    public void Load_IsTheBusiestEngineTypeSummedOverProcesses()
    {
        var engines = new Dictionary<string, double>
        {
            ["pid_1_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D"] = 30,
            ["pid_2_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D"] = 25,
            ["pid_1_luid_0x00000000_0x0000AAAA_phys_0_eng_4_engtype_VideoDecode"] = 40,
            ["pid_3_luid_0x00000000_0x0000BBBB_phys_0_eng_0_engtype_3D"] = 5,
        };
        var memory = new Dictionary<string, double>
        {
            ["luid_0x00000000_0x0000AAAA_phys_0"] = 2.5 * 1024 * 1024 * 1024,
        };

        var result = GpuTelemetryService.Aggregate(engines, memory);

        Assert.Equal(55, result[0xAAAA].LoadPercent);
        Assert.Equal(2.5, result[0xAAAA].DedicatedUsedGigabytes);
        Assert.Equal(5, result[0xBBBB].LoadPercent);
        Assert.Equal(0, result[0xBBBB].DedicatedUsedGigabytes);
    }

    // ---- Displays ----------------------------------------------------------------------------

    [Theory]
    [InlineData(60, 144, true)]
    [InlineData(120, 240, true)]
    // 59.94 / 60 and 143.9 / 144 are Windows' rounding, not a setting.
    [InlineData(59, 60, false)]
    [InlineData(143, 144, false)]
    [InlineData(144, 144, false)]
    // 0 and 1 mean "the default rate" (remote and virtual displays): nothing to compare.
    [InlineData(1, 60, false)]
    [InlineData(60, 0, false)]
    public void ScreenBelowItsRefreshRate_IsCalledOut(int current, int max, bool expected)
    {
        var display = new DisplayHardwareInfo { Width = 2560, Height = 1440, RefreshRateHz = current, MaxRefreshRateHz = max };
        Assert.Equal(expected, display.IsBelowMaxRefresh);
        Assert.Equal(expected, display.RefreshWarning.Length > 0);
    }

    [Fact]
    public void Display_UsesTheMonitorsOwnName_AndSaysWhetherHdrIsOn()
    {
        var display = new DisplayHardwareInfo { DeviceName = "Primary display", MonitorName = "AW3425DW", HdrSupported = true, HdrEnabled = false };
        Assert.Equal("AW3425DW", display.Title);
        Assert.Equal("HDR off", display.HdrDisplay);

        var plain = new DisplayHardwareInfo { DeviceName = "Display 2" };
        Assert.Equal("Display 2", plain.Title);
        Assert.Equal("", plain.HdrDisplay);
        Assert.False(plain.HasHdr);
    }

    // ---- NVIDIA's driver service -------------------------------------------------------------

    private const string ProductList = """
        <?xml version="1.0" encoding="UTF-8"?><LookupValueSearch><LookupValues>
        <LookupValue ParentID="131">
        <Name>NVIDIA GeForce RTX 5080</Name>
        <Value>1065</Value>
        </LookupValue>
        <LookupValue ParentID="127"><Name>GeForce RTX 4090</Name><Value>995</Value></LookupValue>
        <LookupValue ParentID="133"><Name>NVIDIA GeForce RTX 5080 Laptop GPU</Name><Value>1080</Value></LookupValue>
        </LookupValues></LookupValueSearch>
        """;

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5080", 131, 1065)]
    // NVIDIA's list names some cards without the "NVIDIA " prefix Windows gives them.
    [InlineData("NVIDIA GeForce RTX 4090", 127, 995)]
    [InlineData("NVIDIA GeForce RTX 5080 Laptop GPU", 133, 1080)]
    public void Gpu_IsFoundInNvidiasProductList(string gpu, int psid, int pfid) =>
        Assert.Equal((psid, pfid), NvidiaDriverService.FindProduct(ProductList, gpu));

    [Fact]
    public void UnlistedGpu_IsNotGuessed() =>
        Assert.Null(NvidiaDriverService.FindProduct(ProductList, "NVIDIA GeForce RTX 5070"));

    [Fact]
    public void NewestDriver_IsReadFromTheReply()
    {
        const string reply = """
            { "Success" : "1", "IDS" : [{ "downloadInfo": { "Success" : "1", "ID" : "279803", "Version" : "617.14",
              "ReleaseDateTime" : "Tue Sep 22, 2026", "DetailsURL" : "https://www.nvidia.com/en-us/drivers/details/279803/" } }] }
            """;
        var latest = NvidiaDriverService.ParseLatestDriver(reply);

        Assert.NotNull(latest);
        Assert.Equal("617.14", latest.Value.Version);
        Assert.Equal("Tue Sep 22, 2026", latest.Value.ReleaseDate);
        Assert.Equal("https://www.nvidia.com/en-us/drivers/details/279803/", latest.Value.DetailsUrl);
    }

    [Fact]
    public void ReplyWithNoDriver_IsNotTakenAsAnAnswer()
    {
        const string reply = """{ "Success" : "0", "IDS" : [{ "downloadInfo": { "Success" : "0", "ID" : "" } }] }""";
        Assert.Null(NvidiaDriverService.ParseLatestDriver(reply));
    }

    /// <summary>A details link that isn't nvidia.com is dropped rather than opened.</summary>
    [Fact]
    public void DetailsLink_MustBeNvidias()
    {
        const string reply = """{ "IDS" : [{ "downloadInfo": { "Success" : "1", "Version" : "617.14", "DetailsURL" : "https://example.com/x" } }] }""";
        Assert.Null(NvidiaDriverService.ParseLatestDriver(reply)!.Value.DetailsUrl);
    }

    [Theory]
    [InlineData("617.14", "617.14", 0)]
    [InlineData("617.14", "617.20", -1)]
    [InlineData("617.14", "581.80", 1)]
    [InlineData("617.9", "617.14", -1)]
    public void DriverVersions_CompareNumerically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(NvidiaDriverService.CompareVersions(a, b)));

    // ---- PCIe link -----------------------------------------------------------------------------

    [Fact]
    public void FullLink_ReadsAsGenerationAndWidth_WithNoWarning()
    {
        var gpu = new GpuHardwareInfo { PcieCurrentWidth = 16, PcieMaxWidth = 16, PcieCurrentGeneration = 5, PcieMaxGeneration = 5 };
        Assert.Equal("PCIe 5.0 x16", gpu.PcieDisplay);
        Assert.False(gpu.IsPcieNarrowed);
        Assert.Equal("", gpu.PcieWarning);
    }

    /// <summary>An x16 card on x8 lanes: the second slot, or lanes shared with an M.2 drive.</summary>
    [Fact]
    public void NarrowedLink_IsCalledOut()
    {
        var gpu = new GpuHardwareInfo { PcieCurrentWidth = 8, PcieMaxWidth = 16, PcieCurrentGeneration = 4, PcieMaxGeneration = 4 };
        Assert.True(gpu.IsPcieNarrowed);
        Assert.Contains("x8 of the x16", gpu.PcieWarning);
    }

    /// <summary>A slower slot is shown, not warned about: a generation down costs games little, and
    /// the speed drops at idle anyway.</summary>
    [Fact]
    public void SlowerSlot_IsInformationOnly()
    {
        var gpu = new GpuHardwareInfo { PcieCurrentWidth = 16, PcieMaxWidth = 16, PcieCurrentGeneration = 4, PcieMaxGeneration = 5 };
        Assert.Equal("PCIe 4.0 x16 (card supports PCIe 5.0)", gpu.PcieDisplay);
        Assert.False(gpu.IsPcieNarrowed);
    }

    /// <summary>A built-in GPU, or a laptop's discrete GPU powered down, reports no link: say nothing.</summary>
    [Fact]
    public void NoLink_ShowsNothing()
    {
        var gpu = new GpuHardwareInfo();
        Assert.False(gpu.HasPcieLink);
        Assert.Equal("", gpu.PcieDisplay);
        Assert.False(gpu.IsPcieNarrowed);
    }

    // ---- Refresh rate, always with the max ---------------------------------------------------------

    [Theory]
    [InlineData(240, 240, "3440 × 1440 · 240 Hz (max 240 Hz)")]
    [InlineData(120, 240, "3440 × 1440 · 120 Hz (max 240 Hz)")]
    [InlineData(60, 0, "3440 × 1440 · 60 Hz")]
    [InlineData(1, 60, "3440 × 1440")]
    public void Mode_ShowsTheRateAndTheMax(int current, int max, string expected) =>
        Assert.Equal(expected, new DisplayHardwareInfo { Width = 3440, Height = 1440, RefreshRateHz = current, MaxRefreshRateHz = max }.ModeDisplay);

    // ---- Games per drive ---------------------------------------------------------------------------

    [Fact]
    public void Drive_CountsItsGames_AndAHardDriveSaysSo()
    {
        var hdd = new DriveStorageInfo { DriveLetter = "D:", MediaTypeDisplay = "HDD", GameCount = 12 };
        Assert.Equal("12 games", hdd.GameCountDisplay);
        Assert.True(hdd.HasGamesOnHdd);

        var ssd = new DriveStorageInfo { DriveLetter = "E:", MediaTypeDisplay = "NVMe SSD", GameCount = 1 };
        Assert.Equal("1 game", ssd.GameCountDisplay);
        Assert.False(ssd.HasGamesOnHdd);

        var empty = new DriveStorageInfo { DriveLetter = "F:", MediaTypeDisplay = "HDD", GameCount = 0 };
        Assert.False(empty.HasGameCount);
        Assert.False(empty.HasGamesOnHdd);
    }

    [Fact]
    public void GameDrive_IsTheDriveItsFolderIsOn()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, "game.exe");
            File.WriteAllText(exe, "");
            string expected = Path.GetPathRoot(dir)![..2].ToUpperInvariant();

            Assert.Equal(expected, SystemInfoService.GameDriveLetter(new GameEntry { ExecutablePath = exe }));
            // A Steam game launched by URL: its working folder says where it is.
            Assert.Equal(expected, SystemInfoService.GameDriveLetter(new GameEntry { ExecutablePath = "steam://rungameid/570", WorkingDirectory = dir }));
            // Nowhere on disk: no drive, rather than a guess.
            Assert.Null(SystemInfoService.GameDriveLetter(new GameEntry { ExecutablePath = "steam://rungameid/570" }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
