using System;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The driver-touching half of <see cref="DlssOverrideService"/>, in the same spirit as
/// <see cref="ISystemTweakBackend"/>: the production implementation forwards to
/// <see cref="NvApi"/>, and tests substitute a fake so the ownership rules - capture, write,
/// and refuse to undo a value something else changed - can be exercised without an NVIDIA machine.
///
/// <para>The plan said to put this behind <c>ISystemTweakBackend</c>. It is a separate interface
/// instead: that one is the performance-profile surface (power plans, Defender, HDR, timer
/// resolution) and DRS sessions have a lifetime of their own, which that interface has no shape
/// for. Same pattern, different seam.</para>
/// </summary>
public interface IDrsBackend
{
    /// <summary>False on machines with no NVIDIA driver. The card and its actions stay hidden.</summary>
    bool IsAvailable { get; }

    /// <summary>Opens a settings session, or returns null with the reason.</summary>
    IDrsSession? OpenSession(out string? error);

    /// <summary>
    /// The driver's own name for a setting id, or null when it does not have that setting.
    /// Needed because a setting nobody has set and an id the driver will refuse both read as
    /// absent through a profile - only this tells them apart.
    /// </summary>
    string? GetSettingName(uint settingId);
}

/// <summary>
/// One DRS session. Reads see the whole database including inherited layers; writes are in-memory
/// until <see cref="Save"/>.
/// </summary>
public interface IDrsSession : IDisposable
{
    /// <summary>
    /// The profile the driver would apply to this executable, or null when NVIDIA has no entry for
    /// it - which is a normal answer, not a failure, and leaves <paramref name="error"/> null.
    /// <b>Null with an error is a failed lookup</b>: the profile may well be there, and a caller
    /// must not go on as though it were not.
    ///
    /// <para>Takes a full path where one is known. NVIDIA's own entries can be qualified by folder
    /// or launcher, and a bare <c>game.exe</c> can match another game's profile.</para>
    /// </summary>
    DrsProfileHandle? FindProfileForExecutable(string exePathOrFileName, out string? error);

    /// <summary>Creates a profile for an executable NVIDIA does not know about.</summary>
    DrsProfileHandle? CreateProfileForExecutable(string profileName, string exeFileName, out string? error);

    /// <summary>
    /// The setting as it applies to this profile, including which layer supplied it. Null with no
    /// <paramref name="error"/> means absent at every layer; <b>null with an error means the read
    /// failed</b>, and recording that as "absent" would make undo delete a value that was there.
    /// </summary>
    DrsSettingReading? GetSetting(DrsProfileHandle profile, uint settingId, out string? error);

    bool SetSetting(DrsProfileHandle profile, uint settingId, uint value, out string? error);

    bool DeleteSetting(DrsProfileHandle profile, uint settingId, out string? error);

    /// <summary>Puts NVIDIA's predefined value back where a user value is sitting over it.</summary>
    bool RestoreSettingDefault(DrsProfileHandle profile, uint settingId, out string? error);

    /// <summary>What a profile holds, as this session sees it - unsaved deletes included.</summary>
    (uint Applications, uint Settings)? GetProfileCounts(DrsProfileHandle profile);

    /// <summary>Removes a whole profile. Only for one TrayTrigger created and has since emptied.</summary>
    bool DeleteProfile(DrsProfileHandle profile, out string? error);

    /// <summary>Commits to the driver's database. The only step expected to need elevation.</summary>
    bool Save(out string? error);
}

/// <summary>
/// An opaque profile reference. Carries the name so a record can be written without a second
/// lookup, and so the fake can identify profiles without pointers.
/// </summary>
public sealed record DrsProfileHandle(IntPtr Handle, string Name, bool IsPredefined);

/// <summary>One setting as the driver reports it, reduced to what the ownership record needs.</summary>
public sealed record DrsSettingReading(uint Value, DlssSettingOrigin Origin);

/// <summary>Production <see cref="IDrsBackend"/> - thin pass-throughs to <see cref="NvApi"/>, no logic.</summary>
public sealed class NvApiDrsBackend : IDrsBackend
{
    public bool IsAvailable => NvApi.TryInitialize();

    public string? GetSettingName(uint settingId) => NvApi.GetSettingName(settingId);

    public IDrsSession? OpenSession(out string? error)
    {
        var session = NvApi.Session.TryOpen(out error);
        return session == null ? null : new NvApiDrsSession(session);
    }

    private sealed class NvApiDrsSession(NvApi.Session session) : IDrsSession
    {
        public DrsProfileHandle? FindProfileForExecutable(string exePathOrFileName, out string? error)
        {
            var info = session.FindProfileForExecutable(exePathOrFileName, out IntPtr handle, out error);
            if (info != null) return new DrsProfileHandle(handle, info.ProfileName, info.IsPredefined);
            if (session.LastStatus == NvApi.StatusExecutableNotFound) error = null;
            return null;
        }

        public DrsProfileHandle? CreateProfileForExecutable(string profileName, string exeFileName, out string? error)
        {
            IntPtr handle = session.CreateProfileForExecutable(profileName, exeFileName, out error);
            return handle == IntPtr.Zero ? null : new DrsProfileHandle(handle, profileName, false);
        }

        public DrsSettingReading? GetSetting(DrsProfileHandle profile, uint settingId, out string? error)
        {
            var value = session.GetSetting(profile.Handle, settingId, out error);
            if (value == null)
            {
                if (session.LastStatus == NvApi.StatusSettingNotFound) error = null;
                return null;
            }
            // The driver's built-in default is "absent at every layer", which this interface
            // promises as null. Handing back a reading with an Absent origin instead matches
            // neither "ours" nor "what we captured", and Reapply would call it a conflict.
            var origin = Translate(value);
            return origin == DlssSettingOrigin.Absent ? null : new DrsSettingReading(value.CurrentValue, origin);
        }

        public bool SetSetting(DrsProfileHandle profile, uint settingId, uint value, out string? error) =>
            session.SetSetting(profile.Handle, settingId, value, out error);

        public bool DeleteSetting(DrsProfileHandle profile, uint settingId, out string? error) =>
            session.DeleteSetting(profile.Handle, settingId, out error);

        public bool RestoreSettingDefault(DrsProfileHandle profile, uint settingId, out string? error) =>
            session.RestoreSettingDefault(profile.Handle, settingId, out error);

        public (uint Applications, uint Settings)? GetProfileCounts(DrsProfileHandle profile) =>
            session.GetProfileCounts(profile.Handle);

        public bool DeleteProfile(DrsProfileHandle profile, out string? error) =>
            session.DeleteProfile(profile.Handle, out error);

        public bool Save(out string? error) => session.Save(out error);

        public void Dispose() => session.Dispose();

        /// <summary>
        /// NVAPI's two fields collapse into the plan's origin. A value on the game's own profile is
        /// NVIDIA's if <c>isCurrentPredefined</c> says so and the user's otherwise; anything from a
        /// lower layer is inherited, whoever set it there.
        /// </summary>
        private static DlssSettingOrigin Translate(NvApi.DrsSettingValue value) => value.Origin switch
        {
            NvApi.SettingOrigin.ApplicationProfile =>
                value.IsCurrentPredefined ? DlssSettingOrigin.Predefined : DlssSettingOrigin.UserSet,
            NvApi.SettingOrigin.GlobalProfile or NvApi.SettingOrigin.BaseProfile => DlssSettingOrigin.Inherited,
            _ => DlssSettingOrigin.Absent
        };
    }
}
