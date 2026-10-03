using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrayTrigger.Services;

/// <summary>
/// Freezes and thaws a process with NtSuspendProcess / NtResumeProcess - what Resource Monitor's
/// "Suspend process" does. Every thread stops where it is, so the game simply carries on from the
/// same frame when it is resumed. The calls nest: a process suspended twice needs two resumes, so
/// callers keep their own record and resume exactly what they suspended.
/// </summary>
internal static partial class ProcessSuspender
{
    private const uint ProcessSuspendResume = 0x0800;

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    private const int StatusProcessIsTerminating = unchecked((int)0xC000010A);

    internal enum Outcome { Done, Gone, Refused }

    /// <summary>Suspends the process with this id, provided it is still the one that started at <paramref name="startedUtc"/>.</summary>
    internal static Outcome Suspend(int pid, DateTime startedUtc, out string? error) => Call(pid, startedUtc, suspend: true, out error);

    /// <summary>Resumes the process with this id, provided it is still the one that started at <paramref name="startedUtc"/>.</summary>
    internal static Outcome Resume(int pid, DateTime startedUtc, out string? error) => Call(pid, startedUtc, suspend: false, out error);

    private static Outcome Call(int pid, DateTime startedUtc, bool suspend, out string? error)
    {
        var opened = ProcessHandles.OpenSame(pid, startedUtc, ProcessSuspendResume, out var handle, out error);
        if (opened != ProcessHandles.OpenResult.Opened) return opened == ProcessHandles.OpenResult.Gone ? Outcome.Gone : Outcome.Refused;

        using (handle)
        {
            int status = suspend ? NtSuspendProcess(handle!) : NtResumeProcess(handle!);
            if (status < 0)
            {
                // A process that has exited, or is on its way out (Force Close), can still be opened
                // while something holds a handle to it; there is nothing left to suspend or resume.
                if (status == StatusProcessIsTerminating || ProcessHandles.HasExited(handle!)) return Outcome.Gone;
                error = new Win32Exception((int)RtlNtStatusToDosError(status)).Message.TrimEnd('.');
                return Outcome.Refused;
            }
            return Outcome.Done;
        }
    }
}

/// <summary>
/// Mutes and unmutes the audio of particular processes - their own sessions in the Volume Mixer,
/// not the whole device. A suspended game can't refill its sound buffer, and older audio paths
/// (DirectSound's looping buffers) then repeat the last fraction of a second as a buzz until it
/// is resumed. Best-effort throughout: a PC with no playback device is a normal state.
/// </summary>
internal static class ProcessAudioMuter
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioSessionManager2Iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const int EDataFlowRender = 0;
    private const int DeviceStateActive = 0x1;
    private const int ClsCtxAll = 23;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    // IAudioSessionManager's two methods first: COM interop lays out each interface's vtable from
    // its own declaration, so the inherited slots are declared again.
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>
    /// Mutes every audio session belonging to these processes that isn't muted already, on every
    /// playback device. Returns the ids whose sound was muted here - only those are unmuted later,
    /// so a game the player had muted themselves stays muted.
    /// </summary>
    internal static HashSet<int> Mute(IReadOnlySet<int> pids)
    {
        var muted = new HashSet<int>();
        ForEachSession(pids, (pid, volume) =>
        {
            var context = Guid.Empty;
            if (volume.GetMute(out bool already) == 0 && !already && volume.SetMute(true, ref context) == 0)
            {
                muted.Add(pid);
            }
        });
        return muted;
    }

    /// <summary>Unmutes every audio session belonging to these processes.</summary>
    internal static int Unmute(IReadOnlySet<int> pids)
    {
        int count = 0;
        ForEachSession(pids, (_, volume) =>
        {
            var context = Guid.Empty;
            if (volume.SetMute(false, ref context) == 0) count++;
        });
        return count;
    }

    private static void ForEachSession(IReadOnlySet<int> pids, Action<int, ISimpleAudioVolume> act)
    {
        if (pids.Count == 0) return;
        var toRelease = new List<object>();
        try
        {
            var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid);
            if (type == null || Activator.CreateInstance(type) is not IMMDeviceEnumerator enumerator) return;
            toRelease.Add(enumerator);
            if (enumerator.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out var devices) != 0 || devices == null) return;
            toRelease.Add(devices);
            if (devices.GetCount(out uint deviceCount) != 0) return;

            for (uint d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0 || device == null) continue;
                toRelease.Add(device);
                var iid = AudioSessionManager2Iid;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object managerObject) != 0 || managerObject is not IAudioSessionManager2 manager) continue;
                toRelease.Add(manager);
                if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions == null) continue;
                toRelease.Add(sessions);
                if (sessions.GetCount(out int sessionCount) != 0) continue;

                for (int s = 0; s < sessionCount; s++)
                {
                    if (sessions.GetSession(s, out object session) != 0 || session == null) continue;
                    toRelease.Add(session);
                    if (session is not IAudioSessionControl2 control || control.GetProcessId(out uint pid) != 0) continue;
                    if (!pids.Contains((int)pid) || session is not ISimpleAudioVolume volume) continue;
                    act((int)pid, volume);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("Audio", $"Could not reach the game's audio sessions: {ex.Message}");
        }
        finally
        {
            foreach (var com in toRelease)
            {
                try { Marshal.ReleaseComObject(com); } catch (ArgumentException) { /* not a COM object after all; nothing to release */ }
            }
        }
    }
}
