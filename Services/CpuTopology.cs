using System;
using System.Collections.Generic;
using System.Linq;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What the CPU Cores choice can do on this PC, worked out from how its cores are built.</summary>
public enum CpuCoreLayout
{
    /// <summary>Every core alike and one L3 cache, or nothing could be read: there is nothing to choose.</summary>
    Uniform,
    /// <summary>Performance and efficiency cores (Intel 12th gen and later, and similar designs).</summary>
    Hybrid,
    /// <summary>Two or more CCDs, one with 3D V-Cache (7950X3D, 9950X3D, 7900X3D, ...).</summary>
    VCacheMultiCcd,
    /// <summary>One CCD with 3D V-Cache (5800X3D, 7800X3D, 9800X3D): every core already has it.</summary>
    VCacheSingleCcd,
    /// <summary>Two or more CCDs with no V-Cache difference between them (7950X, 9950X, ...).</summary>
    MultiCcd
}

/// <summary>One logical processor as Windows' CPU Sets describe it.</summary>
public readonly record struct CpuSetEntry(uint Id, ushort Group, byte LogicalIndex, byte CoreIndex, byte LastLevelCacheIndex, byte EfficiencyClass);

/// <summary>One L3 cache: its size and which logical processors of one processor group share it.</summary>
public readonly record struct L3CacheInfo(long SizeBytes, ushort Group, ulong Mask);

/// <summary>The logical processors that share one L3 cache - a CCD on a Ryzen.</summary>
public sealed class CpuCacheGroup
{
    internal CpuCacheGroup(long l3Bytes, IReadOnlyList<CpuSetEntry> cpus)
    {
        L3Bytes = l3Bytes;
        Cpus = cpus;
    }

    /// <summary>0 when Windows did not say how big the cache is.</summary>
    public long L3Bytes { get; }
    public IReadOnlyList<CpuSetEntry> Cpus { get; }
    public int PhysicalCores => Cpus.Select(c => (c.Group, c.CoreIndex)).Distinct().Count();
    public int L3Megabytes => (int)Math.Round(L3Bytes / (1024.0 * 1024.0));
}

/// <summary>
/// How this PC's cores are built, and what each CPU Cores choice means on it. Built by
/// <see cref="Build"/> from Windows' own description of the cores, with no list of CPU models: the
/// V-Cache CCD is the one whose L3 cache is far bigger than the others', so a CPU released after
/// this build is recognized the same way.
/// </summary>
public sealed class CpuTopology
{
    /// <summary>An L3 this big or bigger is 3D V-Cache. Every X3D part has 96 MB; no ordinary CCD has more than 32.</summary>
    internal const long VCacheMinBytes = 64L * 1024 * 1024;

    private CpuTopology(int logicalProcessorCount, IReadOnlyList<CpuSetEntry> cpus, IReadOnlyList<CpuCacheGroup> cacheGroups,
        bool isHybrid, CpuCoreLayout layout, int vCacheGroup)
    {
        LogicalProcessorCount = logicalProcessorCount;
        Cpus = cpus;
        CacheGroups = cacheGroups;
        IsHybrid = isHybrid;
        Layout = layout;
        _vCacheGroup = vCacheGroup;

        byte maxClass = cpus.Count == 0 ? (byte)0 : cpus.Max(c => c.EfficiencyClass);
        PerformanceCpus = isHybrid ? cpus.Where(c => c.EfficiencyClass == maxClass).ToList() : cpus;
    }

    private readonly int _vCacheGroup;

    public int LogicalProcessorCount { get; }
    public IReadOnlyList<CpuSetEntry> Cpus { get; }
    /// <summary>One entry per L3 cache, in processor order (CCD 0 first).</summary>
    public IReadOnlyList<CpuCacheGroup> CacheGroups { get; }
    public bool IsHybrid { get; }
    public CpuCoreLayout Layout { get; }

