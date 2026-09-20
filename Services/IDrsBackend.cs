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
}

/// <summary>
/// One DRS session. Reads see the whole database including inherited layers; writes are in-memory
/// until <see cref="Save"/>.
/// </summary>
public interface IDrsSession : IDisposable
{
    /// <summary>
    /// The profile the driver would apply to this executable, or null when NVIDIA has no entry for
    /// it - which is a normal answer, not a failure.
    /// </summary>
    DrsProfileHandle? FindProfileForExecutable(string exeFileName, out string? error);

    /// <summary>Creates a profile for an executable NVIDIA does not know about.</summary>
    DrsProfileHandle? CreateProfileForExecutable(string profileName, string exeFileName, out string? error);

    /// <summary>
    /// The setting as it applies to this profile, including which layer supplied it. Null means
    /// absent at every layer.
    /// </summary>
    DrsSettingReading? GetSetting(DrsProfileHandle profile, uint settingId, out string? error);

    bool SetSetting(DrsProfileHandle profile, uint settingId, uint value, out string? error);

    bool DeleteSetting(DrsProfileHandle profile, uint settingId, out string? error);

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

    public IDrsSession? OpenSession(out string? error)
    {
        var session = NvApi.Session.TryOpen(out error);
        return session == null ? null : new NvApiDrsSession(session);
    }

    private sealed class NvApiDrsSession(NvApi.Session session) : IDrsSession
    {
        public DrsProfileHandle? FindProfileForExecutable(string exeFileName, out string? error)
        {
            var info = session.FindProfileForExecutable(exeFileName, out IntPtr handle, out error);
            return info == null ? null : new DrsProfileHandle(handle, info.ProfileName, info.IsPredefined);
        }

        public DrsProfileHandle? CreateProfileForExecutable(string profileName, string exeFileName, out string? error)
        {
            IntPtr handle = session.CreateProfileForExecutable(profileName, exeFileName, out error);
            return handle == IntPtr.Zero ? null : new DrsProfileHandle(handle, profileName, false);
        }

        public DrsSettingReading? GetSetting(DrsProfileHandle profile, uint settingId, out string? error)
        {
            var value = session.GetSetting(profile.Handle, settingId, out error);
            return value == null ? null : new DrsSettingReading(value.CurrentValue, Translate(value));
        }

        public bool SetSetting(DrsProfileHandle profile, uint settingId, uint value, out string? error) =>
            session.SetSetting(profile.Handle, settingId, value, out error);

        public bool DeleteSetting(DrsProfileHandle profile, uint settingId, out string? error) =>
            session.DeleteSetting(profile.Handle, settingId, out error);

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
