using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// Minimal interop for NVIDIA's driver settings database (DRS) - the store behind
/// "Manage 3D settings" and the per-game profiles NVIDIA App and Profile Inspector write. The DLSS
/// override settings live there; see docs/dlss-plan.md.
///
/// <para>Reads are free of consequence; writes are not. Every write goes through
/// <see cref="Session.SetSetting"/>/<see cref="Session.DeleteSetting"/> and reaches disk only on
/// <see cref="Session.Save"/>, and callers are expected to have captured the previous value and
/// its origin first - see <c>DlssOverrideService</c>, which is the only thing that should call
/// them.</para>
///
/// <para>NVAPI ships no import library: every entry point is reached through the single exported
/// <c>nvapi_QueryInterface</c>, keyed by a published 32-bit function id. An id this driver does not
/// expose returns null rather than crashing, which is why every call site checks.</para>
/// </summary>
public static unsafe partial class NvApi
{
    // Published NVAPI function ids. NVIDIA treats these as ABI and they have been stable for years,
    // but a null lookup is still handled - an id NVIDIA retired would present exactly that way.
    private const uint IdInitialize                 = 0x0150E828;
    private const uint IdGetErrorMessage            = 0x6C2D048C;
    private const uint IdDrsCreateSession           = 0x0694D52E;
    private const uint IdDrsDestroySession          = 0xDAD9CFF8;
    private const uint IdDrsLoadSettings            = 0x375DBD6B;
    private const uint IdDrsFindApplicationByName   = 0xEEE566B2;
    private const uint IdDrsGetProfileInfo          = 0x61CD6FD6;
    private const uint IdDrsGetSetting              = 0x73BF8338;
    private const uint IdDrsSetSetting              = 0x577DD202;
    private const uint IdDrsDeleteProfileSetting    = 0xE4A26362;
    private const uint IdDrsSaveSettings            = 0xFCBC7E14;
    private const uint IdDrsCreateProfile           = 0xCC176068;
    private const uint IdDrsDeleteProfile           = 0x17093206;
    private const uint IdDrsRestoreDefaultSetting   = 0x53F0381E;

    /// <summary>The two statuses that are answers rather than failures. Everything else is an error.</summary>
    public const int StatusSettingNotFound = -160;
    public const int StatusExecutableNotFound = -166;
    private const uint IdDrsCreateApplication       = 0x4347A9DE;
    private const uint IdDrsGetSettingNameFromId    = 0xD61CBE6E;

    private const int UnicodeStringMax = 2048;

