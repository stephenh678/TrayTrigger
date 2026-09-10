using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Hybrid-CPU awareness for the per-game "Performance cores only" affinity option. Uses
/// GetSystemCpuSetInformation (Windows 10 1709+), whose EfficiencyClass field is how Windows
/// itself distinguishes P-cores (higher class) from E-cores (lower class) on Intel 12th gen+ and
/// similar designs. On a homogeneous CPU every logical processor has the same class, so the
/// "performance" mask is simply every core and the option is a no-op.
/// </summary>
public static partial class CpuTopologyService
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    // SYSTEM_CPU_SET_INFORMATION layout (Type=0 -> CpuSet union member), from winnt.h.
    private const int OffsetSize = 0;
    private const int OffsetType = 4;
    // CpuSet: ULONG Id(8), USHORT Group(12), BYTE LogicalProcessorIndex(14), BYTE CoreIndex(15),
    //         BYTE LastLevelCacheIndex(16), BYTE NumaNodeIndex(17), BYTE EfficiencyClass(18), ...
    private const int OffsetGroup = 12;
    private const int OffsetLogicalIndex = 14;
    private const int OffsetEfficiencyClass = 18;

    public readonly record struct CpuTopology(bool IsHybrid, int LogicalProcessorCount, int PerformanceCoreCount, ulong PerformanceCoreMask);

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
        ulong allMask = logical >= 64 ? ulong.MaxValue : (1UL << logical) - 1;
        var fallback = new CpuTopology(false, logical, logical, allMask);

        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0) return fallback;

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, needed, out uint returned, IntPtr.Zero, 0)) return fallback;

                byte maxClass = 0;
                var entries = new System.Collections.Generic.List<(ushort Group, byte Index, byte Class)>();
                int offset = 0;
                while (offset + 4 <= returned)
                {
                    int size = Marshal.ReadInt32(buffer, offset + OffsetSize);
                    if (size <= 0) break;
                    int type = Marshal.ReadInt32(buffer, offset + OffsetType);
                    if (type == 0 && offset + OffsetEfficiencyClass < returned)
                    {
                        ushort group = (ushort)Marshal.ReadInt16(buffer, offset + OffsetGroup);
                        byte index = Marshal.ReadByte(buffer, offset + OffsetLogicalIndex);
                        byte cls = Marshal.ReadByte(buffer, offset + OffsetEfficiencyClass);
                        entries.Add((group, index, cls));
                        if (cls > maxClass) maxClass = cls;
                    }
                    offset += size;
                }

                if (entries.Count == 0) return fallback;

                bool hybrid = entries.Exists(e => e.Class != maxClass);
                ulong mask = 0;
                int pCount = 0;
                foreach (var (group, index, cls) in entries)
                {
                    // Process affinity masks are per processor group; only group 0 is addressable
                    // through Process.ProcessorAffinity, which covers every consumer machine.
                    if (group != 0 || index >= 64) continue;
                    if (cls == maxClass)
                    {
                        mask |= 1UL << index;
                        pCount++;
                    }
                }

                if (mask == 0) return fallback;
                return new CpuTopology(hybrid, entries.Count, pCount, mask);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("CpuTopology", $"GetSystemCpuSetInformation failed: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// Applies the game's affinity choice to its live process. Silent no-op for
    /// <see cref="CpuAffinityMode.Default"/> and for non-hybrid CPUs, so the option is safe to
    /// leave on for a library that moves between machines.
    /// </summary>
    public static void ApplyAffinity(Process process, GameEntry game)
    {
        if (game.CpuAffinity == CpuAffinityMode.Default) return;

        var topology = GetTopology();
        if (!topology.IsHybrid)
        {
            LoggingService.Verbose("CpuTopology", $"'{game.Name}' asks for performance cores only, but this CPU is not hybrid - leaving affinity alone.");
            return;
        }

        try
        {
            process.ProcessorAffinity = (IntPtr)(long)topology.PerformanceCoreMask;
            LoggingService.Info("CpuTopology", $"Pinned '{game.Name}' (PID {process.Id}) to {topology.PerformanceCoreCount} performance cores (mask 0x{topology.PerformanceCoreMask:X}).");
        }
        catch (Exception ex)
        {
            // Elevated/anti-cheat protected processes refuse PROCESS_SET_INFORMATION from a
            // normal-integrity caller; log and move on rather than fail the launch.
            LoggingService.Warn("CpuTopology", $"Could not set affinity for '{game.Name}': {ex.Message}");
        }
    }
}
