using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Stutter Check is a pure function of what the probes found: each check's verdict, what is left
/// out when the PC gives nothing to judge, the summary line and the copied text.
/// </summary>
public class StutterCheckServiceTests
{
    private static SystemTweakItem Tweak(string id, bool optimal, bool available = true) =>
        new() { Id = id, Name = id, IsOptimal = optimal, IsAvailable = available };

    private static SystemHardwareReport Healthy() => new()
    {
        Ram = new RamHardwareInfo { SpeedMhz = 6000, RatedSpeedMhz = 6000, IsXmpActive = true },
        Displays = { new DisplayHardwareInfo { MonitorName = "AW3425DW", RefreshRateHz = 240, MaxRefreshRateHz = 240, Width = 3440, Height = 1440 } },
        Drives = { new DriveStorageInfo { DriveLetter = "C:", MediaTypeDisplay = "NVMe SSD", GameCount = 12 } },
        Gpus = { new GpuHardwareInfo { ModelName = "RTX 4080", IsDedicated = true, PcieCurrentWidth = 16, PcieMaxWidth = 16, PcieCurrentGeneration = 4, LargestBarBytes = 16L << 30, VramGigabytes = 16, Vendor = GpuVendor.Nvidia } },
    };

    [Fact]
    public void HealthyPc_IsAllFine()
    {
        var report = StutterCheckService.Run(new StutterCheckInputs
        {
            Hardware = Healthy(),
            Tweaks = [Tweak("hags", true), Tweak("game_dvr", true), Tweak("game_mode", true), Tweak("power_plan", true), Tweak("core_isolation", false)],
            OverlayApps = ["Discord"],
            Apps = [new AppAccelerationState("Chrome", Installed: true, HardwareAccelerationOn: true, IsRunning: false, DedicatedGpuBytes: 0)],
            SearchIndexerBusy = false,
            IntelApo = IntelApoState.NotNeeded,
        });

        Assert.Equal(14, report.Items.Count);
        Assert.All(report.Items, i => Assert.Equal(StutterVerdict.Fine, i.Verdict));
        Assert.Equal("14 checks: all fine", report.Summary);
        Assert.Empty(report.Attention);
    }

    [Fact]
    public void TheMockedUpPc_GetsTheMockedUpVerdicts()
    {
        var hw = Healthy();
        hw.Displays[0].RefreshRateHz = 60;
        var report = StutterCheckService.Run(new StutterCheckInputs
        {
            Hardware = hw,
            Tweaks = [Tweak("hags", false), Tweak("game_dvr", false), Tweak("game_mode", true), Tweak("power_plan", true), Tweak("core_isolation", true)],
            OverlayApps = ["Discord", "NVIDIA App overlay", "RivaTuner (RTSS)"],
            Apps =
            [
                new AppAccelerationState("Chrome", true, true, IsRunning: true, DedicatedGpuBytes: 800L << 20),
                new AppAccelerationState("Discord", true, true, IsRunning: true, DedicatedGpuBytes: 300L << 20),
                new AppAccelerationState("Edge", Installed: false, HardwareAccelerationOn: false, IsRunning: false, DedicatedGpuBytes: 0),
            ],
            SearchIndexerBusy = false,
            IntelApo = IntelApoState.NotNeeded,
        });

        Assert.Equal("14 checks: 3 need a look, 2 TrayTrigger can fix, 1 to know about, 8 fine", report.Summary);

        var attention = report.Attention;
        Assert.Equal(new[] { StutterVerdict.NeedsLook, StutterVerdict.NeedsLook, StutterVerdict.NeedsLook, StutterVerdict.CanFix, StutterVerdict.CanFix, StutterVerdict.Info },
            attention.Select(i => i.Verdict));

        var overlays = attention.Single(i => i.Id == "overlays");
        Assert.Equal("3 overlay apps running: Discord, NVIDIA App overlay and RivaTuner (RTSS)", overlays.Title);
        Assert.Equal(StutterAction.HelpTopic, overlays.Action);
        Assert.Equal("tweaks/stutter_overlays", overlays.ActionArg);

        var accel = attention.Single(i => i.Id == "app_accel");
        Assert.Equal("Chrome and Discord use GPU hardware acceleration", accel.Title);
        Assert.Contains("1.1 GB", accel.Detail);

        var refresh = attention.Single(i => i.Id == "refresh_rate");
        Assert.Equal("AW3425DW is running at 60 Hz, offers 240 Hz", refresh.Title);
        Assert.Equal("ms-settings:display-advanced", refresh.ActionArg);

        var hags = attention.Single(i => i.Id == "hags");
        Assert.Equal(StutterAction.ApplyTweak, hags.Action);
        Assert.Equal("Fix it", hags.ActionLabel);
        Assert.Equal("hags", hags.ActionArg);

        Assert.Equal("windowsdefender://coreisolation", attention.Single(i => i.Id == "core_isolation").ActionArg);
    }

