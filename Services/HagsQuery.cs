using System;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// The real Hardware-Accelerated GPU Scheduling state, straight from the display driver via the
/// same kernel-mode-thunk query the Windows Settings page uses (D3DKMTQueryAdapterInfo with
/// KMTQAITYPE_WDDM_2_7_CAPS). The HwSchMode registry value is only the user's *request*: on a
/// GPU/driver without support Windows ignores it, and when the value is absent the driver default
/// (often "on" for recent NVIDIA/AMD drivers on Windows 11) applies - so the registry alone gets
/// both "unsupported but shown as on" and "on by default but shown as off" wrong.
/// </summary>
public static partial class HagsQuery
{
    public readonly record struct HagsState(bool Queried, bool Supported, bool Enabled, bool EnabledByDefault);

    private const uint KMTQAITYPE_WDDM_2_7_CAPS = 70;
    private const uint HwSchSupportedBit = 0x1;
    private const uint HwSchEnabledBit = 0x2;
    private const uint HwSchEnabledByDefaultBit = 0x4;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public uint hAdapter;
        public ulong AdapterLuid;
        public uint VidPnSourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public uint Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int D3DKMTOpenAdapterFromGdiDisplayName(ref D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    /// <summary>State for the adapter driving the primary display (falls back to the first attached one).</summary>
    public static HagsState Query()
    {
        string? deviceName = FindPrimaryDisplayName();
        if (deviceName == null) return default;

        var open = new D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME { DeviceName = deviceName };
        try
        {
            if (D3DKMTOpenAdapterFromGdiDisplayName(ref open) != 0 || open.hAdapter == 0) return default;

            try
            {
                IntPtr caps = Marshal.AllocHGlobal(sizeof(uint));
                try
                {
                    Marshal.WriteInt32(caps, 0);
                    var query = new D3DKMT_QUERYADAPTERINFO
                    {
                        hAdapter = open.hAdapter,
                        Type = KMTQAITYPE_WDDM_2_7_CAPS,
                        pPrivateDriverData = caps,
                        PrivateDriverDataSize = sizeof(uint)
                    };
                    if (D3DKMTQueryAdapterInfo(ref query) != 0) return default;

                    uint value = (uint)Marshal.ReadInt32(caps);
                    return new HagsState(
                        Queried: true,
                        Supported: (value & HwSchSupportedBit) != 0,
                        Enabled: (value & HwSchEnabledBit) != 0,
                        EnabledByDefault: (value & HwSchEnabledByDefaultBit) != 0);
                }
                finally
                {
                    Marshal.FreeHGlobal(caps);
                }
            }
            finally
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("HagsQuery", $"D3DKMT query failed: {ex.Message}");
            return default;
        }
    }

    private static string? FindPrimaryDisplayName()
    {
        try
        {
            string? firstAttached = null;
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
                if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) return dd.DeviceName;
                firstAttached ??= dd.DeviceName;
            }
            return firstAttached ?? @"\\.\DISPLAY1";
        }
        catch
        {
            return @"\\.\DISPLAY1";
        }
    }
}
