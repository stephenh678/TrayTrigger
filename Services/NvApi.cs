using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// Minimal, read-only interop for NVIDIA's driver settings database (DRS) - the store behind
/// "Manage 3D settings" and the per-game profiles NVIDIA App and Profile Inspector write. The DLSS
/// override settings live there; see docs/dlss-plan.md.
///
/// <para>Deliberately read-only. Writing requires the ownership record described in the plan
/// (capture previous value <i>and</i> origin, undo only when the current value still matches what
/// was written), which does not exist yet. Nothing here calls NvAPI_DRS_SaveSettings.</para>
///
/// <para>NVAPI ships no import library: every entry point is reached through the single exported
/// <c>nvapi_QueryInterface</c>, keyed by a published 32-bit function id. An id this driver does not
/// expose returns null rather than crashing, which is why every call site checks.</para>
/// </summary>
public static unsafe class NvApi
{
    // Published NVAPI function ids. NVIDIA treats these as ABI and they have been stable for years,
    // but a null lookup is still handled - an id NVIDIA retired would present exactly that way.
    private const uint IdInitialize                 = 0x0150E828;
    private const uint IdGetErrorMessage            = 0x6C2D048C;
    private const uint IdDrsCreateSession           = 0x0694D52E;
    private const uint IdDrsDestroySession          = 0xDAD9CFF8;
    private const uint IdDrsLoadSettings            = 0x375DBD6B;
    private const uint IdDrsGetBaseProfile          = 0xDA8466A0;
    private const uint IdDrsGetCurrentGlobalProfile = 0x617BFF9F;
    private const uint IdDrsFindApplicationByName   = 0xEEE566B2;
    private const uint IdDrsGetProfileInfo          = 0x61CD6FD6;
    private const uint IdDrsGetSetting              = 0x73BF8338;
    private const uint IdDrsEnumSettings            = 0xAE3039DA;

    private const int UnicodeStringMax = 2048;

    private static IntPtr _module;
    private static delegate* unmanaged[Cdecl]<uint, IntPtr> _queryInterface;
    private static bool _initialised;
    private static string? _loadFailure;

    /// <summary>Why NVAPI is unavailable, or null when it loaded. Reported verbatim by the probe.</summary>
    public static string? UnavailableReason => _loadFailure;

