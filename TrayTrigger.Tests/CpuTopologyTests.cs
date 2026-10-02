using System.IO;
using System.Runtime.ExceptionServices;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Tests;

/// <summary>
/// CPU Cores: how a CPU's layout is worked out from Windows' CPU Sets and L3 caches, with no list
/// of models, and what each choice keeps a game on. Every CPU here is built from the shape Windows
/// reports for it: logical processors with an efficiency class and a core index, and L3 caches
/// with the mask of the processors that share them.
/// </summary>
public class CpuTopologyTests : IDisposable
{
    private const long MB = 1024L * 1024;

    // Edit Game's view model needs a StorageService, which creates its folders on construction.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Logical processors <paramref name="first"/>.. with two threads per core (SMT), CPU Set ids from 256 as Windows numbers them.</summary>
    private static IEnumerable<CpuSetEntry> Cpus(int first, int count, byte efficiencyClass = 0, int threadsPerCore = 2, byte llc = 0)
    {
        for (int i = first; i < first + count; i++)
        {
            int core = first + (i - first) / threadsPerCore * threadsPerCore;
            yield return new CpuSetEntry((uint)(256 + i), 0, (byte)i, (byte)core, llc, efficiencyClass);
        }
    }

    private static L3CacheInfo L3(long size, int first, int count) =>
        new(size, 0, Enumerable.Range(first, count).Aggregate(0UL, (mask, i) => mask | (1UL << i)));

    private static CpuTopology I9_12900K() => CpuTopology.Build(
        Cpus(0, 16, efficiencyClass: 1).Concat(Cpus(16, 8, efficiencyClass: 0, threadsPerCore: 1)).ToList(),
        [L3(30 * MB, 0, 24)], 24);

    private static CpuTopology R9_7950X3D() => CpuTopology.Build(
        Cpus(0, 16, llc: 0).Concat(Cpus(16, 16, llc: 1)).ToList(),
        [L3(96 * MB, 0, 16), L3(32 * MB, 16, 16)], 32);

    private static CpuTopology R9_7900X3D() => CpuTopology.Build(
        Cpus(0, 12, llc: 0).Concat(Cpus(12, 12, llc: 1)).ToList(),
        [L3(96 * MB, 0, 12), L3(32 * MB, 12, 12)], 24);

    private static CpuTopology R7_9800X3D() => CpuTopology.Build(Cpus(0, 16).ToList(), [L3(96 * MB, 0, 16)], 16);

    private static CpuTopology R9_9950X() => CpuTopology.Build(
        Cpus(0, 16, llc: 0).Concat(Cpus(16, 16, llc: 1)).ToList(),
        [L3(32 * MB, 0, 16), L3(32 * MB, 16, 16)], 32);

    private static CpuTopology I7_9700K() => CpuTopology.Build(Cpus(0, 8, threadsPerCore: 1).ToList(), [L3(12 * MB, 0, 8)], 8);

    // ------------------------------------------------------------------ layouts

    [Fact]
    public void IntelHybrid_OffersPerformanceCores_AndAutoPicksThem()
    {
        var cpu = I9_12900K();

        Assert.Equal(CpuCoreLayout.Hybrid, cpu.Layout);
        Assert.Equal([CpuAffinityMode.Default, CpuAffinityMode.Auto, CpuAffinityMode.PerformanceCoresOnly], cpu.Options);
        Assert.Equal(CpuAffinityMode.PerformanceCoresOnly, cpu.Recommended);
        Assert.Equal(16, cpu.CpusFor(CpuAffinityMode.PerformanceCoresOnly)!.Count);
        Assert.Equal(cpu.CpusFor(CpuAffinityMode.PerformanceCoresOnly), cpu.CpusFor(CpuAffinityMode.Auto));
        Assert.Equal("Hybrid: 8 performance cores and 8 efficiency cores.", cpu.Summary);
        Assert.Equal("Performance cores only (8 cores, 16 threads)", cpu.OptionLabel(CpuAffinityMode.PerformanceCoresOnly));
    }