    [Fact]
    public void ChecksThePcCantJudge_AreLeftOut_NotFailed()
    {
        var report = StutterCheckService.Run(new StutterCheckInputs
        {
            Hardware = new SystemHardwareReport(), // no displays, no RAM speed, no GPUs, drives not counted
            Tweaks = [Tweak("hags", false, available: false)],
            SearchIndexerBusy = null,
            IntelApo = IntelApoState.Unknown,
        });

        // Only overlays and app acceleration can always be judged.
        Assert.Equal(new[] { "overlays", "app_accel" }, report.Items.Select(i => i.Id));
        Assert.DoesNotContain(report.Items, i => i.Verdict != StutterVerdict.Fine);
    }

    [Fact]
    public void OneOverlay_IsFine_TwoNeedALook()
    {
        var one = StutterCheckService.Run(new StutterCheckInputs { OverlayApps = ["Discord"] }).Items.Single(i => i.Id == "overlays");
        Assert.Equal(StutterVerdict.Fine, one.Verdict);
        Assert.Equal("One overlay app running: Discord", one.Title);

        var two = StutterCheckService.Run(new StutterCheckInputs { OverlayApps = ["Discord", "Medal"] }).Items.Single(i => i.Id == "overlays");
        Assert.Equal(StutterVerdict.NeedsLook, two.Verdict);
        Assert.Equal("2 overlay apps running: Discord and Medal", two.Title);
    }

    [Fact]
    public void AnAppWithAccelerationOff_OrNotRunning_DoesntCount()
    {
        var report = StutterCheckService.Run(new StutterCheckInputs
        {
            Apps =
            [
                new AppAccelerationState("Chrome", true, HardwareAccelerationOn: false, IsRunning: true, DedicatedGpuBytes: 500L << 20),
                new AppAccelerationState("Discord", true, HardwareAccelerationOn: true, IsRunning: false, DedicatedGpuBytes: 0),
            ],
        });
        Assert.Equal(StutterVerdict.Fine, report.Items.Single(i => i.Id == "app_accel").Verdict);
    }

    [Fact]
    public void Memory_BelowRated_NeedsALook_WithTheBiosPage()
    {
        var hw = new SystemHardwareReport { Ram = new RamHardwareInfo { SpeedMhz = 4800, RatedSpeedMhz = 6000 } };
        var xmp = StutterCheckService.Run(new StutterCheckInputs { Hardware = hw }).Items.Single(i => i.Id == "xmp");
        Assert.Equal(StutterVerdict.NeedsLook, xmp.Verdict);
        Assert.Equal("Memory runs at 4800 MHz, rated for 6000 MHz", xmp.Title);
        Assert.Equal("tweaks/stutter_xmp", xmp.ActionArg);
    }

    [Fact]
    public void GamesOnAHardDrive_AreCounted_AcrossDrives()
    {
        var hw = new SystemHardwareReport
        {
            Drives =
            {
                new DriveStorageInfo { DriveLetter = "C:", MediaTypeDisplay = "NVMe SSD", GameCount = 10 },
                new DriveStorageInfo { DriveLetter = "D:", MediaTypeDisplay = "HDD", GameCount = 3 },
                new DriveStorageInfo { DriveLetter = "E:", MediaTypeDisplay = "HDD", GameCount = 1 },
            }
        };
        var hdd = StutterCheckService.Run(new StutterCheckInputs { Hardware = hw }).Items.Single(i => i.Id == "hdd_games");
        Assert.Equal(StutterVerdict.NeedsLook, hdd.Verdict);
        Assert.Equal("4 library games on a hard drive (D: and E:)", hdd.Title);
    }