    /// <summary>The performance cores of a hybrid CPU; every core on any other.</summary>
    public IReadOnlyList<CpuSetEntry> PerformanceCpus { get; }
    public int PerformanceCoreCount => PerformanceCpus.Count;
    public int PerformancePhysicalCores => PerformanceCpus.Select(c => (c.Group, c.CoreIndex)).Distinct().Count();
    public int EfficiencyPhysicalCores => Cpus.Except(PerformanceCpus).Select(c => (c.Group, c.CoreIndex)).Distinct().Count();

    /// <summary>The CCD with 3D V-Cache, on a CPU that has one beside others without it.</summary>
    public CpuCacheGroup? VCacheGroup => _vCacheGroup >= 0 ? CacheGroups[_vCacheGroup] : null;

    /// <summary>The cores outside the V-Cache CCD.</summary>
    public IReadOnlyList<CpuSetEntry> FrequencyCpus => VCacheGroup is { } v ? Cpus.Except(v.Cpus).ToList() : Array.Empty<CpuSetEntry>();

    /// <summary>The CPU Cores choices worth offering here, Default first. Empty when there is nothing to choose.</summary>
    public IReadOnlyList<CpuAffinityMode> Options => Layout switch
    {
        CpuCoreLayout.Hybrid => [CpuAffinityMode.Default, CpuAffinityMode.Auto, CpuAffinityMode.PerformanceCoresOnly],
        CpuCoreLayout.VCacheMultiCcd => [CpuAffinityMode.Default, CpuAffinityMode.Auto, CpuAffinityMode.VCacheCores, CpuAffinityMode.FrequencyCores],
        CpuCoreLayout.MultiCcd => [CpuAffinityMode.Default, CpuAffinityMode.Auto, CpuAffinityMode.OneCcd],
        _ => []
    };

    public bool HasOptions => Options.Count > 0;

    /// <summary>What Auto does here.</summary>
    public CpuAffinityMode Recommended => Layout switch
    {
        CpuCoreLayout.Hybrid => CpuAffinityMode.PerformanceCoresOnly,
        CpuCoreLayout.VCacheMultiCcd => CpuAffinityMode.VCacheCores,
        _ => CpuAffinityMode.Default
    };

    /// <summary>The choice actually carried out here: Auto resolved, anything else as it is.</summary>
    public CpuAffinityMode Resolve(CpuAffinityMode mode) => mode == CpuAffinityMode.Auto ? Recommended : mode;

    /// <summary>
    /// The cores <paramref name="mode"/> keeps a game on, or null when it changes nothing on this PC
    /// (Default, a choice for another kind of CPU, or one that would leave every core in).
    /// </summary>
    public IReadOnlyList<CpuSetEntry>? CpusFor(CpuAffinityMode mode)
    {
        IReadOnlyList<CpuSetEntry>? cpus = Resolve(mode) switch
        {
            CpuAffinityMode.PerformanceCoresOnly when IsHybrid => PerformanceCpus,
            CpuAffinityMode.VCacheCores when VCacheGroup != null => VCacheGroup.Cpus,
            CpuAffinityMode.FrequencyCores when VCacheGroup != null => FrequencyCpus,
            CpuAffinityMode.OneCcd when CacheGroups.Count > 1 => VCacheGroup?.Cpus ?? CacheGroups[0].Cpus,
            _ => null
        };
        return cpus == null || cpus.Count == 0 || cpus.Count == Cpus.Count ? null : cpus;
    }

    /// <summary>Why <paramref name="mode"/> does nothing here, for the log and Edit Game; null when it does something.</summary>
    public string? WhyNoEffect(CpuAffinityMode mode)
    {
        if (CpusFor(mode) != null) return null;
        return Resolve(mode) switch
        {
            CpuAffinityMode.Default => mode == CpuAffinityMode.Auto ? "this CPU is best left to Windows" : "Default leaves every core in",
            CpuAffinityMode.PerformanceCoresOnly => "this CPU has no efficiency cores",
            CpuAffinityMode.VCacheCores or CpuAffinityMode.FrequencyCores => Layout == CpuCoreLayout.VCacheSingleCcd
                ? "every core on this CPU has the 3D V-Cache"
                : "this CPU has no CCD with 3D V-Cache beside one without",
            CpuAffinityMode.OneCcd => "this CPU has only one CCD",
            _ => "it doesn't apply to this CPU"
        };
    }