    [Fact]
    public void DualCcdX3D_FindsTheVCacheCcdByItsCacheSize()
    {
        var cpu = R9_7950X3D();

        Assert.Equal(CpuCoreLayout.VCacheMultiCcd, cpu.Layout);
        Assert.Equal([CpuAffinityMode.Default, CpuAffinityMode.Auto, CpuAffinityMode.VCacheCores, CpuAffinityMode.FrequencyCores], cpu.Options);
        Assert.Equal(CpuAffinityMode.VCacheCores, cpu.Recommended);
        Assert.Equal(Enumerable.Range(256, 16).Select(i => (uint)i), cpu.CpusFor(CpuAffinityMode.VCacheCores)!.Select(c => c.Id));
        Assert.Equal(Enumerable.Range(272, 16).Select(i => (uint)i), cpu.CpusFor(CpuAffinityMode.FrequencyCores)!.Select(c => c.Id));
        Assert.Equal(cpu.CpusFor(CpuAffinityMode.VCacheCores), cpu.CpusFor(CpuAffinityMode.Auto));
        Assert.Equal("2 CCDs: 3D V-Cache (96 MB L3) on CPUs 0-15, 32 MB L3 on CPUs 16-31.", cpu.Summary);
    }

    [Fact]
    public void VCacheOnTheSecondCcd_IsFoundThere()
    {
        // Nothing says the cache CCD comes first; the size decides.
        var cpu = CpuTopology.Build(Cpus(0, 32).ToList(), [L3(32 * MB, 0, 16), L3(96 * MB, 16, 16)], 32);

        Assert.Equal(CpuCoreLayout.VCacheMultiCcd, cpu.Layout);
        Assert.Equal(16u + 256, cpu.CpusFor(CpuAffinityMode.VCacheCores)![0].Id);
        Assert.Contains("on CPUs 16-31", cpu.Summary);
    }

    [Fact]
    public void SixCoreCcds_X3D_CountsCoresAndThreads()
    {
        var cpu = R9_7900X3D();
        Assert.Equal("3D V-Cache cores (6 cores, 12 threads)", cpu.OptionLabel(CpuAffinityMode.VCacheCores));
        Assert.Equal("Frequency cores (6 cores, 12 threads)", cpu.OptionLabel(CpuAffinityMode.FrequencyCores));
    }

    [Fact]
    public void SingleCcdX3D_HasNothingToChoose_ButSaysItHasTheCache()
    {
        var cpu = R7_9800X3D();

        Assert.Equal(CpuCoreLayout.VCacheSingleCcd, cpu.Layout);
        Assert.Empty(cpu.Options);
        Assert.Null(cpu.CpusFor(CpuAffinityMode.VCacheCores));
        Assert.Equal("every core on this CPU has the 3D V-Cache", cpu.WhyNoEffect(CpuAffinityMode.VCacheCores));
        Assert.Equal("3D V-Cache (96 MB L3) shared by every core.", cpu.Summary);
    }

    [Fact]
    public void DualCcdWithoutVCache_OffersOneCcd_AndAutoChangesNothing()
    {
        var cpu = R9_9950X();

        Assert.Equal(CpuCoreLayout.MultiCcd, cpu.Layout);
        Assert.Equal([CpuAffinityMode.Default, CpuAffinityMode.Auto, CpuAffinityMode.OneCcd], cpu.Options);
        Assert.Equal(CpuAffinityMode.Default, cpu.Recommended);
        Assert.Null(cpu.CpusFor(CpuAffinityMode.Auto));
        Assert.Equal(16, cpu.CpusFor(CpuAffinityMode.OneCcd)!.Count);
        Assert.Equal("Auto (no change on this CPU)", cpu.OptionLabel(CpuAffinityMode.Auto));
        Assert.Equal("2 CCDs, each with its own L3 cache (32 MB / 32 MB).", cpu.Summary);
    }

