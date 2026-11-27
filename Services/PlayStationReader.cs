using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TrayTrigger.Services;

/// <summary>
/// Reads Sony controllers (DualShock 4, DualSense, DualSense Edge), which Windows shows only as a plain HID game
/// controller and not through XInput, unless Steam or DS4Windows is translating them. Their input reports are
/// read straight from the device, wired or Bluetooth, and turned into the same <see cref="PadState"/> as an Xbox
/// controller's: Cross is A, Circle B, Square X, Triangle Y, Options Start, Create/Share Back, L1/R1 the shoulders.
/// <para>
/// Reading is passive. Nothing is sent to the controller (its mode, lights and rumble are left alone), and other
/// programs, a game or Steam included, go on reading it as before. The device is held open only while
/// <see cref="Read"/> is being asked, and let go about a second after it stops (another window, a game, in front).
/// </para>
/// </summary>
public sealed class PlayStationReader
{
    private const ushort SonyVendorId = 0x054C;
    private static readonly ushort[] DualShock4 = { 0x05C4, 0x09CC, 0x0BA0 };
    private static readonly ushort[] DualSense = { 0x0CE6, 0x0DF2 };
    // Looking for a controller walks every HID device, so it is done again only this often while none is found.
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(2);
    private const long LetGoAfterMs = 1000;

    private readonly object _lock = new();
    private Device? _device;
    private DateTime _nextScan;
    private long _lastAsked;
    private string? _announced;

    private sealed class Device
    {
        public required FileStream Stream;
        public required ushort ProductId;
        public required bool Bluetooth;
        public required int ReportLength;
        public PadState State;
        public volatile bool Gone;
        public volatile bool Failed;
    }

    /// <summary>The first Sony controller's state, or null when none is connected.</summary>
    public PadState? Read()
    {
        Interlocked.Exchange(ref _lastAsked, Environment.TickCount64);
        lock (_lock)
        {
            if (_device is { Gone: true } old)
            {
                // Let go while nothing asked: back at once. Lost (unplugged, out of range): on the next scan.
                if (!old.Failed) _nextScan = DateTime.MinValue;
                _device = null;
            }
            if (_device == null)
            {
                var now = DateTime.UtcNow;
                if (now < _nextScan) return null;
                _nextScan = now + Rescan;
                _device = Open();
                if (_device == null) return null;
                var device = _device;
                Task.Run(() => ReadLoop(device));
            }
            return _device.State;
        }
    }

