using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// Toggles Windows' native "Use HDR" display setting (Settings &gt; System &gt; Display &gt; HDR)
/// via the same Connecting and Configuring Displays (CCD) API the Settings app itself uses -
/// DisplayConfigGetDeviceInfo/DisplayConfigSetDeviceInfo with the DISPLAYCONFIG_(GET|SET)_
/// ADVANCED_COLOR_(INFO|STATE) device-info types. There is no registry value that turns HDR on:
/// it's a live per-target color-pipeline mode negotiated with the monitor over EDID, not a static
/// setting, so it has to go through this API.
/// </summary>
public static class HdrControlService
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>IsWcg is only ever true on the 24H2+ path (see s_useHdrState2Api) - the legacy
    /// API has no way to distinguish WCG from HDR, so it's always false there.</summary>
    public readonly record struct DisplayColorState(LUID AdapterId, uint TargetId, bool Supported, bool Enabled, bool IsWcg = false);

    // Windows 11 24H2 (build 26100) shipped "Automatically manage color for apps" (Auto Color
    // Management). With it on, the legacy advancedColorEnabled bit reflects whether the
    // pipeline is *currently rendering* in an advanced-color mode - which ACM can flip on for
    // any app - not whether the user's HDR toggle is actually on. Every third-party HDR toggle
    // tool (HDRTray, LenovoLegionToolkit, Kodi) hit this and had to move to the new
    // GET_ADVANCED_COLOR_INFO_2 / SET_HDR_STATE device-info types, which report the real
    // per-display color mode (activeColorMode) instead. Below build 26100 those types don't
    // exist, so the legacy API is kept as the fallback.
    private static readonly bool s_useHdrState2Api = Environment.OSVersion.Version.Build >= 26100;

    /// <summary>Advanced-color state for every active display target, including non-HDR-capable ones (Supported=false).</summary>
    public static List<DisplayColorState> GetDisplayStates()
    {
        var result = new List<DisplayColorState>();

        LoggingService.Verbose("HdrControlService", $"Reading HDR state via {(s_useHdrState2Api ? "GET_ADVANCED_COLOR_INFO_2 (24H2+)" : "legacy GET_ADVANCED_COLOR_INFO")} API.");

        int bufferSizesResult = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (bufferSizesResult != ERROR_SUCCESS)
        {
            LoggingService.Warn("HdrControlService", $"GetDisplayConfigBufferSizes failed (error {bufferSizesResult}); no display states will be reported.");
            return result;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        int queryResult = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (queryResult != ERROR_SUCCESS)
        {
            LoggingService.Warn("HdrControlService", $"QueryDisplayConfig failed (error {queryResult}); no display states will be reported.");
            return result;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var target = paths[i].targetInfo;

            if (s_useHdrState2Api)
            {
                var colorInfo2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = (uint)DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(),
                        adapterId = target.adapterId,
                        id = target.id
                    }
                };

                int getInfo2Result = DisplayConfigGetDeviceInfo2(ref colorInfo2);
                if (getInfo2Result != ERROR_SUCCESS)
                {
                    LoggingService.Warn("HdrControlService", $"DisplayConfigGetDeviceInfo (advanced color v2) failed for target {target.id} (error {getInfo2Result}); skipping this display.");
                    continue;
                }

                bool supported2 = (colorInfo2.value & 0x1) != 0; // advancedColorSupported
                bool enabled2 = colorInfo2.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR;
                bool isWcg2 = colorInfo2.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG;
                result.Add(new DisplayColorState(target.adapterId, target.id, supported2, enabled2, isWcg2));
                continue;
            }

            var colorInfo = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = target.adapterId,
                    id = target.id
                }
            };

            int getInfoResult = DisplayConfigGetDeviceInfo(ref colorInfo);
            if (getInfoResult != ERROR_SUCCESS)
            {
                LoggingService.Warn("HdrControlService", $"DisplayConfigGetDeviceInfo (advanced color) failed for target {target.id} (error {getInfoResult}); skipping this display.");
                continue;
            }

            bool supported = (colorInfo.value & 0x1) != 0;
            bool enabled = (colorInfo.value & 0x2) != 0;
            result.Add(new DisplayColorState(target.adapterId, target.id, supported, enabled));
        }

        return result;
    }

    /// <summary>Returns true if Windows accepted the change (regardless of what the prior state was).</summary>
    public static bool SetDisplayHdrEnabled(LUID adapterId, uint targetId, bool enable)
    {
        if (s_useHdrState2Api)
        {
            var hdrState = new DISPLAYCONFIG_SET_HDR_STATE
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = (uint)DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>(),
                    adapterId = adapterId,
                    id = targetId
                },
                value = enable ? 1u : 0u
            };

            int hdrStateResult = DisplayConfigSetDeviceInfoHdrState(ref hdrState);
            if (hdrStateResult != ERROR_SUCCESS)
            {
                LoggingService.Warn("HdrControlService", $"DisplayConfigSetDeviceInfo (HDR state) failed for target {targetId} (error {hdrStateResult}), requested enable={enable}.");
            }
            return hdrStateResult == ERROR_SUCCESS;
        }

        var state = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = adapterId,
                id = targetId
            },
            value = enable ? 1u : 0u
        };

        int setStateResult = DisplayConfigSetDeviceInfo(ref state);
        if (setStateResult != ERROR_SUCCESS)
        {
            LoggingService.Warn("HdrControlService", $"DisplayConfigSetDeviceInfo (advanced color state) failed for target {targetId} (error {setStateResult}), requested enable={enable}.");
        }
        return setStateResult == ERROR_SUCCESS;
    }

    // =========================================================================
    // Win32 CCD (Connecting and Configuring Displays) API
    // =========================================================================

    private const int ERROR_SUCCESS = 0;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const uint DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;

    // Windows 11 24H2+ (build 26100) only - not yet in the Windows SDK headers TrayTrigger
    // builds against, so declared locally. Verified against Kodi's shipped fix for this same
    // Auto Color Management issue (xbmc/xbmc PR #26096, SDK_26100.h).
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE = 16;
    private const int DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG = 1;
    private const int DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    // Bit 0 = advancedColorSupported, bit 1 = advancedColorEnabled, bit 2 = wideColorEnforced,
    // bit 3 = advancedColorForceDisabled - we only need the first two. colorEncoding/
    // bitsPerColorChannel are unused here but must stay: Windows validates header.size against
    // the real (32-byte) struct size for this device-info type and rejects the call outright if
    // it's short, so omitting these silently breaks every call rather than just leaving fields unread.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    // Bit 0 = enableAdvancedColor.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

    // Bit 0 = advancedColorSupported, bit 1 = advancedColorActive (pipeline is currently running
    // advanced color - can be true under Auto Color Management even when the user's HDR toggle
    // is off), bit 2 = reserved1, bit 3 = advancedColorLimitedByPolicy, bit 4 =
    // highDynamicRangeSupported, bit 5 = highDynamicRangeUserEnabled, bit 6 = wideColorSupported,
    // bit 7 = wideColorUserEnabled. activeColorMode is the field that actually reflects ground
    // truth (SDR/WCG/HDR) regardless of ACM.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
        public int activeColorMode;
    }

    // Bit 0 = enableHdr.
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SET_HDR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
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
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
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

    // The real DISPLAYCONFIG_MODE_INFO is a tagged union (source/target/desktop-image mode) that
    // is always 64 bytes on x64. QueryDisplayConfig only needs a correctly-sized buffer to write
    // into - this code never reads any of its fields - so it's declared as an opaque blob.
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
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo2(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int DisplayConfigSetDeviceInfoHdrState(ref DISPLAYCONFIG_SET_HDR_STATE requestPacket);
}