    /// <summary>The menu wording of a choice, Title Case, as the library's right-click menu shows it.</summary>
    public static string MenuLabel(CpuAffinityMode mode) => mode switch
    {
        CpuAffinityMode.Default => "All Cores (Windows default)",
        CpuAffinityMode.Auto => "Auto (Recommended)",
        CpuAffinityMode.PerformanceCoresOnly => "Performance Cores Only",
        CpuAffinityMode.VCacheCores => "3D V-Cache Cores",
        CpuAffinityMode.FrequencyCores => "Frequency Cores",
        CpuAffinityMode.OneCcd => "First CCD Only",
        _ => mode.ToString()
    };

    /// <summary>The Edit Game wording of a choice, with what it means on this CPU.</summary>
    public string OptionLabel(CpuAffinityMode mode) => mode switch
    {
        CpuAffinityMode.Default => "Default (all cores, Windows decides)",
        CpuAffinityMode.Auto => Recommended switch
        {
            CpuAffinityMode.PerformanceCoresOnly => "Auto (recommended: performance cores)",
            CpuAffinityMode.VCacheCores => "Auto (recommended: 3D V-Cache cores)",
            _ => "Auto (no change on this CPU)"
        },
        CpuAffinityMode.PerformanceCoresOnly => IsHybrid
            ? $"Performance cores only ({PerformancePhysicalCores} cores, {PerformanceCoreCount} threads)"
            : "Performance cores only",
        CpuAffinityMode.VCacheCores => VCacheGroup is { } v
            ? $"3D V-Cache cores ({v.PhysicalCores} cores, {v.Cpus.Count} threads)"
            : "3D V-Cache cores",
        CpuAffinityMode.FrequencyCores => VCacheGroup != null
            ? $"Frequency cores ({FrequencyCpus.Select(c => (c.Group, c.CoreIndex)).Distinct().Count()} cores, {FrequencyCpus.Count} threads)"
            : "Frequency cores",
        CpuAffinityMode.OneCcd => CacheGroups.Count > 1
            ? $"First CCD only ({CacheGroups[0].PhysicalCores} cores, {CacheGroups[0].Cpus.Count} threads)"
            : "First CCD only",
        _ => mode.ToString()
    };

    /// <summary>One line on how the cores are built, for System › Hardware and the diagnostic report. Empty when there is nothing to say.</summary>
    public string Summary => Layout switch
    {
        CpuCoreLayout.Hybrid => $"Hybrid: {PerformancePhysicalCores} performance cores and {EfficiencyPhysicalCores} efficiency cores.",
        CpuCoreLayout.VCacheMultiCcd when VCacheGroup is { } v =>
            $"{CacheGroups.Count} CCDs: 3D V-Cache ({v.L3Megabytes} MB L3) on CPUs {Ranges(v.Cpus)}, "
            + string.Join(", ", CacheGroups.Where(g => !ReferenceEquals(g, v)).Select(g => $"{g.L3Megabytes} MB L3 on CPUs {Ranges(g.Cpus)}")) + ".",
        CpuCoreLayout.VCacheSingleCcd => $"3D V-Cache ({CacheGroups[0].L3Megabytes} MB L3) shared by every core.",
        CpuCoreLayout.MultiCcd => $"{CacheGroups.Count} CCDs, each with its own L3 cache"
            + (CacheGroups.All(g => g.L3Bytes > 0) ? $" ({string.Join(" / ", CacheGroups.Select(g => $"{g.L3Megabytes} MB"))})." : "."),
        _ => string.Empty
    };