    private async Task ReadLoop(Device device)
    {
        var buffer = new byte[device.ReportLength];
        try
        {
            while (Environment.TickCount64 - Interlocked.Read(ref _lastAsked) < LetGoAfterMs)
            {
                int n;
                // A timeout so a controller that goes quiet doesn't keep the device open once nothing asks.
                using (var timeout = new CancellationTokenSource(500))
                {
                    try
                    {
                        n = await device.Stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // The half-second timeout: no report in that time (nothing moved), which is normal. Go round and look
                        // again whether anything still asks.
                        continue;
                    }
                }
                if (n == 0)
                {
                    device.Failed = true;
                    break;
                }
                if (Parse(buffer.AsSpan(0, n), device.ProductId, device.Bluetooth) is { } state)
                    lock (_lock) device.State = state;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Unplugged, switched off or out of Bluetooth range: looked for again on the next scan.
            device.Failed = true;
            LoggingService.Swallowed("Controller", ex, "reading the PlayStation controller");
        }
        finally
        {
            device.Gone = true;
            device.Stream.Dispose();
        }
    }

    /// <summary>
    /// One input report as a <see cref="PadState"/>, or null for a report that carries no buttons. Internal for the
    /// tests. Layouts, by report ID (byte 0): DualShock 4 0x01 (USB, or Bluetooth before a program switches it to
    /// full reports) and DualSense 0x01 over Bluetooth have the sticks from byte 1 and the buttons from byte 5;
    /// DualSense 0x01 over USB has the buttons from byte 8; DualSense 0x31 and DualShock 4 0x11 are the full
    /// Bluetooth reports, one and two bytes further on.
    /// </summary>
    internal static PadState? Parse(ReadOnlySpan<byte> report, ushort productId, bool bluetooth)
    {
        if (report.Length < 1) return null;
        bool dualSense = Array.IndexOf(DualSense, productId) >= 0;
        (int sticks, int buttons) = (report[0], dualSense) switch
        {
            (0x01, true) => bluetooth ? (1, 5) : (1, 8),
            (0x31, true) => (2, 9),
            (0x01, false) => (1, 5),
            (0x11, false) => (3, 7),
            _ => (-1, -1),
        };
        if (sticks < 0 || report.Length < buttons + 2) return null;

        byte face = report[buttons], more = report[buttons + 1];
        ushort pad = (face & 0x0F) switch // the D-pad is a hat: 0 is up, then clockwise, 8 is centred
        {
            0 => XInputReader.DPadUp,
            1 => XInputReader.DPadUp | XInputReader.DPadRight,
            2 => XInputReader.DPadRight,
            3 => XInputReader.DPadDown | XInputReader.DPadRight,
            4 => XInputReader.DPadDown,
            5 => XInputReader.DPadDown | XInputReader.DPadLeft,
            6 => XInputReader.DPadLeft,
            7 => XInputReader.DPadUp | XInputReader.DPadLeft,
            _ => 0,
        };
        if ((face & 0x10) != 0) pad |= XInputReader.X; // Square
        if ((face & 0x20) != 0) pad |= XInputReader.A; // Cross
        if ((face & 0x40) != 0) pad |= XInputReader.B; // Circle
        if ((face & 0x80) != 0) pad |= XInputReader.Y; // Triangle
        if ((more & 0x01) != 0) pad |= XInputReader.LeftShoulder;
        if ((more & 0x02) != 0) pad |= XInputReader.RightShoulder;
        if ((more & 0x10) != 0) pad |= XInputReader.Back; // Create / Share
        if ((more & 0x20) != 0) pad |= XInputReader.Start; // Options

        // Sticks are 0..255 with 128 centred and down as 255; XInput's are signed with up positive.
        short lx = Axis(report[sticks] - 128), ly = Axis(128 - report[sticks + 1]);
        if (Math.Abs((int)lx) <= 8000 && Math.Abs((int)ly) <= 8000) lx = ly = 0;
        return new PadState(pad, lx, ly);
    }

    private static short Axis(int centred) => (short)Math.Clamp(centred * 256, short.MinValue, short.MaxValue);

    private Device? Open()
    {
        HidD_GetHidGuid(out var hidGuid);
        IntPtr set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return null;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data)) return null;
                if (InterfacePath(set, ref data) is not { } path) continue;
                if (TryOpen(path) is { } device) return device;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private Device? TryOpen(string path)
    {
        ushort productId;
        int reportLength;
        // Opened first without read access, which never disturbs a device, to see whose it is.
        using (var probe = CreateFileW(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
        {
            if (probe.IsInvalid) return null;
            var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            if (!HidD_GetAttributes(probe, ref attributes) || attributes.VendorId != SonyVendorId) return null;
            if (Array.IndexOf(DualShock4, attributes.ProductId) < 0 && Array.IndexOf(DualSense, attributes.ProductId) < 0) return null;
            if (!HidD_GetPreparsedData(probe, out var preparsed)) return null;
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess || caps.InputReportByteLength == 0) return null;
                productId = attributes.ProductId;
                reportLength = caps.InputReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }
        var handle = CreateFileW(path, GenericRead, FileShareReadWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        // HID over Bluetooth Classic shows up under its service class, 00001124-....
        bool bluetooth = path.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);
        if (_announced != path)
        {
            _announced = path;
            LoggingService.Info("Controller", $"PlayStation controller found (product {productId:X4}, {(bluetooth ? "Bluetooth" : "USB")})");
        }
        return new Device { Stream = new FileStream(handle, FileAccess.Read, 0, isAsync: true), ProductId = productId, Bluetooth = bluetooth, ReportLength = reportLength };
    }

    private static string? InterfacePath(IntPtr set, ref SpDeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 4) return null;
        IntPtr detail = Marshal.AllocHGlobal(size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W's cbSize: a DWORD and one WCHAR, padded to 8 on 64-bit.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private const int DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000, FileShareReadWrite = 0x3, OpenExisting = 3, FileFlagOverlapped = 0x40000000;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId, ProductId, VersionNumber;
    }

    // HIDP_CAPS is 32 USHORTs; only the report lengths at its start are read.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    }

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int detailSize, out int requiredSize, IntPtr deviceInfo);

    [DllImport("setupapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