    [Fact]
    public void TwoUnevenCachesBothSmall_AreNotVCache()
    {
        // A laptop part with a full and a compact core complex: 16 MB beside 8 MB is uneven, but
        // neither is anywhere near 3D V-Cache.
        var cpu = CpuTopology.Build(Cpus(0, 8).Concat(Cpus(8, 16)).ToList(), [L3(16 * MB, 0, 8), L3(8 * MB, 8, 16)], 24);
        Assert.Equal(CpuCoreLayout.MultiCcd, cpu.Layout);
        Assert.Null(cpu.VCacheGroup);
    }

    [Fact]
    public void TwoVCacheCcds_AreAlike_SoOnlyOneCcdIsOffered()
    {
        var cpu = CpuTopology.Build(Cpus(0, 32).ToList(), [L3(96 * MB, 0, 16), L3(96 * MB, 16, 16)], 32);
        Assert.Equal(CpuCoreLayout.MultiCcd, cpu.Layout);
        Assert.Null(cpu.CpusFor(CpuAffinityMode.VCacheCores));
    }

    [Fact]
    public void WithoutCacheSizes_LastLevelCacheIndexStillSplitsTheCcds()
    {
        var cpu = CpuTopology.Build(Cpus(0, 16, llc: 0).Concat(Cpus(16, 16, llc: 1)).ToList(), [], 32);
        Assert.Equal(CpuCoreLayout.MultiCcd, cpu.Layout);
        Assert.Equal(2, cpu.CacheGroups.Count);
        Assert.Equal("2 CCDs, each with its own L3 cache.", cpu.Summary);
    }

    [Fact]
    public void UniformCpu_HasNoOptions_AndEveryChoiceDoesNothing()
    {
        var cpu = I7_9700K();

        Assert.Equal(CpuCoreLayout.Uniform, cpu.Layout);
        Assert.Empty(cpu.Options);
        Assert.Equal(string.Empty, cpu.Summary);
        foreach (var mode in Enum.GetValues<CpuAffinityMode>()) Assert.Null(cpu.CpusFor(mode));
        Assert.Equal("this CPU has no efficiency cores", cpu.WhyNoEffect(CpuAffinityMode.PerformanceCoresOnly));
    }

    [Fact]
    public void NothingRead_IsUniform()
    {
        var cpu = CpuTopology.Build([], [], 12);
        Assert.Equal(CpuCoreLayout.Uniform, cpu.Layout);
        Assert.Equal(12, cpu.LogicalProcessorCount);
        Assert.Empty(cpu.Options);
    }

