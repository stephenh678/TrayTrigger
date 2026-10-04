using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Each active display as the Settings app knows it: the monitor's own name, the mode it runs in,
/// the fastest refresh rate it offers at that resolution, and whether HDR is on. Read through the
/// same Connecting and Configuring Displays API <see cref="HdrControlService"/> uses, plus
/// EnumDisplaySettings for the modes. No administrator rights needed.
/// </summary>
public partial class SystemInfoService
{
    /// <summary>The displays, primary first; null when the display configuration can't be read,
    /// so the caller can fall back to the plain mode query.</summary>
    private List<DisplayHardwareInfo>? GetDisplaysFromDisplayConfig()
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
            return null;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return null;

        var hdr = new Dictionary<(uint, int, uint), HdrControlService.DisplayColorState>();
        try
        {
            foreach (var state in HdrControlService.GetDisplayStates())
                hdr[(state.AdapterId.LowPart, state.AdapterId.HighPart, state.TargetId)] = state;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemInfo", $"Could not read the displays' HDR state: {ex.Message}");
        }

        var list = new List<DisplayHardwareInfo>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pathCount; i++)
        {
            var source = paths[i].sourceInfo;
            var target = paths[i].targetInfo;

            var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), source.adapterLowPart, source.adapterHighPart, source.id)
            };
            if (DisplayConfigGetDeviceInfo(ref sourceName) != 0 || string.IsNullOrEmpty(sourceName.viewGdiDeviceName))
                continue;
            // A cloned (duplicated) display shares its source with another path; it's one desktop.
            if (!seenSources.Add(sourceName.viewGdiDeviceName))
                continue;

            var current = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsW(sourceName.viewGdiDeviceName, ENUM_CURRENT_SETTINGS, ref current))
                continue;

            var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), target.adapterLowPart, target.adapterHighPart, target.id)
            };
            string monitorName = DisplayConfigGetDeviceInfo(ref targetName) == 0 ? targetName.monitorFriendlyDeviceName?.Trim() ?? "" : "";

            hdr.TryGetValue((target.adapterLowPart, target.adapterHighPart, target.id), out var color);
            bool isPrimary = current.dmPositionX == 0 && current.dmPositionY == 0;

            list.Add(new DisplayHardwareInfo
            {
                MonitorName = monitorName,
                Width = (int)current.dmPelsWidth,
                Height = (int)current.dmPelsHeight,
                RefreshRateHz = (int)current.dmDisplayFrequency,
                MaxRefreshRateHz = MaxRefreshAt(sourceName.viewGdiDeviceName, current.dmPelsWidth, current.dmPelsHeight),
                IsPrimary = isPrimary,
                HdrSupported = color.Supported,
                HdrEnabled = color.Supported && color.Enabled
            });
        }

        if (list.Count == 0)
            return null;

        list = list.OrderByDescending(d => d.IsPrimary).ToList();
        for (int i = 0; i < list.Count; i++)
            list[i].DeviceName = i == 0 ? "Primary display" : $"Display {i + 1}";

        foreach (var d in list)
            LoggingService.Verbose("SystemInfo", $"Display '{d.Title}': {d.Width}x{d.Height} @ {d.RefreshRateHz} Hz (offers up to {d.MaxRefreshRateHz} Hz), HDR {(d.HdrSupported ? (d.HdrEnabled ? "on" : "off") : "not supported")}.");
        return list;
    }

    /// <summary>The fastest progressive mode the display lists at this resolution.</summary>
    private static int MaxRefreshAt(string deviceName, uint width, uint height)
    {
        int max = 0;
        var mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        for (int i = 0; EnumDisplaySettingsW(deviceName, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height && (mode.dmDisplayFlags & DM_INTERLACED) == 0)
                max = Math.Max(max, (int)mode.dmDisplayFrequency);
            mode = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        }
        return max;
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size, uint adapterLow, int adapterHigh, uint id) => new()
    {
        type = type,
        size = (uint)size,
        adapterLowPart = adapterLow,
        adapterHighPart = adapterHigh,
        id = id
    };

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DM_INTERLACED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public uint adapterLowPart;
        public int adapterHighPart;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public uint adapterLowPart;
        public int adapterHighPart;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public uint adapterLowPart;
        public int adapterHighPart;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // A 64-byte tagged union this code never reads; QueryDisplayConfig only needs the room.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}