    [Fact]
    public void ShaderCaches_AreFixItRows_WhenTheirTweaksAreOff()
    {
        var report = StutterCheckService.Run(new StutterCheckInputs { Tweaks = [Tweak("nv_shader_cache", false), Tweak("dx_shader_cache", true)] });
        var nv = report.Items.Single(i => i.Id == "nv_shader_cache");
        Assert.Equal(StutterVerdict.CanFix, nv.Verdict);
        Assert.Equal("NVIDIA shader cache is capped", nv.Title);
        Assert.Equal(StutterAction.ApplyTweak, nv.Action);
        Assert.Equal("Windows keeps the DirectX shader cache", report.Items.Single(i => i.Id == "dx_shader_cache").Title);
        // No NVIDIA driver: the row is left out, not failed.
        Assert.DoesNotContain(StutterCheckService.Run(new StutterCheckInputs { Tweaks = [Tweak("nv_shader_cache", false, available: false)] }).Items, i => i.Id == "nv_shader_cache");
    }

    [Fact]
    public void DriveSpace_FlagsAGameOrWindowsDrive_Under10PercentOr20Gb()
    {
        var hw = new SystemHardwareReport
        {
            Drives =
            {
                new DriveStorageInfo { DriveLetter = "C:", TotalGigabytes = 1000, FreeGigabytes = 60, GameCount = 0 },   // Windows, 6 %
                new DriveStorageInfo { DriveLetter = "D:", TotalGigabytes = 2000, FreeGigabytes = 900, GameCount = 20 }, // games, plenty
                new DriveStorageInfo { DriveLetter = "E:", TotalGigabytes = 500, FreeGigabytes = 5, GameCount = 0 },     // nearly full but holds nothing that matters
            }
        };
        var row = StutterCheckService.Run(new StutterCheckInputs { Hardware = hw, WindowsDriveLetter = "C:" }).Items.Single(i => i.Id == "drive_space");
        Assert.Equal(StutterVerdict.NeedsLook, row.Verdict);
        Assert.Equal("C: is nearly full: 60 GB of 1000 GB free", row.Title);
        Assert.StartsWith("That's the Windows drive.", row.Detail);
        Assert.Equal("ms-settings:storagesense", row.ActionArg);

        hw.Drives[0].FreeGigabytes = 300;
        Assert.Equal(StutterVerdict.Fine, StutterCheckService.Run(new StutterCheckInputs { Hardware = hw, WindowsDriveLetter = "C:" }).Items.Single(i => i.Id == "drive_space").Verdict);

        // A small drive: 20 GB is the floor even when that is more than 10 %.
        var small = new SystemHardwareReport { Drives = { new DriveStorageInfo { DriveLetter = "C:", TotalGigabytes = 120, FreeGigabytes = 15 } } };
        Assert.Equal(StutterVerdict.NeedsLook, StutterCheckService.Run(new StutterCheckInputs { Hardware = small, WindowsDriveLetter = "C:" }).Items.Single(i => i.Id == "drive_space").Verdict);

        // No sizes read: left out.
        Assert.DoesNotContain(StutterCheckService.Run(new StutterCheckInputs { Hardware = new SystemHardwareReport { Drives = { new DriveStorageInfo { DriveLetter = "C:" } } }, WindowsDriveLetter = "C:" }).Items, i => i.Id == "drive_space");
    }

    [Fact]
    public void GameMode_SaysItIsContested()
    {
        var row = StutterCheckService.Run(new StutterCheckInputs { Tweaks = [Tweak("game_mode", false)] }).Items.Single(i => i.Id == "game_mode");
        Assert.Equal(StutterVerdict.CanFix, row.Verdict);
        Assert.Contains("try it the other way", row.Detail);
    }

    [Fact]
    public void PowerPlan_FineWhenTheProfileSwitchesIt_InfoWhenNothingDoes()
    {
        var tweaks = new[] { Tweak("power_plan", false) };
        Assert.Equal(StutterVerdict.Fine, StutterCheckService.Run(new StutterCheckInputs { Tweaks = tweaks, ProfileSwitchesPowerPlan = true }).Items.Single(i => i.Id == "power_plan").Verdict);
        Assert.Equal(StutterVerdict.Info, StutterCheckService.Run(new StutterCheckInputs { Tweaks = tweaks, ProfileSwitchesPowerPlan = false }).Items.Single(i => i.Id == "power_plan").Verdict);
    }