    [Fact]
    public void AChoiceForAnotherKindOfCpu_DoesNothingHere()
    {
        // A library moved from a 7950X3D to an i9: its games keep V-Cache, which the i9 ignores.
        var intel = I9_12900K();
        Assert.Null(intel.CpusFor(CpuAffinityMode.VCacheCores));
        Assert.Equal("this CPU has no CCD with 3D V-Cache beside one without", intel.WhyNoEffect(CpuAffinityMode.VCacheCores));

        var amd = R9_7950X3D();
        Assert.Null(amd.CpusFor(CpuAffinityMode.PerformanceCoresOnly));
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3 }, "0-3")]
    [InlineData(new[] { 0, 1, 4, 5, 6 }, "0-1, 4-6")]
    [InlineData(new[] { 7 }, "7")]
    public void Ranges_ReadLikeTaskManager(int[] processors, string expected)
    {
        var cpus = processors.Select(p => new CpuSetEntry((uint)p, 0, (byte)p, (byte)p, 0, 0));
        Assert.Equal(expected, CpuTopology.Ranges(cpus));
    }

    [Fact]
    public void Menu_ShowsOnlyThisCpusChoices()
    {
        var x3d = CpuCoreMenu.For(R9_7950X3D());
        Assert.True(x3d.IsVisible && x3d.ShowAuto && x3d.ShowVCache && x3d.ShowFrequency);
        Assert.False(x3d.ShowPerformanceCores || x3d.ShowOneCcd);

        var intel = CpuCoreMenu.For(I9_12900K());
        Assert.True(intel.IsVisible && intel.ShowPerformanceCores);
        Assert.False(intel.ShowVCache || intel.ShowFrequency || intel.ShowOneCcd);

        Assert.False(CpuCoreMenu.For(R7_9800X3D()).IsVisible);
        Assert.False(CpuCoreMenu.For(I7_9700K()).IsVisible);
    }

    // ------------------------------------------------------------------ Edit Game

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private GameEditViewModel Edit(GameEntry game, CpuTopology topology) =>
        new(game, new[] { "Action" },
            new IconExtractorService(new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"))))
        {
            Topology = topology
        };

    [Fact]
    public void EditGame_ListsThisCpusChoices_WordedForIt() => Sta(() =>
    {
        var vm = Edit(new GameEntry { Name = "Hades" }, R9_7950X3D());

        Assert.True(vm.ShowCpuCores);
        Assert.Equal(
            ["Default (all cores, Windows decides)", "Auto (recommended: 3D V-Cache cores)", "3D V-Cache cores (8 cores, 16 threads)", "Frequency cores (8 cores, 16 threads)"],
            vm.CpuAffinityOptions.Select(o => o.Label));
    });

    [Fact]
    public void EditGame_KeepsAChoiceFromAnotherPc_AndSaysItDoesNothingHere() => Sta(() =>
    {
        var vm = Edit(new GameEntry { Name = "Hades", CpuAffinity = CpuAffinityMode.VCacheCores }, I9_12900K());

        var last = vm.CpuAffinityOptions[^1];
        Assert.Equal(CpuAffinityMode.VCacheCores, last.Value);
        Assert.Equal("3D V-Cache Cores (does nothing on this PC)", last.Label);
        Assert.Contains("Changes nothing on this PC", vm.CpuAffinityHint);
    });

    [Fact]
    public void EditGame_HidesCpuCores_WhereThereIsNothingToChoose() => Sta(() =>
    {
        Assert.False(Edit(new GameEntry { Name = "Hades" }, I7_9700K()).ShowCpuCores);
        Assert.True(Edit(new GameEntry { Name = "Hades", CpuAffinity = CpuAffinityMode.PerformanceCoresOnly }, I7_9700K()).ShowCpuCores);
    });

    [Fact]
    public void EditGame_SavesTheDelay_AndRefusesOneOutOfRange() => Sta(() =>
    {
        var game = new GameEntry { Name = "Hades" };
        var vm = Edit(game, R9_7950X3D());
        vm.CpuAffinity = CpuAffinityMode.VCacheCores;
        Assert.True(vm.ShowCpuCoresDelay);

        GameEditViewModel.EditField? refused = null;
        vm.ValidationFailed += f => refused = f;
        vm.CpuCoresDelaySeconds = "900";
        vm.SaveCommand.Execute(null);
        Assert.Equal(GameEditViewModel.EditField.CpuCoresDelay, refused);
        Assert.Equal(CpuAffinityMode.Default, game.CpuAffinity);

        vm.CpuCoresDelaySeconds = "30";
        vm.SaveCommand.Execute(null);
        Assert.Equal(CpuAffinityMode.VCacheCores, game.CpuAffinity);
        Assert.Equal(30, game.CpuCoresDelaySeconds);
    });

    [Fact]
    public void ApplyingADefaultOrNoOpChoice_LeavesTheProcessAlone()
    {
        // Default never reads the CPU at all; nothing here may throw or block.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        CpuTopologyService.ApplyCpuCores(self, new GameEntry { Name = "Hades", CpuAffinity = CpuAffinityMode.Default });
    }
}
