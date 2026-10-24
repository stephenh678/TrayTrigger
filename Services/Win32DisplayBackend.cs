using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// <see cref="IDisplayBackend"/> over user32: EnumDisplayDevices for the displays, EnumDisplaySettingsEx
/// for their modes and ChangeDisplaySettingsEx to set one. The mode is only ever changed with
/// CDS_FULLSCREEN, a temporary mode Windows itself undoes when this process ends.
/// </summary>
internal sealed class Win32DisplayBackend : IDisplayBackend
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int ENUM_REGISTRY_SETTINGS = -2;
    private const uint CDS_TEST = 0x2;
    private const uint CDS_FULLSCREEN = 0x4;
    private const uint DM_PELSWIDTH = 0x80000;
    private const uint DM_PELSHEIGHT = 0x100000;
    private const uint DM_DISPLAYFREQUENCY = 0x400000;
    private const uint DM_INTERLACED = 0x2;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    /// <summary>A pseudo device that remoting software mirrors drawing through; it has no monitor you can see.</summary>
    private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    private const uint CDS_UPDATEREGISTRY = 0x1;
    private const uint CDS_SET_PRIMARY = 0x10;
    private const uint CDS_NORESET = 0x10000000;
    private const uint DM_POSITION = 0x20;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_BADMODE = -2;
    private const int SM_REMOTESESSION = 0x1000;
    private const int SM_CMONITORS = 80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceW
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DisplayDeviceW lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string? lpszDeviceName, int iModeNum, ref DevModeW lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DevModeW lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    /// <summary>The same call with no mode: back to the desktop's own.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public bool IsRemoteSession => GetSystemMetrics(SM_REMOTESESSION) != 0;

    /// <summary>How many monitors are attached to the desktop; Windows leaves out the pseudo ones remoting software adds.</summary>
    public static int MonitorCount() => GetSystemMetrics(SM_CMONITORS);

    /// <summary>A real monitor's output: on the desktop, and not a mirroring pseudo device.</summary>
    private static bool IsShownOnDesktop(DisplayDeviceW device) =>
        (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 && (device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) == 0;

    public string? PrimaryDevice()
    {
        foreach (var device in Devices())
        {
            if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0 && (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
            {
                return device.DeviceName;
            }
        }
        return null;
    }

    public bool IsAttached(string device)
    {
        foreach (var d in Devices())
        {
            if (string.Equals(d.DeviceName, device, StringComparison.OrdinalIgnoreCase)
                && (d.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0) return true;
        }
        return false;
    }

    public IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        foreach (var device in Devices())
        {
            if (!IsShownOnDesktop(device)) continue;
            var current = NewMode();
            if (!EnumDisplaySettingsExW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref current, 0)) continue;

            // The monitor itself, one level down. Its interface path stays with the monitor and its connection, where
            // the \\.\DISPLAYn name can change when cables are swapped.
            var monitor = new DisplayDeviceW { cb = (uint)Marshal.SizeOf<DisplayDeviceW>() };
            string id = device.DeviceName, name = string.Empty;
            if (EnumDisplayDevicesW(device.DeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
            {
                if (!string.IsNullOrWhiteSpace(monitor.DeviceID)) id = monitor.DeviceID;
                name = monitor.DeviceString?.Trim() ?? string.Empty;
            }
            var bounds = new ScreenRect(current.dmPositionX, current.dmPositionY, current.dmPositionX + (int)current.dmPelsWidth, current.dmPositionY + (int)current.dmPelsHeight);
            list.Add(new MonitorInfo(device.DeviceName, id, name, (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0, bounds));
        }
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Number).ToList();
    }

    public IReadOnlyList<MonitorPlacement> Placements()
    {
        var list = new List<MonitorPlacement>();
        foreach (var device in Devices())
        {
            if (!IsShownOnDesktop(device)) continue;
            var current = NewMode();
            if (EnumDisplaySettingsExW(device.DeviceName, ENUM_CURRENT_SETTINGS, ref current, 0))
            {
                list.Add(new MonitorPlacement(device.DeviceName, current.dmPositionX, current.dmPositionY));
            }
        }
        return list;
    }

    /// <summary>
    /// Stages each monitor's place in the desktop's own setting (the one at 0, 0 as the main display), then applies
    /// them together: Windows asks for the new main display to be at 0, 0 and for every other place to be given
    /// relative to it, and for the change to be applied once, after the last is staged.
    /// </summary>
    public DisplayChange SetLayout(IReadOnlyList<MonitorPlacement> placements)
    {
        foreach (var (p, setPrimary) in DisplayLayout.StagingPlan(placements))
        {
            // Built on the desktop's own saved settings, not on what the monitor runs this moment: the staged change is written
            // to the desktop's setting, and a temporary mode (a game in exclusive full screen, say) would be saved with it.
            var mode = NewMode();
            if (!EnumDisplaySettingsExW(p.Device, ENUM_REGISTRY_SETTINGS, ref mode, 0))
            {
                mode = NewMode();
                if (!EnumDisplaySettingsExW(p.Device, ENUM_CURRENT_SETTINGS, ref mode, 0)) return DisplayChange.Failed;
            }
            mode.dmPositionX = p.X;
            mode.dmPositionY = p.Y;
            // The fields the monitor already reports (its size, colour depth, rate) stay: Windows works out the shape
            // of the desktop from them. Only the position is added.
            mode.dmFields |= DM_POSITION;
            mode.dmDriverExtra = 0;
            uint flags = CDS_UPDATEREGISTRY | CDS_NORESET | (setPrimary ? CDS_SET_PRIMARY : 0);
            var staged = Map(ChangeDisplaySettingsExW(p.Device, ref mode, IntPtr.Zero, flags, IntPtr.Zero));
            if (staged != DisplayChange.Ok) return staged;
        }
        return Map(ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero));
    }

    public DisplayMode? Current(string device)
    {
        var mode = NewMode();
        if (!EnumDisplaySettingsExW(device, ENUM_CURRENT_SETTINGS, ref mode, 0)) return null;
        return new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency);
    }

    public IReadOnlyList<DisplayMode> Modes(string device)
    {
        var modes = new List<DisplayMode>();
        var current = NewMode();
        if (!EnumDisplaySettingsExW(device, ENUM_CURRENT_SETTINGS, ref current, 0)) return modes;

        var seen = new HashSet<DisplayMode>();
        for (int i = 0; ; i++)
        {
            var m = NewMode();
            if (!EnumDisplaySettingsExW(device, i, ref m, 0)) break;
            if (!Comparable(m, current)) continue;
            var mode = new DisplayMode((int)m.dmPelsWidth, (int)m.dmPelsHeight, (int)m.dmDisplayFrequency);
            if (mode.RefreshHz > 1 && seen.Add(mode)) modes.Add(mode);
        }
        return modes;
    }

    public DisplayChange Test(string device, DisplayMode mode) => Change(device, mode, CDS_TEST);

    public DisplayChange ApplyTemporary(string device, DisplayMode mode) => Change(device, mode, CDS_FULLSCREEN);

    public DisplayChange RestoreDefault(string device) => Map(ChangeDisplaySettingsExW(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero));

    private static DevModeW NewMode() => new() { dmSize = (ushort)Marshal.SizeOf<DevModeW>() };

    /// <summary>Same colour depth, orientation and scan type as the display runs now, so the list holds one entry per resolution and rate.</summary>
    private static bool Comparable(DevModeW m, DevModeW current) =>
        m.dmBitsPerPel == current.dmBitsPerPel
        && m.dmDisplayOrientation == current.dmDisplayOrientation
        && (m.dmDisplayFlags & DM_INTERLACED) == 0;

    private static DisplayChange Change(string device, DisplayMode mode, uint flags)
    {
        var current = NewMode();
        if (!EnumDisplaySettingsExW(device, ENUM_CURRENT_SETTINGS, ref current, 0)) return DisplayChange.Failed;

        // The driver's own entry for the mode, as listed, so nothing in it is made up; then only the
        // three fields the change is about are marked, which leaves the position and colour depth alone.
        for (int i = 0; ; i++)
        {
            var m = NewMode();
            if (!EnumDisplaySettingsExW(device, i, ref m, 0)) return DisplayChange.BadMode;
            if (!Comparable(m, current)) continue;
            if (m.dmPelsWidth != mode.Width || m.dmPelsHeight != mode.Height || m.dmDisplayFrequency != mode.RefreshHz) continue;

            m.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            // The driver's private data is not passed back: this struct is exactly dmSize bytes, so a
            // count left over from the enumeration would have the driver read past its end.
            m.dmDriverExtra = 0;
            return Map(ChangeDisplaySettingsExW(device, ref m, IntPtr.Zero, flags, IntPtr.Zero));
        }
    }

    private static DisplayChange Map(int result) => result switch
    {
        DISP_CHANGE_SUCCESSFUL => DisplayChange.Ok,
        DISP_CHANGE_BADMODE => DisplayChange.BadMode,
        _ => DisplayChange.Failed,
    };

    private static IEnumerable<DisplayDeviceW> Devices()
    {
        for (uint i = 0; ; i++)
        {
            var d = new DisplayDeviceW { cb = (uint)Marshal.SizeOf<DisplayDeviceW>() };
            if (!EnumDisplayDevicesW(null, i, ref d, 0)) yield break;
            yield return d;
        }
    }
}