    private static IntPtr _module;
    private static delegate* unmanaged[Cdecl]<uint, IntPtr> _queryInterface;
    private static volatile bool _initialised;
    private static readonly object InitLock = new();
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
        // The card probes on a pool thread while a launch can be re-applying on another.
        lock (InitLock) return InitializeLocked();
    }

    private static bool InitializeLocked()
    {
        if (_initialised) return true;
        if (_loadFailure != null) return false;

        try
        {
            // System32 only. The default search starts in TrayTrigger's own folder, and a portable
            // copy run from Downloads would load whatever nvapi64.dll had been dropped beside it -
            // into a process that may be elevated.
            if (!NativeLibrary.TryLoad("nvapi64.dll", typeof(NvApi).Assembly, DllImportSearchPath.System32, out _module))
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

    /// <summary>
    /// The driver's own name for a setting id, or null when it does not recognise it. Session-free,
    /// so it answers "does this driver know this setting at all" independently of any profile -
    /// which is the difference between a setting nobody has set and an id the driver will refuse
    /// to write.
    /// </summary>
    public static string? GetSettingName(uint settingId)
    {
        if (!TryInitialize()) return null;
        var fn = (delegate* unmanaged[Cdecl]<uint, ushort*, int>)Lookup(IdDrsGetSettingNameFromId);
        if (fn == null) return null;

        ushort* buffer = stackalloc ushort[UnicodeStringMax];
        for (int i = 0; i < UnicodeStringMax; i++) buffer[i] = 0;
        return fn(settingId, buffer) == 0 ? ReadFixed(buffer) : null;
    }

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

    /// <summary>Where a setting's current value comes from.</summary>
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
        bool IsCurrentPredefined);

    /// <summary>A DRS profile as the driver describes it.</summary>
    public sealed record DrsProfileInfo(string ProfileName, bool IsPredefined, uint ApplicationCount, uint SettingCount);

    /// <summary>
    /// An open DRS session. Loading pulls the whole settings database into memory; nothing reaches
    /// disk until <see cref="Save"/>.
    /// </summary>
    public sealed partial class Session : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        /// <summary>
        /// The raw status of the last <see cref="FindProfileForExecutable"/> or
        /// <see cref="GetSetting"/>. A null from either means "not there" only when this is the
        /// matching not-found status; anything else is a failure, and a caller that treated it as
        /// absent would capture - and later restore - the wrong thing.
        /// </summary>
        public int LastStatus { get; private set; }

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
            LastStatus = status;
            if (status != 0) { error = Describe(status); return null; }

            profile = h;
            return DescribeProfile(h, out error);
        }

        private DrsProfileInfo? DescribeProfile(IntPtr profile, out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NvDrsProfile*, int>)Lookup(IdDrsGetProfileInfo);
            if (fn == null) { error = "NvAPI_DRS_GetProfileInfo not exposed."; return null; }

            var info = new NvDrsProfile { Version = VersionOf<NvDrsProfile>(1) };
            int status = fn(_handle, profile, &info);
            if (status != 0) { error = Describe(status); return null; }

            return new DrsProfileInfo(ReadFixed(info.ProfileName), info.IsPredefined != 0, info.NumOfApps, info.NumOfSettings);
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
            LastStatus = status;
            if (status != 0) { error = Describe(status); return null; }

            // Only DWORD settings matter for DLSS; reading a binary or string one as a DWORD would
            // be meaningless, so it is reported as unsupported rather than as a bogus number.
            if (s.SettingType != 0)
            {
                LastStatus = int.MinValue;   // not "absent": present, and unreadable
                error = $"setting type {s.SettingType} is not a DWORD";
                return null;
            }

            return ToValue(&s);
        }

        private static DrsSettingValue ToValue(NvDrsSetting* s) => new(
            s->SettingId,
            ReadFixed(s->SettingName),
            *(uint*)s->CurrentValue,
            (SettingOrigin)Math.Min(s->SettingLocation, 3u),
            s->IsCurrentPredefined != 0);

        // ---- Writes ------------------------------------------------------------------------
        // Nothing below reaches disk until Save(). Callers must have captured the previous value
        // and origin first; this type enforces nothing about that, DlssOverrideService does.

        /// <summary>
        /// Writes a DWORD setting onto a profile. The setting is created if the profile does not
        /// have it. In-memory only until <see cref="Save"/>.
        /// </summary>
        public bool SetSetting(IntPtr profile, uint settingId, uint value, out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NvDrsSetting*, int>)Lookup(IdDrsSetSetting);
            if (fn == null) { error = "NvAPI_DRS_SetSetting not exposed."; return false; }

            var s = new NvDrsSetting
            {
                Version = VersionOf<NvDrsSetting>(1),
                SettingId = settingId,
                SettingType = 0,       // DWORD
                SettingLocation = 0    // this profile - the driver overwrites this on read anyway
            };
            *(uint*)s.CurrentValue = value;

            int status = fn(_handle, profile, &s);
            if (status != 0) { error = Describe(status); return false; }
            return true;
        }

        /// <summary>
        /// Removes a setting from a profile, so it falls back to whatever the lower layers say.
        /// This is the correct undo only where the setting was genuinely absent before - writing an
        /// explicit zero instead would invent a value the user never had.
        /// </summary>
        public bool DeleteSetting(IntPtr profile, uint settingId, out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)Lookup(IdDrsDeleteProfileSetting);
            if (fn == null) { error = "NvAPI_DRS_DeleteProfileSetting not exposed."; return false; }

            int status = fn(_handle, profile, settingId);
            if (status != 0) { error = Describe(status); return false; }
            return true;
        }

        /// <summary>
        /// Puts NVIDIA's own predefined value back on a setting a user value is sitting over. This
        /// - not <see cref="DeleteSetting"/> - is the undo for a setting that was predefined
        /// before: deleting is for a value that was never there.
        /// </summary>
        public bool RestoreSettingDefault(IntPtr profile, uint settingId, out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)Lookup(IdDrsRestoreDefaultSetting);
            if (fn == null) { error = "NvAPI_DRS_RestoreProfileDefaultSetting not exposed."; return false; }

            int status = fn(_handle, profile, settingId);
            if (status != 0) { error = Describe(status); return false; }
            return true;
        }

        /// <summary>How many applications and settings a profile holds, as this session sees it.</summary>
        public (uint Applications, uint Settings)? GetProfileCounts(IntPtr profile)
        {
            var info = DescribeProfile(profile, out _);
            return info == null ? null : (info.ApplicationCount, info.SettingCount);
        }

        /// <summary>Removes a whole profile. Only ever for one TrayTrigger created and has emptied.</summary>
        public bool DeleteProfile(IntPtr profile, out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Lookup(IdDrsDeleteProfile);
            if (fn == null) { error = "NvAPI_DRS_DeleteProfile not exposed."; return false; }

            int status = fn(_handle, profile);
            if (status != 0) { error = Describe(status); return false; }
            return true;
        }

        /// <summary>
        /// Commits the session to the driver's database. This is the only call here that touches
        /// disk, and the only one expected to need elevation.
        /// </summary>
        public bool Save(out string? error)
        {
            error = null;
            var fn = (delegate* unmanaged[Cdecl]<IntPtr, int>)Lookup(IdDrsSaveSettings);
            if (fn == null) { error = "NvAPI_DRS_SaveSettings not exposed."; return false; }

            int status = fn(_handle);
            if (status != 0) { error = Describe(status); return false; }
            return true;
        }

        /// <summary>
        /// Creates a profile holding one executable, for a game NVIDIA has no entry for. Returns
        /// IntPtr.Zero on failure - a name collision with an existing profile is the usual cause.
        /// </summary>
        public IntPtr CreateProfileForExecutable(string profileName, string exeFileName, out string? error)
        {
            error = null;
            var createProfile = (delegate* unmanaged[Cdecl]<IntPtr, NvDrsProfile*, IntPtr*, int>)Lookup(IdDrsCreateProfile);
            var createApp = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NvDrsApplicationV4*, int>)Lookup(IdDrsCreateApplication);
            if (createProfile == null || createApp == null) { error = "DRS profile creation not exposed."; return IntPtr.Zero; }

            var profile = new NvDrsProfile { Version = VersionOf<NvDrsProfile>(1) };
            WriteFixed(profile.ProfileName, profileName);

            IntPtr handle;
            int status = createProfile(_handle, &profile, &handle);
            if (status != 0) { error = $"NvAPI_DRS_CreateProfile: {Describe(status)}"; return IntPtr.Zero; }

            var app = new NvDrsApplicationV4 { Version = VersionOf<NvDrsApplicationV4>(4), IsPredefined = 0 };
            WriteFixed(app.AppName, exeFileName);
            WriteFixed(app.UserFriendlyName, exeFileName);

            status = createApp(_handle, handle, &app);
            if (status != 0)
            {
                error = $"NvAPI_DRS_CreateApplication: {Describe(status)}";
                // The profile already exists in this session. Left there, a caller that goes on to
                // Save for other reasons would commit an empty profile nobody owns.
                var deleteProfile = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Lookup(IdDrsDeleteProfile);
                if (deleteProfile != null) deleteProfile(_handle, handle);
                return IntPtr.Zero;
            }

            return handle;
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
