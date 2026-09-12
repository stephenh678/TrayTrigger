using System;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// Reads and writes the mute flag on the default playback device - the one the volume flyout's
/// speaker icon toggles.
///
/// Core Audio (IMMDeviceEnumerator -> IAudioEndpointVolume) rather than a registry value: the
/// endpoint's mute state lives in the audio engine, so a registry write would not reach the
/// running mixer and would not survive a device switch. This is the same API the Windows volume
/// UI itself calls, needs no elevation, and applies to whichever device is default at the moment
/// it runs - so a user who switches from speakers to a headset between sessions gets the headset.
///
/// Every method is best-effort and swallows COM failures: a machine with no playback device at
/// all (a headless VM, every endpoint disabled) is a legitimate state, not an error worth
/// interrupting a game launch for.
/// </summary>
internal static class AudioEndpointService
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const int EDataFlowRender = 0;   // playback
    private const int ERoleConsole = 0;      // games, system sounds, voice - what the volume flyout shows

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        // The rest of the vtable is unused but must be declared so the slots line up.
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private const int ClsCtxAll = 23;

    /// <summary>The default playback device's mute flag, or null when there is no such device.</summary>
    internal static bool? GetDefaultRenderMuted()
    {
        try
        {
            var volume = OpenDefaultRenderVolume();
            if (volume == null) return null;
            try
            {
                return volume.GetMute(out bool muted) == 0 ? muted : null;
            }
            finally
            {
                Marshal.ReleaseComObject(volume);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Audio", $"Could not read the default playback device's mute state: {ex.Message}");
            return null;
        }
    }

    /// <summary>Sets the default playback device's mute flag. Returns false when there is no device or the call failed.</summary>
    internal static bool SetDefaultRenderMuted(bool muted)
    {
        try
        {
            var volume = OpenDefaultRenderVolume();
            if (volume == null) return false;
            try
            {
                // A null GUID means "this change did not come from a specific control", which is
                // what every caller outside a volume UI passes.
                var context = Guid.Empty;
                return volume.SetMute(muted, ref context) == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(volume);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Audio", $"Could not set the default playback device's mute state: {ex.Message}");
            return false;
        }
    }

    private static IAudioEndpointVolume? OpenDefaultRenderVolume()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid);
        if (type == null) return null;

        object? raw = Activator.CreateInstance(type);
        if (raw is not IMMDeviceEnumerator enumerator) return null;

        try
        {
            // E_NOTFOUND when nothing is plugged in and every endpoint is disabled.
            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleConsole, out IMMDevice device) != 0 || device == null)
            {
                return null;
            }

            try
            {
                var iid = AudioEndpointVolumeIid;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object instance) != 0) return null;
                return instance as IAudioEndpointVolume;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }
}