    [Fact]
    public void SearchIndexer_BusyNeedsALook_IdleIsFine()
    {
        var busy = StutterCheckService.Run(new StutterCheckInputs { SearchIndexerBusy = true, SearchIndexerCpuPercent = 12.4 }).Items.Single(i => i.Id == "search_indexer");
        Assert.Equal(StutterVerdict.NeedsLook, busy.Verdict);
        Assert.Equal("Windows Search is indexing right now (12 % CPU)", busy.Title);
        Assert.Equal(StutterVerdict.Fine, StutterCheckService.Run(new StutterCheckInputs { SearchIndexerBusy = false }).Items.Single(i => i.Id == "search_indexer").Verdict);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", true)]
    [InlineData("Intel(R) Core(TM) i9-14900K", true)]
    [InlineData("Intel(R) Core(TM) i7-14700KF", true)]
    [InlineData("Intel(R) Core(TM) i7-13700K", false)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", false)]
    public void IntelApo_AppliesToCoreUltraAnd14thGenK(string cpu, bool applies)
    {
        Assert.Equal(applies, StutterProbes.IsApoCpu(cpu));
    }

    [Fact]
    public void ChromiumAndDiscordSettings_ReadAccelerationOff_OnlyWhenWritten()
    {
        Assert.True(StutterProbes.ChromiumAccelerationOn("{}"));
        Assert.True(StutterProbes.ChromiumAccelerationOn(null));
        Assert.True(StutterProbes.ChromiumAccelerationOn("{\"hardware_acceleration_mode\":{\"enabled\":true}}"));
        Assert.False(StutterProbes.ChromiumAccelerationOn("{\"hardware_acceleration_mode\":{\"enabled\":false}}"));
        Assert.True(StutterProbes.ChromiumAccelerationOn("not json"));

        Assert.True(StutterProbes.DiscordAccelerationOn("{\"BACKGROUND_COLOR\":\"#202225\"}"));
        Assert.False(StutterProbes.DiscordAccelerationOn("{\"enableHardwareAcceleration\":false}"));
    }

    [Fact]
    public void EveryHelpPageARowOpens_Exists()
    {
        // Every row with a Here's how button at once: two overlays, a browser holding memory, XMP
        // off, the indexer busy, and a CPU without Intel APO.
        var report = StutterCheckService.Run(new StutterCheckInputs
        {
            Hardware = new SystemHardwareReport { Ram = new RamHardwareInfo { SpeedMhz = 4800, RatedSpeedMhz = 6000 } },
            OverlayApps = ["Discord", "Medal"],
            Apps = [new AppAccelerationState("Chrome", Installed: true, HardwareAccelerationOn: true, IsRunning: true, DedicatedGpuBytes: 0)],
            SearchIndexerBusy = true,
            IntelApo = IntelApoState.Missing,
        });

        var topics = report.Items.Where(i => i.Action == StutterAction.HelpTopic).Select(i => i.ActionArg).ToList();
        Assert.Equal(5, topics.Count);
        Assert.All(topics, t => Assert.True(HelpContentService.HasTopic(t), $"Help/{t}.md is missing"));
        Assert.True(HelpContentService.HasTopic(StutterCheckService.HelpTopic));
    }

    [Fact]
    public void CopiedText_LeadsWithTheSummary_AttentionFirst_AndNamesTheVersion()
    {
        var report = StutterCheckService.Run(new StutterCheckInputs
        {
            Tweaks = [Tweak("hags", false)],
            OverlayApps = [],
            SearchIndexerBusy = false,
        }, now: new DateTime(2026, 10, 8, 21, 30, 0));

        var lines = report.ToLines();
        Assert.StartsWith("Stutter Check, 2026-10-08 21:30: 4 checks: 1 TrayTrigger can fix, 3 fine", lines[0]);
        Assert.StartsWith("[TRAYTRIGGER CAN FIX] Hardware-Accelerated GPU Scheduling is off - ", lines[1]);
        Assert.Equal("[FINE] No overlay apps running", lines[2]);
        Assert.Contains("TrayTrigger ", report.ToText());
    }
}
