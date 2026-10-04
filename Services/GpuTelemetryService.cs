using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TrayTrigger.Services;

/// <summary>Live load and video memory in use for one GPU, keyed by its adapter LUID.</summary>
public readonly record struct GpuLiveSample(int LoadPercent, double DedicatedUsedGigabytes);

/// <summary>
/// GPU load and video memory in use, from Windows' own GPU performance counters - the numbers Task
/// Manager's GPU graphs show, read through PDH with no administrator rights and no vendor tools.
///
/// <para>Load is Task Manager's: per adapter, each engine type's utilisation summed over every
/// process, and the busiest engine type is the GPU's load. The utilisation counter is a rate, so
/// the first <see cref="Sample"/> after creation has nothing to compare with and returns no load;
/// from the second on it does.</para>
/// </summary>
public sealed class GpuTelemetryService : IDisposable
{
    private IntPtr _query;
    private IntPtr _engineCounter;
    private IntPtr _memoryCounter;
    private bool _available;
    private readonly object _lock = new();

    public GpuTelemetryService()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            // English names, so it works whatever language Windows is in.
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engineCounter) != 0) return;
            if (PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memoryCounter) != 0) return;
            PdhCollectQueryData(_query);
            _available = true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfo", $"GPU performance counters unavailable: {ex.Message}");
        }
        if (!_available)
            LoggingService.Info("SystemInfo", "GPU performance counters unavailable; the GPU card shows no live load.");
    }

    /// <summary>Load and memory per adapter LUID. Empty when the counters aren't available.</summary>
    public IReadOnlyDictionary<long, GpuLiveSample> Sample()
    {
        lock (_lock)
        {
            if (!_available || _query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
                return new Dictionary<long, GpuLiveSample>();

            var engines = ReadArray(_engineCounter);
            var memory = ReadArray(_memoryCounter);
            return Aggregate(engines, memory);
        }
    }

    /// <summary>
    /// Folds the raw counter instances into one reading per adapter. Engine instances are named
    /// "pid_1234_luid_0x00000000_0x0000D1F3_phys_0_eng_0_engtype_3D"; memory instances
    /// "luid_0x00000000_0x0000D1F3_phys_0".
    /// </summary>
    internal static Dictionary<long, GpuLiveSample> Aggregate(
        IEnumerable<KeyValuePair<string, double>> engines, IEnumerable<KeyValuePair<string, double>> memory)
    {
        var byEngineType = new Dictionary<(long Luid, string Type), double>();
        foreach (var (name, value) in engines)
        {
            if (!TryParseLuid(name, out long luid)) continue;
            var type = EngineType.Match(name);
            var key = (luid, type.Success ? type.Groups[1].Value : "");
            byEngineType[key] = byEngineType.GetValueOrDefault(key) + value;
        }

        var load = new Dictionary<long, double>();
        foreach (var ((luid, _), sum) in byEngineType)
            load[luid] = Math.Max(load.GetValueOrDefault(luid), sum);

        var used = new Dictionary<long, double>();
        foreach (var (name, bytes) in memory)
        {
            if (TryParseLuid(name, out long luid))
                used[luid] = used.GetValueOrDefault(luid) + bytes;
        }

        var result = new Dictionary<long, GpuLiveSample>();
        foreach (long luid in load.Keys.Concat(used.Keys).Distinct())
        {
            int percent = (int)Math.Round(Math.Clamp(load.GetValueOrDefault(luid), 0, 100));
            result[luid] = new GpuLiveSample(percent, Math.Round(used.GetValueOrDefault(luid) / (1024.0 * 1024.0 * 1024.0), 1));
        }
        return result;
    }

    private static readonly Regex Luid = new(@"luid_0x([0-9a-fA-F]+)_0x([0-9a-fA-F]+)", RegexOptions.CultureInvariant);
    private static readonly Regex EngineType = new(@"engtype_([^_]+)$", RegexOptions.CultureInvariant);

    /// <summary>The LUID in a counter instance name, packed as DXGI's (high &lt;&lt; 32 | low).</summary>
    internal static bool TryParseLuid(string instance, out long luid)
    {
        luid = 0;
        var m = Luid.Match(instance);
        if (!m.Success) return false;
        if (!uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high)) return false;
        if (!uint.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low)) return false;
        luid = ((long)(int)high << 32) | low;
        return true;
    }

    private static List<KeyValuePair<string, double>> ReadArray(IntPtr counter)
    {
        var values = new List<KeyValuePair<string, double>>();
        uint size = 0;
        int status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return values;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer) != 0)
                return values;

            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * itemSize);
                // A process that exited between the two samples reports an invalid status: skip it.
                if (item.FmtValue.CStatus != 0) continue;
                string? name = Marshal.PtrToStringUni(item.szName);
                if (name != null)
                    values.Add(new(name, item.FmtValue.doubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return values;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
            _available = false;
        }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
