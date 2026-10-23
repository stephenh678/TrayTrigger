using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// <see cref="IAudioBackend"/> over Core Audio: IMMDeviceEnumerator to list the playback devices and find the ones
/// doing each job, and IPolicyConfig to hand a job to another device. IPolicyConfig is what Windows' own sound
/// settings use; it is not documented, so only the one call needed is declared in full and its position in the
/// interface is what must be right. Each call is made for one job at a time, with Windows' own device id string.
/// </summary>
internal sealed class Win32AudioBackend : IAudioBackend
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyConfigClientClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private const int eRender = 0;
    private const uint DEVICE_STATE_ACTIVE = 1;
    private const int STGM_READ = 0;
    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Type, R1, R2, R3; public IntPtr Data1; public IntPtr Data2; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    /// <summary>The system's policy config; only SetDefaultEndpoint is used, the rest keep its place in the table.</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, int useDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, int useDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, int store, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, int store, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, int visible);
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    private static IMMDeviceEnumerator? Enumerator()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid);
        return type == null ? null : Activator.CreateInstance(type) as IMMDeviceEnumerator;
    }

    public IReadOnlyList<AudioDevice> PlaybackDevices()
    {
        var list = new List<AudioDevice>();
        var enumerator = Enumerator();
        if (enumerator == null) return list;
        try
        {
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection) != 0 || collection == null) return list;
            try
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0 || device == null) continue;
                    try
                    {
                        if (device.GetId(out string id) == 0 && !string.IsNullOrEmpty(id)) list.Add(new AudioDevice(id, FriendlyName(device) ?? id));
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(collection); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return list;
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != 0 || store == null) return null;
        try
        {
            // PKEY_Device_FriendlyName
            var key = new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try
            {
                return value.Type == VT_LPWSTR && value.Data1 != IntPtr.Zero ? Marshal.PtrToStringUni(value.Data1) : null;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    public string? DefaultId(AudioRole role)
    {
        var enumerator = Enumerator();
        if (enumerator == null) return null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(eRender, (int)role, out var device) != 0 || device == null) return null;
            try { return device.GetId(out string id) == 0 ? id : null; }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public bool SetDefault(string id, AudioRole role)
    {
        var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid);
        if (type == null || Activator.CreateInstance(type) is not IPolicyConfig policy) return false;
        try { return policy.SetDefaultEndpoint(id, (int)role) == 0; }
        finally { Marshal.ReleaseComObject(policy); }
    }
}
