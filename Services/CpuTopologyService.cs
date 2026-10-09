using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The per-game CPU Cores choice (Edit Game › Performance). Reads how the cores are built from
/// GetSystemCpuSetInformation - EfficiencyClass, which is how Windows itself tells P-cores from
/// E-cores, and the CPU Set ids - and GetLogicalProcessorInformationEx's L3 caches, whose sizes
/// find a Ryzen's 3D V-Cache CCD. See <see cref="CpuTopology"/> for what each choice means.
///
/// <para>Applied with SetProcessDefaultCpuSets, not a hard affinity mask. CPU Sets are Windows'
/// soft form of affinity: the scheduler keeps the game's threads on those cores but stays free to
/// work with power management, and a game that sets its own thread affinity still can. A hard mask
/// is what crashed some games and what anti-cheat objects to most.</para>
/// </summary>
public static partial class CpuTopologyService
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint[] cpuSetIds, uint cpuSetIdCount);

    // SYSTEM_CPU_SET_INFORMATION (Type 0 = CpuSet), from winnt.h:
    //   DWORD Size(0), CPU_SET_INFORMATION_TYPE Type(4), then CpuSet: DWORD Id(8), WORD Group(12),
    //   BYTE LogicalProcessorIndex(14), BYTE CoreIndex(15), BYTE LastLevelCacheIndex(16),
    //   BYTE NumaNodeIndex(17), BYTE EfficiencyClass(18), ...
    private const int OffsetSize = 0;
    private const int OffsetType = 4;
    private const int OffsetId = 8;
    private const int OffsetGroup = 12;
    private const int OffsetLogicalIndex = 14;
    private const int OffsetCoreIndex = 15;
    private const int OffsetLastLevelCacheIndex = 16;
    private const int OffsetEfficiencyClass = 18;

    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: DWORD Relationship(0), DWORD Size(4), then for
    // RelationCache a CACHE_RELATIONSHIP at 8: BYTE Level(+0), BYTE Associativity(+1), WORD LineSize(+2),
    // DWORD CacheSize(+4), PROCESSOR_CACHE_TYPE Type(+8), BYTE Reserved[18](+12), WORD GroupCount(+30),
    // GROUP_AFFINITY GroupMasks[](+32): KAFFINITY Mask, WORD Group, WORD Reserved[3].
    private const int RelationCache = 2;
    private const int OffsetCacheLevel = 8;
    private const int OffsetCacheSize = 12;
    private const int OffsetCacheGroupCount = 38;
    private const int OffsetCacheGroupMasks = 40;
    private static readonly int GroupAffinitySize = IntPtr.Size + 8;

    private const uint ProcessSetLimitedInformation = 0x2000;

    /// <summary>The longest "wait before applying" Edit Game accepts.</summary>
    public const int MaxDelaySeconds = 300;

    /// <summary>
    /// After the first pass, how often, and for how long, to look again for processes the game has
    /// started since - the game a launcher starts when Play is pressed in it, a renderer, a crash
    /// handler - and keep them on the same cores. CPU Sets aren't passed on to a child process the
    /// way an affinity mask is, so each one has to be set. Stops early once the game and everything
    /// it started have exited.
    /// </summary>
    private static readonly TimeSpan FollowUpInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FollowUpWindow = TimeSpan.FromMinutes(10);

    private static CpuTopology? _cached;

    public static CpuTopology GetTopology()
    {
        if (_cached is { } cached) return cached;
        var topology = Query();
        _cached = topology;
        return topology;
    }

    private static CpuTopology Query()
    {
        int logical = Environment.ProcessorCount;
        try
        {
            var cpus = ReadCpuSets();
            var caches = ReadL3Caches();
            var topology = CpuTopology.Build(cpus, caches, logical);
            if (LoggingService.IsVerboseEnabled)
            {
                LoggingService.Verbose("CpuTopology", $"{cpus.Count} CPU Sets, {caches.Count} L3 cache(s) "
                    + $"({string.Join(", ", caches.Select(c => $"{c.SizeBytes / (1024 * 1024)} MB, group {c.Group} mask 0x{c.Mask:X}"))}); "
                    + $"layout {topology.Layout}. {topology.Summary}");
            }
            return topology;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("CpuTopology", $"Reading the CPU layout failed: {ex.Message}; CPU Cores options are hidden.");
            return CpuTopology.Unknown(logical);
        }
    }

    private static List<CpuSetEntry> ReadCpuSets()
    {
        var entries = new List<CpuSetEntry>();
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
        if (needed == 0)
        {
            LoggingService.Verbose("CpuTopology", "GetSystemCpuSetInformation returned nothing.");
            return entries;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, needed, out uint returned, IntPtr.Zero, 0))
            {
                LoggingService.Verbose("CpuTopology", $"GetSystemCpuSetInformation failed (error {Marshal.GetLastPInvokeError()}).");
                return entries;
            }

            int offset = 0;
            while (offset + 8 <= returned)
            {
                int size = Marshal.ReadInt32(buffer, offset + OffsetSize);
                if (size <= 0) break;
                int type = Marshal.ReadInt32(buffer, offset + OffsetType);
                if (type == 0 && offset + OffsetEfficiencyClass < returned)
                {
                    entries.Add(new CpuSetEntry(
                        Id: (uint)Marshal.ReadInt32(buffer, offset + OffsetId),
                        Group: (ushort)Marshal.ReadInt16(buffer, offset + OffsetGroup),
                        LogicalIndex: Marshal.ReadByte(buffer, offset + OffsetLogicalIndex),
                        CoreIndex: Marshal.ReadByte(buffer, offset + OffsetCoreIndex),
                        LastLevelCacheIndex: Marshal.ReadByte(buffer, offset + OffsetLastLevelCacheIndex),
                        EfficiencyClass: Marshal.ReadByte(buffer, offset + OffsetEfficiencyClass)));
                }
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return entries;
    }

    private static List<L3CacheInfo> ReadL3Caches()
    {
        var caches = new List<L3CacheInfo>();
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationCache, IntPtr.Zero, ref length);
        if (length == 0)
        {
            LoggingService.Verbose("CpuTopology", $"GetLogicalProcessorInformationEx gave no cache list (error {Marshal.GetLastPInvokeError()}); V-Cache can't be told apart.");
            return caches;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationCache, buffer, ref length))
            {
                LoggingService.Verbose("CpuTopology", $"GetLogicalProcessorInformationEx failed (error {Marshal.GetLastPInvokeError()}); V-Cache can't be told apart.");
                return caches;
            }

            int offset = 0;
            while (offset + 8 <= length)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                if (relationship == RelationCache && Marshal.ReadByte(buffer, offset + OffsetCacheLevel) == 3)
                {
                    long cacheSize = (uint)Marshal.ReadInt32(buffer, offset + OffsetCacheSize);
                    // Windows before 20H2 left GroupCount zero and filled the single GroupMask.
                    int groupCount = Math.Max(1, (int)(ushort)Marshal.ReadInt16(buffer, offset + OffsetCacheGroupCount));
                    for (int g = 0; g < groupCount; g++)
                    {
                        int at = offset + OffsetCacheGroupMasks + g * GroupAffinitySize;
                        if (at + GroupAffinitySize > offset + size) break;
                        ulong mask = (ulong)(long)Marshal.ReadIntPtr(buffer, at);
                        ushort group = (ushort)Marshal.ReadInt16(buffer, at + IntPtr.Size);
                        caches.Add(new L3CacheInfo(cacheSize, group, mask));
                    }
                }
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return caches;
    }

    /// <summary>
    /// Keeps the game's live process, and every process it starts, on the cores its CPU Cores
    /// choice names - after the game's "wait before applying" delay, if it has one, for anti-cheat
    /// titles that refuse a change made while they start. Returns at once; the work runs in the
    /// background. A choice that does nothing on this CPU is a silent no-op, so the setting is safe
    /// to leave on for a library that moves between PCs. Nothing to put back afterwards: CPU Sets
    /// end with the process.
    /// </summary>
    /// <param name="startedByTrayTrigger">
    /// The process is the one TrayTrigger's own launch started, so the Process object already holds
    /// the handle the launch returned. Only then is that handle used: asking any other Process object
    /// for its handle would open a full-access handle to the game and hold it for the whole session,
    /// which is exactly what anti-cheat looks for.
    /// </param>
    public static void ApplyCpuCores(Process process, GameEntry game, bool startedByTrayTrigger = false)
    {
        if (game.CpuAffinity == CpuAffinityMode.Default) return;

        var topology = GetTopology();
        var cpus = topology.CpusFor(game.CpuAffinity);
        if (cpus == null)
        {
            LoggingService.Verbose("CpuTopology", $"'{game.Name}' is set to {CpuTopology.MenuLabel(game.CpuAffinity)}, which changes nothing on this PC: {topology.WhyNoEffect(game.CpuAffinity)}. Leaving its cores alone.");
            LaunchRecord.Note(game.Id, LaunchRecord.Game, $"CPU Cores ({CpuTopology.MenuLabel(game.CpuAffinity)}) changes nothing on this PC, cores left alone");
            return;
        }

        int pid;
        DateTime startedUtc;
        try
        {
            pid = process.Id;
            startedUtc = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex)
        {
            LoggingService.Warn("CpuTopology", $"Could not set the CPU cores for '{game.Name}': its process could not be read ({ex.Message}).");
            LaunchRecord.Note(game.Id, LaunchRecord.Game, "CPU Cores couldn't be set: the game's process couldn't be read");
            return;
        }

        // The handle TrayTrigger launched the game with. A game run as administrator refuses to be
        // opened by a TrayTrigger that isn't, but the launch handle was granted full rights - the old
        // affinity mask was set through it - so it is the way in for that one process.
        SafeProcessHandle? launchHandle = null;
        if (startedByTrayTrigger)
        {
            try { launchHandle = process.SafeHandle; }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // No handle of our own to fall back on: the cores are set by id, as for every other process.
            }
        }

        uint[] ids = cpus.Select(c => c.Id).ToArray();
        string what = Describe(topology, game.CpuAffinity, cpus);
        int delay = Math.Clamp(game.CpuCoresDelaySeconds, 0, MaxDelaySeconds);
        string name = game.Name;
        string gameId = game.Id;

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > 0)
                {
                    LoggingService.Verbose("CpuTopology", $"Waiting {delay}s before keeping '{name}' on {what}.");
                    await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
                }
                var done = new HashSet<int>();
                bool kept = ApplyToTree(pid, startedUtc, launchHandle, ids, name, what, done, firstPass: true);
                LaunchRecord.Note(gameId, LaunchRecord.Game, kept
                    ? $"kept on {what}{(delay > 0 ? $" after {delay}s" : string.Empty)}"
                    : "CPU Cores couldn't be set (the game refused, or had exited)");
                if (!kept) return;
                var until = DateTime.UtcNow + FollowUpWindow;
                while (DateTime.UtcNow < until)
                {
                    await Task.Delay(FollowUpInterval).ConfigureAwait(false);
                    if (!ApplyToTree(pid, startedUtc, launchHandle, ids, name, what, done, firstPass: false)) return;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn("CpuTopology", $"Setting the CPU cores for '{name}' failed: {ex.Message}");
                LaunchRecord.Note(gameId, LaunchRecord.Game, "CPU Cores couldn't be set");
            }
        });
    }

    /// <summary>"the 8 performance cores (CPUs 0-15)", for the log.</summary>
    private static string Describe(CpuTopology topology, CpuAffinityMode mode, IReadOnlyList<CpuSetEntry> cpus)
    {
        int cores = cpus.Select(c => (c.Group, c.CoreIndex)).Distinct().Count();
        string kind = topology.Resolve(mode) switch
        {
            CpuAffinityMode.PerformanceCoresOnly => "performance cores",
            CpuAffinityMode.VCacheCores => "3D V-Cache cores",
            CpuAffinityMode.FrequencyCores => "frequency cores",
            CpuAffinityMode.OneCcd => "cores of the first CCD",
            _ => "cores"
        };
        string auto = mode == CpuAffinityMode.Auto ? ", picked by Auto" : "";
        return $"the {cores} {kind} (CPUs {CpuTopology.Ranges(cpus)}){auto}";
    }

    private enum SetResult { Done, Gone, Refused }

    /// <summary>
    /// One pass over the game's process and everything it has started. False when there is no
    /// point in another: the game refused, or it has exited and so has everything it started.
    /// </summary>
    private static bool ApplyToTree(int rootPid, DateTime rootStartedUtc, SafeProcessHandle? launchHandle, uint[] ids, string gameName, string what, HashSet<int> done, bool firstPass)
    {
        if (!done.Contains(rootPid))
        {
            var result = TrySet(rootPid, rootStartedUtc, ids, out string? error, launchHandle);
            if (result == SetResult.Gone)
            {
                LoggingService.Verbose("CpuTopology", $"'{gameName}' (PID {rootPid}) exited before its cores could be set.");
                return false;
            }
            if (result == SetResult.Refused)
            {
                LoggingService.Warn("CpuTopology", $"Could not keep '{gameName}' (PID {rootPid}) on {what}: {error}. "
                    + "A game running as administrator, or protected by anti-cheat, can refuse it; a delay in Edit Game › Performance helps some anti-cheat titles.");
                return false;
            }
            done.Add(rootPid);
        }

        int children = 0;
        var descendants = ProcessTree.Descendants(rootPid, rootStartedUtc, "CpuTopology");
        if (!firstPass && descendants.Count == 0 && !ProcessHandles.IsRunning(rootPid, rootStartedUtc))
        {
            LoggingService.Verbose("CpuTopology", $"'{gameName}' (PID {rootPid}) and everything it started have exited; no more looking for new processes.");
            return false;
        }
        foreach (var (pid, childName, started) in descendants)
        {
            if (done.Contains(pid)) continue;
            var result = TrySet(pid, started, ids, out string? error);
            if (result == SetResult.Done)
            {
                done.Add(pid);
                children++;
            }
            else if (result == SetResult.Refused)
            {
                // Once, then left alone: a child that refuses now refuses on the follow-up too.
                done.Add(pid);
                LoggingService.Verbose("CpuTopology", $"'{gameName}': {childName} (PID {pid}) refused its cores: {error}.");
            }
        }

        if (firstPass)
        {
            LoggingService.Info("CpuTopology", $"Kept '{gameName}' (PID {rootPid}{(children > 0 ? $" and {children} process(es) it started" : "")}) on {what}.");
        }
        else if (children > 0)
        {
            LoggingService.Verbose("CpuTopology", $"Kept {children} more process(es) '{gameName}' started since on the same cores.");
        }
        return true;
    }

    /// <summary>
    /// Sets the process's CPU Sets. <paramref name="launchHandle"/>, for the game itself, is tried when
    /// Windows refuses to open it by id - a game run as administrator.
    /// </summary>
    private static SetResult TrySet(int pid, DateTime startedUtc, uint[] ids, out string? error, SafeProcessHandle? launchHandle = null)
    {
        var opened = ProcessHandles.OpenSame(pid, startedUtc, ProcessSetLimitedInformation, out var handle, out error);
        if (opened == ProcessHandles.OpenResult.Gone) return SetResult.Gone;
        if (opened == ProcessHandles.OpenResult.Refused)
        {
            if (launchHandle == null) return SetResult.Refused;
            try
            {
                if (launchHandle.IsClosed || ProcessHandles.HasExited(launchHandle)) return SetResult.Gone;
                if (SetProcessDefaultCpuSets(launchHandle, ids, (uint)ids.Length)) return SetResult.Done;
                error = new Win32Exception(Marshal.GetLastPInvokeError()).Message.TrimEnd('.');
                return SetResult.Refused;
            }
            catch (ObjectDisposedException)
            {
                // The session ended and closed its handle meanwhile: the game has gone.
                return SetResult.Gone;
            }
        }

        using (handle)
        {
            if (SetProcessDefaultCpuSets(handle!, ids, (uint)ids.Length)) return SetResult.Done;
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message.TrimEnd('.');
            return SetResult.Refused;
        }
    }
}