    /// <summary>"0-15" or "0-7, 16-23": logical processor numbers as Task Manager counts them.</summary>
    internal static string Ranges(IEnumerable<CpuSetEntry> cpus)
    {
        var numbers = cpus.Select(c => c.Group * 64 + c.LogicalIndex).Distinct().OrderBy(n => n).ToList();
        var parts = new List<string>();
        for (int i = 0; i < numbers.Count;)
        {
            int start = numbers[i], end = start;
            while (i + 1 < numbers.Count && numbers[i + 1] == end + 1) { i++; end++; }
            parts.Add(start == end ? $"{start}" : $"{start}-{end}");
            i++;
        }
        return string.Join(", ", parts);
    }

    /// <summary>Nothing could be read: every core treated alike, and nothing offered.</summary>
    public static CpuTopology Unknown(int logicalProcessorCount) =>
        new(logicalProcessorCount, Array.Empty<CpuSetEntry>(), Array.Empty<CpuCacheGroup>(), false, CpuCoreLayout.Uniform, -1);

    /// <summary>
    /// Works the layout out from Windows' CPU Sets and its L3 caches. Pure, so it can be tested with
    /// the shape of any CPU. A logical processor is put with the L3 cache whose mask holds it; when
    /// Windows gave no cache sizes, CPU Sets' own LastLevelCacheIndex groups them instead, and no
    /// V-Cache can be told apart.
    /// </summary>
    public static CpuTopology Build(IReadOnlyList<CpuSetEntry> cpus, IReadOnlyList<L3CacheInfo> l3Caches, int logicalProcessorCount)
    {
        if (cpus.Count == 0) return Unknown(logicalProcessorCount);

        var byKey = new Dictionary<int, List<CpuSetEntry>>();
        foreach (var cpu in cpus)
        {
            int key = -1;
            for (int i = 0; i < l3Caches.Count; i++)
            {
                var cache = l3Caches[i];
                if (cache.Group == cpu.Group && cpu.LogicalIndex < 64 && (cache.Mask & (1UL << cpu.LogicalIndex)) != 0)
                {
                    key = i;
                    break;
                }
            }
            if (key < 0) key = 1000 + cpu.Group * 256 + cpu.LastLevelCacheIndex;
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<CpuSetEntry>();
            list.Add(cpu);
        }

        var groups = byKey
            .OrderBy(kv => kv.Value.Min(c => c.Group * 64 + c.LogicalIndex))
            .Select(kv => new CpuCacheGroup(kv.Key < 1000 ? l3Caches[kv.Key].SizeBytes : 0, kv.Value))
            .ToList();

        bool hybrid = cpus.Select(c => c.EfficiencyClass).Distinct().Count() > 1;

        int vCache = -1;
        CpuCoreLayout layout;
        if (hybrid)
        {
            // P-cores first: an Intel hybrid part has one L3, and on anything that is both, keeping a
            // game off the efficiency cores is the change that matters most.
            layout = CpuCoreLayout.Hybrid;
        }
        else if (groups.Count >= 2)
        {
            long largest = groups.Max(g => g.L3Bytes);
            var withLargest = groups.Select((g, i) => (g, i)).Where(x => x.g.L3Bytes == largest).ToList();
            bool oneStandsOut = largest >= VCacheMinBytes
                && withLargest.Count == 1
                && groups.All(g => ReferenceEquals(g, withLargest[0].g) || g.L3Bytes * 2 <= largest);
            if (oneStandsOut)
            {
                vCache = withLargest[0].i;
                layout = CpuCoreLayout.VCacheMultiCcd;
            }
            else
            {
                layout = CpuCoreLayout.MultiCcd;
            }
        }
        else
        {
            layout = groups[0].L3Bytes >= VCacheMinBytes ? CpuCoreLayout.VCacheSingleCcd : CpuCoreLayout.Uniform;
        }

        return new CpuTopology(Math.Max(logicalProcessorCount, cpus.Count), cpus, groups, hybrid, layout, vCache);
    }
}