    /// <summary>
    /// Loads nvapi64.dll and calls NvAPI_Initialize once. False on any machine without an NVIDIA
    /// driver - the expected result on AMD and Intel, not an error.
    /// </summary>
    public static bool TryInitialize()
    {
        if (_initialised) return true;
        if (_loadFailure != null) return false;

        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out _module))
            {
                _loadFailure = "nvapi64.dll not present (no NVIDIA display driver).";
                return false;
            }
            if (!NativeLibrary.TryGetExport(_module, "nvapi_QueryInterface", out IntPtr qi))
            {
                _loadFailure = "nvapi64.dll exports no nvapi_QueryInterface.";
                return false;
            }
            _queryInterface = (delegate* unmanaged[Cdecl]<uint, IntPtr>)qi;

            var init = (delegate* unmanaged[Cdecl]<int>)Lookup(IdInitialize);
            if (init == null)
            {
                _loadFailure = "NvAPI_Initialize not exposed by this driver.";
                return false;
            }
            int status = init();
            if (status != 0)
            {
                _loadFailure = $"NvAPI_Initialize returned {Describe(status)}.";
                return false;
            }
            _initialised = true;
            return true;
        }
        catch (Exception ex)
        {
            _loadFailure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static IntPtr Lookup(uint id) => _queryInterface == null ? IntPtr.Zero : _queryInterface(id);

    /// <summary>NVAPI's own text for a status code, falling back to the raw number.</summary>
    public static string Describe(int status)
    {
        if (status == 0) return "NVAPI_OK";
        var fn = (delegate* unmanaged[Cdecl]<int, byte*, int>)Lookup(IdGetErrorMessage);
        if (fn != null)
        {
            byte* buf = stackalloc byte[64];
            if (fn(status, buf) == 0)
            {
                string s = Marshal.PtrToStringAnsi((IntPtr)buf) ?? string.Empty;
                if (s.Length > 0) return $"{s} ({status})";
            }
        }
        return $"NVAPI status {status}";
    }

    // ---- DRS structures -------------------------------------------------------------------
    // Layout must match nvapi.h exactly. Rather than hard-coding NVIDIA's version constants, each
    // is computed the way the MAKE_NVAPI_VERSION macro does - sizeof(struct) | (ver << 16) - so a
    // layout mistake here cannot silently hand the driver a plausible-looking version number.

    [StructLayout(LayoutKind.Sequential)]
    private struct NvDrsProfile
    {
        public uint Version;
        public fixed ushort ProfileName[UnicodeStringMax];
        public uint GpuSupport;
        public uint IsPredefined;
        public uint NumOfApps;
        public uint NumOfSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvDrsApplicationV4
    {
        public uint Version;
        public uint IsPredefined;
        public fixed ushort AppName[UnicodeStringMax];
        public fixed ushort UserFriendlyName[UnicodeStringMax];
        public fixed ushort Launcher[UnicodeStringMax];
        public fixed ushort FileInFolder[UnicodeStringMax];
        public uint Flags;
        public fixed ushort CommandLine[UnicodeStringMax];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvDrsSetting
    {
        public uint Version;
        public fixed ushort SettingName[UnicodeStringMax];
        public uint SettingId;
        public uint SettingType;      // 0 DWORD, 1 BINARY, 2 STRING, 3 WSTRING
        public uint SettingLocation;  // 0 this profile, 1 global, 2 base, 3 driver default
        public uint IsCurrentPredefined;
        public uint IsPredefinedValid;
        public fixed byte PredefinedValue[4100];  // union: NvU32 | NVDRS_BINARY_SETTING | strings
        public fixed byte CurrentValue[4100];
    }

    private static uint VersionOf<T>(uint ver) where T : unmanaged => (uint)sizeof(T) | (ver << 16);

    private static string ReadFixed(ushort* p)
    {
        int n = 0;
        while (n < UnicodeStringMax && p[n] != 0) n++;
        return new string((char*)p, 0, n);
    }

    private static void WriteFixed(ushort* dest, string value)
    {
        int n = Math.Min(value.Length, UnicodeStringMax - 1);
        for (int i = 0; i < n; i++) dest[i] = value[i];
        for (int i = n; i < UnicodeStringMax; i++) dest[i] = 0;
    }

    /// <summary>Where a setting's current value comes from - the plan's "origin".</summary>
    public enum SettingOrigin
    {
        /// <summary>Set on the game's own application profile.</summary>
        ApplicationProfile = 0,
        /// <summary>Inherited from the user's Global profile - the RHI / NVIDIA App case.</summary>
        GlobalProfile = 1,
        /// <summary>Inherited from the driver's base profile.</summary>
        BaseProfile = 2,
        /// <summary>The driver's built-in default; nothing has been written at any layer.</summary>
        DriverDefault = 3
    }

    /// <summary>One DRS setting as the driver currently reports it for a given profile.</summary>
    public sealed record DrsSettingValue(
        uint SettingId,
        string Name,
        uint CurrentValue,
        SettingOrigin Origin,
        bool IsCurrentPredefined,
        bool HasPredefined,
        uint PredefinedValue)
    {
        /// <summary>
        /// The plan's origin label. "Predefined" means the value is NVIDIA's own for this game
        /// rather than something a user or another tool wrote - the distinction that decides
        /// whether TrayTrigger may later undo it.
        /// </summary>
        public string OriginLabel => Origin switch
        {
            SettingOrigin.ApplicationProfile => IsCurrentPredefined ? "Predefined" : "UserSet",
            SettingOrigin.GlobalProfile => "Inherited (Global profile)",
            SettingOrigin.BaseProfile => "Inherited (base profile)",
            _ => "DriverDefault"
        };
    }

    /// <summary>A DRS profile and, when the lookup was by executable, the application entry inside it.</summary>
    public sealed record DrsProfileInfo(
        string ProfileName,
        bool IsPredefined,
        uint ApplicationCount,
        uint SettingCount,
        string? MatchedApplicationName,
        string? MatchedFriendlyName);

    /// <summary>
    /// An open DRS session. Loading pulls the whole settings database into memory; nothing reaches
    /// disk unless SaveSettings is called, which this type does not expose.
    /// </summary>
    public sealed class Session : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        private Session(IntPtr handle) => _handle = handle;

        /// <summary>Opens and loads a session, or returns null with the reason.</summary>
        public static Session? TryOpen(out string? error)
        {
            error = null;
            if (!TryInitialize()) { error = UnavailableReason; return null; }

            var create = (delegate* unmanaged[Cdecl]<IntPtr*, int>)Lookup(IdDrsCreateSession);
            var load = (delegate* unmanaged[Cdecl]<IntPtr, int>)Lookup(IdDrsLoadSettings);
            if (create == null || load == null) { error = "DRS entry points not exposed by this driver."; return null; }

            IntPtr h;
            int status = create(&h);
            if (status != 0) { error = $"NvAPI_DRS_CreateSession: {Describe(status)}"; return null; }

            status = load(h);
            if (status != 0)
            {
                var destroy = (delegate* unmanaged[Cdecl]<IntPtr, int>)Lookup(IdDrsDestroySession);
                if (destroy != null) destroy(h);
                error = $"NvAPI_DRS_LoadSettings: {Describe(status)}";
                return null;
            }
            return new Session(h);
        }

        /// <summary>
        /// Finds the profile the driver would apply to an executable, matching on file name the way
        /// the driver does. Null means no profile covers it - the interesting case, because it says
        /// the game is absent from NVIDIA's database and a profile would have to be created.
        /// </summary>
        public DrsProfileInfo? FindProfileForExecutable(string exeFileName, out IntPtr profile, out string? error)
        {
            profile = IntPtr.Zero;
            error = null;
            var find = (delegate* unmanaged[Cdecl]<IntPtr, ushort*, IntPtr*, NvDrsApplicationV4*, int>)Lookup(IdDrsFindApplicationByName);
            if (find == null) { error = "NvAPI_DRS_FindApplicationByName not exposed."; return null; }

            ushort* name = stackalloc ushort[UnicodeStringMax];
            WriteFixed(name, exeFileName);

            var app = new NvDrsApplicationV4 { Version = VersionOf<NvDrsApplicationV4>(4) };
            IntPtr h;
            int status = find(_handle, name, &h, &app);
            if (status != 0) { error = Describe(status); return null; }

            profile = h;
            return DescribeProfile(h, ReadFixed(app.AppName), ReadFixed(app.UserFriendlyName), out error);
        }

        /// <summary>The user's Global profile - the layer tools such as RHI write, above the base profile.</summary>
        public DrsProfileInfo? GetGlobalProfile(out IntPtr profile, out string? error)
        {
            profile = IntPtr.Zero;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)Lookup(IdDrsGetCurrentGlobalProfile);
            if (fn == null) { error = "NvAPI_DRS_GetCurrentGlobalProfile not exposed."; return null; }
            IntPtr h;
            int status = fn(_handle, &h);
            if (status != 0) { error = Describe(status); return null; }
            profile = h;
            return DescribeProfile(h, null, null, out error);
        }

        /// <summary>The driver's base profile - the bottom layer, below Global.</summary>
        public DrsProfileInfo? GetBaseProfile(out IntPtr profile, out string? error)
        {
            profile = IntPtr.Zero;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)Lookup(IdDrsGetBaseProfile);
            if (fn == null) { error = "NvAPI_DRS_GetBaseProfile not exposed."; return null; }
            IntPtr h;
            int status = fn(_handle, &h);
            if (status != 0) { error = Describe(status); return null; }
            profile = h;
            return DescribeProfile(h, null, null, out error);
        }

        private DrsProfileInfo? DescribeProfile(IntPtr profile, string? appName, string? friendly, out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NvDrsProfile*, int>)Lookup(IdDrsGetProfileInfo);
            if (fn == null) { error = "NvAPI_DRS_GetProfileInfo not exposed."; return null; }

            var info = new NvDrsProfile { Version = VersionOf<NvDrsProfile>(1) };
            int status = fn(_handle, profile, &info);
            if (status != 0) { error = Describe(status); return null; }

            return new DrsProfileInfo(
                ReadFixed(info.ProfileName),
                info.IsPredefined != 0,
                info.NumOfApps,
                info.NumOfSettings,
                string.IsNullOrEmpty(appName) ? null : appName,
                string.IsNullOrEmpty(friendly) ? null : friendly);
        }

        /// <summary>
        /// Reads one setting as it applies to a profile. A non-zero status normally means the
        /// setting is not present at any layer for this profile, which callers report as "Absent"
        /// rather than as a failure.
        /// </summary>
        public DrsSettingValue? GetSetting(IntPtr profile, uint settingId, out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, NvDrsSetting*, int>)Lookup(IdDrsGetSetting);
            if (fn == null) { error = "NvAPI_DRS_GetSetting not exposed."; return null; }

            var s = new NvDrsSetting { Version = VersionOf<NvDrsSetting>(1) };
            int status = fn(_handle, profile, settingId, &s);
            if (status != 0) { error = Describe(status); return null; }

            // Only DWORD settings matter for DLSS; reading a binary or string one as a DWORD would
            // be meaningless, so it is reported as unsupported rather than as a bogus number.
            if (s.SettingType != 0)
            {
                error = $"setting type {s.SettingType} is not a DWORD";
                return null;
            }

            return new DrsSettingValue(
                s.SettingId,
                ReadFixed(s.SettingName),
                *(uint*)s.CurrentValue,
                (SettingOrigin)Math.Min(s.SettingLocation, 3u),
                s.IsCurrentPredefined != 0,
                s.IsPredefinedValid != 0,
                *(uint*)s.PredefinedValue);
        }

        /// <summary>
        /// Every DWORD setting explicitly stored on a profile. This is how the Global profile's
        /// contents become visible - the confound that made the 2026-09-19 spike ambiguous, because
        /// four DLSS settings were already there and nothing in the UI showed them.
        /// </summary>
        public List<DrsSettingValue> EnumSettings(IntPtr profile, out string? error)
        {
            error = null;
            var result = new List<DrsSettingValue>();
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, uint*, NvDrsSetting*, int>)Lookup(IdDrsEnumSettings);
            if (fn == null) { error = "NvAPI_DRS_EnumSettings not exposed."; return result; }

            // The driver ends enumeration with a non-zero status; the bound is only a guard against
            // a driver that never does.
            for (uint index = 0; index < 4096; index++)
            {
                var s = new NvDrsSetting { Version = VersionOf<NvDrsSetting>(1) };
                uint count = 1;
                int status = fn(_handle, profile, index, &count, &s);
                if (status != 0 || count == 0) break;
                if (s.SettingType != 0) continue;

                result.Add(new DrsSettingValue(
                    s.SettingId,
                    ReadFixed(s.SettingName),
                    *(uint*)s.CurrentValue,
                    (SettingOrigin)Math.Min(s.SettingLocation, 3u),
                    s.IsCurrentPredefined != 0,
                    s.IsPredefinedValid != 0,
                    *(uint*)s.PredefinedValue));
            }
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle == IntPtr.Zero) return;
            var destroy = (delegate* unmanaged[Cdecl]<IntPtr, int>)Lookup(IdDrsDestroySession);
            if (destroy != null) destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
